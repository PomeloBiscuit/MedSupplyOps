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
using Microsoft.Extensions.Hosting;
using Oracle.ManagedDataAccess.Client;

namespace MedSupplyOps.Integration.Tests.Web;

/// <summary>以實際 MVC host 驗證 API 路由、JSON 欄位名稱與數值。</summary>
public sealed class InventoryApiTests : IClassFixture<InventoryApiTests.InventoryWebApplicationFactory>, IAsyncLifetime
{
    // 查的是 cold-start 時以 TRUNC(SYSDATE) 建立的種子批次；基準日必須跟著真實業務日期，
    // 不可使用只供 Web host 邊界測試的固定假時鐘（見 L-033）。
    private static readonly DateOnly Today = TestBusinessCalendar.SystemToday;
    private readonly HttpClient _client;

    public InventoryApiTests(InventoryWebApplicationFactory factory)
    {
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
    }

    public Task InitializeAsync() => WebAuthTestHelpers.LoginAsync(_client, TestIdentitySeeder.RequesterEmail);

    public Task DisposeAsync() => Task.CompletedTask;

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
        Assert.Equal(await GetAvailableQuantityAsync(itemId, Today), root.GetProperty("availableQuantity").GetInt32());
        Assert.Equal(await GetEarliestUsableExpiryAsync(itemId, Today), root.GetProperty("earliestUsableExpiry").GetString());
        var expiredLot = root.GetProperty("lots").EnumerateArray()
            .Single(lot => lot.GetProperty("lotNumber").GetString() == "GLO-EXPIRED-01");
        Assert.False(expiredLot.GetProperty("isAvailable").GetBoolean());
    }

    [Fact]
    public async Task GetAvailability_keeps_zero_quantity_lot_but_excludes_it_from_availability()
    {
        var itemCode = "ITEST-ZERO-" + Guid.NewGuid().ToString("N")[..10];
        var lotNumber = "ITEST-ZERO-LOT-" + Guid.NewGuid().ToString("N")[..8];
        var asOf = TestBusinessCalendar.Today;

        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.ExecuteAsync(
            """
            INSERT INTO items (
                item_code, item_name, specification, unit_of_measure,
                tracks_lot, tracks_expiry, safety_stock_qty, created_by
            ) VALUES (
                :itemCode, '零數量批次測試品項', '測試規格', '件',
                1, 1, 0, 'itest'
            )
            """,
            new { itemCode });
        var itemId = await GetItemIdAsync(itemCode);
        await connection.ExecuteAsync(
            """
            INSERT INTO stock_lots (
                item_id, lot_number, expiry_date, quantity, storage_location, created_by
            ) VALUES (
                :itemId, :lotNumber, :expiryDate, 0, '中央庫房-A01', 'itest'
            )
            """,
            new { itemId, lotNumber, expiryDate = asOf.AddDays(30).ToDateTime(TimeOnly.MinValue) });

        try
        {
            var response = await _client.GetAsync($"/api/items/{itemId}/availability?asOf={asOf:yyyy-MM-dd}");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = json.RootElement;

            Assert.Equal(0, root.GetProperty("availableQuantity").GetInt32());
            var zeroLot = Assert.Single(root.GetProperty("lots").EnumerateArray());
            Assert.Equal(lotNumber, zeroLot.GetProperty("lotNumber").GetString());
            Assert.Equal(0, zeroLot.GetProperty("quantity").GetInt32());
            Assert.False(zeroLot.GetProperty("isAvailable").GetBoolean());
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM stock_lots WHERE item_id = :itemId", new { itemId });
            await connection.ExecuteAsync("DELETE FROM items WHERE item_id = :itemId", new { itemId });
        }
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
        Assert.Equal(await GetLotQuantityAsync("GLO-FEFO-A"), gloveLot.GetProperty("quantity").GetInt32());
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

    private static async Task<int> GetLotQuantityAsync(string lotNumber)
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        return await connection.QuerySingleAsync<int>(
            "SELECT quantity FROM stock_lots WHERE lot_number = :lotNumber",
            new { lotNumber });
    }

    private static async Task<int> GetAvailableQuantityAsync(long itemId, DateOnly asOf)
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        var quantity = await connection.QuerySingleAsync<decimal>(
            """
            SELECT NVL(SUM(CASE
                       WHEN l.expiry_date >= :asOf AND l.quantity > 0 THEN l.quantity
                       ELSE 0
                   END), 0)
            FROM items i
            LEFT JOIN stock_lots l ON l.item_id = i.item_id
            WHERE i.item_id = :itemId
              AND i.is_deleted = 0
            """,
            new { itemId, asOf = asOf.ToDateTime(TimeOnly.MinValue) });
        return decimal.ToInt32(quantity);
    }

    private static async Task<string?> GetEarliestUsableExpiryAsync(long itemId, DateOnly asOf)
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        var expiryDate = await connection.QuerySingleAsync<DateTime?>(
            """
            SELECT MIN(l.expiry_date)
            FROM items i
            LEFT JOIN stock_lots l ON l.item_id = i.item_id
            WHERE i.item_id = :itemId
              AND i.is_deleted = 0
              AND l.quantity > 0
              AND l.expiry_date >= :asOf
            """,
            new { itemId, asOf = asOf.ToDateTime(TimeOnly.MinValue) });
        return expiryDate is null
            ? null
            : DateOnly.FromDateTime(expiryDate.Value).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    public sealed class InventoryWebApplicationFactory : WebApplicationFactory<Program>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            var host = base.CreateHost(builder);
            TestIdentitySeeder.SeedAsync(host.Services).GetAwaiter().GetResult();
            return host;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                TestBusinessCalendar.ReplaceHostClock(services);
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
