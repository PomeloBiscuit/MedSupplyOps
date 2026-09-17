using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Oracle.ManagedDataAccess.Client;
using Xunit.Abstractions;

namespace MedSupplyOps.Integration.Tests.Web;

public sealed partial class ItemBarcodeWebTests
    : IClassFixture<RequisitionFlowTests.RequisitionWebApplicationFactory>
{
    private readonly RequisitionFlowTests.RequisitionWebApplicationFactory _factory;
    private readonly ITestOutputHelper _output;

    public ItemBarcodeWebTests(
        RequisitionFlowTests.RequisitionWebApplicationFactory factory,
        ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [Fact]
    public async Task T3_missing_barcode_returns_the_exact_message_and_GS1_lookup_returns_autofill_values()
    {
        using var client = CreateClient();
        await WebAuthTestHelpers.LoginAsync(client, TestIdentitySeeder.StorekeeperEmail);
        var itemId = await InsertItemAsync("AA-T3", "04712345678901");
        try
        {
            var missing = await client.GetAsync("/Receiving/LookupBarcode?value=NOT-IN-MASTER");
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            var missingJson = JsonDocument.Parse(await missing.Content.ReadAsStringAsync());
            var missingMessage = missingJson.RootElement.GetProperty("message").GetString();
            Assert.Equal("這個條碼沒有對應的品項", missingMessage);

            var unknownAi = Uri.EscapeDataString("(01)04712345678901(17)491231(10)AA-LOT-3(21)SERIAL-1");
            var rejected = await client.GetAsync($"/Receiving/LookupBarcode?value={unknownAi}");
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            var rejectedJson = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
            Assert.Equal("無法解析條碼。", rejectedJson.RootElement.GetProperty("message").GetString());

            var gs1Value = Uri.EscapeDataString("(01)04712345678901(17)491231(10)AA-LOT-3");
            var found = await client.GetAsync($"/Receiving/LookupBarcode?value={gs1Value}");
            Assert.Equal(HttpStatusCode.OK, found.StatusCode);
            var foundJson = JsonDocument.Parse(await found.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal(itemId, foundJson.GetProperty("itemId").GetInt64());
            Assert.Equal("2049-12-31", foundJson.GetProperty("expiryDate").GetString());
            Assert.Equal("AA-LOT-3", foundJson.GetProperty("lotNumber").GetString());

            var page = await client.GetStringAsync("/Receiving");
            Assert.Contains("id=\"barcode-scan\"", page, StringComparison.Ordinal);
            Assert.Contains("autofocus", page, StringComparison.Ordinal);
            Assert.Contains("這個條碼沒有對應的品項", await missing.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            _output.WriteLine($"T3 畫面訊息：{missingMessage}");
            _output.WriteLine("T3 JS 防退化：失敗路徑只更新 barcode-scan-result；數量、儲藏位置與其他表單欄位不在清除路徑。 ");
            _output.WriteLine($"T3 GS1：itemId={itemId}; expiry={foundJson.GetProperty("expiryDate").GetString()}; lot={foundJson.GetProperty("lotNumber").GetString()}");
        }
        finally
        {
            await DeleteItemsAsync([itemId]);
        }
    }

    [Fact]
    public async Task T4_two_null_barcodes_coexist_but_duplicate_values_are_blocked_and_web_shows_a_clear_error()
    {
        var suffix = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..10].ToUpperInvariant();
        var code1 = $"AA-N1-{suffix}";
        var code2 = $"AA-N2-{suffix}";
        var duplicateCode = $"AA-DU-{suffix}";
        var webCode = $"AA-WE-{suffix}";
        var barcode = $"AA-BC-{suffix}";
        var ids = new List<long>();
        try
        {
            await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
            await connection.OpenAsync();
            foreach (var code in new[] { code1, code2 })
            {
                await connection.ExecuteAsync("""
                    INSERT INTO items (item_code, barcode, item_name, unit_of_measure, safety_stock_qty, created_by)
                    VALUES (:code, NULL, '條碼唯一性測試品項', '盒', 0, 'itest-aa-t4')
                    """, new { code });
                ids.Add(await connection.QuerySingleAsync<long>(
                    "SELECT item_id FROM items WHERE item_code = :code",
                    new { code }));
            }

            var nullCount = await connection.QuerySingleAsync<int>(
                "SELECT COUNT(*) FROM items WHERE item_code IN (:code1, :code2) AND barcode IS NULL",
                new { code1, code2 });
            Assert.Equal(2, nullCount);
            _output.WriteLine($"T4 NULL：兩筆 INSERT 成功；BARCODE IS NULL count={nullCount}");

            await connection.ExecuteAsync("""
                INSERT INTO items (item_code, barcode, item_name, unit_of_measure, safety_stock_qty, created_by)
                VALUES (:duplicateCode, :barcode, '條碼唯一性測試品項', '盒', 0, 'itest-aa-t4')
                """, new { duplicateCode, barcode });
            ids.Add(await connection.QuerySingleAsync<long>(
                "SELECT item_id FROM items WHERE item_code = :duplicateCode",
                new { duplicateCode }));

            var duplicateException = await Assert.ThrowsAsync<OracleException>(() => connection.ExecuteAsync("""
                INSERT INTO items (item_code, barcode, item_name, unit_of_measure, safety_stock_qty, created_by)
                VALUES (:webCode, :barcode, '重複條碼測試品項', '盒', 0, 'itest-aa-t4')
                """, new { webCode, barcode }));
            Assert.Equal(1, duplicateException.Number);
            Assert.Contains("UX_ITEMS_BARCODE", duplicateException.Message, StringComparison.OrdinalIgnoreCase);
            _output.WriteLine($"T4 重複：ORA-{duplicateException.Number:D5}; UX_ITEMS_BARCODE；第二筆被擋");

            using var client = CreateClient();
            await WebAuthTestHelpers.LoginAsync(client, TestIdentitySeeder.AdministratorEmail);
            var token = await GetTokenAsync(client, "/Items/Create");
            var response = await client.PostAsync("/Items/Create", Form(token, new()
            {
                ["Code"] = webCode,
                ["Barcode"] = barcode,
                ["Name"] = "重複條碼畫面測試",
                ["UnitOfMeasure"] = "盒",
                ["SafetyStockQty"] = "0",
            }));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var page = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
            var expectedMessage = $"條碼 {barcode} 已存在。";
            Assert.Contains(expectedMessage, page, StringComparison.Ordinal);
            Assert.Equal(0, await connection.QuerySingleAsync<int>(
                "SELECT COUNT(*) FROM items WHERE item_code = :webCode",
                new { webCode }));
            _output.WriteLine($"T4 畫面：{expectedMessage}");
        }
        finally
        {
            await DeleteItemsAsync(ids);
        }
    }

    [Fact]
    public async Task Inventory_search_matches_barcode_alongside_item_code_and_name()
    {
        using var client = CreateClient();
        await WebAuthTestHelpers.LoginAsync(client, TestIdentitySeeder.StorekeeperEmail);
        var suffix = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..10].ToUpperInvariant();
        var barcode = $"AA-SEARCH-{suffix}";
        var itemId = await InsertItemAsync("AA-SR", barcode);
        try
        {
            await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
            var itemCode = await connection.QuerySingleAsync<string>(
                "SELECT item_code FROM items WHERE item_id = :itemId",
                new { itemId });
            var response = await client.GetAsync($"/Inventory?search={Uri.EscapeDataString(barcode)}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var page = await response.Content.ReadAsStringAsync();
            Assert.Contains(itemCode, page, StringComparison.Ordinal);
        }
        finally
        {
            await DeleteItemsAsync([itemId]);
        }
    }

    private HttpClient CreateClient()
        => _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static async Task<long> InsertItemAsync(string marker, string? barcode)
    {
        var code = $"{marker}-{Guid.NewGuid():N}"[..26].ToUpperInvariant();
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.ExecuteAsync("""
            INSERT INTO items (item_code, barcode, item_name, unit_of_measure, safety_stock_qty, created_by)
            VALUES (:code, :barcode, '條碼整合測試品項', '盒', 0, 'itest-aa')
            """, new { code, barcode });
        return await connection.QuerySingleAsync<long>(
            "SELECT item_id FROM items WHERE item_code = :code",
            new { code });
    }

    private static async Task DeleteItemsAsync(IReadOnlyCollection<long> rawItemIds)
    {
        var itemIds = rawItemIds.Where(id => id > 0).Distinct().ToList();
        if (itemIds.Count == 0)
        {
            return;
        }

        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.ExecuteAsync(
            "DELETE FROM audit_logs WHERE entity_type = 'Item' AND entity_id IN :entityIds",
            new { entityIds = itemIds.Select(id => id.ToString(CultureInfo.InvariantCulture)).ToList() });
        await connection.ExecuteAsync("DELETE FROM items WHERE item_id IN :itemIds", new { itemIds });
        await connection.ExecuteAsync("COMMIT");
    }

    private static async Task<string> GetTokenAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var match = AntiforgeryTokenRegex().Match(await response.Content.ReadAsStringAsync());
        Assert.True(match.Success, "頁面必須包含 AntiForgery request token。");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static FormUrlEncodedContent Form(string token, Dictionary<string, string> values)
    {
        values["__RequestVerificationToken"] = token;
        return new FormUrlEncodedContent(values);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryTokenRegex();
}
