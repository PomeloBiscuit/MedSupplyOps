using System.Globalization;
using System.Net;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Oracle.ManagedDataAccess.Client;
using Xunit.Abstractions;

namespace MedSupplyOps.Integration.Tests.Web;

public sealed class Code128BarcodeWebTests
    : IClassFixture<RequisitionFlowTests.RequisitionWebApplicationFactory>
{
    private readonly RequisitionFlowTests.RequisitionWebApplicationFactory _factory;
    private readonly ITestOutputHelper _output;

    public Code128BarcodeWebTests(
        RequisitionFlowTests.RequisitionWebApplicationFactory factory,
        ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [Fact]
    public async Task Storekeeper_sees_item_management_but_not_the_deactivation_button()
    {
        var (itemId, code) = await InsertItemAsync("AC-AUTH", "AC-1234");
        try
        {
            using var keeper = CreateClient();
            await WebAuthTestHelpers.LoginAsync(keeper, TestIdentitySeeder.StorekeeperEmail);
            var keeperResponse = await keeper.GetAsync($"/Items?search={code}");
            var keeperHtml = WebUtility.HtmlDecode(await keeperResponse.Content.ReadAsStringAsync());

            Assert.Equal(HttpStatusCode.OK, keeperResponse.StatusCode);
            Assert.Contains("編輯品項", keeperHtml, StringComparison.Ordinal);
            Assert.Contains("顯示條碼", keeperHtml, StringComparison.Ordinal);
            Assert.DoesNotContain("停用品項", keeperHtml, StringComparison.Ordinal);

            using var administrator = CreateClient();
            await WebAuthTestHelpers.LoginAsync(administrator, TestIdentitySeeder.AdministratorEmail);
            var administratorHtml = WebUtility.HtmlDecode(
                await administrator.GetStringAsync($"/Items?search={code}"));
            Assert.Contains("停用品項", administratorHtml, StringComparison.Ordinal);

            _output.WriteLine($"庫管員 HTML：{code}|編輯品項|顯示條碼；停用品項=不存在");
            _output.WriteLine($"管理員 HTML：{code}|停用品項=存在");
        }
        finally
        {
            await DeleteItemsAsync([itemId]);
        }
    }

    [Fact]
    public async Task Valid_saved_barcode_renders_svg_on_list_and_edit_while_empty_barcode_has_no_button()
    {
        var (barcodeItemId, barcodeCode) = await InsertItemAsync("AC-SVG", "ITEM-1234");
        var (emptyItemId, emptyCode) = await InsertItemAsync("AC-EMPTY", null);
        try
        {
            using var client = CreateClient();
            await WebAuthTestHelpers.LoginAsync(client, TestIdentitySeeder.StorekeeperEmail);

            var listResponse = await client.GetAsync($"/Items?search={barcodeCode}");
            var listHtml = WebUtility.HtmlDecode(await listResponse.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
            Assert.Contains("顯示條碼", listHtml, StringComparison.Ordinal);
            Assert.Contains("data-testid=\"code128-barcode\"", listHtml, StringComparison.Ordinal);
            Assert.Contains("<rect", listHtml, StringComparison.Ordinal);
            Assert.Contains("data-mso-print-barcode", listHtml, StringComparison.Ordinal);

            var editResponse = await client.GetAsync($"/Items/Edit/{barcodeItemId}");
            var editHtml = WebUtility.HtmlDecode(await editResponse.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.OK, editResponse.StatusCode);
            Assert.Contains("data-testid=\"persisted-barcode\"", editHtml, StringComparison.Ordinal);
            Assert.Contains("ITEM-1234", editHtml, StringComparison.Ordinal);
            Assert.Contains("<rect", editHtml, StringComparison.Ordinal);

            var emptyHtml = WebUtility.HtmlDecode(await client.GetStringAsync($"/Items?search={emptyCode}"));
            Assert.Contains(emptyCode, emptyHtml, StringComparison.Ordinal);
            Assert.DoesNotContain("顯示條碼", emptyHtml, StringComparison.Ordinal);
            _output.WriteLine("有效條碼：清單彈窗與編輯頁皆有 SVG/rect；空條碼：無顯示條碼按鈕。");
        }
        finally
        {
            await DeleteItemsAsync([barcodeItemId, emptyItemId]);
        }
    }

    [Fact]
    public async Task T6_non_ASCII_barcode_returns_200_and_shows_the_localized_explanation_on_list_and_edit()
    {
        var (itemId, code) = await InsertItemAsync("AC-UTF8", "中文條碼");
        try
        {
            using var client = CreateClient();
            await WebAuthTestHelpers.LoginAsync(client, TestIdentitySeeder.StorekeeperEmail);

            foreach (var path in new[] { $"/Items?search={code}", $"/Items/Edit/{itemId}" })
            {
                var response = await client.GetAsync(path);
                var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Contains("此條碼無法以 Code 128 呈現", html, StringComparison.Ordinal);
                Assert.DoesNotContain("data-testid=\"code128-barcode\"", html, StringComparison.Ordinal);
                _output.WriteLine($"{path} | HTTP {(int)response.StatusCode} | 此條碼無法以 Code 128 呈現");
            }
        }
        finally
        {
            await DeleteItemsAsync([itemId]);
        }
    }

    [Fact]
    public void T7_barcode_Razor_files_do_not_use_Html_Raw()
    {
        foreach (var relativePath in new[]
        {
            "src/MedSupplyOps.Web/Views/Items/Index.cshtml",
            "src/MedSupplyOps.Web/Views/Items/Edit.cshtml",
            "src/MedSupplyOps.Web/Views/Shared/_Code128Barcode.cshtml",
        })
        {
            var source = File.ReadAllText(FindRepositoryFile(relativePath));
            Assert.DoesNotContain("Html.Raw", source, StringComparison.Ordinal);
            _output.WriteLine($"{relativePath} | Html.Raw=0");
        }
    }

    private HttpClient CreateClient()
        => _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static async Task<(long Id, string Code)> InsertItemAsync(string marker, string? barcode)
    {
        var code = $"{marker}-{Guid.NewGuid():N}"[..28].ToUpperInvariant();
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.ExecuteAsync("""
            INSERT INTO items (item_code, barcode, item_name, unit_of_measure, safety_stock_qty, created_by)
            VALUES (:code, :barcode, '條碼測試品項', '盒', 0, 'itest-ac')
            """, new { code, barcode });
        var id = await connection.QuerySingleAsync<long>(
            "SELECT item_id FROM items WHERE item_code = :code",
            new { code });
        return (id, code);
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

    private static string FindRepositoryFile(string relativePath)
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException($"找不到 {relativePath}。");
    }
}
