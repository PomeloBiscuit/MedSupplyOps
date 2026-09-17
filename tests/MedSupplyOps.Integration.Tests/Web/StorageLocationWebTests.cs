using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc.Testing;
using Oracle.ManagedDataAccess.Client;
using Xunit.Abstractions;

namespace MedSupplyOps.Integration.Tests.Web;

public sealed partial class StorageLocationWebTests
    : IClassFixture<RequisitionFlowTests.RequisitionWebApplicationFactory>
{
    private static readonly string EnglishCultureCookie =
        $"{CookieRequestCultureProvider.DefaultCookieName}={Uri.EscapeDataString(CookieRequestCultureProvider.MakeCookieValue(new RequestCulture("en")))}";

    private readonly RequisitionFlowTests.RequisitionWebApplicationFactory _factory;
    private readonly ITestOutputHelper _output;

    public StorageLocationWebTests(
        RequisitionFlowTests.RequisitionWebApplicationFactory factory,
        ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [Fact]
    public async Task T1_receiving_rejects_unknown_and_disabled_storage_locations()
    {
        // ★ 批號每次都不同，清理也不假設「一定被拒」。
        //   覆核時把入庫的儲藏位置驗證拿掉，這條如預期變紅——但被誤建的批次沒人清，
        //   而原本的批號是固定值，之後每一次執行都因為「已經有這筆」而紅，資料庫清潔關卡也跟著紅。
        //   防護退化時，測試只該變紅一次，不該把資料庫一起弄髒。
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var unknownLot = "T1U-" + suffix;
        var disabledLot = "T1D-" + suffix;
        var disabled = await InsertLocationAsync("T1", isDeleted: true);
        try
        {
            using var client = CreateClient();
            await WebAuthTestHelpers.LoginAsync(client, TestIdentitySeeder.StorekeeperEmail);
            var itemId = await GetSeedItemIdAsync();

            var receivingHtml = Decode(await client.GetStringAsync("/Receiving"));
            Assert.Contains("<select class=\"form-select\"", receivingHtml, StringComparison.Ordinal);
            Assert.Contains("中央庫房-A01", receivingHtml, StringComparison.Ordinal);
            Assert.DoesNotContain(disabled.Name, receivingHtml, StringComparison.Ordinal);

            var unknown = await PostReceivingAsync(client, itemId, unknownLot, "不存在的位置");
            var unknownHtml = Decode(await unknown.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.OK, unknown.StatusCode);
            Assert.Contains("儲藏位置不存在或已停用。", unknownHtml, StringComparison.Ordinal);

            var inactive = await PostReceivingAsync(client, itemId, disabledLot, disabled.Name);
            var inactiveHtml = Decode(await inactive.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.OK, inactive.StatusCode);
            Assert.Contains("儲藏位置不存在或已停用。", inactiveHtml, StringComparison.Ordinal);

            await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
            var written = await connection.QuerySingleAsync<int>(
                "SELECT COUNT(*) FROM stock_lots WHERE lot_number IN (:unknownLot, :disabledLot)",
                new { unknownLot, disabledLot });
            Assert.Equal(0, written);

            _output.WriteLine($"T1 unknown POST（{unknownLot}）: 儲藏位置不存在或已停用。；DB rows=0");
            _output.WriteLine($"T1 disabled POST（{disabledLot}）: 儲藏位置不存在或已停用。；DB rows=0");
            _output.WriteLine("T1 GET /Receiving: select；中央庫房-A01 present；disabled option absent");
        }
        finally
        {
            await using var cleanup = new OracleConnection(OracleTestDatabase.ConnectionString);
            var strayLotIds = (await cleanup.QueryAsync<long>(
                "SELECT stock_lot_id FROM stock_lots WHERE lot_number IN (:unknownLot, :disabledLot)",
                new { unknownLot, disabledLot })).ToList();
            foreach (var lotId in strayLotIds)
            {
                await cleanup.ExecuteAsync(
                    "DELETE FROM audit_logs WHERE entity_type = 'StockLot' AND entity_id = :id",
                    new { id = lotId.ToString(CultureInfo.InvariantCulture) });
            }

            await cleanup.ExecuteAsync(
                "DELETE FROM stock_lots WHERE lot_number IN (:unknownLot, :disabledLot)",
                new { unknownLot, disabledLot });
            await cleanup.ExecuteAsync("COMMIT");
            await DeleteLocationAsync(disabled.Id);
        }
    }

    [Fact]
    public async Task T2_English_inventory_uses_master_name_and_falls_back_for_unmatched_text()
    {
        var itemId = await GetSeedItemIdAsync();
        var lotNumber = "ITSL-T2-" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.ExecuteAsync("""
            INSERT INTO stock_lots
                (item_id, lot_number, expiry_date, quantity, storage_location, created_by)
            VALUES
                (:itemId, :lotNumber, :expiryDate, 1, 'ITEST-A01', 'itest-woae-t2')
            """, new
        {
            itemId,
            lotNumber,
            expiryDate = TestBusinessCalendar.Today.AddDays(120).ToDateTime(TimeOnly.MinValue),
        });

        try
        {
            using var client = CreateClient(english: true);
            await WebAuthTestHelpers.LoginAsync(client, TestIdentitySeeder.StorekeeperEmail);
            var html = Decode(await client.GetStringAsync("/Inventory?search=MD-0001"));

            Assert.Contains("Central Warehouse A01", html, StringComparison.Ordinal);
            Assert.Contains("ITEST-A01", html, StringComparison.Ordinal);
            _output.WriteLine("T2 English master match: Central Warehouse A01");
            _output.WriteLine("T2 unmatched fallback: ITEST-A01");
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM stock_lots WHERE lot_number = :lotNumber", new { lotNumber });
            await connection.ExecuteAsync("COMMIT");
        }
    }

    [Fact]
    public async Task T3_disable_is_blocked_by_positive_stock_then_succeeds_after_quantity_is_zero()
    {
        var location = await InsertLocationAsync("T3");
        var itemId = await InsertItemAsync("T3");
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.ExecuteAsync("""
            INSERT INTO stock_lots
                (item_id, lot_number, expiry_date, quantity, storage_location, created_by)
            VALUES
                (:itemId, :lotNumber, :expiryDate, 7, :name, 'itest-woae-t3')
            """, new
        {
            itemId,
            lotNumber = location.Code + "-LOT",
            expiryDate = TestBusinessCalendar.Today.AddDays(180).ToDateTime(TimeOnly.MinValue),
            location.Name,
        });

        try
        {
            using var client = CreateClient();
            await WebAuthTestHelpers.LoginAsync(client, TestIdentitySeeder.AdministratorEmail);

            var blocked = await DisableAsync(client, location.Id);
            var blockedMessage = $"儲藏位置 {location.Name} 仍有庫存 7，請先清空後再停用。";
            Assert.Contains(blockedMessage, blocked, StringComparison.Ordinal);

            await connection.ExecuteAsync(
                "UPDATE stock_lots SET quantity = 0 WHERE item_id = :itemId AND storage_location = :name",
                new { itemId, location.Name });
            var disabled = await DisableAsync(client, location.Id);
            var successMessage = $"儲藏位置 {location.Name} 已停用。";
            Assert.Contains(successMessage, disabled, StringComparison.Ordinal);
            Assert.Equal(1, await connection.QuerySingleAsync<int>(
                "SELECT is_deleted FROM storage_locations WHERE location_id = :id",
                new { location.Id }));

            _output.WriteLine($"T3 blocked: {blockedMessage}");
            _output.WriteLine($"T3 after quantity=0: {successMessage}");
        }
        finally
        {
            await CleanupLocationAndItemAsync(location.Id, itemId);
        }
    }

    [Fact]
    public async Task T4_forged_name_edit_is_ignored_but_English_name_is_updated()
    {
        var location = await InsertLocationAsync("T4");
        try
        {
            using var client = CreateClient();
            await WebAuthTestHelpers.LoginAsync(client, TestIdentitySeeder.StorekeeperEmail);
            var token = await GetTokenAsync(client, $"/StorageLocations/Edit/{location.Id}");
            var response = await client.PostAsync($"/StorageLocations/Edit/{location.Id}", Form(token, new()
            {
                ["Code"] = "FORGED-CODE",
                ["Name"] = "偽造名稱",
                ["EnglishName"] = "Updated English Location",
            }));
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

            await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
            var row = await connection.QuerySingleAsync<LocationValueRow>("""
                SELECT location_code AS Code,
                       name AS Name,
                       name_en AS EnglishName
                FROM storage_locations
                WHERE location_id = :id
                """, new { location.Id });
            Assert.Equal(location.Code, row.Code);
            Assert.Equal(location.Name, row.Name);
            Assert.Equal("Updated English Location", row.EnglishName);

            _output.WriteLine(
                $"T4 allow-list ignored Code=FORGED-CODE and Name=偽造名稱; DB={row.Code}|{row.Name}|{row.EnglishName}");
        }
        finally
        {
            await DeleteLocationAsync(location.Id);
        }
    }

    [Fact]
    public async Task T5_storekeeper_can_create_but_cannot_deactivate()
    {
        var suffix = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var code = $"ITSL-T5-{suffix}";
        var name = $"測試儲藏位置-T5-{suffix}";
        long locationId = 0;
        try
        {
            using var client = CreateClient();
            await WebAuthTestHelpers.LoginAsync(client, TestIdentitySeeder.StorekeeperEmail);
            var token = await GetTokenAsync(client, "/StorageLocations/Create");
            var created = await client.PostAsync("/StorageLocations/Create", Form(token, new()
            {
                ["Code"] = $" {code.ToLowerInvariant()} ",
                ["Name"] = name,
                ["EnglishName"] = "Storekeeper-created Location",
            }));
            Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);

            await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
            locationId = await connection.QuerySingleAsync<long>(
                "SELECT location_id FROM storage_locations WHERE location_code = :code",
                new { code });
            var listHtml = Decode(await client.GetStringAsync("/StorageLocations"));
            Assert.Contains(name, listHtml, StringComparison.Ordinal);
            Assert.DoesNotContain("停用儲藏位置", listHtml, StringComparison.Ordinal);

            var disableToken = await GetTokenAsync(client, "/StorageLocations");
            var denied = await client.PostAsync(
                $"/StorageLocations/Disable/{locationId}",
                Form(disableToken, []));
            Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
            Assert.Equal("/Account/AccessDenied", denied.Headers.Location?.AbsolutePath);
            Assert.Equal(0, await connection.QuerySingleAsync<int>(
                "SELECT is_deleted FROM storage_locations WHERE location_id = :locationId",
                new { locationId }));

            _output.WriteLine(
                $"T5 Storekeeper create: {code}|{name}|Storekeeper-created Location; Disable -> /Account/AccessDenied; is_deleted=0");
        }
        finally
        {
            if (locationId > 0)
            {
                await DeleteLocationAsync(locationId);
            }
        }
    }

    private HttpClient CreateClient(bool english = false)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (english)
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", EnglishCultureCookie);
        }

        return client;
    }

    private static async Task<HttpResponseMessage> PostReceivingAsync(
        HttpClient client,
        long itemId,
        string lotNumber,
        string storageLocation)
    {
        var token = await GetTokenAsync(client, "/Receiving");
        return await client.PostAsync("/Receiving", Form(token, new()
        {
            ["ItemId"] = itemId.ToString(CultureInfo.InvariantCulture),
            ["LotNumber"] = lotNumber,
            ["ExpiryDate"] = TestBusinessCalendar.Today.AddDays(30).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["Quantity"] = "1",
            ["StorageLocation"] = storageLocation,
        }));
    }

    private static async Task<string> DisableAsync(HttpClient client, long id)
    {
        var token = await GetTokenAsync(client, "/StorageLocations");
        var response = await client.PostAsync($"/StorageLocations/Disable/{id}", Form(token, []));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return Decode(await client.GetStringAsync(response.Headers.Location));
    }

    private static async Task<string> GetTokenAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var match = AntiforgeryTokenRegex().Match(await response.Content.ReadAsStringAsync());
        Assert.True(match.Success, $"{path} 必須包含 AntiForgery request token。");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static FormUrlEncodedContent Form(string token, Dictionary<string, string> values)
    {
        values["__RequestVerificationToken"] = token;
        return new FormUrlEncodedContent(values);
    }

    private static async Task<long> GetSeedItemIdAsync()
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        return await connection.QuerySingleAsync<long>(
            "SELECT item_id FROM items WHERE item_code = 'MD-0001' AND is_deleted = 0");
    }

    private static async Task<(long Id, string Code, string Name)> InsertLocationAsync(string marker, bool isDeleted = false)
    {
        var suffix = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var code = $"ITSL-{marker}-{suffix}";
        var name = $"測試儲藏位置-{marker}-{suffix}";
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        if (isDeleted)
        {
            await connection.ExecuteAsync("""
                INSERT INTO storage_locations
                    (location_code, name, name_en, is_deleted, deleted_at, deleted_by, created_by)
                VALUES
                    (:code, :name, 'Disabled Test Location', 1, :deletedAt, 'itest-woae', 'itest-woae')
                """, new
            {
                code,
                name,
                deletedAt = TestBusinessCalendar.Today.ToDateTime(TimeOnly.MinValue),
            });
        }
        else
        {
            await connection.ExecuteAsync("""
                INSERT INTO storage_locations (location_code, name, name_en, created_by)
                VALUES (:code, :name, 'Test Storage Location', 'itest-woae')
                """, new { code, name });
        }

        var id = await connection.QuerySingleAsync<long>(
            "SELECT location_id FROM storage_locations WHERE location_code = :code",
            new { code });
        return (id, code, name);
    }

    private static async Task<long> InsertItemAsync(string marker)
    {
        var code = $"ITSL-{marker}-{Guid.NewGuid():N}"[..28].ToUpperInvariant();
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.ExecuteAsync("""
            INSERT INTO items (item_code, item_name, unit_of_measure, safety_stock_qty, created_by)
            VALUES (:code, '儲藏位置驗收品項', '盒', 0, 'itest-woae')
            """, new { code });
        return await connection.QuerySingleAsync<long>(
            "SELECT item_id FROM items WHERE item_code = :code",
            new { code });
    }

    private static async Task DeleteLocationAsync(long id)
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.ExecuteAsync(
            "DELETE FROM audit_logs WHERE entity_type = 'StorageLocation' AND entity_id = :entityId",
            new { entityId = id.ToString(CultureInfo.InvariantCulture) });
        await connection.ExecuteAsync("DELETE FROM storage_locations WHERE location_id = :id", new { id });
        await connection.ExecuteAsync("COMMIT");
    }

    private static async Task CleanupLocationAndItemAsync(long locationId, long itemId)
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.ExecuteAsync(
            "DELETE FROM audit_logs WHERE entity_type = 'StorageLocation' AND entity_id = :entityId",
            new { entityId = locationId.ToString(CultureInfo.InvariantCulture) });
        await connection.ExecuteAsync("DELETE FROM stock_lots WHERE item_id = :itemId", new { itemId });
        await connection.ExecuteAsync("DELETE FROM items WHERE item_id = :itemId", new { itemId });
        await connection.ExecuteAsync("DELETE FROM storage_locations WHERE location_id = :locationId", new { locationId });
        await connection.ExecuteAsync("COMMIT");
    }

    private static string Decode(string value) => WebUtility.HtmlDecode(value);

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryTokenRegex();

    private sealed class LocationValueRow
    {
        public string Code { get; init; } = string.Empty;

        public string Name { get; init; } = string.Empty;

        public string? EnglishName { get; init; }
    }
}
