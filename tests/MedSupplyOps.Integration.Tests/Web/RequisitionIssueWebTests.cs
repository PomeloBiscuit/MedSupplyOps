using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Dapper;
using MedSupplyOps.Domain.Requisitions;
using MedSupplyOps.Integration.Tests.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Oracle.ManagedDataAccess.Client;
using Xunit.Abstractions;

namespace MedSupplyOps.Integration.Tests.Web;

/// <summary>發料按鈕的整合測試：從 HTTP 表單一路驗證到真實 Oracle 配批資料。</summary>
public sealed partial class RequisitionIssueWebTests
    : IClassFixture<RequisitionFlowTests.RequisitionWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client;
    private readonly ITestOutputHelper _output;

    public RequisitionIssueWebTests(
        RequisitionFlowTests.RequisitionWebApplicationFactory factory,
        ITestOutputHelper output)
    {
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        _output = output;
    }

    /// <summary>既有流程同時涵蓋建立、核准與發料，因此使用具完整權限的專用測試管理員。</summary>
    public Task InitializeAsync() => WebAuthTestHelpers.LoginAsync(_client, TestIdentitySeeder.AdministratorEmail);

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Issue_uses_Taipei_today_at_the_UTC_boundary_and_never_allocates_yesterdays_lot()
    {
        await using var scenario = await RequisitionIssueScenario.CreateAsync(
            [new("BOUNDARY", [("BOUNDARY-A", 0, 10), ("BOUNDARY-B", 0, 10)], RequestQuantity: 1)]);
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.ExecuteAsync(
            "UPDATE stock_lots SET expiry_date = DATE '2026-09-10' WHERE lot_number = 'BOUNDARY-A'");
        await connection.ExecuteAsync(
            "UPDATE stock_lots SET expiry_date = DATE '2026-09-11' WHERE lot_number = 'BOUNDARY-B'");
        var requisitionId = await CreateAndApproveAsync(scenario, [(scenario.ItemIdOf("BOUNDARY"), 1)]);

        try
        {
            var response = await PostIssueAsync(requisitionId, await GetDetailsTokenAsync(requisitionId));
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            var allocatedLots = (await connection.QueryAsync<string>("""
                SELECT l.lot_number
                FROM issue_allocations a
                JOIN stock_lots l ON l.stock_lot_id = a.stock_lot_id
                JOIN requisition_lines rl ON rl.requisition_line_id = a.requisition_line_id
                WHERE rl.requisition_id = :requisitionId
                ORDER BY l.lot_number
                """, new { requisitionId })).ToList();

            Assert.Equal(new DateOnly(2026, 9, 11), TestBusinessCalendar.Today);
            Assert.Equal(["BOUNDARY-B"], allocatedLots);
            _output.WriteLine($"T1 BusinessCalendar.Today={TestBusinessCalendar.Today:yyyy-MM-dd}");
            _output.WriteLine("T1 allocations=BOUNDARY-B (BOUNDARY-A=0)");
        }
        finally
        {
            await DeleteRequisitionAsync(requisitionId);
        }
    }

    [Fact]
    public async Task Create_submit_approve_issue_shows_allocations_and_rejects_a_duplicate_post()
    {
        await using var scenario = await RequisitionIssueScenario.CreateAsync(
            [new("A", [("WEB-EARLY", 30, 3), ("WEB-LATE", 60, 8)], RequestQuantity: 1)]);
        var requisitionId = await CreateAndApproveAsync(scenario, [(scenario.ItemIdOf("A"), 5)]);

        try
        {
            var token = await GetDetailsTokenAsync(requisitionId);

            using var missingTokenContent = new FormUrlEncodedContent(new Dictionary<string, string>());
            var missingToken = await _client.PostAsync($"/Requisitions/Issue/{requisitionId}", missingTokenContent);
            Assert.Equal(HttpStatusCode.BadRequest, missingToken.StatusCode);

            var response = await PostIssueAsync(requisitionId, token);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            var page = await _client.GetAsync(response.Headers.Location);
            var html = HtmlDecode(await page.Content.ReadAsStringAsync());

            Assert.Contains("請領單已發料，請核對下列配批明細。", html, StringComparison.Ordinal);
            Assert.Contains("發料配批明細", html, StringComparison.Ordinal);
            Assert.Contains("WEB-EARLY", html, StringComparison.Ordinal);
            Assert.Contains("WEB-LATE", html, StringComparison.Ordinal);
            Assert.Equal("Issued", await GetStatusAsync(requisitionId));

            await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
            var allocations = (await connection.QueryAsync<AllocationRow>("""
                SELECT l.lot_number AS "LotNumber",
                       a.expiry_date_at_issue AS "ExpiryDate",
                       a.quantity AS "Quantity"
                FROM issue_allocations a
                INNER JOIN requisition_lines rl ON rl.requisition_line_id = a.requisition_line_id
                INNER JOIN stock_lots l ON l.stock_lot_id = a.stock_lot_id
                WHERE rl.requisition_id = :requisitionId
                ORDER BY l.lot_number
                """, new { requisitionId })).ToList();
            Assert.Equal(["WEB-EARLY", "WEB-LATE"], allocations.Select(row => row.LotNumber));
            Assert.Equal([3L, 2L], allocations.Select(row => row.Quantity));
            foreach (var allocation in allocations)
            {
                Assert.Contains(allocation.LotNumber, html, StringComparison.Ordinal);
                Assert.Contains(allocation.ExpiryDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), html, StringComparison.Ordinal);
            }

            var duplicateResponse = await PostIssueAsync(requisitionId, token);
            Assert.Equal(HttpStatusCode.Redirect, duplicateResponse.StatusCode);
            var duplicatePage = await _client.GetAsync(duplicateResponse.Headers.Location);
            var duplicateHtml = HtmlDecode(await duplicatePage.Content.ReadAsStringAsync());
            Assert.Contains("此單目前為「已發料」，無法發料。", duplicateHtml, StringComparison.Ordinal);

            _output.WriteLine("T3 USER: 此單目前為「已發料」，無法發料。");
            _output.WriteLine($"T4 HTTP={(int)missingToken.StatusCode} {missingToken.ReasonPhrase}");
            foreach (var allocation in allocations)
            {
                _output.WriteLine(
                    $"T5 PAGE/DB: 批號 {allocation.LotNumber}；效期 {allocation.ExpiryDate:yyyy-MM-dd}；數量 {allocation.Quantity}");
            }
        }
        finally
        {
            await DeleteRequisitionAsync(requisitionId);
        }
    }

    [Fact]
    public async Task Issue_insufficient_stock_shows_the_item_and_leaves_the_database_unchanged()
    {
        await using var scenario = await RequisitionIssueScenario.CreateAsync(
            [new("SHORT", [("WEB-SHORT", 30, 3)], RequestQuantity: 1)]);
        var requisitionId = await CreateAndApproveAsync(scenario, [(scenario.ItemIdOf("SHORT"), 4)]);

        try
        {
            var before = await GetLotQuantityAsync(scenario.ItemIdOf("SHORT"), "WEB-SHORT");
            var token = await GetDetailsTokenAsync(requisitionId);
            var response = await PostIssueAsync(requisitionId, token);
            var page = await _client.GetAsync(response.Headers.Location);
            var html = HtmlDecode(await page.Content.ReadAsStringAsync());
            var after = await GetLotQuantityAsync(scenario.ItemIdOf("SHORT"), "WEB-SHORT");

            Assert.Contains("品項", html, StringComparison.Ordinal);
            Assert.Contains("庫存不足：需要 4、目前可用 3。", html, StringComparison.Ordinal);
            Assert.Equal(3, before);
            Assert.Equal(before, after);
            Assert.Equal("Approved", await GetStatusAsync(requisitionId));

            _output.WriteLine($"T1 USER: {ExtractErrorMessage(html)}");
            _output.WriteLine($"DB: 發料前={before}，發料後={after}，STATUS=Approved");
        }
        finally
        {
            await DeleteRequisitionAsync(requisitionId);
        }
    }

    [Fact]
    public async Task Issue_pending_requisition_reports_its_actual_status()
    {
        await using var scenario = await RequisitionIssueScenario.CreateAsync(
            [new("PENDING", [("WEB-PENDING", 30, 10)], RequestQuantity: 1)],
            status: RequisitionStatus.PendingApproval);

        var details = await GetDetailsFormAsync(scenario.RequisitionId);
        var response = await PostIssueAsync(scenario.RequisitionId, details.Token);
        var page = await _client.GetAsync(response.Headers.Location);
        var html = HtmlDecode(await page.Content.ReadAsStringAsync());

        Assert.Contains("此單目前為「待審核」，無法發料。", html, StringComparison.Ordinal);
        Assert.Equal(RequisitionStatus.PendingApproval.ToString(), await GetStatusAsync(scenario.RequisitionId));
        _output.WriteLine($"T1 USER: {ExtractErrorMessage(html)}");
    }

    [Fact]
    public async Task Issue_lock_timeout_shows_retry_without_claiming_stock_is_insufficient()
    {
        await using var scenario = await RequisitionIssueScenario.CreateAsync(
            [new("LOCK", [("WEB-LOCK", 30, 10)], RequestQuantity: 1)]);
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var transactionReleased = false;

        try
        {
            _ = await connection.QueryAsync<decimal>(
                "SELECT stock_lot_id FROM stock_lots WHERE item_id = :itemId FOR UPDATE",
                new { itemId = scenario.ItemIdOf("LOCK") },
                transaction);
            var token = await GetDetailsTokenAsync(scenario.RequisitionId);
            var issueTask = PostIssueAsync(scenario.RequisitionId, token);

            // 正確實作會在 5 秒後回傳 LockTimeout；多等 1 秒才釋放鎖。
            // 若 P7 把 SELECT ... FOR UPDATE 拿掉，後續 UPDATE 會等待這把鎖，
            // 釋放後反而會錯誤地成功，讓測試明確變紅但不會把 testhost 卡住。
            await Task.Delay(TimeSpan.FromSeconds(6));
            await transaction.RollbackAsync();
            transactionReleased = true;

            var response = await issueTask;
            var page = await _client.GetAsync(response.Headers.Location);
            var html = HtmlDecode(await page.Content.ReadAsStringAsync());

            Assert.Contains("系統忙碌中，請稍後再試。", html, StringComparison.Ordinal);
            Assert.Contains(">重試發料<", html, StringComparison.Ordinal);
            Assert.DoesNotContain("庫存不足", html, StringComparison.Ordinal);
            Assert.Equal(RequisitionStatus.Approved.ToString(), await GetStatusAsync(scenario.RequisitionId));
            _output.WriteLine($"T1 USER: {ExtractErrorMessage(html)}；BUTTON=重試發料");
        }
        finally
        {
            if (!transactionReleased)
            {
                await transaction.RollbackAsync();
            }
        }
    }

    [Fact]
    public async Task Issue_rolls_back_all_three_items_when_the_third_is_insufficient()
    {
        await using var scenario = await RequisitionIssueScenario.CreateAsync(
        [
            new("A", [("WEB-A", 30, 100)], RequestQuantity: 1),
            new("B", [("WEB-B", 30, 100)], RequestQuantity: 1),
            new("C", [("WEB-C", 30, 1)], RequestQuantity: 1),
        ]);
        var requisitionId = await CreateAndApproveAsync(
            scenario,
            [(scenario.ItemIdOf("A"), 10), (scenario.ItemIdOf("B"), 20), (scenario.ItemIdOf("C"), 99)]);

        try
        {
            var before = await GetLotQuantitiesAsync(scenario);
            var token = await GetDetailsTokenAsync(requisitionId);
            var response = await PostIssueAsync(requisitionId, token);
            var page = await _client.GetAsync(response.Headers.Location);
            var html = HtmlDecode(await page.Content.ReadAsStringAsync());
            var after = await GetLotQuantitiesAsync(scenario);

            Assert.Contains("庫存不足：需要 99、目前可用 1。", html, StringComparison.Ordinal);
            Assert.Equal(before, after);
            Assert.Equal("Approved", await GetStatusAsync(requisitionId));

            _output.WriteLine($"T2 USER: {ExtractErrorMessage(html)}");
            _output.WriteLine($"T2 DB: A {before[0]}→{after[0]}；B {before[1]}→{after[1]}；C {before[2]}→{after[2]}；STATUS=Approved");
        }
        finally
        {
            await DeleteRequisitionAsync(requisitionId);
        }
    }

    private async Task<long> CreateAndApproveAsync(
        RequisitionIssueScenario scenario,
        IReadOnlyList<(long ItemId, int Quantity)> lines)
    {
        var createPage = await _client.GetAsync("/Requisitions/Create");
        var token = ExtractToken(await createPage.Content.ReadAsStringAsync());
        var values = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["AsOf"] = TestBusinessCalendar.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["DepartmentId"] = scenario.DepartmentId.ToString(CultureInfo.InvariantCulture),
        };
        for (var index = 0; index < lines.Count; index++)
        {
            values[$"Lines[{index}].ItemId"] = lines[index].ItemId.ToString(CultureInfo.InvariantCulture);
            values[$"Lines[{index}].Quantity"] = lines[index].Quantity.ToString(CultureInfo.InvariantCulture);
        }

        var createResponse = await _client.PostAsync("/Requisitions/Create", new FormUrlEncodedContent(values));
        Assert.Equal(HttpStatusCode.Redirect, createResponse.StatusCode);
        var requisitionId = ExtractId(createResponse.Headers.Location);
        var details = await GetDetailsFormAsync(requisitionId);
        var approveResponse = await PostReviewAsync("Approve", requisitionId, details.RowVersion, details.Token);
        Assert.Equal(HttpStatusCode.Redirect, approveResponse.StatusCode);
        Assert.Equal("Approved", await GetStatusAsync(requisitionId));
        return requisitionId;
    }

    private async Task<(string Token, long RowVersion)> GetDetailsFormAsync(long requisitionId)
    {
        var response = await _client.GetAsync($"/Requisitions/Details/{requisitionId}");
        var html = await response.Content.ReadAsStringAsync();
        var rowVersion = long.Parse(RowVersionRegex().Match(html).Groups[1].Value, CultureInfo.InvariantCulture);
        return (ExtractToken(html), rowVersion);
    }

    private async Task<string> GetDetailsTokenAsync(long requisitionId)
    {
        var response = await _client.GetAsync($"/Requisitions/Details/{requisitionId}");
        return ExtractToken(await response.Content.ReadAsStringAsync());
    }

    private Task<HttpResponseMessage> PostIssueAsync(long requisitionId, string token)
        => _client.PostAsync($"/Requisitions/Issue/{requisitionId}", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));

    private Task<HttpResponseMessage> PostReviewAsync(string action, long requisitionId, long rowVersion, string token)
        => _client.PostAsync($"/Requisitions/{action}/{requisitionId}", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["rowVersion"] = rowVersion.ToString(CultureInfo.InvariantCulture),
            }));

    private static async Task<int> GetLotQuantityAsync(long itemId, string lotNumber)
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        return await connection.QuerySingleAsync<int>(
            "SELECT quantity FROM stock_lots WHERE item_id = :itemId AND lot_number = :lotNumber",
            new { itemId, lotNumber });
    }

    private static Task<int[]> GetLotQuantitiesAsync(RequisitionIssueScenario scenario)
        => Task.WhenAll(
            GetLotQuantityAsync(scenario.ItemIdOf("A"), "WEB-A"),
            GetLotQuantityAsync(scenario.ItemIdOf("B"), "WEB-B"),
            GetLotQuantityAsync(scenario.ItemIdOf("C"), "WEB-C"));

    private static async Task<string> GetStatusAsync(long requisitionId)
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        return await connection.QuerySingleAsync<string>(
            "SELECT status FROM requisitions WHERE requisition_id = :requisitionId",
            new { requisitionId });
    }

    private static async Task DeleteRequisitionAsync(long requisitionId)
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync(
            "DELETE FROM audit_logs WHERE entity_type = 'Requisition' AND entity_id = TO_CHAR(:requisitionId)",
            new { requisitionId });
        await connection.ExecuteAsync("""
            DELETE FROM issue_allocations
            WHERE requisition_line_id IN (
                SELECT requisition_line_id FROM requisition_lines WHERE requisition_id = :requisitionId)
            """, new { requisitionId });
        await connection.ExecuteAsync("DELETE FROM requisition_lines WHERE requisition_id = :requisitionId", new { requisitionId });
        await connection.ExecuteAsync("DELETE FROM requisitions WHERE requisition_id = :requisitionId", new { requisitionId });
        await connection.ExecuteAsync("COMMIT");
    }

    private static string HtmlDecode(string html) => WebUtility.HtmlDecode(html);

    private static string ExtractErrorMessage(string html)
    {
        var match = ErrorAlertRegex().Match(html);
        Assert.True(match.Success, "頁面必須顯示錯誤訊息。");
        return match.Groups[1].Value;
    }

    private static string ExtractToken(string html)
    {
        var match = AntiforgeryTokenRegex().Match(html);
        Assert.True(match.Success, "頁面必須包含 AntiForgery request token。");
        return HtmlDecode(match.Groups[1].Value);
    }

    private static long ExtractId(Uri? location)
    {
        Assert.NotNull(location);
        return long.Parse(location.OriginalString.Split('/').Last(), CultureInfo.InvariantCulture);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryTokenRegex();

    [GeneratedRegex("name=\"rowVersion\" value=\"([0-9]+)\"")]
    private static partial Regex RowVersionRegex();

    [GeneratedRegex("<div class=\"alert alert-danger\" role=\"alert\">([^<]+)</div>")]
    private static partial Regex ErrorAlertRegex();

    private sealed record AllocationRow(string LotNumber, DateTime ExpiryDate, long Quantity);
}
