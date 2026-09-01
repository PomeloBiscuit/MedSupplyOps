using System.Globalization;
using System.Net;
using System.Text.Json;
using Dapper;
using MedSupplyOps.Infrastructure.Persistence;
using MedSupplyOps.Infrastructure.Queries;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Oracle.ManagedDataAccess.Client;

namespace MedSupplyOps.Integration.Tests.Web;

/// <summary>以實際 MVC host 驗證 API 路由、JSON 欄位名稱與數值。</summary>
public sealed class InventoryApiTests : IClassFixture<InventoryApiTests.InventoryWebApplicationFactory>
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);
    private readonly HttpClient _client;

    public InventoryApiTests(InventoryWebApplicationFactory factory)
    {
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
    }

    [Fact]
    public async Task GetAvailability_returns_camel_case_fields_and_MD_0001_values()
    {
        var itemId = await GetItemIdAsync("MD-0001");
        var response = await _client.GetAsync($"/api/items/{itemId}/availability?asOf={Today:yyyy-MM-dd}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        Assert.True(root.TryGetProperty("itemId", out var itemIdProperty));
        Assert.False(root.TryGetProperty("ItemId", out _));
        Assert.Equal(itemId, itemIdProperty.GetInt64());
        Assert.Equal(210, root.GetProperty("availableQuantity").GetInt32());
        Assert.Equal(await GetLotExpiryDateAsync("GLO-FEFO-A"), root.GetProperty("earliestUsableExpiry").GetString());
        Assert.Equal("GLO-EXPIRED-01", root.GetProperty("lots")[0].GetProperty("lotNumber").GetString());
        Assert.False(root.GetProperty("lots")[0].GetProperty("isAvailable").GetBoolean());
    }

    [Fact]
    public async Task GetAvailability_keeps_zero_quantity_lot_but_excludes_it_from_MD_0003_availability()
    {
        var itemId = await GetItemIdAsync("MD-0003");
        var response = await _client.GetAsync($"/api/items/{itemId}/availability?asOf={Today:yyyy-MM-dd}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        Assert.Equal(20, root.GetProperty("availableQuantity").GetInt32());
        var zeroLot = root.GetProperty("lots").EnumerateArray()
            .Single(lot => lot.GetProperty("lotNumber").GetString() == "IVSET-ZERO-01");
        Assert.Equal(0, zeroLot.GetProperty("quantity").GetInt32());
        Assert.False(zeroLot.GetProperty("isAvailable").GetBoolean());
    }

    [Fact]
    public async Task GetExpiring_returns_camel_case_fields_and_usable_seed_lot_values()
    {
        var response = await _client.GetAsync($"/api/inventory/expiring?withinDays=30&asOf={Today:yyyy-MM-dd}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        var gloveLot = root.EnumerateArray().Single(lot => lot.GetProperty("lotNumber").GetString() == "GLO-FEFO-A");

        Assert.True(gloveLot.TryGetProperty("stockLotId", out var stockLotId));
        Assert.False(gloveLot.TryGetProperty("StockLotId", out _));
        Assert.Equal(JsonValueKind.Number, stockLotId.ValueKind);
        Assert.Equal("MD-0001", gloveLot.GetProperty("itemCode").GetString());
        Assert.Equal(40, gloveLot.GetProperty("quantity").GetInt32());
        Assert.Equal(await GetLotExpiryDateAsync("GLO-FEFO-A"), gloveLot.GetProperty("expiryDate").GetString());
    }

    [Fact]
    public async Task Inventory_page_html_encodes_item_names_and_removes_the_probe_item()
    {
        var itemCode = "XSS-" + Guid.NewGuid().ToString("N")[..12];
        const string probeName = "<script>alert(1)</script>";

        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.ExecuteAsync(
            """
            INSERT INTO items (
                item_code, item_name, specification, unit_of_measure,
                tracks_lot, tracks_expiry, safety_stock_qty, created_by
            ) VALUES (
                :itemCode, :itemName, '輸出編碼探針', '件',
                1, 1, 0, 'test'
            )
            """,
            new { itemCode, itemName = probeName });

        try
        {
            var response = await _client.GetAsync($"/Inventory?asOf={Today:yyyy-MM-dd}");
            var html = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html, StringComparison.Ordinal);
            Assert.DoesNotContain(probeName, html, StringComparison.Ordinal);
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM items WHERE item_code = :itemCode", new { itemCode });
        }
    }

    private static async Task<long> GetItemIdAsync(string itemCode)
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        return await connection.QuerySingleAsync<long>(
            "SELECT item_id FROM items WHERE item_code = :itemCode",
            new { itemCode });
    }

    private static async Task<string> GetLotExpiryDateAsync(string lotNumber)
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        var expiryDate = await connection.QuerySingleAsync<DateTime>(
            "SELECT expiry_date FROM stock_lots WHERE lot_number = :lotNumber",
            new { lotNumber });
        return DateOnly.FromDateTime(expiryDate).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    public sealed class InventoryWebApplicationFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<MedSupplyOpsDbContext>>();
                services.RemoveAll<MedSupplyOpsDbContext>();
                services.RemoveAll<InventoryQueries>();
                services.AddDbContext<MedSupplyOpsDbContext>(options => options.UseOracle(OracleTestDatabase.ConnectionString));
                services.AddScoped<InventoryQueries>(serviceProvider =>
                    new InventoryQueries(serviceProvider.GetRequiredService<MedSupplyOpsDbContext>().Database.GetDbConnection()));
            });
        }
    }
}
