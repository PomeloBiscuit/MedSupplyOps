using System.Text.RegularExpressions;
using MedSupplyOps.Infrastructure.Time;

namespace MedSupplyOps.Integration.Tests;

public sealed class BusinessTimeGuardTests
{
    private static readonly Regex ForbiddenHostClock = new(
        @"\b(?:DateTime\.Today|DateTime\.Now|DateTimeOffset\.Now)\b",
        RegexOptions.Compiled);

    // 保留明確白名單，而不是用目錄或萬用字元把未來的違規一起遮掉。
    private static readonly HashSet<string> AllowedFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "src/MedSupplyOps.Infrastructure/Time/BusinessCalendar.cs",
    };

    [Fact]
    public void Production_and_test_CSharp_code_do_not_read_the_host_local_clock()
    {
        var root = FindRepositoryRoot();
        var violations = new List<string>();

        foreach (var directory in new[] { "src", "tests" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, directory), "*.cs", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (relativePath.Contains("/bin/", StringComparison.OrdinalIgnoreCase) ||
                    relativePath.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
                    AllowedFiles.Contains(relativePath))
                {
                    continue;
                }

                var code = StripCommentsAndStrings(File.ReadAllText(file));
                foreach (Match match in ForbiddenHostClock.Matches(code))
                {
                    var line = code.AsSpan(0, match.Index).Count('\n') + 1;
                    violations.Add($"{relativePath}:{line}: {match.Value}");
                }
            }
        }

        Assert.True(violations.Count == 0, "禁止讀取主機本地時鐘：" + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void Business_calendar_uses_Taipei_date_and_converts_utc_for_display()
    {
        var provider = new TestClock(TestBusinessCalendar.DefaultInstant);
        var calendar = new BusinessCalendar(provider, TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei"));

        Assert.Equal(new DateOnly(2026, 9, 11), calendar.Today);
        Assert.Equal(new DateTime(2026, 9, 11, 3, 0, 0), calendar.ToBusinessTime(calendar.UtcNow));
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

        throw new InvalidOperationException("找不到 MedSupplyOps.slnx，無法執行主機時鐘結構性關卡。");
    }

    private static string StripCommentsAndStrings(string source)
        => Regex.Replace(
            source,
            @"//[^\r\n]*|/\*[\s\S]*?\*/|@?""(?:""""|[^""])*""",
            match => new string(match.Value.Select(character => character is '\r' or '\n' ? character : ' ').ToArray()));
}
