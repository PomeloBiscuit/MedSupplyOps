using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Dapper;
using MedSupplyOps.Infrastructure.Localization;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc.Testing;
using Oracle.ManagedDataAccess.Client;
using Xunit.Abstractions;

namespace MedSupplyOps.Integration.Tests.Web;

public sealed partial class BilingualMasterDataWebTests
    : IClassFixture<RequisitionFlowTests.RequisitionWebApplicationFactory>
{
    private static readonly string EnglishCultureCookie =
        $"{CookieRequestCultureProvider.DefaultCookieName}={Uri.EscapeDataString(CookieRequestCultureProvider.MakeCookieValue(new RequestCulture("en")))}";

    private readonly RequisitionFlowTests.RequisitionWebApplicationFactory _factory;
    private readonly ITestOutputHelper _output;

    public BilingualMasterDataWebTests(
        RequisitionFlowTests.RequisitionWebApplicationFactory factory,
        ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [Fact]
    public async Task T1_English_item_display_silently_falls_back_then_uses_the_override()
    {
        var code = NewCode("T1");
        var itemId = await InsertItemAsync(code, "T1 僅原文品項", englishName: null);
        try
        {
            using var client = CreateEnglishClient();
            await WebAuthTestHelpers.LoginAsync(client, TestIdentitySeeder.AdministratorEmail);

            var fallbackHtml = await GetDecodedAsync(client, $"/Items?search={Uri.EscapeDataString(code)}");
            Assert.Contains("T1 僅原文品項", fallbackHtml, StringComparison.Ordinal);
            Assert.DoesNotContain(">null<", fallbackHtml, StringComparison.OrdinalIgnoreCase);
            _output.WriteLine("T1 fallback /Items: T1 僅原文品項");

            await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
            await connection.ExecuteAsync(
                "UPDATE items SET item_name_en = 'T1 Original-only Item' WHERE item_id = :itemId",
                new { itemId });
            await connection.ExecuteAsync("COMMIT");

            var englishHtml = await GetDecodedAsync(client, $"/Items?search={Uri.EscapeDataString(code)}");
            Assert.Contains("T1 Original-only Item", englishHtml, StringComparison.Ordinal);
            Assert.DoesNotContain("T1 僅原文品項", englishHtml, StringComparison.Ordinal);
            _output.WriteLine("T1 override /Items: T1 Original-only Item");
        }
        finally
        {
            await DeleteItemAsync(itemId);
        }
    }

    [Fact]
    public void T2_views_cannot_select_language_from_persistence_English_properties()
    {
        var root = FindRepositoryRoot();
        var viewRoot = Path.Combine(root, "src", "MedSupplyOps.Web", "Views");
        var forbidden = new Regex(@"\b(?:NameEn|SpecificationEn|UnitOfMeasureEn|DisplayNameEn)\b", RegexOptions.Compiled);
        var violations = Directory.EnumerateFiles(viewRoot, "*.cshtml", SearchOption.AllDirectories)
            .Select(path => new { Path = path, Source = File.ReadAllText(path) })
            .Where(file => forbidden.IsMatch(file.Source))
            .Select(file => Path.GetRelativePath(root, file.Path).Replace('\\', '/'))
            .ToList();

        Assert.Empty(violations);
        Assert.Equal("原文", BilingualText.Resolve("原文", null, CultureInfo.GetCultureInfo("en")));
        Assert.Equal("English", BilingualText.Resolve("原文", "English", CultureInfo.GetCultureInfo("en")));
        Assert.Equal("Emergency（急診）", BilingualText.Option("急診", "Emergency", CultureInfo.GetCultureInfo("en")));
        _output.WriteLine("T2 Razor scan: 0 direct _EN persistence-property reads; all selection uses BilingualText.Resolve/Option.");
    }

    [Fact]
    public async Task T3_item_and_user_search_match_English_columns()
    {
        var code = NewCode("T3");
        const string englishName = "T3 Searchable Sterile Dressing";
        var itemId = await InsertItemAsync(code, "T3 中文敷料", englishName);
        try
        {
            using var client = CreateEnglishClient();
            await WebAuthTestHelpers.LoginAsync(client, TestIdentitySeeder.AdministratorEmail);

            var itemHtml = await GetDecodedAsync(client, $"/Items?search={Uri.EscapeDataString(englishName)}");
            Assert.Contains(code, itemHtml, StringComparison.Ordinal);
            Assert.Contains(englishName, itemHtml, StringComparison.Ordinal);

            var userHtml = await GetDecodedAsync(client, "/Users?search=Xiaoming");
            Assert.Contains("Xiaoming Wang", userHtml, StringComparison.Ordinal);
            Assert.Contains("requester@example.local", userHtml, StringComparison.Ordinal);

            _output.WriteLine($"T3 /Items?search={englishName}: {code}|{englishName}");
            _output.WriteLine("T3 /Users?search=Xiaoming: Xiaoming Wang|requester@example.local");
        }
        finally
        {
            await DeleteItemAsync(itemId);
        }
    }

    [Fact]
    public async Task T4_English_department_dropdown_is_bilingual_while_inventory_table_is_single_language()
    {
        using var client = CreateEnglishClient();
        await WebAuthTestHelpers.LoginAsync(client, TestIdentitySeeder.AdministratorEmail);
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        var requesterId = await connection.QuerySingleAsync<string>(
            "SELECT id FROM identity_users WHERE normalized_email = 'REQUESTER@EXAMPLE.LOCAL'");

        var userEditHtml = await GetDecodedAsync(client, $"/Users/Edit/{requesterId}");
        Assert.Contains("DEP-ER — Emergency Department（急診）", userEditHtml, StringComparison.Ordinal);

        // 品項與儲藏位置自己建：種子資料的英文名稱可以在網站上改，不能拿來當固定的期望值。
        var code = NewCode("T4");
        const string englishName = "T4 Single-language Gloves";
        var suffix = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var locationCode = $"ITBL-T4-{suffix}";
        var locationName = $"測試庫房-T4-{suffix}";
        var locationEnglishName = $"T4 Test Warehouse {suffix}";
        var itemId = await InsertItemAsync(code, "T4 單語手套", englishName);
        await connection.ExecuteAsync("""
            INSERT INTO storage_locations (location_code, name, name_en, created_by)
            VALUES (:locationCode, :locationName, :locationEnglishName, 'itest-bilingual')
            """, new { locationCode, locationName, locationEnglishName });
        await connection.ExecuteAsync("""
            INSERT INTO stock_lots (item_id, lot_number, expiry_date, quantity, storage_location, created_by)
            VALUES (:itemId, :lotNumber, :expiryDate, 3, :locationName, 'itest-bilingual')
            """, new
        {
            itemId,
            lotNumber = locationCode,
            expiryDate = TestBusinessCalendar.Today.AddDays(60).ToDateTime(TimeOnly.MinValue),
            locationName,
        });

        try
        {
            var inventoryHtml = await GetDecodedAsync(client, $"/Inventory?search={Uri.EscapeDataString(code)}");
            Assert.Contains($">{englishName}</td>", inventoryHtml, StringComparison.Ordinal);
            Assert.DoesNotContain($">{englishName}（T4 單語手套）</td>", inventoryHtml, StringComparison.Ordinal);
            Assert.Contains(locationEnglishName, inventoryHtml, StringComparison.Ordinal);
            Assert.DoesNotContain(locationName, inventoryHtml, StringComparison.Ordinal);
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM stock_lots WHERE item_id = :itemId", new { itemId });
            await connection.ExecuteAsync(
                "DELETE FROM storage_locations WHERE location_code = :locationCode",
                new { locationCode });
            await connection.ExecuteAsync("COMMIT");
            await DeleteItemAsync(itemId);
        }

        _output.WriteLine("T4 Users/Edit department option: DEP-ER — Emergency Department（急診）");
        _output.WriteLine($"T4 Inventory item cell: {englishName}; storage location: {locationEnglishName}");
    }

    [Fact]
    public async Task T5_changing_only_English_item_name_is_recorded_in_audit_JSON()
    {
        var code = NewCode("T5");
        var itemId = await InsertItemAsync(code, "T5 稽核品項", "T5 Audit Item Before");
        try
        {
            using var client = CreateEnglishClient();
            await WebAuthTestHelpers.LoginAsync(client, TestIdentitySeeder.AdministratorEmail);
            var token = await GetTokenAsync(client, $"/Items/Edit/{itemId}");
            var response = await client.PostAsync($"/Items/Edit/{itemId}", new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["__RequestVerificationToken"] = token,
                    ["Name"] = "T5 稽核品項",
                    ["EnglishName"] = "T5 Audit Item After",
                    ["Specification"] = "原規格",
                    ["EnglishSpecification"] = "Original specification",
                    ["EnglishUnitOfMeasure"] = "Box",
                    ["SafetyStockQty"] = "0",
                }));
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

            await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
            var audit = await connection.QuerySingleAsync<AuditRow>("""
                SELECT old_value AS OldValue, new_value AS NewValue
                FROM audit_logs
                WHERE entity_type = 'Item'
                  AND entity_id = TO_CHAR(:itemId)
                  AND action = 'Update'
                ORDER BY audit_log_id DESC
                FETCH FIRST 1 ROW ONLY
                """, new { itemId });
            Assert.Contains("\"nameEn\":\"T5 Audit Item Before\"", audit.OldValue, StringComparison.Ordinal);
            Assert.Contains("\"nameEn\":\"T5 Audit Item After\"", audit.NewValue, StringComparison.Ordinal);
            _output.WriteLine($"T5 audit old={audit.OldValue}");
            _output.WriteLine($"T5 audit new={audit.NewValue}");
        }
        finally
        {
            await DeleteItemAsync(itemId);
        }
    }

    private HttpClient CreateEnglishClient()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", EnglishCultureCookie);
        return client;
    }

    private static async Task<long> InsertItemAsync(string code, string name, string? englishName)
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.ExecuteAsync("""
            INSERT INTO items
                (item_code, item_name, item_name_en, specification, specification_en,
                 unit_of_measure, unit_of_measure_en, safety_stock_qty, created_by)
            VALUES
                (:code, :name, :englishName, '原規格', 'Original specification', '盒', 'Box', 0, 'itest-bilingual')
            """, new { code, name, englishName });
        await connection.ExecuteAsync("COMMIT");
        return await connection.QuerySingleAsync<long>(
            "SELECT item_id FROM items WHERE item_code = :code AND is_deleted = 0",
            new { code });
    }

    private static async Task DeleteItemAsync(long itemId)
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.ExecuteAsync(
            "DELETE FROM audit_logs WHERE entity_type = 'Item' AND entity_id = TO_CHAR(:itemId)",
            new { itemId });
        await connection.ExecuteAsync("DELETE FROM items WHERE item_id = :itemId", new { itemId });
        await connection.ExecuteAsync("COMMIT");
    }

    private static async Task<string> GetDecodedAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    private static async Task<string> GetTokenAsync(HttpClient client, string path)
    {
        var html = await GetDecodedAsync(client, path);
        var match = AntiforgeryTokenRegex().Match(html);
        Assert.True(match.Success, $"{path} 必須包含 AntiForgery request token。");
        return match.Groups[1].Value;
    }

    private static string NewCode(string marker)
        => $"IT-Z-{marker}-{Guid.NewGuid():N}"[..26].ToUpperInvariant();

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "MedSupplyOps.slnx")))
            {
                return current.FullName;
            }
        }

        throw new InvalidOperationException("找不到 MedSupplyOps.slnx。");
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryTokenRegex();

    private sealed class AuditRow
    {
        public string OldValue { get; init; } = string.Empty;
        public string NewValue { get; init; } = string.Empty;
    }
}
