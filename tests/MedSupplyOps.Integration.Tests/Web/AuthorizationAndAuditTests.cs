using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Oracle.ManagedDataAccess.Client;
using Xunit.Abstractions;

namespace MedSupplyOps.Integration.Tests.Web;

public sealed partial class AuthorizationAndAuditTests
    : IClassFixture<RequisitionFlowTests.RequisitionWebApplicationFactory>, IAsyncLifetime
{
    private readonly RequisitionFlowTests.RequisitionWebApplicationFactory _factory;
    private readonly ITestOutputHelper _output;

    public AuthorizationAndAuditTests(
        RequisitionFlowTests.RequisitionWebApplicationFactory factory,
        ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    /// <summary>
    /// ★ 強制 Host 先建起來，測試專用帳號才會存在於資料庫。
    ///
    /// <see cref="WebApplicationFactory{TEntryPoint}"/> 是**惰性**的：
    /// 不碰 <c>CreateClient()</c> 或 <c>Services</c> 就不會建 Host，
    /// 而 <c>itest-*</c> 帳號是在 Host 啟動時由 <see cref="TestIdentitySeeder"/> 種進去的。
    ///
    /// 本類別有測試會**先直連資料庫查那些帳號、之後才 CreateClient()**
    /// （見 <c>GetRequesterDepartmentIdAsync</c>）。少了這一行，
    /// 在**全新的資料庫**上那個查詢會回傳 0 列並丟出
    /// <c>Sequence contains no elements</c> ——
    /// 而在跑過幾輪的資料庫上它會通過，因為帳號是前幾輪留下來的。
    ///
    /// 也就是「測試通過只因為環境有殘留」（踩坑紀錄 L-023）。
    /// 其他四個 Web 測試類別都有 <c>IAsyncLifetime</c>，
    /// 它們在 <c>InitializeAsync</c> 裡登入，順帶就把 Host 建起來了；
    /// 只有這一個類別漏掉，於是沒有任何東西保證順序。
    /// </summary>
    public Task InitializeAsync()
    {
        _ = _factory.Services;
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Anonymous_page_redirects_but_api_returns_401_and_login_ends_the_redirect_chain()
    {
        using var client = CreateClient();

        var home = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/"));
        Assert.Equal(HttpStatusCode.Redirect, home.StatusCode);
        Assert.Equal("/Account/Login", home.Headers.Location?.AbsolutePath);

        var login = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, home.Headers.Location));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Null(login.Headers.Location);

        var api = await client.GetAsync("/api/inventory/expiring");
        var apiBody = await api.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);
        Assert.True(string.IsNullOrEmpty(apiBody) || api.Content.Headers.ContentType?.MediaType == "application/json");

        using var authenticated = CreateClient();
        await WebAuthTestHelpers.LoginAsync(authenticated, TestIdentitySeeder.RequesterEmail);
        var authenticatedApi = await authenticated.GetAsync("/api/inventory/expiring");
        Assert.Equal(HttpStatusCode.OK, authenticatedApi.StatusCode);

        _output.WriteLine($"REDIRECT 1: / -> {(int)home.StatusCode} {home.Headers.Location}");
        _output.WriteLine($"REDIRECT 2: {home.Headers.Location?.AbsolutePath} -> {(int)login.StatusCode}; Location=<none>");
        _output.WriteLine($"API anonymous: HTTP {(int)api.StatusCode}; body[0..200]={apiBody[..Math.Min(200, apiBody.Length)]}");
        _output.WriteLine($"API authenticated: HTTP {(int)authenticatedApi.StatusCode}");
    }

    [Fact]
    public async Task Requester_cannot_review_or_issue()
    {
        var item = await CreateTestItemAsync(10);
        long requisitionId = 0;
        try
        {
            using var requester = CreateClient();
            using var keeper = CreateClient();
            await WebAuthTestHelpers.LoginAsync(requester, TestIdentitySeeder.RequesterEmail);
            await WebAuthTestHelpers.LoginAsync(keeper, TestIdentitySeeder.StorekeeperEmail);
            var departmentId = await GetRequesterDepartmentIdAsync();
            requisitionId = await CreateRequisitionAsync(requester, departmentId, item.ItemId, 1);

            var requesterToken = await GetPageTokenAsync(requester, requisitionId);
            var approveDenied = await PostReviewAsync(
                requester, "Approve", requisitionId, 0, requesterToken);
            AssertAccessDenied(approveDenied);

            var keeperForm = await GetDetailsFormAsync(keeper, requisitionId);
            var approved = await PostReviewAsync(
                keeper, "Approve", requisitionId, keeperForm.RowVersion, keeperForm.Token);
            Assert.Equal(HttpStatusCode.Redirect, approved.StatusCode);

            var issueToken = await GetPageTokenAsync(requester, requisitionId);
            var issueDenied = await requester.PostAsync(
                $"/Requisitions/Issue/{requisitionId}",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["__RequestVerificationToken"] = issueToken,
                }));
            AssertAccessDenied(issueDenied);

            _output.WriteLine($"Requester Approve: HTTP {(int)approveDenied.StatusCode}");
            _output.WriteLine($"Requester Issue: HTTP {(int)issueDenied.StatusCode}");
        }
        finally
        {
            await DeleteTestDataAsync([requisitionId], item);
        }
    }

    [Fact]
    public async Task Requester_direct_url_to_another_department_is_forbidden()
    {
        var requesterDepartmentId = await GetRequesterDepartmentIdAsync();
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.OpenAsync();
        var otherDepartmentId = await connection.QuerySingleAsync<long>(
            "SELECT MIN(department_id) FROM departments WHERE department_id <> :requesterDepartmentId AND is_active = 1 AND is_deleted = 0",
            new { requesterDepartmentId });
        var number = "R-T5-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        await connection.ExecuteAsync(
            "INSERT INTO requisitions (requisition_no, department_id, status, created_by) VALUES (:numberValue, :otherDepartmentId, 'PendingApproval', 'itest-t5')",
            new { numberValue = number, otherDepartmentId });
        var requisitionId = await connection.QuerySingleAsync<long>(
            "SELECT requisition_id FROM requisitions WHERE requisition_no = :numberValue",
            new { numberValue = number });

        try
        {
            using var requester = CreateClient();
            await WebAuthTestHelpers.LoginAsync(requester, TestIdentitySeeder.RequesterEmail);
            var list = await requester.GetAsync("/Requisitions");
            var listHtml = await list.Content.ReadAsStringAsync();
            var response = await requester.GetAsync($"/Requisitions/Details/{requisitionId}");

            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            Assert.DoesNotContain(number, listHtml, StringComparison.Ordinal);
            AssertAccessDenied(response);
            _output.WriteLine(
                $"A department {requesterDepartmentId} -> B department {otherDepartmentId}, list contains B=false; direct Details/{requisitionId}: HTTP {(int)response.StatusCode} Location={response.Headers.Location}");
        }
        finally
        {
            await DeleteTestDataAsync([requisitionId], item: null);
        }
    }

    [Fact]
    public async Task Cross_role_flow_records_all_four_actions_with_actual_actors()
    {
        var item = await CreateTestItemAsync(20);
        var requisitionIds = new List<long>();
        try
        {
            using var requester = CreateClient();
            using var keeper = CreateClient();
            await WebAuthTestHelpers.LoginAsync(requester, TestIdentitySeeder.RequesterEmail);
            await WebAuthTestHelpers.LoginAsync(keeper, TestIdentitySeeder.StorekeeperEmail);
            var departmentId = await GetRequesterDepartmentIdAsync();

            var issuedId = await CreateRequisitionAsync(requester, departmentId, item.ItemId, 3);
            requisitionIds.Add(issuedId);
            var approveForm = await GetDetailsFormAsync(keeper, issuedId);
            Assert.Equal(
                HttpStatusCode.Redirect,
                (await PostReviewAsync(keeper, "Approve", issuedId, approveForm.RowVersion, approveForm.Token)).StatusCode);
            var issueToken = await GetPageTokenAsync(keeper, issuedId);
            Assert.Equal(
                HttpStatusCode.Redirect,
                (await keeper.PostAsync(
                    $"/Requisitions/Issue/{issuedId}",
                    new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["__RequestVerificationToken"] = issueToken,
                    }))).StatusCode);

            var rejectedId = await CreateRequisitionAsync(requester, departmentId, item.ItemId, 1);
            requisitionIds.Add(rejectedId);
            var rejectForm = await GetDetailsFormAsync(keeper, rejectedId);
            Assert.Equal(
                HttpStatusCode.Redirect,
                (await PostReviewAsync(
                    keeper, "Reject", rejectedId, rejectForm.RowVersion, rejectForm.Token, "測試駁回原因")).StatusCode);

            await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
            var logs = (await connection.QueryAsync<AuditRow>("""
                SELECT entity_type AS "EntityType",
                       entity_id AS "EntityId",
                       action AS "Action",
                       actor AS "Actor",
                       occurred_at AS "OccurredAt",
                       new_value AS "NewValue"
                FROM audit_logs
                WHERE entity_type = 'Requisition' AND entity_id IN :entityIds
                ORDER BY audit_log_id
                """, new { entityIds = requisitionIds.Select(id => id.ToString(CultureInfo.InvariantCulture)).ToList() })).ToList();

            Assert.Equal(5, logs.Count);
            Assert.Equal(["Create", "Approve", "Issue", "Create", "Reject"], logs.Select(log => log.Action));
            Assert.Equal(TestIdentitySeeder.RequesterEmail, logs[0].Actor);
            Assert.Equal(TestIdentitySeeder.StorekeeperEmail, logs[1].Actor);
            Assert.Equal(TestIdentitySeeder.StorekeeperEmail, logs[2].Actor);
            Assert.Equal(TestIdentitySeeder.RequesterEmail, logs[3].Actor);
            Assert.Equal(TestIdentitySeeder.StorekeeperEmail, logs[4].Actor);
            Assert.Contains("allocations", logs.Single(log => log.Action == "Issue").NewValue, StringComparison.Ordinal);

            foreach (var log in logs)
            {
                _output.WriteLine(
                    $"{log.EntityType}|{log.EntityId}|{log.Action}|{log.Actor}|{log.OccurredAt:O}");
            }

            _output.WriteLine("Issue new_value=" + logs.Single(log => log.Action == "Issue").NewValue);
        }
        finally
        {
            await DeleteTestDataAsync(requisitionIds, item);
        }
    }

    [Fact]
    public async Task Insufficient_issue_rolls_back_the_audit_insert_with_the_stock_changes()
    {
        var item = await CreateTestItemAsync(1);
        long requisitionId = 0;
        try
        {
            using var requester = CreateClient();
            using var keeper = CreateClient();
            await WebAuthTestHelpers.LoginAsync(requester, TestIdentitySeeder.RequesterEmail);
            await WebAuthTestHelpers.LoginAsync(keeper, TestIdentitySeeder.StorekeeperEmail);
            var departmentId = await GetRequesterDepartmentIdAsync();
            requisitionId = await CreateRequisitionAsync(requester, departmentId, item.ItemId, 2);
            var approveForm = await GetDetailsFormAsync(keeper, requisitionId);
            _ = await PostReviewAsync(keeper, "Approve", requisitionId, approveForm.RowVersion, approveForm.Token);

            var before = await CountAuditLogsAsync(requisitionId);
            var issueToken = await GetPageTokenAsync(keeper, requisitionId);
            var response = await keeper.PostAsync(
                $"/Requisitions/Issue/{requisitionId}",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["__RequestVerificationToken"] = issueToken,
                }));
            var after = await CountAuditLogsAsync(requisitionId);

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal(2, before);
            Assert.Equal(before, after);
            _output.WriteLine($"Insufficient issue AUDIT_LOGS: before={before}, after={after}");
        }
        finally
        {
            await DeleteTestDataAsync([requisitionId], item);
        }
    }

    private HttpClient CreateClient()
        => _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static void AssertAccessDenied(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            return;
        }

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Account/AccessDenied", response.Headers.Location?.AbsolutePath);
    }

    private static async Task<long> GetRequesterDepartmentIdAsync()
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        return await connection.QuerySingleAsync<long>(
            "SELECT department_id FROM identity_users WHERE normalized_user_name = :name",
            new { name = TestIdentitySeeder.RequesterEmail.ToUpperInvariant() });
    }

    private static async Task<TestItem> CreateTestItemAsync(int quantity)
    {
        var suffix = Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync("""
            INSERT INTO items (item_code, item_name, unit_of_measure, safety_stock_qty, created_by)
            VALUES (:code, :name, '件', 0, 'itest-h2')
            """, new { code = "H2-" + suffix, name = "測試品項 " + suffix });
        var itemId = await connection.QuerySingleAsync<long>(
            "SELECT item_id FROM items WHERE item_code = :code", new { code = "H2-" + suffix });
        await connection.ExecuteAsync("""
            INSERT INTO stock_lots (item_id, lot_number, expiry_date, quantity, storage_location, created_by)
            VALUES (:itemId, :lot, TRUNC(SYSDATE) + 30, :quantity, 'ITEST-H2', 'itest-h2')
            """, new { itemId, lot = "H2L-" + suffix, quantity });
        return new TestItem(itemId);
    }

    private static async Task<long> CreateRequisitionAsync(
        HttpClient requester,
        long departmentId,
        long itemId,
        int quantity)
    {
        var page = await requester.GetAsync("/Requisitions/Create");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var token = ExtractToken(await page.Content.ReadAsStringAsync());
        var response = await requester.PostAsync(
            "/Requisitions/Create",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["AsOf"] = DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["DepartmentId"] = departmentId.ToString(CultureInfo.InvariantCulture),
                ["Lines[0].ItemId"] = itemId.ToString(CultureInfo.InvariantCulture),
                ["Lines[0].Quantity"] = quantity.ToString(CultureInfo.InvariantCulture),
            }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return long.Parse(response.Headers.Location!.OriginalString.Split('/').Last(), CultureInfo.InvariantCulture);
    }

    private static async Task<(string Token, long RowVersion)> GetDetailsFormAsync(
        HttpClient client,
        long requisitionId)
    {
        var response = await client.GetAsync($"/Requisitions/Details/{requisitionId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        var rowVersionMatch = RowVersionRegex().Match(html);
        Assert.True(rowVersionMatch.Success);
        return (ExtractToken(html), long.Parse(rowVersionMatch.Groups[1].Value, CultureInfo.InvariantCulture));
    }

    private static async Task<string> GetPageTokenAsync(HttpClient client, long requisitionId)
    {
        var response = await client.GetAsync($"/Requisitions/Details/{requisitionId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return ExtractToken(await response.Content.ReadAsStringAsync());
    }

    private static Task<HttpResponseMessage> PostReviewAsync(
        HttpClient client,
        string action,
        long requisitionId,
        long rowVersion,
        string token,
        string? rejectionReason = null)
    {
        var values = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["rowVersion"] = rowVersion.ToString(CultureInfo.InvariantCulture),
        };
        if (rejectionReason is not null)
        {
            values["rejectionReason"] = rejectionReason;
        }

        return client.PostAsync($"/Requisitions/{action}/{requisitionId}", new FormUrlEncodedContent(values));
    }

    private static async Task<int> CountAuditLogsAsync(long requisitionId)
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        return await connection.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM audit_logs WHERE entity_type = 'Requisition' AND entity_id = TO_CHAR(:requisitionId)",
            new { requisitionId });
    }

    private static async Task DeleteTestDataAsync(IReadOnlyCollection<long> requisitionIds, TestItem? item)
    {
        var ids = requisitionIds.Where(id => id > 0).ToList();
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.OpenAsync();
        if (ids.Count > 0)
        {
            var entityIds = ids.Select(id => id.ToString(CultureInfo.InvariantCulture)).ToList();
            await connection.ExecuteAsync(
                "DELETE FROM audit_logs WHERE entity_type = 'Requisition' AND entity_id IN :entityIds", new { entityIds });
            await connection.ExecuteAsync("""
                DELETE FROM issue_allocations WHERE requisition_line_id IN
                    (SELECT requisition_line_id FROM requisition_lines WHERE requisition_id IN :ids)
                """, new { ids });
            await connection.ExecuteAsync("DELETE FROM requisition_lines WHERE requisition_id IN :ids", new { ids });
            await connection.ExecuteAsync("DELETE FROM requisitions WHERE requisition_id IN :ids", new { ids });
        }

        if (item is not null)
        {
            await connection.ExecuteAsync("DELETE FROM stock_lots WHERE item_id = :itemId", new { item.ItemId });
            await connection.ExecuteAsync("DELETE FROM items WHERE item_id = :itemId", new { item.ItemId });
        }

        await connection.ExecuteAsync("COMMIT");
    }

    private static string ExtractToken(string html)
    {
        var match = AntiforgeryTokenRegex().Match(html);
        Assert.True(match.Success, "頁面必須包含 AntiForgery request token。");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryTokenRegex();

    [GeneratedRegex("name=\"rowVersion\" value=\"([0-9]+)\"")]
    private static partial Regex RowVersionRegex();

    private sealed record TestItem(long ItemId);
    private sealed record AuditRow(
        string EntityType,
        string EntityId,
        string Action,
        string Actor,
        DateTime OccurredAt,
        string NewValue);
}
