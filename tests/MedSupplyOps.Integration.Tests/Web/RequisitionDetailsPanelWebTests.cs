using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Dapper;
using MedSupplyOps.Domain.Requisitions;
using Microsoft.AspNetCore.Mvc.Testing;
using Oracle.ManagedDataAccess.Client;
using Xunit.Abstractions;

namespace MedSupplyOps.Integration.Tests.Web;

public sealed partial class RequisitionDetailsPanelWebTests
    : IClassFixture<RequisitionFlowTests.RequisitionWebApplicationFactory>
{
    private readonly RequisitionFlowTests.RequisitionWebApplicationFactory _factory;
    private readonly ITestOutputHelper _output;

    public RequisitionDetailsPanelWebTests(
        RequisitionFlowTests.RequisitionWebApplicationFactory factory,
        ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [Fact]
    public async Task Details_panel_hides_another_department_from_requester_but_allows_storekeeper()
    {
        var scenario = await CreateScenarioAsync();
        try
        {
            using var requester = CreateClient();
            using var storekeeper = CreateClient();
            await WebAuthTestHelpers.LoginAsync(requester, TestIdentitySeeder.RequesterEmail);
            await WebAuthTestHelpers.LoginAsync(storekeeper, TestIdentitySeeder.StorekeeperEmail);

            var requesterResponse = await requester.GetAsync($"/Requisitions/DetailsPanel/{scenario.Id}");
            var storekeeperResponse = await storekeeper.GetAsync($"/Requisitions/DetailsPanel/{scenario.Id}");

            Assert.Equal(HttpStatusCode.NotFound, requesterResponse.StatusCode);
            Assert.Equal(HttpStatusCode.OK, storekeeperResponse.StatusCode);
            Assert.Contains(scenario.Number, await storekeeperResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            _output.WriteLine($"T1 REQUESTER /Requisitions/DetailsPanel/{scenario.Id}: HTTP {(int)requesterResponse.StatusCode} {requesterResponse.ReasonPhrase}");
            _output.WriteLine($"T1 STOREKEEPER /Requisitions/DetailsPanel/{scenario.Id}: HTTP {(int)storekeeperResponse.StatusCode} {storekeeperResponse.ReasonPhrase}");
        }
        finally
        {
            await DeleteScenarioAsync(scenario.Id);
        }
    }

    [Fact]
    public async Task Full_details_hides_another_department_from_requester()
    {
        var scenario = await CreateScenarioAsync();
        try
        {
            using var requester = CreateClient();
            await WebAuthTestHelpers.LoginAsync(requester, TestIdentitySeeder.RequesterEmail);

            var response = await requester.GetAsync($"/Requisitions/Details/{scenario.Id}");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            _output.WriteLine($"T2 full Details/{scenario.Id}: HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        }
        finally
        {
            await DeleteScenarioAsync(scenario.Id);
        }
    }

    [Fact]
    public async Task Approve_with_external_return_url_falls_back_to_requisition_list()
    {
        var scenario = await CreateScenarioAsync();
        try
        {
            using var storekeeper = CreateClient();
            await WebAuthTestHelpers.LoginAsync(storekeeper, TestIdentitySeeder.StorekeeperEmail);
            var panel = await storekeeper.GetAsync($"/Requisitions/DetailsPanel/{scenario.Id}?returnUrl=%2FRequisitions");
            var html = await panel.Content.ReadAsStringAsync();

            var response = await storekeeper.PostAsync(
                $"/Requisitions/Approve/{scenario.Id}",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["__RequestVerificationToken"] = ExtractToken(html),
                    ["rowVersion"] = ExtractRowVersion(html).ToString(CultureInfo.InvariantCulture),
                    ["returnUrl"] = "https://evil.example",
                }));

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal("/Requisitions", response.Headers.Location?.OriginalString);
            var listHtml = WebUtility.HtmlDecode(await storekeeper.GetStringAsync(response.Headers.Location));
            var successAlert = Regex.Match(
                listHtml,
                "<div class=\"alert alert-success\" role=\"status\">([^<]+)</div>");
            _output.WriteLine($"T3 success alert: {(successAlert.Success ? successAlert.Groups[1].Value : "<missing>")}");
            Assert.Contains("請領單已核准。", listHtml, StringComparison.Ordinal);
            _output.WriteLine($"T3 HTTP {(int)response.StatusCode}; Location: {response.Headers.Location}");
        }
        finally
        {
            await DeleteScenarioAsync(scenario.Id);
        }
    }

    [Fact]
    public async Task Invalid_rejection_from_panel_redirects_to_full_details()
    {
        var scenario = await CreateScenarioAsync();
        try
        {
            using var storekeeper = CreateClient();
            await WebAuthTestHelpers.LoginAsync(storekeeper, TestIdentitySeeder.StorekeeperEmail);
            var html = await storekeeper.GetStringAsync($"/Requisitions/DetailsPanel/{scenario.Id}?returnUrl=%2FRequisitions");

            var response = await storekeeper.PostAsync(
                $"/Requisitions/Reject/{scenario.Id}",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["__RequestVerificationToken"] = ExtractToken(html),
                    ["rowVersion"] = ExtractRowVersion(html).ToString(CultureInfo.InvariantCulture),
                    ["rejectionReason"] = " ",
                    ["returnUrl"] = "/Requisitions",
                }));

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal($"/Requisitions/Details/{scenario.Id}", response.Headers.Location?.OriginalString);
        }
        finally
        {
            await DeleteScenarioAsync(scenario.Id);
        }
    }

    [Fact]
    public async Task List_links_keep_full_details_href_for_progressive_enhancement()
    {
        var scenario = await CreateScenarioAsync();
        try
        {
            using var storekeeper = CreateClient();
            await WebAuthTestHelpers.LoginAsync(storekeeper, TestIdentitySeeder.StorekeeperEmail);
            var html = WebUtility.HtmlDecode(await storekeeper.GetStringAsync("/Requisitions"));
            var detailsHref = $"href=\"/Requisitions/Details/{scenario.Id}\"";
            var panelUrl = $"data-panel-url=\"/Requisitions/DetailsPanel/{scenario.Id}\"";

            Assert.Equal(2, Regex.Count(html, Regex.Escape(detailsHref)));
            Assert.Equal(2, Regex.Count(html, Regex.Escape(panelUrl)));
            _output.WriteLine($"T4 單號連結: {detailsHref}");
            _output.WriteLine($"T4 詳情按鈕: {detailsHref}");
        }
        finally
        {
            await DeleteScenarioAsync(scenario.Id);
        }
    }

    [Fact]
    public async Task Frontend_handles_all_failure_classes_and_restores_focus()
    {
        using var client = CreateClient();
        var script = await client.GetStringAsync("/js/requisition-details.js");

        Assert.Contains("response.status === 401", script, StringComparison.Ordinal);
        Assert.Contains("response.status === 403", script, StringComparison.Ordinal);
        Assert.Contains("response.status === 404", script, StringComparison.Ordinal);
        Assert.Contains("serverErrorMessage", script, StringComparison.Ordinal);
        Assert.Contains("networkErrorMessage", script, StringComparison.Ordinal);
        Assert.Contains("lastTrigger?.focus()", script, StringComparison.Ordinal);
        Assert.Contains("event.button !== 0", script, StringComparison.Ordinal);
        _output.WriteLine("T5 JS: 401=重新登入；403=無權限；404=找不到或無權；500/其他=重新整理；network=檢查連線；關閉後 focus 回觸發連結。");
    }

    private HttpClient CreateClient()
        => _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static async Task<Scenario> CreateScenarioAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var number = "REQ-AD-" + suffix;
        var createdAt = TestBusinessCalendar.Today.ToDateTime(new TimeOnly(9, 0));
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.OpenAsync();
        var requesterDepartmentId = await connection.QuerySingleAsync<long>(
            "SELECT department_id FROM identity_users WHERE normalized_user_name = :name",
            new { name = TestIdentitySeeder.RequesterEmail.ToUpperInvariant() });
        var otherDepartmentId = await connection.QuerySingleAsync<long>(
            "SELECT MIN(department_id) FROM departments WHERE department_id <> :requesterDepartmentId AND is_active = 1 AND is_deleted = 0",
            new { requesterDepartmentId });
        var itemId = await connection.QuerySingleAsync<long>(
            "SELECT MIN(item_id) FROM items WHERE is_deleted = 0");
        await connection.ExecuteAsync(
            """
            INSERT INTO requisitions
                (requisition_no, department_id, status, submitted_at, created_at, created_by)
            VALUES
                (:numberValue, :departmentId, :status, :createdAt, :createdAt, 'itest-ad')
            """,
            new
            {
                numberValue = number,
                departmentId = otherDepartmentId,
                status = RequisitionStatus.PendingApproval.ToString(),
                createdAt,
            });
        var id = await connection.QuerySingleAsync<long>(
            "SELECT requisition_id FROM requisitions WHERE requisition_no = :numberValue",
            new { numberValue = number });
        await connection.ExecuteAsync(
            "INSERT INTO requisition_lines (requisition_id, line_no, item_id, quantity) VALUES (:id, 1, :itemId, 1)",
            new { id, itemId });
        await connection.ExecuteAsync("COMMIT");
        return new Scenario(id, number);
    }

    private static async Task DeleteScenarioAsync(long id)
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.ExecuteAsync(
            "DELETE FROM audit_logs WHERE entity_type = 'Requisition' AND entity_id = :entityId",
            new { entityId = id.ToString(CultureInfo.InvariantCulture) });
        await connection.ExecuteAsync("DELETE FROM requisition_lines WHERE requisition_id = :id", new { id });
        await connection.ExecuteAsync("DELETE FROM requisitions WHERE requisition_id = :id", new { id });
        await connection.ExecuteAsync("COMMIT");
    }

    private static string ExtractToken(string html)
    {
        var match = AntiforgeryTokenRegex().Match(html);
        Assert.True(match.Success, "部分檢視必須包含 AntiForgery request token。");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static long ExtractRowVersion(string html)
    {
        var match = RowVersionRegex().Match(html);
        Assert.True(match.Success, "部分檢視必須包含 rowVersion。");
        return long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryTokenRegex();

    [GeneratedRegex("name=\"rowVersion\" value=\"([0-9]+)\"")]
    private static partial Regex RowVersionRegex();

    private sealed record Scenario(long Id, string Number);
}
