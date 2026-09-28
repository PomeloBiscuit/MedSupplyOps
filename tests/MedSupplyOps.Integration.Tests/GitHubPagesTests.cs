using System.Globalization;
using System.Text.RegularExpressions;

namespace MedSupplyOps.Integration.Tests;

public sealed class GitHubPagesTests
{
    private static readonly string[] ExpectedSections = ["verify", "probes", "architecture", "data", "flow", "performance", "ops", "scope"];
    private static readonly string ChinesePage = FindRepositoryFile("docs/index.html");
    private static readonly string EnglishPage = FindRepositoryFile("docs/en/index.html");
    private static readonly string StyleSheet = FindRepositoryFile("docs/assets/site.css");

    [Fact]
    public void Pages_have_the_expected_languages_and_matching_sections()
    {
        var chinese = File.ReadAllText(ChinesePage);
        var english = File.ReadAllText(EnglishPage);
        Assert.Matches("<html\\s+lang=\"zh-Hant\"", chinese);
        Assert.Matches("<html\\s+lang=\"en\"", english);
        var chineseSections = SectionIds(chinese);
        var englishSections = SectionIds(english);
        Assert.Equal(ExpectedSections, chineseSections);
        Assert.True(chineseSections.SequenceEqual(englishSections),
            $"Section sequence differs: Chinese [{string.Join(", ", chineseSections)}], English [{string.Join(", ", englishSections)}]");
    }

    [Fact]
    public void Pages_have_matching_figures_mermaid_blocks_and_numeric_facts()
    {
        var chinese = File.ReadAllText(ChinesePage);
        var english = File.ReadAllText(EnglishPage);
        Assert.Equal(Regex.Count(chinese, @"<img\b"), Regex.Count(english, @"<img\b"));
        Assert.Equal(Regex.Count(chinese, @"<div\b[^>]*class=""mermaid"""),
            Regex.Count(english, @"<div\b[^>]*class=""mermaid"""));
        var chineseNumbers = Numbers(chinese);
        var englishNumbers = Numbers(english);
        Assert.True(chineseNumbers.SetEquals(englishNumbers),
            $"Numeric facts differ: Chinese only [{string.Join(", ", chineseNumbers.Except(englishNumbers))}], English only [{string.Join(", ", englishNumbers.Except(chineseNumbers))}]");
    }

    [Fact]
    public void English_page_has_no_cjk_outside_its_language_button()
    {
        var page = File.ReadAllText(EnglishPage);
        var button = Regex.Matches(page, @"<a\b[^>]*id=""language-toggle""[^>]*>(?<label>[^<]*)</a>");
        Assert.Single(button.Cast<Match>());
        Assert.Equal("中文", button[0].Groups["label"].Value);
        var label = button[0].Groups["label"];
        var withoutAllowedLabel = page[..label.Index] + new string(' ', label.Length) + page[(label.Index + label.Length)..];
        var unexpected = Regex.Match(withoutAllowedLabel, @"[\u3400-\u9fff\uf900-\ufaff]");
        if (unexpected.Success)
        {
            var prefix = withoutAllowedLabel[..unexpected.Index];
            var line = prefix.Count(character => character == '\n') + 1;
            var column = unexpected.Index - prefix.LastIndexOf('\n');
            Assert.Fail($"CJK character '{unexpected.Value}' in docs/en/index.html at line {line}, column {column}");
        }
    }

    [Fact]
    public void Diagram_images_use_existing_language_and_theme_variants()
    {
        foreach (var (path, language) in new[] { (ChinesePage, "zh"), (EnglishPage, "en") })
        {
            foreach (Match image in Regex.Matches(File.ReadAllText(path), @"<img\b[^>]*>"))
            {
                var src = Attribute(image.Value, "src");
                if (src is null || !src.Contains("diagrams/", StringComparison.Ordinal))
                {
                    continue;
                }
                var dark = Attribute(image.Value, "data-src-dark");
                Assert.True(dark is not null, $"Missing data-src-dark on {image.Value}");
                Assert.Contains($"-{language}-light.svg", src, StringComparison.Ordinal);
                Assert.Contains($"-{language}-dark.svg", dark, StringComparison.Ordinal);
                Assert.True(File.Exists(Resolve(path, src)), $"Missing diagram: {src}");
                Assert.True(File.Exists(Resolve(path, dark)), $"Missing diagram: {dark}");
            }
        }
    }

