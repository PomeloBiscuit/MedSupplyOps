using System.Net;
using System.Text.RegularExpressions;
using Dapper;
using MedSupplyOps.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Oracle.ManagedDataAccess.Client;
using Xunit.Abstractions;

namespace MedSupplyOps.Integration.Tests.Web;

public sealed partial class AccountSettingsWebTests
    : IClassFixture<RequisitionFlowTests.RequisitionWebApplicationFactory>
{
    private readonly RequisitionFlowTests.RequisitionWebApplicationFactory _factory;
    private readonly ITestOutputHelper _output;

    public AccountSettingsWebTests(
        RequisitionFlowTests.RequisitionWebApplicationFactory factory,
        ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [Fact]
    public async Task Profile_is_read_only_and_shows_identity_role_and_department()
    {
        using var client = CreateClient();
        await WebAuthTestHelpers.LoginAsync(client, TestIdentitySeeder.RequesterEmail);

        var response = await client.GetAsync("/Account/Profile");
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        var department = await GetRequesterDepartmentNameAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("測試請領員", html, StringComparison.Ordinal);
        Assert.Contains(TestIdentitySeeder.RequesterEmail, html, StringComparison.Ordinal);
        Assert.Contains("請領人", html, StringComparison.Ordinal);
        Assert.Contains(department, html, StringComparison.Ordinal);
        Assert.Contains("個人資料為唯讀；如需修改請聯絡系統管理員。", html, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"DisplayName\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Wrong_current_password_reports_clear_error_and_keeps_hash_unchanged()
    {
        using var client = CreateClient();
        await WebAuthTestHelpers.LoginAsync(client, TestIdentitySeeder.RequesterEmail);
        var beforeHash = await GetPasswordHashAsync(TestIdentitySeeder.RequesterEmail);
        var token = await GetChangePasswordTokenAsync(client);

        var response = await PostChangePasswordAsync(
            client,
            token,
            "Wrong#Password2026",
            "New#Password2026",
            "New#Password2026");
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        var afterHash = await GetPasswordHashAsync(TestIdentitySeeder.RequesterEmail);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("目前密碼不正確，密碼未變更。", html, StringComparison.Ordinal);
        Assert.Equal(beforeHash, afterHash);
        _output.WriteLine("A-T1 實際訊息：目前密碼不正確，密碼未變更。");
        _output.WriteLine("A-T1 DB password_hash 前後比對：相同（未輸出雜湊內容）");
    }

    [Fact]
    public async Task Weak_new_password_uses_the_same_frontend_hint_and_identity_error()
    {
        using var client = CreateClient();
        await WebAuthTestHelpers.LoginAsync(client, TestIdentitySeeder.RequesterEmail);
        var getResponse = await client.GetAsync("/Account/ChangePassword");
        var getHtml = WebUtility.HtmlDecode(await getResponse.Content.ReadAsStringAsync());
        var token = ExtractToken(getHtml);
        const string expected = "新密碼必須至少 12 個字元。";

        var response = await PostChangePasswordAsync(
            client,
            token,
            TestIdentitySeeder.Password,
            "Short#1a",
            "Short#1a");
        var postHtml = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Contains(expected, getHtml, StringComparison.Ordinal);
        Assert.Contains(expected, postHtml, StringComparison.Ordinal);
        Assert.True(await CheckPasswordAsync(TestIdentitySeeder.RequesterEmail, TestIdentitySeeder.Password));
        _output.WriteLine($"A-T2 前端規則提示：{expected}");
        _output.WriteLine($"A-T2 後端 Identity 訊息：{expected}");
    }

    [Fact]
    public async Task Mismatched_confirmation_is_rejected_before_identity_changes_password()
    {
        using var client = CreateClient();
        await WebAuthTestHelpers.LoginAsync(client, TestIdentitySeeder.RequesterEmail);
        var beforeHash = await GetPasswordHashAsync(TestIdentitySeeder.RequesterEmail);
        var token = await GetChangePasswordTokenAsync(client);

        var response = await PostChangePasswordAsync(
            client,
            token,
            TestIdentitySeeder.Password,
            "New#Password2026",
            "Different#Password2026");
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Contains("兩次輸入的新密碼不一致。", html, StringComparison.Ordinal);
        Assert.Equal(beforeHash, await GetPasswordHashAsync(TestIdentitySeeder.RequesterEmail));
    }

    [Fact]
    public async Task Successful_change_rejects_old_login_audits_no_secret_and_invalidates_other_cookie()
    {
        using var clientA = CreateClient();
        using var clientB = CreateClient();
        await WebAuthTestHelpers.LoginAsync(clientA, TestIdentitySeeder.RequesterEmail);
        await WebAuthTestHelpers.LoginAsync(clientB, TestIdentitySeeder.RequesterEmail);

        var token = await GetChangePasswordTokenAsync(clientB);
        var newPassword = $"ITestNew#{Guid.NewGuid():N}aA1";
        var userId = await GetUserIdAsync(TestIdentitySeeder.RequesterEmail);
        var beforeHash = await GetPasswordHashAsync(TestIdentitySeeder.RequesterEmail);

        try
        {
            var response = await PostChangePasswordAsync(
                clientB,
                token,
                TestIdentitySeeder.Password,
                newPassword,
                newPassword);
            var afterHash = await GetPasswordHashAsync(TestIdentitySeeder.RequesterEmail);

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.NotEqual(beforeHash, afterHash);

            using var oldPasswordClient = CreateClient();
            using var newPasswordClient = CreateClient();
            var oldLogin = await LoginAsync(oldPasswordClient, TestIdentitySeeder.RequesterEmail, TestIdentitySeeder.Password);
            var newLogin = await LoginAsync(newPasswordClient, TestIdentitySeeder.RequesterEmail, newPassword);
            Assert.Equal(HttpStatusCode.OK, oldLogin.StatusCode);
            Assert.Equal(HttpStatusCode.Redirect, newLogin.StatusCode);

            var audit = await GetLatestPasswordAuditAsync(userId);
            Assert.Equal("User", audit.EntityType);
            Assert.Equal("ChangePassword", audit.Action);
            Assert.Equal(TestIdentitySeeder.RequesterEmail, audit.Actor);
            var allAuditFields = string.Join('|', audit.EntityType, audit.EntityId, audit.Action, audit.Actor, audit.OccurredAt, audit.OldValue, audit.NewValue);
            Assert.DoesNotContain(TestIdentitySeeder.Password, allAuditFields, StringComparison.Ordinal);
            Assert.DoesNotContain(newPassword, allAuditFields, StringComparison.Ordinal);
            Assert.Null(audit.OldValue);
            Assert.Null(audit.NewValue);

            var staleCookieResponse = await clientA.GetAsync("/");
            var refreshedCookieResponse = await clientB.GetAsync("/");
            Assert.Equal(HttpStatusCode.Redirect, staleCookieResponse.StatusCode);
            Assert.Equal("/Account/Login", staleCookieResponse.Headers.Location?.AbsolutePath);
            Assert.Equal(HttpStatusCode.OK, refreshedCookieResponse.StatusCode);

            _output.WriteLine("A-T3 password_hash 前後比對：不同（未輸出雜湊內容）");
            _output.WriteLine("A-T3 舊密碼登入：HTTP 200 留在登入頁（失敗）；新密碼登入：HTTP 302（成功）");
            _output.WriteLine($"A-T3 稽核：ENTITY_TYPE={audit.EntityType}, ACTION={audit.Action}, ACTOR={audit.Actor}, OLD_VALUE/NEW_VALUE 皆空；任何欄位均不含目前或新密碼");
            _output.WriteLine($"A-T4 A 用戶端下一請求：HTTP {(int)staleCookieResponse.StatusCode} -> {staleCookieResponse.Headers.Location?.AbsolutePath}；B 用戶端：HTTP {(int)refreshedCookieResponse.StatusCode}");
        }
        finally
        {
            await RestoreTestPasswordAsync(userId);
            await DeletePasswordAuditsAsync(userId);
        }
    }

    private HttpClient CreateClient()
        => _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static async Task<string> GetChangePasswordTokenAsync(HttpClient client)
    {
        var response = await client.GetAsync("/Account/ChangePassword");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return ExtractToken(await response.Content.ReadAsStringAsync());
    }

    private static async Task<HttpResponseMessage> PostChangePasswordAsync(
        HttpClient client,
        string token,
        string currentPassword,
        string newPassword,
        string confirmPassword)
        => await client.PostAsync("/Account/ChangePassword", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["CurrentPassword"] = currentPassword,
                ["NewPassword"] = newPassword,
                ["ConfirmPassword"] = confirmPassword,
            }));

    private static async Task<HttpResponseMessage> LoginAsync(HttpClient client, string email, string password)
    {
        var loginPage = await client.GetAsync("/Account/Login");
        var token = ExtractToken(await loginPage.Content.ReadAsStringAsync());
        return await client.PostAsync("/Account/Login", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["Email"] = email,
                ["Password"] = password,
            }));
    }

    private async Task<bool> CheckPasswordAsync(string email, string password)
    {
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByEmailAsync(email) ?? throw new InvalidOperationException($"找不到測試帳號 {email}。");
        return await userManager.CheckPasswordAsync(user, password);
    }

    private async Task RestoreTestPasswordAsync(string userId)
    {
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByIdAsync(userId) ?? throw new InvalidOperationException($"找不到測試帳號 {userId}。");
        user.AccessFailedCount = 0;
        user.LockoutEnd = null;
        EnsureSucceeded(await userManager.UpdateAsync(user), "清除測試帳號的登入失敗狀態");
        if (await userManager.HasPasswordAsync(user))
        {
            EnsureSucceeded(await userManager.RemovePasswordAsync(user), "移除測試期間的密碼");
        }

        EnsureSucceeded(await userManager.AddPasswordAsync(user, TestIdentitySeeder.Password), "還原測試帳號密碼");
    }

    private static void EnsureSucceeded(IdentityResult result, string action)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"{action}失敗：{string.Join("；", result.Errors.Select(error => error.Description))}");
        }
    }

    private static async Task<string> GetPasswordHashAsync(string email)
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        return await connection.QuerySingleAsync<string>(
            "SELECT password_hash FROM identity_users WHERE normalized_email = :email",
            new { email = email.ToUpperInvariant() });
    }

    private static async Task<string> GetUserIdAsync(string email)
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        return await connection.QuerySingleAsync<string>(
            "SELECT id FROM identity_users WHERE normalized_email = :email",
            new { email = email.ToUpperInvariant() });
    }

    private static async Task<string> GetRequesterDepartmentNameAsync()
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        return await connection.QuerySingleAsync<string>(
            """
            SELECT d.department_name
            FROM identity_users u
            JOIN departments d ON d.department_id = u.department_id
            WHERE u.normalized_email = :email
            """,
            new { email = TestIdentitySeeder.RequesterEmail.ToUpperInvariant() });
    }

    private static async Task<PasswordAuditRow> GetLatestPasswordAuditAsync(string userId)
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        return await connection.QuerySingleAsync<PasswordAuditRow>(
            """
            SELECT entity_type AS EntityType,
                   entity_id AS EntityId,
                   action AS Action,
                   actor AS Actor,
                   TO_CHAR(occurred_at, 'YYYY-MM-DD HH24:MI:SS.FF6') AS OccurredAt,
                   NVL(DBMS_LOB.SUBSTR(old_value, 4000, 1), '') AS OldValue,
                   NVL(DBMS_LOB.SUBSTR(new_value, 4000, 1), '') AS NewValue
            FROM audit_logs
            WHERE entity_type = 'User' AND entity_id = :userId AND action = 'ChangePassword'
            ORDER BY audit_log_id DESC
            FETCH FIRST 1 ROW ONLY
            """,
            new { userId });
    }

    private static async Task DeletePasswordAuditsAsync(string userId)
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync(
            "DELETE FROM audit_logs WHERE entity_type = 'User' AND entity_id = :userId AND action = 'ChangePassword'",
            new { userId });
        await connection.ExecuteAsync("COMMIT");
    }

    private static string ExtractToken(string html)
    {
        var match = TokenRegex().Match(html);
        Assert.True(match.Success, "頁面必須包含 AntiForgery request token。");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex TokenRegex();

    private sealed record PasswordAuditRow(
        string EntityType,
        string EntityId,
        string Action,
        string Actor,
        string OccurredAt,
        string? OldValue,
        string? NewValue);
}
