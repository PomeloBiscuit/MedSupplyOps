using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Dapper;
using MedSupplyOps.Domain.Requisitions;
using MedSupplyOps.Infrastructure.Time;
using MedSupplyOps.Integration.Tests.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Oracle.ManagedDataAccess.Client;
using Xunit.Abstractions;

namespace MedSupplyOps.Integration.Tests.Web;

/// <summary>發料按鈕的整合測試：從 HTTP 表單一路驗證到真實 Oracle 配批資料。</summary>
public sealed partial class RequisitionIssueWebTests
    : IClassFixture<RequisitionFlowTests.RequisitionWebApplicationFactory>, IAsyncLifetime
{
    private readonly RequisitionFlowTests.RequisitionWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly ITestOutputHelper _output;

    public RequisitionIssueWebTests(
        RequisitionFlowTests.RequisitionWebApplicationFactory factory,
        ITestOutputHelper output)
    {
        _factory = factory;
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        _output = output;
    }

    /// <summary>既有流程同時涵蓋建立、核准與發料，因此使用具完整權限的專用測試管理員。</summary>
    public Task InitializeAsync() => WebAuthTestHelpers.LoginAsync(_client, TestIdentitySeeder.AdministratorEmail);

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// ★ T1：UTC 還在前一天的邊界時段，過期批次絕不能被發出。
    ///
    /// 刻意把時間撥到 **2030 年**，而不是「今天附近」。第一版用的是 2026-09-11 03:00（台灣），
    /// 而寫下那條測試的日子**正好就是 2026-09-11** —— 假日期等於真日期，
    /// 產品用假時鐘或真時鐘配出來的批次完全一樣，測試在那一天沒有任何鑑別力（L-026）。
    ///
    /// 選一個不可能是真實今天的日期之後：
    ///   產品用**假時鐘**（台灣 2030-06-16）→ A（2030-06-15）已過期 → 只配到 B
    ///   產品用**真實時鐘**（2026 年）       → 兩批都在遙遠的未來 → FEFO 先配 A
    /// 兩者結果不同，所以這條測試不論哪一天跑都有鑑別力，
    /// 也順帶證明產品的日曆**真的**從 DI 取了時鐘（見 <see cref="TestBusinessCalendar"/> 的說明）。
    /// </summary>
    [Fact]
    public async Task Issue_uses_Taipei_today_at_the_UTC_boundary_and_never_allocates_yesterdays_lot()
    {
        var boundary = new DateTimeOffset(2030, 6, 15, 19, 0, 0, TimeSpan.Zero); // = 台灣 2030-06-16 03:00
        TestBusinessCalendar.HostClock.UtcNow = boundary;
        try
        {
            // ★ 撥動 DI 裡的時鐘，影響的**不只是業務日曆** —— ASP.NET Core 的 Cookie 驗證也用它判斷到期。
            //   建構時（2026 年）簽發的登入 Cookie 在 2030 年已經過期，不重新登入的話，
            //   下一個請求就會被導向登入頁（實際踩到：失敗訊息是「頁面必須包含 AntiForgery token」，
            //   完全不像是時間的問題）。所以在撥好時鐘之後，於同一個時間重新登入一次。
            //   xUnit 每個測試方法都會建新的類別實例，這個 client 只屬於本測試，不影響其他測試。
            await WebAuthTestHelpers.LoginAsync(_client, TestIdentitySeeder.AdministratorEmail);

            await using var scenario = await RequisitionIssueScenario.CreateAsync(
                [new("BOUNDARY", [("BOUNDARY-A", 0, 10), ("BOUNDARY-B", 0, 10)], RequestQuantity: 1)]);
            await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
            await connection.ExecuteAsync(
                "UPDATE stock_lots SET expiry_date = DATE '2030-06-15' WHERE lot_number = 'BOUNDARY-A'");
            await connection.ExecuteAsync(
                "UPDATE stock_lots SET expiry_date = DATE '2030-06-16' WHERE lot_number = 'BOUNDARY-B'");
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

                // ★ 斷言的是**產品 DI 裡的那個日曆**，不是測試輔助類別自己算的日期。
                var productToday = _factory.Services.GetRequiredService<BusinessCalendar>().Today;
                Assert.Equal(new DateOnly(2030, 6, 16), productToday);
                Assert.Equal(["BOUNDARY-B"], allocatedLots);
                _output.WriteLine($"T1 產品的 BusinessCalendar.Today={productToday:yyyy-MM-dd}（UTC {boundary:O}）");
                _output.WriteLine($"T1 配到的批次={string.Join(",", allocatedLots)}（BOUNDARY-A 已過期，一個都不能出）");
            }
            finally
            {
                await DeleteRequisitionAsync(requisitionId);
            }
        }
        finally
        {
            TestBusinessCalendar.HostClock.UtcNow = TestBusinessCalendar.DefaultInstant;
        }
    }

    /// <summary>
    /// ★ T3：畫面上的時間必須是業務時區（台灣），不是資料庫存的 UTC。
    ///
    /// 儲存時間戳來自主機的 UTC 時鐘，測試無法用假時鐘控制它（這正是這一題原本做不下去的原因）。
    /// 所以直接把資料庫裡的時間戳改成一個已知的 UTC 值，再看畫面怎麼顯示 ——
    /// 這一題要驗的本來就只是「顯示那一步有沒有轉換」。
    /// </summary>
    [Fact]
    public async Task Details_page_shows_times_in_Taipei_not_utc()
    {
        await using var scenario = await RequisitionIssueScenario.CreateAsync(
            [new("DISPLAY", [("DISPLAY-A", 30, 5)], RequestQuantity: 1)]);
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        var requisitionId = await CreateAndApproveAsync(scenario, [(scenario.ItemIdOf("DISPLAY"), 1)]);

        try
        {
            // UTC 2026-09-11 01:12:34 = 台灣 2026-09-11 09:12:34
            await connection.ExecuteAsync("""
                UPDATE requisitions
                SET created_at = TIMESTAMP '2026-09-11 01:12:34',
                    submitted_at = TIMESTAMP '2026-09-11 01:12:34'
                WHERE requisition_id = :requisitionId
                """, new { requisitionId });

            var html = await (await _client.GetAsync($"/Requisitions/Details/{requisitionId}"))
                .Content.ReadAsStringAsync();

            Assert.Contains("2026-09-11 09:12:34", html, StringComparison.Ordinal);
            Assert.DoesNotContain("2026-09-11 01:12:34", html, StringComparison.Ordinal);
            _output.WriteLine("T3 資料庫存 UTC 2026-09-11 01:12:34 → 畫面顯示 2026-09-11 09:12:34");
        }
        finally
        {
            await DeleteRequisitionAsync(requisitionId);
        }
    }

    /// <summary>
    /// ★ T4：請領單號的日期部分是業務日期。UTC 還停在前一天的時段，單號不可以是前一天。
    /// 同樣撥到 2030 年，理由見 T1：固定在「今天附近」的假日期，會在剛好等於真實日期的那一天失去鑑別力。
    /// </summary>
    [Fact]
    public async Task Requisition_number_uses_Taipei_date_at_the_utc_boundary()
    {
        var boundary = new DateTimeOffset(2030, 6, 15, 19, 0, 0, TimeSpan.Zero); // = 台灣 2030-06-16 03:00
        TestBusinessCalendar.HostClock.UtcNow = boundary;
        try
        {
            await WebAuthTestHelpers.LoginAsync(_client, TestIdentitySeeder.AdministratorEmail); // 見 T1 的說明
            await using var scenario = await RequisitionIssueScenario.CreateAsync(
                [new("NUMBER", [("NUMBER-A", 0, 5)], RequestQuantity: 1)]);
            await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
            await connection.ExecuteAsync(
                "UPDATE stock_lots SET expiry_date = DATE '2031-01-01' WHERE lot_number = 'NUMBER-A'");
            var requisitionId = await CreateAndApproveAsync(scenario, [(scenario.ItemIdOf("NUMBER"), 1)]);

            try
            {
                var number = await connection.QuerySingleAsync<string>(
                    "SELECT requisition_no FROM requisitions WHERE requisition_id = :requisitionId",
                    new { requisitionId });

                Assert.StartsWith("REQ-20300616-", number, StringComparison.Ordinal);
                _output.WriteLine($"T4 UTC {boundary:O}（仍是 06-15）→ 單號 {number}（台灣 06-16）");
            }
            finally
            {
                await DeleteRequisitionAsync(requisitionId);
            }
        }
        finally
        {
            TestBusinessCalendar.HostClock.UtcNow = TestBusinessCalendar.DefaultInstant;
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
