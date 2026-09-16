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

    /// <summary>
    /// 完全收合時，細列（.mso-compact-header）必須是整頁寬的獨立頂列，
    /// 不可以還是橫向 flex 列（.mso-app-shell）裡擠在左邊的子項。
    /// 做法是在完全收合時多掛一個 .mso-app-shell-hidden，讓 CSS 把 shell 從
    /// <c>flex-direction: row</c>（預設）改成 <c>column</c>——細列與主內容因此各自撐滿整頁寬、
    /// 上下堆疊，而不是左右並排。這裡釘住「細列緊接在帶有該 class 的 shell 開始標籤之後」，
    /// 證明它仍是 shell 的直接子項、只是版面方向被改成縱向。
    /// </summary>
    [Fact]
    public async Task Hidden_sidebar_state_stacks_the_compact_header_as_a_full_width_top_row()
    {
        using var client = CreateSidebarClient("hidden");
        await WebAuthTestHelpers.LoginAsync(client, TestIdentitySeeder.StorekeeperEmail);

        var html = await GetHtmlAsync(client, "/");

        Assert.Matches(
            new Regex("<div class=\"mso-app-shell mso-app-shell-hidden\">\\s*<header class=\"mso-compact-header\""),
            html);

        var cssPath = FindRepositoryFile("src/MedSupplyOps.Web/wwwroot/css/site.css");
        var css = await File.ReadAllTextAsync(cssPath);
        Assert.Contains(".mso-app-shell-hidden { flex-direction: column; }", css, StringComparison.Ordinal);

        _output.WriteLine($"T1 hidden shell: {Fragment(html, "mso-app-shell-hidden")}");
    }

    /// <summary>
    /// 展開與半收折兩態不可以被 D1 的修正動到：shell 不掛 -hidden class，維持橫向 flex 列。
    /// </summary>
    [Theory]
    [InlineData("expanded")]
    [InlineData("compact")]
    public async Task Expanded_and_compact_sidebar_states_keep_the_row_shell_unmodified(string state)
    {
        using var client = CreateSidebarClient(state);
        await WebAuthTestHelpers.LoginAsync(client, TestIdentitySeeder.StorekeeperEmail);

        var html = await GetHtmlAsync(client, "/");

        Assert.DoesNotContain("mso-app-shell-hidden", html, StringComparison.Ordinal);
        Assert.Matches(new Regex("<div class=\"mso-app-shell\\s*\">"), html);
    }

    /// <summary>
    /// 頂列副標只在登入頁（未登入）出現；登入後不論角色、不論導覽版面都不顯示。
    /// <see cref="Login_page_has_a_clean_navigation_and_email_only_demo_fill_controls"/> 已經釘住登入頁「有」副標的一半；
    /// 這裡補「登入後三種角色、兩種導覽版面都沒有」的另一半。
    /// </summary>
    [Theory]
    [InlineData("top")]
    [InlineData("sidebar")]
    public async Task Navbar_subtitle_is_absent_after_login_for_every_role_and_navigation_layout(string navigationLayout)
    {
        foreach (var email in new[] { TestIdentitySeeder.RequesterEmail, TestIdentitySeeder.StorekeeperEmail, TestIdentitySeeder.AdministratorEmail })
        {
            using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            if (navigationLayout == "sidebar")
            {
                client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", "mso-navigation-layout=sidebar; mso-sidebar-state=expanded");
            }

            await WebAuthTestHelpers.LoginAsync(client, email);
            var html = await GetHtmlAsync(client, "/");

            Assert.DoesNotContain("mso-navbar-subtitle", html, StringComparison.Ordinal);
            _output.WriteLine($"T2 {navigationLayout}/{email}: navbar 無副標（{Fragment(html, "navbar-brand")}）");
        }
    }

    private static string FindRepositoryFile(string relativePath)
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException($"找不到 {relativePath}。");
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
        // ★ 版權字串改成走資源檔（鍵帶 {0} 年份），英文版才不會夾中文標點與「版權所有」。
        //   這條測試驗的是「年份取自業務日曆」，不是那句話怎麼寫；繁中版的實際文字見資源鍵。見 L-038。
        var copyright = $"© {year} MedSupplyOps．版權所有";
        const string warning = "本系統為院內作業系統，內容僅供授權人員使用，禁止未經授權之重製、散布、擷取或外傳。";

        Assert.Contains(copyright, anonymousHtml, StringComparison.Ordinal);
        Assert.Contains(warning, anonymousHtml, StringComparison.Ordinal);
        Assert.Contains(copyright, authenticatedHtml, StringComparison.Ordinal);
        Assert.Contains(warning, authenticatedHtml, StringComparison.Ordinal);
        _output.WriteLine($"T5 anonymous footer: {Fragment(anonymousHtml, warning)}");
        _output.WriteLine($"T5 authenticated footer: {Fragment(authenticatedHtml, warning)}");
    }

    [Fact]
    public async Task Login_page_has_a_clean_navigation_and_email_only_demo_fill_controls()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var html = await GetHtmlAsync(client, "/Account/Login");

        Assert.Contains("醫材耗材請領與庫存管理", html, StringComparison.Ordinal);
        foreach (var functionName in new[] { "首頁", "庫存查詢", "效期預警", "請領單", "品項管理", "入庫" })
        {
            Assert.DoesNotContain(functionName, html, StringComparison.Ordinal);
        }

        Assert.Equal(3, Regex.Count(html, "data-mso-fill-email"));
        Assert.Contains("value=\"requester@example.local\"", html, StringComparison.Ordinal);
        Assert.Contains("value=\"keeper@example.local\"", html, StringComparison.Ordinal);
        Assert.Contains("value=\"admin@example.local\"", html, StringComparison.Ordinal);
        Assert.Equal(1, Regex.Count(html, "Demo#2026pass"));
        Assert.DoesNotMatch("(?:value|data-[^=]*)=\"[^\"]*Demo#2026pass", html);
        Assert.Contains("忘記密碼請聯絡系統管理員。", html, StringComparison.Ordinal);
        _output.WriteLine($"T1 login header: {Fragment(html, "醫材耗材請領與庫存管理")}");
        _output.WriteLine($"T1 demo card: {Fragment(html, "data-mso-fill-email")}");
    }

    [Theory]
    [InlineData("light", "light")]
    [InlineData("dark", "dark")]
    [InlineData("contrast", "light")]
    public async Task Theme_cookie_is_emitted_by_razor_on_the_first_response(string preference, string bootstrapTheme)
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", $"mso-theme={preference}");

        var html = await GetHtmlAsync(client, "/Account/Login");

        Assert.Contains($"data-bs-theme=\"{bootstrapTheme}\"", html, StringComparison.Ordinal);
        Assert.Contains($"data-mso-theme=\"{preference}\"", html, StringComparison.Ordinal);
        Assert.Contains("data-density=\"comfortable\"", html, StringComparison.Ordinal);
        _output.WriteLine($"T3 {preference}: {Fragment(html, $"data-mso-theme=\"{preference}\"")}");
    }

    [Fact]
    public async Task Dark_theme_renders_every_in_scope_page_without_a_server_error()
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.OpenAsync();
        var requisitionNo = "U2-DARK-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        var departmentId = await connection.ExecuteScalarAsync<long>(
            "SELECT department_id FROM departments WHERE department_code = 'DEP-ER' AND is_deleted = 0");
        long requisitionId = 0;
        try
        {
            await connection.ExecuteAsync(
                "INSERT INTO requisitions (requisition_no, department_id, status, created_by) VALUES (:no, :departmentId, 'Draft', 'itest-u2')",
                new { no = requisitionNo, departmentId });
            requisitionId = await connection.ExecuteScalarAsync<long>(
                "SELECT requisition_id FROM requisitions WHERE requisition_no = :no", new { no = requisitionNo });

            using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            await WebAuthTestHelpers.LoginAsync(client, TestIdentitySeeder.AdministratorEmail);
            client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", "mso-theme=dark");
            var pages = new[]
            {
                "/",
                "/Inventory",
                "/Inventory/Expiring",
                "/Items",
                "/Items/Create",
                "/Receiving",
                "/Requisitions",
                "/Requisitions/Create",
                $"/Requisitions/Details/{requisitionId}",
                "/Home/Error",
                "/Account/Login",
                "/Account/Register",
                "/Account/AccessDenied",
            };

            foreach (var path in pages)
            {
                var response = await client.GetAsync(path);
                Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path}: expected 200 but received {(int)response.StatusCode}.");
                var html = await response.Content.ReadAsStringAsync();
                Assert.Contains("data-bs-theme=\"dark\"", html, StringComparison.Ordinal);
                Assert.Contains("data-mso-theme=\"dark\"", html, StringComparison.Ordinal);
                Assert.DoesNotContain("An unhandled exception", html, StringComparison.Ordinal);
            }

            _output.WriteLine($"T5 dark pages: {string.Join(", ", pages)}（13/13 HTTP 200）");
        }
        finally
        {
            if (requisitionId != 0)
            {
                await connection.ExecuteAsync("DELETE FROM requisitions WHERE requisition_id = :requisitionId", new { requisitionId });
                await connection.ExecuteAsync("COMMIT");
            }
        }
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
