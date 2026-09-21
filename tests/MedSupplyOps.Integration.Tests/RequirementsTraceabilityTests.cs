using System.Text.RegularExpressions;

namespace MedSupplyOps.Integration.Tests;

/// <summary>
/// 需求文件不是一次性描述；本關卡讓需求、追溯表與可定位的證據持續互相約束。
/// </summary>
public sealed class RequirementsTraceabilityTests
{
    private static readonly Regex DefinedRequirement = new(
        @"^\s*(?:-\s+|\|\s*)\*\*((?:FR|SEC|NFR)-\d+)\b",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex RequirementIdentifier = new(@"\bFR-\d+\b", RegexOptions.Compiled);
    private static readonly Regex TestMethodDefinition = new(
        @"\bpublic\s+(?:async\s+)?(?:Task|void)\s+([A-Za-z_]\w*)\s*\(",
        RegexOptions.Compiled);
    private static readonly Regex ProbeIdentifier = new(@"\bP\d+\b", RegexOptions.Compiled);
    private static readonly Regex ProbeName = new(
        @"Name\s*=\s*'(?<id>P\d+)\b",
        RegexOptions.Compiled);
    private static readonly Regex RepositoryPath = new(
        @"(?<![A-Za-z0-9_])(?:(?:src|tests|docs|scripts)/[A-Za-z0-9_.\-/]+|docker-compose\.yml|README\.md|\.gitignore)",
        RegexOptions.Compiled);

    [Fact]
    public void Requirements_traceability_table_covers_every_defined_requirement_exactly_once()
    {
        var root = FindRepositoryRoot();
        var document = File.ReadAllText(Path.Combine(root, "docs", "requirements.md"));
        var defined = DefinedRequirements(document);
        var rows = TraceabilityRows(document);
        var failures = new List<string>();

        foreach (var requirement in defined)
        {
            var count = rows.Count(row => row.Requirement == requirement);
            if (count == 0)
            {
                failures.Add($"{requirement}：需求文件第 3～5 章有定義，但需求追溯表少一列。");
            }
            else if (count != 1)
            {
                failures.Add($"{requirement}：需求追溯表出現 {count} 列，必須恰好一列。");
            }
        }

        foreach (var row in rows.Where(row => !defined.Contains(row.Requirement)))
        {
            failures.Add($"{row.Requirement}：需求追溯表有列，但不是需求文件第 3～5 章定義的編號。");
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void Implemented_requirements_have_real_evidence_and_other_statuses_have_none()
    {
        var root = FindRepositoryRoot();
        var document = File.ReadAllText(Path.Combine(root, "docs", "requirements.md"));
        var testMethods = TestMethodNames(root);
        var probes = ProbeNames(root);
        var failures = new List<string>();

        foreach (var row in TraceabilityRows(document))
        {
            if (row.Status is not ("已實作" or "部分實作" or "v1 不做" or "延後"))
            {
                failures.Add($"{row.Requirement}：狀態「{row.Status}」不合法；只允許 已實作、部分實作、v1 不做、延後。");
                continue;
            }

            // ★ 「部分：」開頭的實作位置，狀態不可以是「已實作」。
            //   狀態欄才是別人會讀的那一格；把缺口寫在另一欄不能抵銷一個過度宣稱。
            //   覆核時實測抓到：FR-501 的實作位置寫著「未涵蓋 FR-301／FR-303、沒有 OpenAPI 文件」，
            //   狀態卻是「已實作」。
            if (row.Status == "已實作" && row.Implementation.TrimStart().StartsWith("部分：", StringComparison.Ordinal))
            {
                failures.Add($"{row.Requirement}：實作位置以「部分：」開頭，狀態不可以是「已實作」；請改成「部分實作」。");
                continue;
            }

            if (row.Status == "部分實作" && !row.Implementation.TrimStart().StartsWith("部分：", StringComparison.Ordinal))
            {
                failures.Add($"{row.Requirement}：狀態是「部分實作」，實作位置必須以「部分：」開頭並寫明缺什麼。");
                continue;
            }

            if (row.Status is not ("已實作" or "部分實作"))
            {
                if (row.Verification != "—")
                {
                    failures.Add($"{row.Requirement}：狀態是「{row.Status}」，驗證欄必須是 —。");
                }

                continue;
            }

            if (string.IsNullOrWhiteSpace(row.Verification) || row.Verification == "—")
            {
                failures.Add($"{row.Requirement}：已實作但驗證欄沒有參照。");
                continue;
            }

            var foundEvidence = false;
            foreach (Match match in ProbeIdentifier.Matches(row.Verification))
            {
                if (probes.Contains(match.Value))
                {
                    foundEvidence = true;
                }
                else
                {
                    failures.Add($"{row.Requirement}：已實作但驗證欄的探針編號 {match.Value} 不存在於 scripts/mutation-probe.ps1 的 Name 欄位。");
                }
            }

            foreach (Match match in RepositoryPath.Matches(row.Verification))
            {
                var path = match.Value.Replace('/', Path.DirectorySeparatorChar);
                if (File.Exists(Path.Combine(root, path)))
                {
                    foundEvidence = true;
                }
                else
                {
                    failures.Add($"{row.Requirement}：已實作但驗證欄的檔案 {match.Value} 不存在。");
                }
            }

            foreach (var candidate in CandidateTestMethods(row.Verification))
            {
                if (testMethods.Contains(candidate))
                {
                    foundEvidence = true;
                }
                else
                {
                    failures.Add($"{row.Requirement}：已實作但驗證欄的測試方法 {candidate} 不存在於 tests/**/*.cs。");
                }
            }

            if (!foundEvidence)
            {
                failures.Add($"{row.Requirement}：已實作但驗證欄沒有可辨識的測試方法、探針編號或 repo 檔案路徑。");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void Source_code_does_not_reference_orphan_functional_requirements()
    {
        var root = FindRepositoryRoot();
        var document = File.ReadAllText(Path.Combine(root, "docs", "requirements.md"));
        var definedFunctionalRequirements = DefinedRequirements(document)
            .Where(requirement => requirement.StartsWith("FR-", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        var failures = new List<string>();

        foreach (var directory in new[] { "src", "tests" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, directory), "*.cs", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (relativePath.Contains("/bin/", StringComparison.OrdinalIgnoreCase) ||
                    relativePath.Contains("/obj/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var source = File.ReadAllText(file);
                foreach (Match match in RequirementIdentifier.Matches(source))
                {
                    if (!definedFunctionalRequirements.Contains(match.Value))
                    {
                        var line = source.AsSpan(0, match.Index).Count('\n') + 1;
                        failures.Add($"{match.Value}：{relativePath}:{line} 引用了需求文件不存在的功能編號。");
                    }
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static HashSet<string> DefinedRequirements(string document)
    {
        var section = Section(document, "## 3.", "## 6.");
        return DefinedRequirement.Matches(section)
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static List<TraceabilityRow> TraceabilityRows(string document)
    {
        var section = Section(document, "## 9.", null);
        var rows = new List<TraceabilityRow>();
        foreach (var line in section.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith('|') || line.StartsWith("|---", StringComparison.Ordinal))
            {
                continue;
            }

            var cells = line.Split('|').Select(cell => cell.Trim()).ToArray();
            if (cells.Length != 6 || cells[1] == "需求")
            {
                continue;
            }

            rows.Add(new TraceabilityRow(
                cells[1].Trim('`', '*'),
                cells[2],
                cells[3],
                cells[4]));
        }

        return rows;
    }

    private static IEnumerable<string> CandidateTestMethods(string verification)
    {
        var scrubbed = ProbeIdentifier.Replace(RepositoryPath.Replace(verification, " "), " ");
        return Regex.Matches(scrubbed, @"\b[A-Za-z_][A-Za-z0-9_]*\b")
            .Select(match => match.Value)
            .Where(value => value.Contains('_', StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal);
    }

    private static HashSet<string> TestMethodNames(string root)
        => Directory.EnumerateFiles(Path.Combine(root, "tests"), "*.cs", SearchOption.AllDirectories)
            .SelectMany(file => TestMethodDefinition.Matches(File.ReadAllText(file)).Select(match => match.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> ProbeNames(string root)
        => ProbeName.Matches(File.ReadAllText(Path.Combine(root, "scripts", "mutation-probe.ps1")))
            .Select(match => match.Groups["id"].Value)
            .ToHashSet(StringComparer.Ordinal);

    private static string Section(string document, string startHeading, string? endHeading)
    {
        var start = document.IndexOf(startHeading, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidOperationException($"找不到文件章節 {startHeading}。");
        }

        var end = endHeading is null
            ? document.Length
            : document.IndexOf(endHeading, start, StringComparison.Ordinal);
        if (end < 0)
        {
            throw new InvalidOperationException($"找不到文件章節 {endHeading}。");
        }

        return document[start..end];
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

        throw new InvalidOperationException("找不到 MedSupplyOps.slnx，無法執行需求追溯結構性關卡。");
    }

    private sealed record TraceabilityRow(string Requirement, string Status, string Implementation, string Verification);
}
