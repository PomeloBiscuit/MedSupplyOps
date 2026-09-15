using System.Net;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Oracle.ManagedDataAccess.Client;
using Xunit.Abstractions;

namespace MedSupplyOps.Integration.Tests.Web;

/// <summary>U1：Cookie 版面、三段側欄與科室範圍角標的端對端驗證。</summary>
public sealed partial class SidebarNavigationWebTests
    : IClassFixture<RequisitionFlowTests.RequisitionWebApplicationFactory>, IAsyncLifetime
{
    private readonly RequisitionFlowTests.RequisitionWebApplicationFactory _factory;
    private readonly ITestOutputHelper _output;

    public SidebarNavigationWebTests(
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

    [Theory]
    [InlineData("expanded", "data-sidebar-width=\"248\"", "MedSupplyOps", "mso-sidebar-expanded")]
    [InlineData("compact", "data-sidebar-width=\"64\"", "data-bs-toggle=\"tooltip\"", "mso-sidebar-compact")]
    [InlineData("hidden", "mso-navigation-offcanvas", "導覽", "mso-compact-header")]
    public async Task Sidebar_cookie_renders_the_requested_server_side_state(
        string state,
        string requiredOne,
        string requiredTwo,
        string requiredClass)
    {
        using var client = CreateSidebarClient(state);
        await WebAuthTestHelpers.LoginAsync(client, TestIdentitySeeder.StorekeeperEmail);

        var html = await GetHtmlAsync(client, "/");

        Assert.Contains("data-mso-navigation=\"sidebar\"", html, StringComparison.Ordinal);
        Assert.Contains(requiredOne, html, StringComparison.Ordinal);
        Assert.Contains(requiredTwo, html, StringComparison.Ordinal);
        Assert.Contains(requiredClass, html, StringComparison.Ordinal);
        if (state == "hidden")
        {
            Assert.DoesNotContain("class=\"mso-sidebar ", html, StringComparison.Ordinal);
        }

        _output.WriteLine($"T1 {state}: {Fragment(html, requiredClass)}");
    }

    [Fact]
    public async Task Navigation_badges_respect_department_scope_and_no_role_has_no_function_items()
    {
        const string departmentCode = "DEP-U1-OTHER";
        var requisitionNo = "U1-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        long departmentId = 0;
        long requisitionId = 0;
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.OpenAsync();
        try
        {
            using var requesterBefore = CreateSidebarClient("expanded");
            using var keeperBefore = CreateSidebarClient("expanded");
            await WebAuthTestHelpers.LoginAsync(requesterBefore, TestIdentitySeeder.RequesterEmail);
            await WebAuthTestHelpers.LoginAsync(keeperBefore, TestIdentitySeeder.StorekeeperEmail);
            var requesterCountBefore = BadgeCountOrZero(await GetHtmlAsync(requesterBefore, "/"));
            var keeperCountBefore = BadgeCountOrZero(await GetHtmlAsync(keeperBefore, "/"));

            await connection.ExecuteAsync(
                "INSERT INTO departments (department_code, department_name, created_by) VALUES (:code, :name, 'itest-u1')",
                new { code = departmentCode, name = "U1 其他科室" });
            departmentId = await connection.ExecuteScalarAsync<long>(
                "SELECT department_id FROM departments WHERE department_code = :code", new { code = departmentCode });
            await connection.ExecuteAsync(
                "INSERT INTO requisitions (requisition_no, department_id, status, created_by) VALUES (:requisitionNo, :departmentId, 'PendingApproval', 'itest-u1')",
                new { requisitionNo, departmentId });
            requisitionId = await connection.ExecuteScalarAsync<long>(
                "SELECT requisition_id FROM requisitions WHERE requisition_no = :requisitionNo", new { requisitionNo });

            using var requester = CreateSidebarClient("expanded");
            using var keeper = CreateSidebarClient("expanded");
            using var noRole = CreateSidebarClient("expanded");
            await WebAuthTestHelpers.LoginAsync(requester, TestIdentitySeeder.RequesterEmail);
            await WebAuthTestHelpers.LoginAsync(keeper, TestIdentitySeeder.StorekeeperEmail);
            await WebAuthTestHelpers.LoginAsync(noRole, TestIdentitySeeder.NoRoleEmail);
            var requesterHtml = await GetHtmlAsync(requester, "/");
            var keeperHtml = await GetHtmlAsync(keeper, "/");
            var noRoleHtml = await GetHtmlAsync(noRole, "/");
            var requesterCountAfter = BadgeCountOrZero(requesterHtml);
            var keeperCountAfter = BadgeCountOrZero(keeperHtml);

            Assert.Equal(requesterCountBefore, requesterCountAfter);
            Assert.Equal(keeperCountBefore + 1, keeperCountAfter);
            Assert.DoesNotContain("mso-sidebar-link", noRoleHtml, StringComparison.Ordinal);
            Assert.DoesNotContain("data-testid=\"nav-", noRoleHtml, StringComparison.Ordinal);
            _output.WriteLine($"T2 requester DEP-ER: {requesterCountAfter}（其他科室 {requisitionNo} 未計入）");
            _output.WriteLine($"T2 keeper: {keeperCountAfter}（其他科室 {requisitionNo} 已計入）");
            _output.WriteLine("T2 no-role: 0 個角標、0 個功能項目");
        }
        finally
        {
            if (requisitionId != 0)
            {
                await connection.ExecuteAsync("DELETE FROM requisitions WHERE requisition_id = :requisitionId", new { requisitionId });
            }

            if (departmentId != 0)
            {
                await connection.ExecuteAsync("DELETE FROM departments WHERE department_id = :departmentId", new { departmentId });
            }

            await connection.ExecuteAsync("COMMIT");
        }
    }

    [Fact]
    public async Task Global_footer_uses_business_calendar_year_for_anonymous_and_authenticated_pages()
    {
        using var anonymous = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var authenticated = CreateSidebarClient("expanded");
        await WebAuthTestHelpers.LoginAsync(authenticated, TestIdentitySeeder.StorekeeperEmail);
        var year = TestBusinessCalendar.Today.Year;
        var anonymousHtml = await GetHtmlAsync(anonymous, "/Account/Login");
        var authenticatedHtml = await GetHtmlAsync(authenticated, "/");
        var copyright = $"© {year} MedSupplyOps．版權所有 All rights reserved.";
        const string warning = "本系統為院內作業系統，內容僅供授權人員使用，禁止未經授權之重製、散布、擷取或外傳。";

        Assert.Contains(copyright, anonymousHtml, StringComparison.Ordinal);
        Assert.Contains(warning, anonymousHtml, StringComparison.Ordinal);
        Assert.Contains(copyright, authenticatedHtml, StringComparison.Ordinal);
        Assert.Contains(warning, authenticatedHtml, StringComparison.Ordinal);
        _output.WriteLine($"T5 anonymous footer: {Fragment(anonymousHtml, warning)}");
        _output.WriteLine($"T5 authenticated footer: {Fragment(authenticatedHtml, warning)}");
    }

    private HttpClient CreateSidebarClient(string state)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Cookie",
            $"mso-navigation-layout=sidebar; mso-sidebar-state={state}");
        return client;
    }

    private static async Task<string> GetHtmlAsync(HttpClient client, string path)
        => WebUtility.HtmlDecode(await client.GetStringAsync(path));

    private static int BadgeCountOrZero(string html)
    {
        var match = BadgeRegex().Match(html);
        return match.Success
            ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
            : 0;
    }

    private static string Fragment(string html, string marker)
    {
        var start = Math.Max(0, html.IndexOf(marker, StringComparison.Ordinal) - 90);
        return Regex.Replace(html[start..Math.Min(html.Length, start + 300)], "\\s+", " ").Trim();
    }

    [GeneratedRegex("data-testid=\\\"nav-requisition-badge\\\">\\s*(\\d+)\\s*<")]
    private static partial Regex BadgeRegex();
}