    [Fact]
    public void Data_model_figures_include_system_modules_and_relational_schema_in_order()
    {
        foreach (var (path, language) in new[] { (ChinesePage, "zh"), (EnglishPage, "en") })
        {
            var page = File.ReadAllText(path);
            var data = Regex.Match(page, @"<section id=""data"">(?<body>[\s\S]*?)</section>").Groups["body"].Value;
            var images = Regex.Matches(data, @"<div class=""figure-scroll""><a\b[^>]*><img\b[^>]*></a></div>")
                .Select(match => match.Value).ToArray();
            var stems = new[] { "er-chen", "er-requisition", "er-identity", "relational-schema" };
            Assert.Equal(stems.Length, images.Length);
            for (var index = 0; index < stems.Length; index++)
            {
                var stem = stems[index];
                Assert.Contains($"{stem}-{language}-light.svg", images[index], StringComparison.Ordinal);
                Assert.Contains($"{stem}-{language}-dark.svg", images[index], StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Navigation_controls_are_outside_scrollable_links()
    {
        foreach (var path in new[] { ChinesePage, EnglishPage })
        {
            var nav = Regex.Match(File.ReadAllText(path), @"<nav class=""top"">(?<body>[\s\S]*?)</nav>").Groups["body"].Value;
            var links = Regex.Match(nav, @"<div class=""nav-links"">(?<body>[\s\S]*?)</div>").Groups["body"].Value;
            var tools = Regex.Match(nav, @"<div class=""nav-tools"">(?<body>[\s\S]*?)</div>").Groups["body"].Value;
            Assert.NotEmpty(links);
            Assert.Contains("id=\"language-toggle\"", tools, StringComparison.Ordinal);
            Assert.Contains("id=\"theme-toggle\"", tools, StringComparison.Ordinal);
            Assert.DoesNotContain("id=\"language-toggle\"", links, StringComparison.Ordinal);
            Assert.DoesNotContain("id=\"theme-toggle\"", links, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Language_and_theme_controls_exist_on_both_pages()
    {
        foreach (var (path, targetLanguage) in new[] { (ChinesePage, "en"), (EnglishPage, "zh-Hant") })
        {
            var page = File.ReadAllText(path);
            var language = Regex.Match(page, @"<a\b[^>]*id=""language-toggle""[^>]*>[^<]+</a>");
            Assert.True(language.Success, $"Missing language control in {path}");
            Assert.Equal(targetLanguage, Attribute(language.Value, "hreflang"));
            Assert.True(File.Exists(Resolve(path, Attribute(language.Value, "href")!)), $"Language target missing in {path}");
            var theme = Regex.Match(page, @"<button\b[^>]*id=""theme-toggle""[^>]*>(?<label>[^<]*)</button>");
            Assert.True(theme.Success, $"Missing theme control in {path}");
            Assert.NotNull(Attribute(theme.Value, "aria-pressed"));
            Assert.True(!string.IsNullOrWhiteSpace(theme.Groups["label"].Value)
                || !string.IsNullOrWhiteSpace(Attribute(theme.Value, "aria-label")), $"Theme control has no accessible name in {path}");
        }
    }

    [Fact]
    public void Theme_bootstrap_runs_in_head_before_stylesheet()
    {
        foreach (var path in new[] { ChinesePage, EnglishPage })
        {
            var page = File.ReadAllText(path);
            var head = Regex.Match(page, @"<head>(?<body>[\s\S]*?)</head>").Groups["body"].Value;
            var bootstrap = Regex.Match(head, @"<script>(?<body>[\s\S]*?)</script>");
            var stylesheet = Regex.Match(head, @"<link\b[^>]*rel=""stylesheet""[^>]*>");
            Assert.True(bootstrap.Success && stylesheet.Success, $"Missing head script or stylesheet in {path}");
            Assert.True(bootstrap.Index < stylesheet.Index, $"Theme bootstrap appears after stylesheet in {path}");
            Assert.Contains("localStorage.getItem", bootstrap.Value, StringComparison.Ordinal);
            Assert.Contains("prefers-color-scheme: dark", bootstrap.Value, StringComparison.Ordinal);
            Assert.Contains("document.documentElement.dataset.theme", bootstrap.Value, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Alternate_language_links_are_reciprocal()
    {
        foreach (var (path, target, language) in new[]
                 { (ChinesePage, EnglishPage, "en"), (EnglishPage, ChinesePage, "zh-Hant") })
        {
            var head = Regex.Match(File.ReadAllText(path), @"<head>(?<body>[\s\S]*?)</head>").Groups["body"].Value;
            var alternate = Regex.Matches(head, @"<link\b[^>]*rel=""alternate""[^>]*>");
            Assert.Single(alternate.Cast<Match>());
            Assert.Equal(language, Attribute(alternate[0].Value, "hreflang"));
            Assert.Equal(Path.GetFullPath(target.Replace('/', Path.DirectorySeparatorChar)),
                Resolve(path, Attribute(alternate[0].Value, "href")!));
        }
    }

    [Fact]
    public void All_relative_page_links_point_to_existing_files()
    {
        foreach (var path in new[] { ChinesePage, EnglishPage })
        {
            var page = File.ReadAllText(path);
            foreach (Match tag in Regex.Matches(page, @"<(?:a|link|img|script)\b[^>]*>"))
            {
                foreach (var name in new[] { "href", "src", "data-src-dark" })
                {
                    var value = Attribute(tag.Value, name);
                    if (value is null || value.StartsWith('#') || value.Contains("://", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    Assert.True(File.Exists(Resolve(path, value)), $"Missing relative {name} target '{value}' in {path}");
                }
            }
        }
    }

    [Fact]
    public void Data_model_figures_scroll_without_expanding_the_page()
    {
        foreach (var path in new[] { ChinesePage, EnglishPage })
        {
            var page = File.ReadAllText(path);
            var figures = Regex.Matches(page, @"<div class=""figure-scroll""><a\b[^>]*><img\b[^>]*\bsrc=""[^""]*diagrams/[^""]+""[^>]*></a></div>");
            Assert.Equal(4, figures.Count);
            foreach (Match figure in figures)
            {
                Assert.Contains("target=\"_blank\"", figure.Value, StringComparison.Ordinal);
            }
        }

        var css = File.ReadAllText(StyleSheet);
        Assert.Matches(@"\.figure-scroll\s*\{[^}]*overflow-x:\s*auto", css);
        Assert.Matches(@"\.figure-scroll img\s*\{[^}]*max-width:\s*none", css);
    }

    [Fact]
    public void Theme_text_contrast_is_at_least_four_point_five()
    {
        var css = File.ReadAllText(StyleSheet);
        var root = Regex.Match(css, @":root\s*\{(?<variables>[^}]+)\}");
        var dark = Regex.Match(css, @":root\[data-theme='dark'\]\s*\{(?<variables>[^}]+)\}");
        Assert.True(root.Success && dark.Success, "Missing light or dark color variables");
        var lightValues = Variables(root.Groups["variables"].Value);
        var darkValues = new Dictionary<string, string>(lightValues, StringComparer.Ordinal);
        foreach (var (name, value) in Variables(dark.Groups["variables"].Value))
        {
            darkValues[name] = value;
        }

        var pairs = new[]
        {
            ("--text", "--bg"), ("--muted", "--bg"), ("--primary", "--bg"),
            ("--text", "--surface"), ("--button-text", "--button-bg")
        };
        foreach (var (theme, values) in new[] { ("light", lightValues), ("dark", darkValues) })
        {
            foreach (var (foreground, background) in pairs)
            {
                var ratio = Contrast(values[foreground], values[background]);
                Assert.True(ratio >= 4.5, $"{theme} contrast {foreground}/{background} = {ratio:F2}:1, below 4.5:1");
            }
        }
    }

    private static string[] SectionIds(string page) => Regex.Matches(page, @"<section\b[^>]*\bid=""(?<id>[^""]+)""")
        .Select(match => match.Groups["id"].Value).ToArray();

    private static HashSet<string> Numbers(string page) => Regex.Matches(page, @"\d{2,}")
        .Select(match => match.Value).ToHashSet(StringComparer.Ordinal);

    private static string? Attribute(string tag, string name)
    {
        var match = Regex.Match(tag, $@"(?:\s|<){Regex.Escape(name)}=""(?<value>[^""]*)""");
        return match.Success ? match.Groups["value"].Value : null;
    }

    private static string Resolve(string page, string relativePath) => Path.GetFullPath(
        Path.Combine(Path.GetDirectoryName(page)!, relativePath.Split('#')[0].Replace('/', Path.DirectorySeparatorChar)));

    private static Dictionary<string, string> Variables(string block) => Regex.Matches(block, @"(?<name>--[a-z-]+):\s*(?<value>#[0-9a-fA-F]{6})\s*;")
        .ToDictionary(match => match.Groups["name"].Value, match => match.Groups["value"].Value, StringComparer.Ordinal);

    private static double Contrast(string foreground, string background)
    {
        static double Luminance(string hex)
        {
            var channels = Enumerable.Range(0, 3).Select(index =>
            {
                var component = int.Parse(hex.AsSpan(1 + index * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
                return component <= 0.04045 ? component / 12.92 : Math.Pow((component + 0.055) / 1.055, 2.4);
            }).ToArray();
            return channels[0] * 0.2126 + channels[1] * 0.7152 + channels[2] * 0.0722;
        }

        var first = Luminance(foreground);
        var second = Luminance(background);
        return (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
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

        throw new FileNotFoundException($"Repository file missing: {relativePath}", relativePath);
    }
}
