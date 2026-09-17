using System.Diagnostics;
using Xunit.Abstractions;

namespace MedSupplyOps.Integration.Tests;

/// <summary>
/// ★ 前端邏輯（可用量的競態、掃描條碼的輸入處理）寫在 <c>wwwroot/js</c>，C# 的測試碰不到。
///
/// 所以 <c>tests/js/</c> 底下有 Node 測試。但**沒有人會跑的測試等於不存在**——
/// 這支測試的唯一職責就是把它們接進第二道關卡。
///
/// ★ 跑的是「<c>tests/js/</c> 底下所有 <c>*.test.js</c>」，不是寫死的檔名清單。
///   第一版只寫死了一個檔名，後來新增的 <c>receiving-barcode.test.js</c> 就成了孤兒：
///   它自己跑是綠的，但沒有任何關卡會去跑它。寫死清單等於要求每個新增測試的人
///   都記得回來改這裡——那不會發生。
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
        var testFiles = Directory.GetFiles(Path.Combine(repositoryRoot, "tests", "js"), "*.test.js")
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        // 至少要有目前已知的兩支。少於這個數字代表有人把測試搬走或改了副檔名，
        // 而這條測試會在「零個檔案」時安靜地通過——那正是要防的事。
        Assert.True(testFiles.Length >= 2, $"tests/js 底下只找到 {testFiles.Length} 支 *.test.js，預期至少 2 支。");

        if (!TryRun("node", "--version", repositoryRoot, out _, out _))
        {
            _output.WriteLine("★ 這台機器沒有 Node，前端競態測試被跳過 —— 這不是通過。");
            _output.WriteLine("  要驗證它，請安裝 Node 後重跑，或直接執行：");
            _output.WriteLine("  node --test " + string.Join(" ", testFiles.Select(path => Path.GetRelativePath(repositoryRoot, path))));
            return;
        }

        // 逐一列出檔案路徑，不交給 node 自己展開目錄或萬用字元：
        // Node 25 把目錄參數當成單一檔案處理會直接失敗，而萬用字元的展開又依殼層而異。
        var arguments = "--test " + string.Join(" ", testFiles.Select(path => $"\"{path}\""));
        var succeeded = TryRun("node", arguments, repositoryRoot, out var output, out var exitCode);
        _output.WriteLine($"執行了 {testFiles.Length} 支前端測試檔：{string.Join("、", testFiles.Select(Path.GetFileName))}");
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
