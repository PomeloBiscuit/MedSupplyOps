using System.Diagnostics;
using Xunit.Abstractions;

namespace MedSupplyOps.Integration.Tests;

/// <summary>
/// ★ 前端的競態邏輯（後發的可用量請求必須贏）寫在 <c>wwwroot/js</c>，C# 的測試碰不到它。
///
/// 所以它有一支 Node 測試：<c>tests/js/requisition-availability.test.js</c>。
/// 但**沒有人會跑的測試等於不存在** —— 它原本不在任何一道關卡裡，
/// 誰把那段邏輯改壞都不會有紅燈。這支測試的唯一職責就是把它接進第二道關卡。
///
/// ★ 環境沒有 Node 時：**明確標示跳過，而不是安靜地通過**。
///   README 只要求 .NET SDK 10 與 Docker（冷啟驗證也是照那兩項做的），
///   不能因為這支測試就把 Node 變成跑起來的必要條件；
///   但也不能讓「沒跑」看起來跟「跑過而且綠燈」一模一樣。
/// </summary>
public sealed class JavaScriptFrontendTests
{
    private readonly ITestOutputHelper _output;

    public JavaScriptFrontendTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Frontend_race_condition_tests_pass_when_node_is_available()
    {
        var repositoryRoot = FindRepositoryRoot();
        var testFile = Path.Combine(repositoryRoot, "tests", "js", "requisition-availability.test.js");
        Assert.True(File.Exists(testFile), $"找不到前端測試檔：{testFile}");

        if (!TryRun("node", "--version", repositoryRoot, out _, out _))
        {
            _output.WriteLine("★ 這台機器沒有 Node，前端競態測試被跳過 —— 這不是通過。");
            _output.WriteLine("  要驗證它，請安裝 Node 後重跑，或直接執行：");
            _output.WriteLine("  node --test tests/js/requisition-availability.test.js");
            return;
        }

        var succeeded = TryRun("node", $"--test \"{testFile}\"", repositoryRoot, out var output, out var exitCode);
        _output.WriteLine(output);
        Assert.True(succeeded && exitCode == 0, $"前端競態測試失敗（exit {exitCode}）：{Environment.NewLine}{output}");
    }

    private static bool TryRun(string fileName, string arguments, string workingDirectory, out string output, out int exitCode)
    {
        output = string.Empty;
        exitCode = -1;

        var startInfo = new ProcessStartInfo(fileName, arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit(60_000);
            exitCode = process.ExitCode;
            return true;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            // 找不到 node 執行檔：交給呼叫端當成「跳過」處理。
            return false;
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "MedSupplyOps.slnx")))
            {
                return current.FullName;
            }
        }

        throw new InvalidOperationException("找不到 MedSupplyOps.slnx，無法定位 repo 根目錄。");
    }
}
