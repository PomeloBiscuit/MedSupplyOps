using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Dapper;
using MedSupplyOps.Infrastructure.Identity;
using MedSupplyOps.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Oracle.ManagedDataAccess.Client;
using Xunit.Abstractions;

namespace MedSupplyOps.Integration.Tests.Web;

public sealed partial class ItemReceivingWebTests
    : IClassFixture<RequisitionFlowTests.RequisitionWebApplicationFactory>, IAsyncLifetime
{
    private readonly RequisitionFlowTests.RequisitionWebApplicationFactory _factory;
    private readonly ITestOutputHelper _output;

    public ItemReceivingWebTests(
        RequisitionFlowTests.RequisitionWebApplicationFactory factory,
        ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    public Task InitializeAsync()
    {
        _ = _factory.Services;
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task T1_receiving_uses_the_Taipei_business_date_at_the_utc_boundary()
    {
        var itemId = await CreateDirectItemAsync("T1");
        var boundary = new DateTimeOffset(2030, 6, 15, 19, 0, 0, TimeSpan.Zero);
        TestBusinessCalendar.HostClock.UtcNow = boundary;
        try
        {
            using var client = CreateClient();
            await WebAuthTestHelpers.LoginAsync(client, TestIdentitySeeder.AdministratorEmail);

            var expired = await PostReceivingAsync(
                client, itemId, " t1-old ", new DateOnly(2030, 6, 15), 3, " room-t1 ");
            var expiredHtml = HtmlDecode(await expired.Content.ReadAsStringAsync());
            const string expiredMessage = "此批次效期 2030-06-15 已過期（今天是 2030-06-16），不可入庫。";
            Assert.Equal(HttpStatusCode.OK, expired.StatusCode);
            Assert.Contains(expiredMessage, expiredHtml, StringComparison.Ordinal);

            await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
            var rowsAfterExpired = await connection.QuerySingleAsync<int>(
                "SELECT COUNT(*) FROM stock_lots WHERE item_id = :itemId",
                new { itemId });
            Assert.Equal(0, rowsAfterExpired);

            var accepted = await PostReceivingAsync(
                client, itemId, " t1-today ", new DateOnly(2030, 6, 16), 4, " room-t1 ");
            Assert.Equal(HttpStatusCode.Redirect, accepted.StatusCode);
            var acceptedPage = await client.GetAsync(accepted.Headers.Location);
            var acceptedHtml = HtmlDecode(await acceptedPage.Content.ReadAsStringAsync());
            const string acceptedMessage = "已入庫：批號 T1-TODAY，入庫後數量 4。";
            Assert.Contains(acceptedMessage, acceptedHtml, StringComparison.Ordinal);

            var row = await connection.QuerySingleAsync<ReceivedLotRow>("""
                SELECT lot_number AS LotNumber,
                       expiry_date AS ExpiryDate,
                       quantity AS Quantity,
                       storage_location AS StorageLocation
                FROM stock_lots
                WHERE item_id = :itemId
                """, new { itemId });
            Assert.Equal("T1-TODAY", row.LotNumber);
            Assert.Equal(new DateTime(2030, 6, 16), row.ExpiryDate);
            Assert.Equal(4, decimal.ToInt32(row.Quantity));
            Assert.Equal("ROOM-T1", row.StorageLocation);

            _output.WriteLine($"T1 UTC={boundary:O}; Taipei asOf=2030-06-16");
            _output.WriteLine($"T1 rejected page: {expiredMessage}; DB rows={rowsAfterExpired}");
            _output.WriteLine(
                $"T1 accepted page: {acceptedMessage}; DB={row.LotNumber}|{row.ExpiryDate:yyyy-MM-dd}|{row.Quantity}|{row.StorageLocation}");
        }
        finally
        {
            TestBusinessCalendar.HostClock.UtcNow = TestBusinessCalendar.DefaultInstant;
            await CleanupItemsAsync([itemId]);
        }
    }

    [Fact]
    public async Task T5_disable_guards_stock_and_open_requisitions_then_allows_safe_reuse_of_code()
    {
        using var client = CreateClient();
        await WebAuthTestHelpers.LoginAsync(client, TestIdentitySeeder.AdministratorEmail);
        var stockItemId = await CreateDirectItemAsync("T5S");
        var requisitionItemId = await CreateDirectItemAsync("T5R");
        var safeCode = NewCode("T5D");
        var safeItemId = await CreateDirectItemAsync("T5D", safeCode);
        long replacementId = 0;
        long requisitionId = 0;

        try
        {
            await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
            await connection.ExecuteAsync("""
                INSERT INTO stock_lots
                    (item_id, lot_number, expiry_date, quantity, storage_location, created_by)
                VALUES
                    (:itemId, 'T5-STOCK', DATE '2032-01-01', 9, 'ROOM-T5', 'itest-t5')
                """, new { itemId = stockItemId });

            var stockPage = await DisableAsync(client, stockItemId);
            const string stockMessage = "仍有庫存 9（批號 T5-STOCK），請先處理。";
            Assert.Contains(stockMessage, stockPage, StringComparison.Ordinal);

            var departmentId = await connection.QuerySingleAsync<long>(
                "SELECT department_id FROM departments WHERE department_code = 'DEP-ER' AND is_deleted = 0");
            var requisitionNo = "IT-T5-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
            await connection.ExecuteAsync("""
                INSERT INTO requisitions (requisition_no, department_id, status, created_by)
                VALUES (:requisitionNo, :departmentId, 'Approved', 'itest-t5')
                """, new { requisitionNo, departmentId });
            requisitionId = await connection.QuerySingleAsync<long>(
                "SELECT requisition_id FROM requisitions WHERE requisition_no = :requisitionNo",
                new { requisitionNo });
            await connection.ExecuteAsync("""
                INSERT INTO requisition_lines (requisition_id, line_no, item_id, quantity)
                VALUES (:requisitionId, 1, :itemId, 1)
                """, new { requisitionId, itemId = requisitionItemId });

            var requisitionPage = await DisableAsync(client, requisitionItemId);
            var requisitionMessage = $"有未結案的請領單：單號 {requisitionNo}。";
            Assert.Contains(requisitionMessage, requisitionPage, StringComparison.Ordinal);

            var successPage = await DisableAsync(client, safeItemId);
            Assert.Contains($"品項 {safeCode} 已停用。", successPage, StringComparison.Ordinal);
            var disabled = await connection.QuerySingleAsync<DisabledItemRow>("""
                SELECT is_deleted AS IsDeleted,
                       deleted_at AS DeletedAt,
                       deleted_by AS DeletedBy
                FROM items
                WHERE item_id = :safeItemId
                """, new { safeItemId });
            Assert.Equal(1, decimal.ToInt32(disabled.IsDeleted));
            Assert.NotNull(disabled.DeletedAt);
            Assert.Equal(TestIdentitySeeder.AdministratorEmail, disabled.DeletedBy);

            await using var receiveConnection = new OracleConnection(OracleTestDatabase.ConnectionString);
            var service = new StockReceivingService(
                receiveConnection,
                new TestCurrentUser("itest-t5-keeper"));
            var receiveDisabled = await service.ReceiveAsync(
                safeItemId,
                "T5-DISABLED",
                new DateOnly(2032, 1, 1),
                1,
                "ROOM-T5",
                TestBusinessCalendar.Today);
            Assert.Equal(ReceiveFailureReason.ItemNotFound, receiveDisabled.FailureReason);

            replacementId = await CreateItemThroughWebAsync(client, safeCode.ToLowerInvariant(), "替代品項", "盒", 0);
            var reusedRows = (await connection.QueryAsync<ReusedCodeRow>("""
                SELECT item_id AS ItemId,
                       item_code AS Code,
                       is_deleted AS IsDeleted
                FROM items
                WHERE item_code = :safeCode
                ORDER BY is_deleted DESC, item_id
                """, new { safeCode })).AsList();
            Assert.Equal(2, reusedRows.Count);
            Assert.Equal([1, 0], reusedRows.Select(row => decimal.ToInt32(row.IsDeleted)));

            _output.WriteLine($"T5(a) {stockMessage}");
            _output.WriteLine($"T5(b) {requisitionMessage}");
            _output.WriteLine(
                $"T5(c) is_deleted={disabled.IsDeleted}, deleted_at={disabled.DeletedAt:O}, deleted_by={disabled.DeletedBy}; receive={receiveDisabled.FailureReason}");
            foreach (var row in reusedRows)
            {
                _output.WriteLine($"T5 reused code row: {row.ItemId}|{row.Code}|is_deleted={row.IsDeleted}");
            }
        }
        finally
        {
            await CleanupItemsAsync([stockItemId, requisitionItemId, safeItemId, replacementId]);
        }
    }

    [Fact]
    public async Task T6_item_code_is_trimmed_and_uppercased_before_duplicate_check()
    {
        using var client = CreateClient();
        await WebAuthTestHelpers.LoginAsync(client, TestIdentitySeeder.AdministratorEmail);
        var response = await PostCreateItemAsync(client, " md-0001 ", "不應建立", "盒", 0);
        var html = HtmlDecode(await response.Content.ReadAsStringAsync());
        const string message = "料號 MD-0001 已存在。";
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(message, html, StringComparison.Ordinal);

        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        var count = await connection.QuerySingleAsync<int>("""
            SELECT COUNT(*)
            FROM items
            WHERE UPPER(item_code) = 'MD-0001'
              AND is_deleted = 0
            """);
        Assert.Equal(1, count);
        _output.WriteLine($"T6 page: {message}; active UPPER(MD-0001) count={count}");
    }

    [Fact]
    public async Task T7_forged_edit_post_cannot_change_code_or_unit_but_can_change_name()
    {
        using var client = CreateClient();
        await WebAuthTestHelpers.LoginAsync(client, TestIdentitySeeder.AdministratorEmail);
        var code = NewCode("T7");
        var itemId = await CreateItemThroughWebAsync(client, code, "原名稱", "盒", 2);
        try
        {
            var token = await GetTokenAsync(client, $"/Items/Edit/{itemId}");
            var response = await client.PostAsync($"/Items/Edit/{itemId}", new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["__RequestVerificationToken"] = token,
                    ["Code"] = "FORGED-CODE",
                    ["UnitOfMeasure"] = "個",
                    ["Name"] = "已更新名稱",
                    ["Specification"] = "更新規格",
                    ["SafetyStockQty"] = "3",
                }));
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

            await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
            var row = await connection.QuerySingleAsync<ItemValueRow>("""
                SELECT item_code AS Code,
                       item_name AS Name,
                       unit_of_measure AS UnitOfMeasure
                FROM items
                WHERE item_id = :itemId
                """, new { itemId });
            Assert.Equal(code, row.Code);
            Assert.Equal("已更新名稱", row.Name);
            Assert.Equal("盒", row.UnitOfMeasure);
            _output.WriteLine($"T7 DB: name={row.Name}; code={row.Code}; unit={row.UnitOfMeasure}");
        }
        finally
        {
            await CleanupItemsAsync([itemId]);
        }
    }

    /// <summary>
    /// ★ 數字欄位的「綁定錯誤」不可以在重新驗證時被吞掉（L-028）。
    ///
    /// 兩個 Controller 都先正規化字串欄位、再重新驗證。若用 <c>ModelState.Clear()</c> 清掉舊結果，
    /// 會連同模型綁定的錯誤一起清掉：數字欄送空白或非數字時，屬性維持預設值
    /// （入庫的數量預設 1、安全存量維持 0），重新驗證又是合法值 ——
    /// 於是一個使用者**沒有輸入**的數字被寫進資料庫，畫面顯示成功。
    /// 頁面有前端驗證會先擋，但前端驗證是介面，不是驗證（跟「藏連結不是授權」同一個道理）。
    /// </summary>
    [Fact]
    public async Task Numeric_binding_errors_are_not_swallowed_when_revalidating_normalized_input()
    {
        using var client = CreateClient();
        await WebAuthTestHelpers.LoginAsync(client, TestIdentitySeeder.AdministratorEmail);
        var itemId = await CreateItemThroughWebAsync(client, NewCode("NB"), "綁定錯誤測試", "盒", 25);
        var itemIdsToClean = new List<long> { itemId };
        try
        {
            // 入庫：數量空白。
            var receiveToken = await GetTokenAsync(client, "/Receiving");
            var receive = await client.PostAsync("/Receiving", Form(receiveToken, new()
            {
                ["ItemId"] = itemId.ToString(CultureInfo.InvariantCulture),
                ["LotNumber"] = "NB-LOT",
                ["ExpiryDate"] = TestBusinessCalendar.Today.AddDays(90).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["Quantity"] = "",
                ["StorageLocation"] = "ROOM-NB",
            }));

            // 編輯：安全存量不是數字。
            var editToken = await GetTokenAsync(client, $"/Items/Edit/{itemId}");
            var edit = await client.PostAsync($"/Items/Edit/{itemId}", Form(editToken, new()
            {
                ["Name"] = "綁定錯誤測試",
                ["SafetyStockQty"] = "abc",
            }));

            // 新增：安全存量不是數字（料號故意帶空白與小寫，確認正規化的重新驗證仍然有效）。
            var createCode = NewCode("NC");
            var create = await client.PostAsync("/Items/Create", Form(await GetTokenAsync(client, "/Items/Create"), new()
            {
                ["Code"] = $" {createCode.ToLowerInvariant()} ",
                ["Name"] = "綁定錯誤測試（新增）",
                ["UnitOfMeasure"] = "盒",
                ["SafetyStockQty"] = "abc",
            }));

            await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
            var createdIds = (await connection.QueryAsync<long>(
                "SELECT item_id FROM items WHERE item_code = :createCode",
                new { createCode })).ToList();
            itemIdsToClean.AddRange(createdIds);
            var receivedQuantity = await connection.QuerySingleAsync<int>(
                "SELECT NVL(SUM(quantity), 0) FROM stock_lots WHERE item_id = :itemId",
                new { itemId });
            var safetyStock = await connection.QuerySingleAsync<int>(
                "SELECT safety_stock_qty FROM items WHERE item_id = :itemId",
                new { itemId });

            // 先收集再一起斷言：兩個 Controller 各自有沒有這個問題，都要看得到。
            var failures = new List<string>();
            if (receive.StatusCode != HttpStatusCode.OK || receivedQuantity != 0)
            {
                failures.Add($"入庫數量空白：回應 {(int)receive.StatusCode}、實際入庫 {receivedQuantity} 件（應回表單、0 件）");
            }

            if (edit.StatusCode != HttpStatusCode.OK || safetyStock != 25)
            {
                failures.Add($"安全存量送 abc：回應 {(int)edit.StatusCode}、資料庫變成 {safetyStock}（應回表單、維持 25）");
            }

            if (create.StatusCode != HttpStatusCode.OK || createdIds.Count != 0)
            {
                failures.Add($"新增品項安全存量送 abc：回應 {(int)create.StatusCode}、建立了 {createdIds.Count} 筆（應回表單、0 筆）");
            }

            _output.WriteLine(
                $"入庫：{(int)receive.StatusCode}／{receivedQuantity} 件；編輯：{(int)edit.StatusCode}／安全存量 {safetyStock}；" +
                $"新增：{(int)create.StatusCode}／{createdIds.Count} 筆");
            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }
        finally
        {
            await CleanupItemsAsync(itemIdsToClean);
        }
    }

    [Fact]
    public async Task T8_direct_endpoint_requests_enforce_both_new_policies_for_every_role_and_verb()
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        var itemId = await connection.QuerySingleAsync<long>(
            "SELECT item_id FROM items WHERE item_code = 'MD-0001' AND is_deleted = 0");

        var roles = new (string Label, string? Email)[]
        {
            ("未登入", null),
            ("請領人", TestIdentitySeeder.RequesterEmail),
            ("庫管員", TestIdentitySeeder.StorekeeperEmail),
            ("管理員", TestIdentitySeeder.AdministratorEmail),
        };
        var allResults = new List<EndpointResult>();

        foreach (var (label, email) in roles)
        {
            using var client = CreateClient();
            if (email is not null)
            {
                await WebAuthTestHelpers.LoginAsync(client, email);
            }

            var itemAuthorized = email == TestIdentitySeeder.AdministratorEmail;
            var receiveAuthorized = email is TestIdentitySeeder.StorekeeperEmail or TestIdentitySeeder.AdministratorEmail;
            var navigationPage = await client.GetAsync(email is null ? "/Account/Login" : "/");
            var navigationHtml = await navigationPage.Content.ReadAsStringAsync();
            Assert.Equal(itemAuthorized, navigationHtml.Contains("href=\"/Items\"", StringComparison.Ordinal));
            Assert.Equal(receiveAuthorized, navigationHtml.Contains("href=\"/Receiving\"", StringComparison.Ordinal));
            _output.WriteLine(
                $"T8 NAV {label}: Items={itemAuthorized}; Receiving={receiveAuthorized}");
            string? itemToken = itemAuthorized ? await GetTokenAsync(client, "/Items/Create") : null;
            string? receiveToken = receiveAuthorized ? await GetTokenAsync(client, "/Receiving") : null;

            allResults.Add(await ProbeAsync(client, label, "GET /Items", HttpMethod.Get, "/Items"));
            allResults.Add(await ProbeAsync(client, label, "GET /Items/Create", HttpMethod.Get, "/Items/Create"));
            allResults.Add(await ProbeAsync(
                client, label, "POST /Items/Create", HttpMethod.Post, "/Items/Create",
                Form(itemToken, new() { ["Code"] = "", ["Name"] = "", ["UnitOfMeasure"] = "", ["SafetyStockQty"] = "0" })));
            allResults.Add(await ProbeAsync(client, label, "GET /Items/Edit", HttpMethod.Get, $"/Items/Edit/{itemId}"));
            allResults.Add(await ProbeAsync(
                client, label, "POST /Items/Edit", HttpMethod.Post, $"/Items/Edit/{itemId}",
                Form(itemToken, new() { ["Name"] = "", ["SafetyStockQty"] = "100" })));
            allResults.Add(await ProbeAsync(
                client, label, "POST /Items/Disable", HttpMethod.Post, $"/Items/Disable/{itemId}",
                Form(itemToken, [])));
            allResults.Add(await ProbeAsync(client, label, "GET /Receiving", HttpMethod.Get, "/Receiving"));
            allResults.Add(await ProbeAsync(
                client, label, "POST /Receiving", HttpMethod.Post, "/Receiving",
                Form(receiveToken, new()
                {
                    ["ItemId"] = "0",
                    ["LotNumber"] = "",
                    ["ExpiryDate"] = "",
                    ["Quantity"] = "0",
                    ["StorageLocation"] = "",
                })));
        }

        foreach (var result in allResults)
        {
            if (result.Role == "未登入")
            {
                Assert.Equal(HttpStatusCode.Redirect, result.StatusCode);
                Assert.Equal("/Account/Login", result.Location);
            }
            else if (result.Role == "請領人" || (result.Role == "庫管員" && result.Endpoint.Contains("/Items", StringComparison.Ordinal)))
            {
                Assert.Equal(HttpStatusCode.Redirect, result.StatusCode);
                Assert.Equal("/Account/AccessDenied", result.Location);
            }
            else if (result.Endpoint == "POST /Items/Disable")
            {
                Assert.Equal(HttpStatusCode.Redirect, result.StatusCode);
                Assert.Equal("/Items", result.Location);
            }
            else
            {
                Assert.Equal(HttpStatusCode.OK, result.StatusCode);
            }

            _output.WriteLine($"T8 {result.Role}|{result.Endpoint}|HTTP {(int)result.StatusCode}|Location={result.Location ?? "<none>"}");
        }
    }

    [Fact]
    public async Task T9_newly_received_earlier_lot_is_selected_first_by_existing_FEFO_issue_path()
    {
        using var client = CreateClient();
        await WebAuthTestHelpers.LoginAsync(client, TestIdentitySeeder.AdministratorEmail);
        var itemId = await CreateDirectItemAsync("T9");
        long requisitionId = 0;
        try
        {
            await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
            await connection.ExecuteAsync("""
                INSERT INTO stock_lots
                    (item_id, lot_number, expiry_date, quantity, storage_location, created_by)
                VALUES
                    (:itemId, 'T9-LATE', DATE '2031-12-31', 10, 'ROOM-T9', 'itest-t9')
                """, new { itemId });

            var receive = await PostReceivingAsync(
                client, itemId, "T9-EARLY", new DateOnly(2031, 1, 1), 5, "ROOM-T9");
            Assert.Equal(HttpStatusCode.Redirect, receive.StatusCode);

            var departmentId = await connection.QuerySingleAsync<long>(
                "SELECT department_id FROM departments WHERE department_code = 'DEP-ER' AND is_deleted = 0");
            requisitionId = await CreateRequisitionAsync(client, departmentId, itemId, 3);
            var review = await GetDetailsFormAsync(client, requisitionId);
            var approve = await client.PostAsync($"/Requisitions/Approve/{requisitionId}", Form(
                review.Token,
                new() { ["rowVersion"] = review.RowVersion.ToString(CultureInfo.InvariantCulture) }));
            Assert.Equal(HttpStatusCode.Redirect, approve.StatusCode);

            var issueToken = await GetTokenAsync(client, $"/Requisitions/Details/{requisitionId}");
            var issue = await client.PostAsync($"/Requisitions/Issue/{requisitionId}", Form(issueToken, []));
            Assert.Equal(HttpStatusCode.Redirect, issue.StatusCode);

            var allocations = (await connection.QueryAsync<AllocationRow>("""
                SELECT sl.lot_number AS LotNumber,
                       ia.quantity AS Quantity,
                       ia.expiry_date_at_issue AS ExpiryDate
                FROM issue_allocations ia
                INNER JOIN requisition_lines rl ON rl.requisition_line_id = ia.requisition_line_id
                INNER JOIN stock_lots sl ON sl.stock_lot_id = ia.stock_lot_id
                WHERE rl.requisition_id = :requisitionId
                ORDER BY ia.issue_allocation_id
                """, new { requisitionId })).AsList();
            Assert.Single(allocations);
            Assert.Equal("T9-EARLY", allocations[0].LotNumber);
            Assert.Equal(3, decimal.ToInt32(allocations[0].Quantity));
            _output.WriteLine(
                $"T9 allocation: {allocations[0].LotNumber}|{allocations[0].ExpiryDate:yyyy-MM-dd}|qty={allocations[0].Quantity} (T9-LATE not selected)");
        }
        finally
        {
            await CleanupItemsAsync([itemId]);
        }
    }

    [Fact]
    public async Task T10_item_and_receiving_actions_write_complete_audits_with_the_logged_in_actor()
    {
        using var client = CreateClient();
        await WebAuthTestHelpers.LoginAsync(client, TestIdentitySeeder.AdministratorEmail);
        var itemCode = NewCode("T10I");
        var itemId = await CreateItemThroughWebAsync(client, itemCode, "稽核原名稱", "盒", 0);
        var receivingItemId = await CreateDirectItemAsync("T10R");
        try
        {
            var editToken = await GetTokenAsync(client, $"/Items/Edit/{itemId}");
            var edit = await client.PostAsync($"/Items/Edit/{itemId}", Form(editToken, new()
            {
                ["Name"] = "稽核新名稱",
                ["Specification"] = "稽核規格",
                ["SafetyStockQty"] = "6",
            }));
            Assert.Equal(HttpStatusCode.Redirect, edit.StatusCode);
            _ = await DisableAsync(client, itemId);

            Assert.Equal(
                HttpStatusCode.Redirect,
                (await PostReceivingAsync(
                    client, receivingItemId, "T10-LOT", new DateOnly(2032, 2, 2), 4, "ROOM-T10")).StatusCode);
            Assert.Equal(
                HttpStatusCode.Redirect,
                (await PostReceivingAsync(
                    client, receivingItemId, " t10-lot ", new DateOnly(2032, 2, 2), 3, " room-t10 ")).StatusCode);

            await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
            var stockLotId = await connection.QuerySingleAsync<long>(
                "SELECT stock_lot_id FROM stock_lots WHERE item_id = :receivingItemId",
                new { receivingItemId });
            var logs = (await connection.QueryAsync<AuditRow>("""
                SELECT entity_type AS EntityType,
                       entity_id AS EntityId,
                       action AS Action,
                       actor AS Actor,
                       old_value AS OldValue,
                       new_value AS NewValue
                FROM audit_logs
                WHERE (entity_type = 'Item' AND entity_id = TO_CHAR(:itemId))
                   OR (entity_type = 'StockLot' AND entity_id = TO_CHAR(:stockLotId))
                ORDER BY audit_log_id
                """, new { itemId, stockLotId })).AsList();
            Assert.Equal(5, logs.Count);
            Assert.Equal(["Create", "Update", "Delete", "Receive", "Receive"], logs.Select(log => log.Action));
            Assert.All(logs, log => Assert.Equal(TestIdentitySeeder.AdministratorEmail, log.Actor));
            Assert.Null(logs[0].OldValue);
            Assert.Null(logs[3].OldValue);
            Assert.Contains("\"quantity\":4", logs[4].OldValue, StringComparison.Ordinal);
            Assert.Contains("\"quantityAfter\":7", logs[4].NewValue, StringComparison.Ordinal);

            foreach (var log in logs)
            {
                _output.WriteLine(
                    $"T10 {log.EntityType}|{log.EntityId}|{log.Action}|{log.Actor}|old={log.OldValue ?? "NULL"}|new={log.NewValue}");
            }
        }
        finally
        {
            await CleanupItemsAsync([itemId, receivingItemId]);
        }
    }

    private HttpClient CreateClient()
        => _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static async Task<HttpResponseMessage> PostReceivingAsync(
        HttpClient client,
        long itemId,
        string lotNumber,
        DateOnly expiryDate,
        int quantity,
        string storageLocation)
    {
        var token = await GetTokenAsync(client, "/Receiving");
        return await client.PostAsync("/Receiving", Form(token, new()
        {
            ["ItemId"] = itemId.ToString(CultureInfo.InvariantCulture),
            ["LotNumber"] = lotNumber,
            ["ExpiryDate"] = expiryDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["Quantity"] = quantity.ToString(CultureInfo.InvariantCulture),
            ["StorageLocation"] = storageLocation,
        }));
    }

    private static async Task<HttpResponseMessage> PostCreateItemAsync(
        HttpClient client,
        string code,
        string name,
        string unit,
        int safetyStock)
    {
        var token = await GetTokenAsync(client, "/Items/Create");
        return await client.PostAsync("/Items/Create", Form(token, new()
        {
            ["Code"] = code,
            ["Name"] = name,
            ["Specification"] = "測試規格",
            ["UnitOfMeasure"] = unit,
            ["SafetyStockQty"] = safetyStock.ToString(CultureInfo.InvariantCulture),
        }));
    }

    private static async Task<long> CreateItemThroughWebAsync(
        HttpClient client,
        string code,
        string name,
        string unit,
        int safetyStock)
    {
        var response = await PostCreateItemAsync(client, code, name, unit, safetyStock);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var normalizedCode = code.Trim().ToUpperInvariant();
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        return await connection.QuerySingleAsync<long>(
            "SELECT item_id FROM items WHERE item_code = :normalizedCode AND is_deleted = 0",
            new { normalizedCode });
    }

    private static async Task<string> DisableAsync(HttpClient client, long itemId)
    {
        var token = await GetTokenAsync(client, "/Items");
        var response = await client.PostAsync($"/Items/Disable/{itemId}", Form(token, []));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var page = await client.GetAsync(response.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        return HtmlDecode(await page.Content.ReadAsStringAsync());
    }

    private static async Task<long> CreateRequisitionAsync(
        HttpClient client,
        long departmentId,
        long itemId,
        int quantity)
    {
        var token = await GetTokenAsync(client, "/Requisitions/Create");
        var response = await client.PostAsync("/Requisitions/Create", Form(token, new()
        {
            ["AsOf"] = TestBusinessCalendar.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["DepartmentId"] = departmentId.ToString(CultureInfo.InvariantCulture),
            ["Lines[0].ItemId"] = itemId.ToString(CultureInfo.InvariantCulture),
            ["Lines[0].Quantity"] = quantity.ToString(CultureInfo.InvariantCulture),
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        return long.Parse(response.Headers.Location.OriginalString.Split('/').Last(), CultureInfo.InvariantCulture);
    }

    private static async Task<(string Token, long RowVersion)> GetDetailsFormAsync(HttpClient client, long requisitionId)
    {
        var response = await client.GetAsync($"/Requisitions/Details/{requisitionId}");
        var html = await response.Content.ReadAsStringAsync();
        var rowVersionMatch = RowVersionRegex().Match(html);
        Assert.True(rowVersionMatch.Success);
        return (
            ExtractToken(html),
            long.Parse(rowVersionMatch.Groups[1].Value, CultureInfo.InvariantCulture));
    }

    private static async Task<string> GetTokenAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return ExtractToken(await response.Content.ReadAsStringAsync());
    }

    private static string ExtractToken(string html)
    {
        var match = AntiforgeryTokenRegex().Match(html);
        Assert.True(match.Success, "頁面必須包含 AntiForgery request token。");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static FormUrlEncodedContent Form(string? token, Dictionary<string, string> values)
    {
        if (token is not null)
        {
            values["__RequestVerificationToken"] = token;
        }

        return new FormUrlEncodedContent(values);
    }

    private static async Task<EndpointResult> ProbeAsync(
        HttpClient client,
        string role,
        string endpoint,
        HttpMethod method,
        string path,
        HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        using var response = await client.SendAsync(request);
        var location = response.Headers.Location;
        var locationPath = location is null
            ? null
            : location.IsAbsoluteUri
                ? location.AbsolutePath
                : location.OriginalString.Split('?', 2)[0];
        return new EndpointResult(role, endpoint, response.StatusCode, locationPath);
    }

    private static async Task<long> CreateDirectItemAsync(string marker, string? code = null)
    {
        code ??= NewCode(marker);
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.ExecuteAsync("""
            INSERT INTO items (item_code, item_name, unit_of_measure, safety_stock_qty, created_by)
            VALUES (:code, 'Web 整合測試品項', '盒', 0, :actor)
            """, new { code, actor = $"itest-{marker.ToLowerInvariant()}" });
        return await connection.QuerySingleAsync<long>(
            "SELECT item_id FROM items WHERE item_code = :code AND is_deleted = 0",
            new { code });
    }

    private static string NewCode(string marker)
        => $"IT-{marker}-{Guid.NewGuid():N}"[..26].ToUpperInvariant();

    private static async Task CleanupItemsAsync(IReadOnlyCollection<long> rawItemIds)
    {
        var itemIds = rawItemIds.Where(id => id > 0).Distinct().ToList();
        if (itemIds.Count == 0)
        {
            return;
        }

        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.OpenAsync();
        var requisitionIds = (await connection.QueryAsync<long>("""
            SELECT DISTINCT requisition_id
            FROM requisition_lines
            WHERE item_id IN :itemIds
            """, new { itemIds })).ToList();
        if (requisitionIds.Count > 0)
        {
            await connection.ExecuteAsync(
                "DELETE FROM audit_logs WHERE entity_type = 'Requisition' AND entity_id IN :entityIds",
                new { entityIds = requisitionIds.Select(id => id.ToString(CultureInfo.InvariantCulture)).ToList() });
            await connection.ExecuteAsync("""
                DELETE FROM issue_allocations
                WHERE requisition_line_id IN (
                    SELECT requisition_line_id FROM requisition_lines WHERE requisition_id IN :requisitionIds)
                """, new { requisitionIds });
            await connection.ExecuteAsync(
                "DELETE FROM requisition_lines WHERE requisition_id IN :requisitionIds",
                new { requisitionIds });
            await connection.ExecuteAsync(
                "DELETE FROM requisitions WHERE requisition_id IN :requisitionIds",
                new { requisitionIds });
        }

        await connection.ExecuteAsync("""
            DELETE FROM audit_logs
            WHERE entity_type = 'StockLot'
              AND entity_id IN (SELECT TO_CHAR(stock_lot_id) FROM stock_lots WHERE item_id IN :itemIds)
            """, new { itemIds });
        await connection.ExecuteAsync("""
            DELETE FROM audit_logs
            WHERE entity_type = 'Item'
              AND entity_id IN :entityIds
            """, new { entityIds = itemIds.Select(id => id.ToString(CultureInfo.InvariantCulture)).ToList() });
        await connection.ExecuteAsync("DELETE FROM stock_lots WHERE item_id IN :itemIds", new { itemIds });
        await connection.ExecuteAsync("DELETE FROM items WHERE item_id IN :itemIds", new { itemIds });
        await connection.ExecuteAsync("COMMIT");
    }

    private static string HtmlDecode(string value) => WebUtility.HtmlDecode(value);

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryTokenRegex();

    [GeneratedRegex("name=\"rowVersion\" value=\"([0-9]+)\"")]
    private static partial Regex RowVersionRegex();

    private sealed record EndpointResult(string Role, string Endpoint, HttpStatusCode StatusCode, string? Location);

    private sealed class ReceivedLotRow
    {
        public string LotNumber { get; init; } = string.Empty;
        public DateTime ExpiryDate { get; init; }
        public decimal Quantity { get; init; }
        public string StorageLocation { get; init; } = string.Empty;
    }

    private sealed class DisabledItemRow
    {
        public decimal IsDeleted { get; init; }
        public DateTime? DeletedAt { get; init; }
        public string? DeletedBy { get; init; }
    }

    private sealed class ReusedCodeRow
    {
        public decimal ItemId { get; init; }
        public string Code { get; init; } = string.Empty;
        public decimal IsDeleted { get; init; }
    }

    private sealed class ItemValueRow
    {
        public string Code { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string UnitOfMeasure { get; init; } = string.Empty;
    }

    private sealed class AllocationRow
    {
        public string LotNumber { get; init; } = string.Empty;
        public decimal Quantity { get; init; }
        public DateTime ExpiryDate { get; init; }
    }

    private sealed class AuditRow
    {
        public string EntityType { get; init; } = string.Empty;
        public string EntityId { get; init; } = string.Empty;
        public string Action { get; init; } = string.Empty;
        public string Actor { get; init; } = string.Empty;
        public string? OldValue { get; init; }
        public string NewValue { get; init; } = string.Empty;
    }

    private sealed class TestCurrentUser(string actor) : ICurrentUser
    {
        public string Actor { get; } = actor;
    }
}
