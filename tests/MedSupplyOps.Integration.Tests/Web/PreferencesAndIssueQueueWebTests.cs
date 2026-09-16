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

/// <summary>顯示時區、i18n、待發料佇列與即時可用量的驗收案例。</summary>
public sealed partial class PreferencesAndIssueQueueWebTests : IClassFixture<RequisitionFlowTests.RequisitionWebApplicationFactory>
{
    private readonly RequisitionFlowTests.RequisitionWebApplicationFactory _factory;
    private readonly ITestOutputHelper _output;

    public PreferencesAndIssueQueueWebTests(
        RequisitionFlowTests.RequisitionWebApplicationFactory factory,
        ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [Fact]
    public async Task B_T1_UTC_display_changes_list_time_but_not_business_date_or_FEFO()
    {
        var suffix = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var displayNo = "itest-vb1-time-" + suffix;
        long createdRequisitionId = 0;

        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.OpenAsync();
        var departmentId = await connection.ExecuteScalarAsync<long>(
            "SELECT department_id FROM departments WHERE department_code = :code",
            new { code = TestIdentitySeeder.RequesterDepartmentCode });
        var itemId = await connection.ExecuteScalarAsync<long>(
            "SELECT item_id FROM items WHERE is_deleted = 0 ORDER BY item_id FETCH FIRST 1 ROWS ONLY");
        await connection.ExecuteAsync(
            """
            INSERT INTO requisitions
                (requisition_no, department_id, status, created_at, created_by)
            VALUES
                (:no, :departmentId, 'PendingApproval', TIMESTAMP '2030-06-30 16:30:00', 'itest')
            """,
            new { no = displayNo, departmentId });

        try
        {
            using var client = CreateHttpsClient();
            await WebAuthTestHelpers.LoginAsync(client, TestIdentitySeeder.AdministratorEmail);

            var taipeiHtml = await GetHtmlAsync(client, "/Requisitions");
            Assert.Contains("2030-07-01 00:30:00", taipeiHtml, StringComparison.Ordinal);

            await SetPreferenceAsync(client, "TimeZone", "timeZone", "UTC", "/Requisitions");
            var utcHtml = await GetHtmlAsync(client, "/Requisitions");
            Assert.Contains("2030-06-30 16:30:00", utcHtml, StringComparison.Ordinal);
            Assert.DoesNotContain("2030-07-01 00:30:00", utcHtml, StringComparison.Ordinal);

            var createHtml = await GetHtmlAsync(client, "/Requisitions/Create");
            var createResponse = await client.PostAsync("/Requisitions/Create", new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["__RequestVerificationToken"] = ExtractToken(createHtml),
                    ["DepartmentId"] = departmentId.ToString(CultureInfo.InvariantCulture),
                    ["AsOf"] = TestBusinessCalendar.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ["Lines[0].ItemId"] = itemId.ToString(CultureInfo.InvariantCulture),
                    ["Lines[0].Quantity"] = "1",
                }));
            Assert.Equal(HttpStatusCode.Redirect, createResponse.StatusCode);
            createdRequisitionId = ExtractId(createResponse.Headers.Location);
            var generatedNo = await connection.ExecuteScalarAsync<string>(
                "SELECT requisition_no FROM requisitions WHERE requisition_id = :id",
                new { id = createdRequisitionId });
            Assert.StartsWith($"REQ-{TestBusinessCalendar.Today:yyyyMMdd}-", generatedNo, StringComparison.Ordinal);

            var earlyLot = "ITEST-BT1-E-" + suffix;
            var lateLot = "ITEST-BT1-L-" + suffix;
            await using var scenario = await RequisitionIssueScenario.CreateAsync(
            [
                new RequisitionItemSpec("A", [(earlyLot, 20, 2), (lateLot, 40, 2)], 2),
            ]);
            var detailsHtml = await GetHtmlAsync(client, $"/Requisitions/Details/{scenario.RequisitionId}");
            var issueResponse = await client.PostAsync(
                $"/Requisitions/Issue/{scenario.RequisitionId}",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["__RequestVerificationToken"] = ExtractToken(detailsHtml),
                }));
            Assert.Equal(HttpStatusCode.Redirect, issueResponse.StatusCode);
            Assert.Equal(0, await scenario.GetLotQuantityAsync(earlyLot));
            Assert.Equal(2, await scenario.GetLotQuantityAsync(lateLot));

            _output.WriteLine("B-T1 顯示時區：Asia/Taipei=2030-07-01 00:30:00；UTC=2030-06-30 16:30:00。");
            _output.WriteLine($"B-T1 單號日期：UTC 顯示偏好下仍產生 {generatedNo}，日期取 BusinessCalendar.Today={TestBusinessCalendar.Today:yyyy-MM-dd}。");
            _output.WriteLine($"B-T1 FEFO：先扣 {earlyLot}=0，後效期 {lateLot}=2；顯示時區未改變配批。");
        }
        finally
        {
            if (createdRequisitionId != 0)
            {
                await DeleteRequisitionAsync(connection, createdRequisitionId);
            }

            await connection.ExecuteAsync("DELETE FROM requisitions WHERE requisition_no = :no", new { no = displayNo });
            await connection.ExecuteAsync("COMMIT");
        }
    }

    [Fact]
    public async Task B_T2_English_localizes_navigation_buttons_status_and_validation_but_not_data()
    {
        using var client = CreateHttpsClient();
        await WebAuthTestHelpers.LoginAsync(client, TestIdentitySeeder.AdministratorEmail);
        await SetPreferenceAsync(client, "Culture", "culture", "en", "/Requisitions/Create");

        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        var itemName = await connection.QuerySingleAsync<string>(
            "SELECT item_name FROM items WHERE is_deleted = 0 ORDER BY item_id FETCH FIRST 1 ROWS ONLY");

        var englishHtml = await GetHtmlAsync(client, "/Requisitions/Create");
        Assert.Contains(">Home<", englishHtml, StringComparison.Ordinal);
        Assert.Contains("Create requisition", englishHtml, StringComparison.Ordinal);
        Assert.Contains("Submit requisition", englishHtml, StringComparison.Ordinal);
        Assert.Contains(itemName, englishHtml, StringComparison.Ordinal);
        // Y2：偏好選單的語言只剩繁體中文與 English 兩個可選項，佔位用的 disabled 日文選項已移除。
        // 斷言「沒有任何 disabled 語言選項」而不是釘住 ja——這樣未來有人再加一個佔位選項，這條會紅。
        var languageMenu = englishHtml[englishHtml.IndexOf("name=\"culture\"", StringComparison.Ordinal)..];
        languageMenu = languageMenu[..languageMenu.IndexOf("</select>", StringComparison.Ordinal)];
        Assert.DoesNotContain("disabled", languageMenu, StringComparison.Ordinal);
        Assert.Contains("value=\"zh-Hant\"", languageMenu, StringComparison.Ordinal);
        Assert.Contains("value=\"en\"", languageMenu, StringComparison.Ordinal);
        Assert.Equal(2, languageMenu.Split("<option", StringSplitOptions.None).Length - 1);
        var passwordHtml = await GetHtmlAsync(client, "/Account/ChangePassword");
        Assert.Contains("Current password", passwordHtml, StringComparison.Ordinal);
        Assert.Contains("The new password must be at least 12 characters long.", passwordHtml, StringComparison.Ordinal);

        var invalidResponse = await client.PostAsync("/Requisitions/Create", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = ExtractToken(englishHtml),
                ["DepartmentId"] = "0",
                ["AsOf"] = TestBusinessCalendar.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["Lines[0].ItemId"] = "0",
                ["Lines[0].Quantity"] = "0",
            }));
        var invalidHtml = WebUtility.HtmlDecode(await invalidResponse.Content.ReadAsStringAsync());
        Assert.Contains("Select a department.", invalidHtml, StringComparison.Ordinal);
        Assert.Contains("Select an item.", invalidHtml, StringComparison.Ordinal);

        var requisitionsHtml = await GetHtmlAsync(client, "/Requisitions");
        Assert.Contains("Pending approval", requisitionsHtml, StringComparison.Ordinal);

        await SetPreferenceAsync(client, "Culture", "culture", "zh-Hant", "/");
        var chineseHtml = await GetHtmlAsync(client, "/");
        Assert.Contains(">首頁<", chineseHtml, StringComparison.Ordinal);

        _output.WriteLine($"B-T2 English：導覽=Home、按鈕=Submit requisition、狀態=Pending approval、驗證=Select a department.；資料品名仍為「{itemName}」。");
        _output.WriteLine("B-T2 日本語顯示為 disabled；切回 zh-Hant 後導覽顯示「首頁」。");
    }

    [Fact]
    public async Task B_T3_issue_queue_orders_by_approval_time_and_is_hidden_from_requesters()
    {
        var suffix = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var departmentCode = "itest-vb3-dept-" + suffix;
        var departmentName = "itest 科室 " + suffix;
        var earlyNo = "itest-vb3-early-" + suffix;
        var middleNo = "itest-vb3-middle-" + suffix;
        var lateNo = "itest-vb3-late-" + suffix;

        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync(
            "INSERT INTO departments (department_code, department_name, created_by) VALUES (:code, :name, 'itest')",
            new { code = departmentCode, name = departmentName });
        var departmentId = await connection.ExecuteScalarAsync<long>(
            "SELECT department_id FROM departments WHERE department_code = :code", new { code = departmentCode });
        var itemId = await connection.ExecuteScalarAsync<long>(
            "SELECT item_id FROM items WHERE is_deleted = 0 ORDER BY item_id FETCH FIRST 1 ROWS ONLY");

        try
        {
            foreach (var row in new[]
            {
                new { No = middleNo, ApprovedAt = new DateTime(2030, 1, 2, 10, 0, 0) },
                new { No = lateNo, ApprovedAt = new DateTime(2030, 1, 2, 11, 0, 0) },
                new { No = earlyNo, ApprovedAt = new DateTime(2030, 1, 2, 9, 0, 0) },
            })
            {
                await connection.ExecuteAsync(
                    """
                    INSERT INTO requisitions
                        (requisition_no, department_id, status, approved_at, created_by)
                    VALUES (:No, :departmentId, 'Approved', :ApprovedAt, 'itest')
                    """,
                    new { row.No, departmentId, row.ApprovedAt });
                var requisitionId = await connection.ExecuteScalarAsync<long>(
                    "SELECT requisition_id FROM requisitions WHERE requisition_no = :no", new { no = row.No });
                await connection.ExecuteAsync(
                    "INSERT INTO requisition_lines (requisition_id, line_no, item_id, quantity) VALUES (:id, 1, :itemId, 1)",
                    new { id = requisitionId, itemId });
            }

            using var keeperClient = CreateHttpsClient();
            await WebAuthTestHelpers.LoginAsync(keeperClient, TestIdentitySeeder.StorekeeperEmail);
            var keeperHtml = await GetHtmlAsync(keeperClient, "/");
            var earlyIndex = keeperHtml.IndexOf(earlyNo, StringComparison.Ordinal);
            var middleIndex = keeperHtml.IndexOf(middleNo, StringComparison.Ordinal);
            var lateIndex = keeperHtml.IndexOf(lateNo, StringComparison.Ordinal);
            Assert.True(earlyIndex >= 0 && earlyIndex < middleIndex && middleIndex < lateIndex);
            Assert.Contains("data-testid=\"approved-issue-queue\"", keeperHtml, StringComparison.Ordinal);
            Assert.DoesNotContain("品項管理", keeperHtml, StringComparison.Ordinal);

            using var requesterClient = CreateHttpsClient();
            await WebAuthTestHelpers.LoginAsync(requesterClient, TestIdentitySeeder.RequesterEmail);
            var requesterHtml = await GetHtmlAsync(requesterClient, "/");
            Assert.DoesNotContain("data-testid=\"approved-issue-queue\"", requesterHtml, StringComparison.Ordinal);
            Assert.DoesNotContain(earlyNo, requesterHtml, StringComparison.Ordinal);

            _output.WriteLine($"B-T3 庫管員佇列實際順序：{earlyNo}（09:00）→ {middleNo}（10:00）→ {lateNo}（11:00）。");
            _output.WriteLine("B-T3 請領人首頁沒有 approved-issue-queue，也沒有上述跨科室單號；庫管員導覽沒有品項管理。");
        }
        finally
        {
            await connection.ExecuteAsync(
                "DELETE FROM requisition_lines WHERE requisition_id IN (SELECT requisition_id FROM requisitions WHERE requisition_no IN (:a, :b, :c))",
                new { a = earlyNo, b = middleNo, c = lateNo });
            await connection.ExecuteAsync(
                "DELETE FROM requisitions WHERE requisition_no IN (:a, :b, :c)",
                new { a = earlyNo, b = middleNo, c = lateNo });
            await connection.ExecuteAsync("DELETE FROM departments WHERE department_id = :id", new { id = departmentId });
            await connection.ExecuteAsync("COMMIT");
        }
    }

    [Fact]
    public async Task B_T5_insufficient_reference_does_not_block_submit_and_server_rechecks_on_issue()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var itemCode = "ITESTVB5" + suffix;
        long requisitionId = 0;

        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.OpenAsync();
        var departmentId = await connection.ExecuteScalarAsync<long>(
            "SELECT department_id FROM departments WHERE department_code = :code",
            new { code = TestIdentitySeeder.RequesterDepartmentCode });
        await connection.ExecuteAsync(
            """
            INSERT INTO items (item_code, item_name, unit_of_measure, safety_stock_qty, created_by)
            VALUES (:code, :name, '個', 0, 'itest')
            """,
            new { code = itemCode, name = "itest 零庫存品項 " + suffix });
        var itemId = await connection.ExecuteScalarAsync<long>(
            "SELECT item_id FROM items WHERE item_code = :code", new { code = itemCode });

        try
        {
            using var client = CreateHttpsClient();
            await WebAuthTestHelpers.LoginAsync(client, TestIdentitySeeder.AdministratorEmail);

            var availability = await client.GetStringAsync($"/api/items/{itemId}/availability?asOf={TestBusinessCalendar.Today:yyyy-MM-dd}");
            Assert.Contains("\"availableQuantity\":0", availability, StringComparison.Ordinal);

            var createHtml = await GetHtmlAsync(client, "/Requisitions/Create");
            var createResponse = await client.PostAsync("/Requisitions/Create", new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["__RequestVerificationToken"] = ExtractToken(createHtml),
                    ["DepartmentId"] = departmentId.ToString(CultureInfo.InvariantCulture),
                    ["AsOf"] = TestBusinessCalendar.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ["Lines[0].ItemId"] = itemId.ToString(CultureInfo.InvariantCulture),
                    ["Lines[0].Quantity"] = "1",
                }));
            Assert.Equal(HttpStatusCode.Redirect, createResponse.StatusCode);
            requisitionId = ExtractId(createResponse.Headers.Location);

            var detailsHtml = await GetHtmlAsync(client, $"/Requisitions/Details/{requisitionId}");
            var approveResponse = await client.PostAsync(
                $"/Requisitions/Approve/{requisitionId}",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["__RequestVerificationToken"] = ExtractToken(detailsHtml),
                    ["rowVersion"] = ExtractRowVersion(detailsHtml).ToString(CultureInfo.InvariantCulture),
                }));
            Assert.Equal(HttpStatusCode.Redirect, approveResponse.StatusCode);

            detailsHtml = await GetHtmlAsync(client, $"/Requisitions/Details/{requisitionId}");
            var issueResponse = await client.PostAsync(
                $"/Requisitions/Issue/{requisitionId}",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["__RequestVerificationToken"] = ExtractToken(detailsHtml),
                }));
            Assert.Equal(HttpStatusCode.Redirect, issueResponse.StatusCode);
            var failedIssueHtml = await GetHtmlAsync(client, issueResponse.Headers.Location!.OriginalString);
            Assert.Contains("庫存不足：需要 1、目前可用 0。", failedIssueHtml, StringComparison.Ordinal);
            var status = await connection.ExecuteScalarAsync<string>(
                "SELECT status FROM requisitions WHERE requisition_id = :id", new { id = requisitionId });
            Assert.Equal(RequisitionStatus.Approved.ToString(), status);

            _output.WriteLine("B-T5 即時 API 回傳 availableQuantity=0；數量 1 仍成功送出請領單（HTTP 302）。");
            _output.WriteLine("B-T5 核准後發料由伺服器最終檢查，顯示「庫存不足：需要 1、目前可用 0。」且狀態維持 Approved。");
        }
        finally
        {
            if (requisitionId != 0)
            {
                await DeleteRequisitionAsync(connection, requisitionId);
            }

            await connection.ExecuteAsync("DELETE FROM items WHERE item_code = :code", new { code = itemCode });
            await connection.ExecuteAsync("COMMIT");
        }
    }

    private HttpClient CreateHttpsClient() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        BaseAddress = new Uri("https://localhost"),
    });

    private static async Task<string> GetHtmlAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    private static async Task SetPreferenceAsync(
        HttpClient client,
        string action,
        string fieldName,
        string value,
        string returnUrl)
    {
        var html = await GetHtmlAsync(client, returnUrl);
        var response = await client.PostAsync($"/Preferences/{action}", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = ExtractToken(html),
                [fieldName] = value,
                ["returnUrl"] = returnUrl,
            }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private static async Task DeleteRequisitionAsync(OracleConnection connection, long requisitionId)
    {
        await connection.ExecuteAsync(
            "DELETE FROM audit_logs WHERE entity_type = 'Requisition' AND entity_id = TO_CHAR(:id)",
            new { id = requisitionId });
        await connection.ExecuteAsync(
            "DELETE FROM issue_allocations WHERE requisition_line_id IN (SELECT requisition_line_id FROM requisition_lines WHERE requisition_id = :id)",
            new { id = requisitionId });
        await connection.ExecuteAsync("DELETE FROM requisition_lines WHERE requisition_id = :id", new { id = requisitionId });
        await connection.ExecuteAsync("DELETE FROM requisitions WHERE requisition_id = :id", new { id = requisitionId });
    }

    private static string ExtractToken(string html)
    {
        var match = AntiforgeryTokenRegex().Match(html);
        Assert.True(match.Success, "頁面必須包含 AntiForgery token。");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static long ExtractRowVersion(string html)
    {
        var match = RowVersionRegex().Match(html);
        Assert.True(match.Success, "詳情頁必須包含 rowVersion。");
        return long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
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
}
