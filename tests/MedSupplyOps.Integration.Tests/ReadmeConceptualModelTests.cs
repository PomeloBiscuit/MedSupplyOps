using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Oracle.ManagedDataAccess.Client;

namespace MedSupplyOps.Integration.Tests;

public sealed partial class ReadmeConceptualModelTests
{
    private const string ConceptTableBegin = "<!-- CONCEPT-TABLE:BEGIN -->";
    private const string ConceptTableEnd = "<!-- CONCEPT-TABLE:END -->";
    private const string SiteMapBegin = "<!-- SITE-MAP:BEGIN -->";
    private const string SiteMapEnd = "<!-- SITE-MAP:END -->";

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
    public void Every_mapped_concept_appears_in_the_conceptual_model_specification()
    {
        var readme = File.ReadAllText(FindRepositoryFile("README.md"));
        var mappings = ParseMappings(ExtractMarkedBlock(readme, ConceptTableBegin, ConceptTableEnd));
        using var specification = JsonDocument.Parse(
            File.ReadAllText(FindRepositoryFile("docs/diagrams/conceptual-model.json")));
        var concepts = specification.RootElement.GetProperty("conceptualDiagrams")
            .EnumerateObject()
            .SelectMany(diagram =>
                diagram.Value.GetProperty("entities").EnumerateArray()
                    .Concat(diagram.Value.GetProperty("relationships").EnumerateArray()))
            .Select(element => element.GetProperty("name").GetString())
            .Where(name => name is not null)
            .ToHashSet(StringComparer.Ordinal);
        var failures = new List<string>();

        foreach (var mapping in mappings.Where(mapping => !mapping.Concept.StartsWith('—')))
        {
            var concept = mapping.Concept.Replace("（關聯）", string.Empty, StringComparison.Ordinal).Trim();
            if (!concepts.Contains(concept))
            {
                failures.Add($"概念「{concept}」沒有出現在圖面規格檔中。");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void Site_map_contains_every_page_controller()
    {
        var readme = File.ReadAllText(FindRepositoryFile("README.md"));
        var siteMap = ExtractMarkedBlock(readme, SiteMapBegin, SiteMapEnd);
        var nodeIds = SiteMapNodeRegex().Matches(siteMap)
            .Select(match => match.Groups["id"].Value)
            .ToHashSet(StringComparer.Ordinal);
        var repositoryRoot = Path.GetDirectoryName(FindRepositoryFile("README.md"))!;
        var controllersDirectory = Path.Combine(repositoryRoot, "src", "MedSupplyOps.Web", "Controllers");
        var failures = new List<string>();

        foreach (var path in Directory.EnumerateFiles(controllersDirectory, "*Controller.cs")
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            var controllerName = Path.GetFileNameWithoutExtension(path);
            if (controllerName.EndsWith("ApiController", StringComparison.Ordinal)
                || controllerName is "FhirController" or "HomeController"
                || !ControllerReturnsPageRegex().IsMatch(File.ReadAllText(path)))
            {
                continue;
            }

            var nodeId = controllerName[..^"Controller".Length].ToUpperInvariant();
            if (!nodeIds.Contains(nodeId))
            {
                failures.Add($"會回傳頁面的 Controller {controllerName} 沒有網站架構圖節點 {nodeId}。");
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

    [GeneratedRegex(@"(?<![A-Z0-9_])(?<id>[A-Z][A-Z0-9_]*)\s*\[")]
    private static partial Regex SiteMapNodeRegex();

    [GeneratedRegex(@"(?:return|=>)\s+View\s*\(")]
    private static partial Regex ControllerReturnsPageRegex();

    private sealed record ConceptMapping(string Concept, IReadOnlyList<string> Tables);
}
