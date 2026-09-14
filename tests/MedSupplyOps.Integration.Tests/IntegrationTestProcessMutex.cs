using Xunit.Abstractions;
using Xunit.Sdk;

namespace MedSupplyOps.Integration.Tests;

/// <summary>
/// 在 testhost 載入組件時取得跨程序鎖。xUnit 的 DisableTestParallelization
/// 只能處理同一個程序內的測試類別，無法保護兩個 dotnet test 程序共用 Oracle。
/// </summary>
internal static class IntegrationTestProcessMutex
{
    private const string MutexName = @"Global\MedSupplyOps.IntegrationTests";
    private static readonly object Sync = new();
    private static Mutex? _mutex;
    private static bool _ownsMutex;

    internal static void Acquire()
    {
        lock (Sync)
        {
            if (_ownsMutex)
            {
                return;
            }

            _mutex = new Mutex(initiallyOwned: false, MutexName);
            try
            {
                _ownsMutex = _mutex.WaitOne(TimeSpan.FromSeconds(5));
            }
            catch (AbandonedMutexException)
            {
                // 前一個 testhost 非正常結束；OS 已釋放鎖，這個程序現在擁有它。
                _ownsMutex = true;
            }

            if (!_ownsMutex)
            {
                _mutex.Dispose();
                _mutex = null;
                var message =
                    "MedSupplyOps integration tests refused to start: another testhost holds "
                    + MutexName
                    + ". This suite shares one Oracle schema. Stop or wait for the other dotnet test process, then retry; this run intentionally waits no longer than 5 seconds.";

                // xUnit v2 的 test-case orderer 會把例外逐類別重試；那會讓第二個
                // dotnet test 在第一個結束後接手執行，正是這道防呆要禁止的排隊行為。
                // 明確印出訊息後終止「這個」testhost，讓 VSTest 立刻把整個第二程序判成失敗。
                Console.Error.WriteLine(message);
                Environment.FailFast(message);
            }

            AppDomain.CurrentDomain.ProcessExit += (_, _) => Release();
        }
    }

    private static void Release()
    {
        if (!_ownsMutex || _mutex is null)
        {
            return;
        }

        try
        {
            _mutex.ReleaseMutex();
        }
        finally
        {
            _ownsMutex = false;
            _mutex.Dispose();
            _mutex = null;
        }
    }
}

/// <summary>
/// xUnit v2 runner 一定會呼叫組件級 test-case orderer；用這個已被 adapter 採用的
/// 擴充點取得鎖，而不依賴該 adapter 未載入的自訂 TestFramework。
/// </summary>
public sealed class MutexTestCaseOrderer : ITestCaseOrderer
{
    public IEnumerable<TTestCase> OrderTestCases<TTestCase>(IEnumerable<TTestCase> testCases)
        where TTestCase : ITestCase
    {
        IntegrationTestProcessMutex.Acquire();
        return testCases;
    }
}
