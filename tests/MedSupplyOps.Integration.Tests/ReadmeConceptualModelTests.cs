using System.Text.RegularExpressions;
using Dapper;
using Oracle.ManagedDataAccess.Client;

namespace MedSupplyOps.Integration.Tests;

public sealed partial class ReadmeConceptualModelTests
{
    private const string ChenBegin = "<!-- CHEN-DIAGRAM:BEGIN -->";
    private const string ChenEnd = "<!-- CHEN-DIAGRAM:END -->";
    private const string ConceptTableBegin = "<!-- CONCEPT-TABLE:BEGIN -->";
    private const string ConceptTableEnd = "<!-- CONCEPT-TABLE:END -->";

    [Fact]
    public async Task Concept_mapping_contains_every_database_table_exactly_once()
    {
        var readme = File.ReadAllText(FindRepositoryFile("README.md"));
        var mappings = ParseMappings(ExtractMarkedBlock(readme, ConceptTableBegin, ConceptTableEnd));

        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.OpenAsync();
        var databaseTables = (await connection.QueryAsync<string>(
            """
            SELECT table_name
              FROM user_tables
             WHERE table_name <> 'SCHEMA_VERSIONS'
               AND table_name NOT LIKE 'BIN$%'
               AND NOT REGEXP_LIKE(table_name, '^CMP[0-9]+\$')
             ORDER BY table_name
            """)).ToHashSet(StringComparer.Ordinal);

        var mappedTables = mappings.SelectMany(mapping => mapping.Tables).ToList();
        var failures = new List<string>();
        foreach (var table in databaseTables.OrderBy(table => table, StringComparer.Ordinal))
        {
            var occurrences = mappedTables.Count(mapped => string.Equals(mapped, table, StringComparison.Ordinal));
            if (occurrences != 1)
            {
                failures.Add($"資料表 {table} 在概念對照表中出現 {occurrences} 次，預期恰好 1 次。");
            }
        }

        foreach (var table in mappedTables.Distinct(StringComparer.Ordinal).OrderBy(table => table, StringComparer.Ordinal))
        {
            if (!databaseTables.Contains(table))
            {
                failures.Add($"概念對照表中的資料表 {table} 不存在於 Oracle 資料字典。");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void Every_mapped_concept_appears_in_the_chen_diagram()
    {
        var readme = File.ReadAllText(FindRepositoryFile("README.md"));
        var chenDiagram = ExtractMarkedBlock(readme, ChenBegin, ChenEnd);
        var mappings = ParseMappings(ExtractMarkedBlock(readme, ConceptTableBegin, ConceptTableEnd));
        var failures = new List<string>();

        foreach (var mapping in mappings.Where(mapping => !mapping.Concept.StartsWith('—')))
        {
            var concept = mapping.Concept.Replace("（關聯）", string.Empty, StringComparison.Ordinal).Trim();
            if (!chenDiagram.Contains(concept, StringComparison.Ordinal))
            {
                failures.Add($"概念「{concept}」沒有出現在 Chen 圖的 Mermaid 區塊中。");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static List<ConceptMapping> ParseMappings(string markdownTable)
    {
        var mappings = new List<ConceptMapping>();
        foreach (Match row in MarkdownRowRegex().Matches(markdownTable))
        {
            var concept = row.Groups["concept"].Value.Trim();
            if (concept is "概念" or "---")
            {
                continue;
            }

            var tables = TableNameRegex().Matches(row.Groups["tables"].Value)
                .Select(match => match.Groups["table"].Value)
                .ToArray();
            Assert.True(tables.Length > 0, $"概念「{concept}」沒有列出任何資料表。");
            mappings.Add(new ConceptMapping(concept, tables));
        }

        Assert.NotEmpty(mappings);
        return mappings;
    }

    private static string ExtractMarkedBlock(string text, string beginMarker, string endMarker)
    {
        var begin = text.IndexOf(beginMarker, StringComparison.Ordinal);
        var end = text.IndexOf(endMarker, StringComparison.Ordinal);
        Assert.True(begin >= 0, $"README 找不到標記 {beginMarker}。");
        Assert.True(end > begin, $"README 找不到標記 {endMarker}，或標記順序錯誤。");
        return text[(begin + beginMarker.Length)..end];
    }

    private static string FindRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"找不到 repository 檔案：{relativePath}。", relativePath);
    }

    [GeneratedRegex(@"^\|\s*(?<concept>[^|]+?)\s*\|\s*(?<tables>[^|]+?)\s*\|\r?$", RegexOptions.Multiline)]
    private static partial Regex MarkdownRowRegex();

    [GeneratedRegex(@"`(?<table>[A-Z][A-Z0-9_]*)`")]
    private static partial Regex TableNameRegex();

    private sealed record ConceptMapping(string Concept, IReadOnlyList<string> Tables);
}
