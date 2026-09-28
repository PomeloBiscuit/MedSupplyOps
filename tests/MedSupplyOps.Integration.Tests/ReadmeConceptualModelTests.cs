using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Dapper;
using Oracle.ManagedDataAccess.Client;

namespace MedSupplyOps.Integration.Tests;

public sealed partial class ReadmeConceptualModelTests
{
    private const string ConceptTableBegin = "<!-- CONCEPT-TABLE:BEGIN -->";
    private const string ConceptTableEnd = "<!-- CONCEPT-TABLE:END -->";
    private const string SiteMapBegin = "<!-- SITE-MAP:BEGIN -->";
    private const string SiteMapEnd = "<!-- SITE-MAP:END -->";
    private static readonly string[] ShapeNames = ["rect", "ellipse", "polygon", "line", "path"];

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
    public void Conceptual_elements_have_both_language_names()
    {
        using var specification = JsonDocument.Parse(
            File.ReadAllText(FindRepositoryFile("docs/diagrams/conceptual-model.json")));
        var failures = new List<string>();
        foreach (var entry in specification.RootElement.GetProperty("conceptualDiagrams").EnumerateObject())
        {
            var diagram = entry.Value;
            CheckName(diagram, entry.Name, "title", "nameEn", failures);
            CheckName(diagram, entry.Name, "caption", "captionEn", failures);
            foreach (var collection in new[] { "entities", "relationships" })
            {
                foreach (var element in diagram.GetProperty(collection).EnumerateArray())
                {
                    var id = element.GetProperty("id").GetString()!;
                    CheckName(element, id, "name", "nameEn", failures);
                    foreach (var attribute in element.GetProperty("attributes").EnumerateArray())
                    {
                        CheckName(attribute, $"{id}.{attribute.GetProperty("id").GetString()}",
                            "name", "nameEn", failures);
                    }
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Theory]
    [InlineData("er-chen")]
    [InlineData("er-requisition")]
    [InlineData("er-identity")]
    [InlineData("relational-schema")]
    public void Diagram_variants_keep_shapes_and_theme_independent_structure(string stem)
    {
        var diagrams = new Dictionary<string, XDocument>();
        foreach (var language in new[] { "zh", "en" })
        {
            foreach (var theme in new[] { "light", "dark" })
            {
                var name = $"{stem}-{language}-{theme}.svg";
                diagrams[$"{language}-{theme}"] = XDocument.Load(FindRepositoryFile($"docs/diagrams/{name}"));
            }
        }

        foreach (var theme in new[] { "light", "dark" })
        {
            var chinese = ShapeCounts(diagrams[$"zh-{theme}"]);
            var english = ShapeCounts(diagrams[$"en-{theme}"]);
            Assert.Equal(chinese, english);
            if (stem == "er-chen")
            {
                XNamespace svg = "http://www.w3.org/2000/svg";
                var attributes = diagrams[$"zh-{theme}"].Descendants(svg + "ellipse").Count();
                var connectors = diagrams[$"zh-{theme}"].Descendants(svg + "line")
                    .Count(line => line.Attribute("data-attribute-line") is not null);
                Assert.Equal(attributes, connectors);
            }
        }

        foreach (var language in new[] { "zh", "en" })
        {
            var light = WithoutColors(diagrams[$"{language}-light"]);
            var dark = WithoutColors(diagrams[$"{language}-dark"]);
            Assert.Equal(light, dark);
        }
    }

    [Theory]
    [InlineData("system", "er-chen")]
    [InlineData("requisition", "er-requisition")]
    [InlineData("identity", "er-identity")]
    public void Every_conceptual_attribute_is_drawn_in_both_languages(string key, string stem)
    {
        using var specification = JsonDocument.Parse(
            File.ReadAllText(FindRepositoryFile("docs/diagrams/conceptual-model.json")));
        var diagram = specification.RootElement.GetProperty("conceptualDiagrams").GetProperty(key);
        XNamespace svg = "http://www.w3.org/2000/svg";
        foreach (var (language, property) in new[] { ("zh", "name"), ("en", "nameEn") })
        {
            var image = XDocument.Load(FindRepositoryFile($"docs/diagrams/{stem}-{language}-light.svg"));
            var labels = image.Descendants(svg + "text").Select(node => node.Value).ToHashSet(StringComparer.Ordinal);
            foreach (var collection in new[] { "entities", "relationships" })
            {
                foreach (var element in diagram.GetProperty(collection).EnumerateArray())
                {
                    foreach (var attribute in element.GetProperty("attributes").EnumerateArray())
                    {
                        var name = attribute.GetProperty(property).GetString()!;
                        Assert.True(labels.Contains(name), $"{stem}-{language} 缺少屬性 {element.GetProperty("id").GetString()}.{attribute.GetProperty("id").GetString()}：{name}。");
                    }
                }
            }
        }
    }

    [Fact]
    public void Readme_sections_use_existing_light_and_dark_diagrams()
    {
        var readme = File.ReadAllText(FindRepositoryFile("README.md"));
        foreach (var (heading, nextHeading, stems) in new[]
                 {
                     ("## ER 圖（Chen 記法）", "## 關聯綱目", new[] { "er-chen", "er-requisition", "er-identity" }),
                     ("## 關聯綱目", "## 序列圖", new[] { "relational-schema" })
                 })
        {
            var begin = readme.IndexOf(heading, StringComparison.Ordinal);
            var end = readme.IndexOf(nextHeading, begin + heading.Length, StringComparison.Ordinal);
            Assert.True(begin >= 0 && end > begin, $"README 找不到 {heading} 區段。");
            var block = readme[begin..end];
            var pictures = Regex.Matches(block, @"<picture>.*?</picture>", RegexOptions.Singleline);
            Assert.Equal(stems.Length, pictures.Count);
            for (var index = 0; index < stems.Length; index++)
            {
                var stem = stems[index];
                var picture = pictures[index].Value;
                var lightPath = $"docs/diagrams/{stem}-zh-light.svg";
                var darkPath = $"docs/diagrams/{stem}-zh-dark.svg";
                Assert.Contains($"src=\"{lightPath}\"", picture, StringComparison.Ordinal);
                Assert.Contains($"srcset=\"{darkPath}\"", picture, StringComparison.Ordinal);
                Assert.Contains("media=\"(prefers-color-scheme: dark)\"", picture, StringComparison.Ordinal);
                Assert.True(File.Exists(FindRepositoryFile(lightPath)), $"缺少 {lightPath}。");
                Assert.True(File.Exists(FindRepositoryFile(darkPath)), $"缺少 {darkPath}。");
            }
        }
    }

    [Fact]
    public void Site_diagram_images_have_dark_variants_and_old_names_are_gone()
    {
        var site = File.ReadAllText(FindRepositoryFile("docs/index.html"));
        var images = Regex.Matches(site, @"<img\b[^>]*\bsrc=""diagrams/[^""]+""[^>]*>");
        Assert.NotEmpty(images);
        foreach (Match image in images)
        {
            var dark = Regex.Match(image.Value, @"\bdata-src-dark=""(?<path>diagrams/[^""]+)""");
            Assert.True(dark.Success, $"圖缺少 data-src-dark：{image.Value}");
            Assert.True(File.Exists(FindRepositoryFile($"docs/{dark.Groups["path"].Value}")),
                $"深色圖不存在：{dark.Groups["path"].Value}");
        }

        foreach (var path in new[] { "README.md", "docs/index.html", "docs/en/index.html" })
        {
            var page = File.ReadAllText(FindRepositoryFile(path));
            Assert.DoesNotMatch(@"(?:er-requisition|er-identity|relational-schema-requisition|relational-schema-identity)\.svg", page);
        }
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

    private static void CheckName(JsonElement element, string id, string chinese, string english,
        List<string> failures)
    {
        foreach (var property in new[] { chinese, english })
        {
            if (!element.TryGetProperty(property, out var value)
                || string.IsNullOrWhiteSpace(value.GetString()))
            {
                failures.Add($"元素 {id} 缺少 {property}。");
            }
        }
    }

    private static string ShapeCounts(XDocument diagram)
    {
        XNamespace svg = "http://www.w3.org/2000/svg";
        return string.Join(",", ShapeNames
            .Select(name => $"{name}:{diagram.Descendants(svg + name).Count()}"));
    }

    private static string WithoutColors(XDocument document)
    {
        var copy = new XDocument(document);
        XNamespace svg = "http://www.w3.org/2000/svg";
        copy.Descendants(svg + "style").Remove();
        foreach (var attribute in copy.Root!.DescendantsAndSelf().Attributes()
                     .Where(attribute => attribute.Name.LocalName is "fill" or "stroke" or "color" or "style")
                     .ToArray())
        {
            attribute.Remove();
        }

        return copy.ToString(SaveOptions.DisableFormatting);
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
