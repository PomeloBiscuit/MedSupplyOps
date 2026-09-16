using System.Net;
using System.Text.RegularExpressions;
using Dapper;
using MedSupplyOps.Infrastructure.Identity;
using MedSupplyOps.Web.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Oracle.ManagedDataAccess.Client;
using Xunit.Abstractions;

namespace MedSupplyOps.Integration.Tests.Web;

public sealed partial class UserManagementWebTests
    : IClassFixture<RequisitionFlowTests.RequisitionWebApplicationFactory>
{
    private const string TestPassword = "ITest#2026PasswordA";
    private static readonly DateTimeOffset DisabledUntil = new(9999, 12, 31, 0, 0, 0, TimeSpan.Zero);

    private readonly RequisitionFlowTests.RequisitionWebApplicationFactory _factory;
    private readonly ITestOutputHelper _output;

    public UserManagementWebTests(
        RequisitionFlowTests.RequisitionWebApplicationFactory factory,
        ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [Fact]
    public async Task T1_last_administrator_and_self_guards_block_all_four_direct_posts()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var actorEmail = $"itest-x-t1-actor-{suffix}@example.local";
        var targetEmail = $"itest-x-t1-target-{suffix}@example.local";
        var actor = await CreateUserAsync(actorEmail, "T1 操作者", ApplicationRoles.Administrator);
        var target = await CreateUserAsync(targetEmail, "T1 最後管理員", ApplicationRoles.Administrator);
        List<AdministratorStatusRow> originalStatuses = [];

        try
        {
            using var client = CreateClient();
            await LoginAsync(client, actorEmail, TestPassword);
            var token = await GetTokenAsync(client, "/Users");
            originalStatuses = await GetAdministratorStatusesAsync();
            await DisableEveryAdministratorExceptAsync(target.Id);
            Assert.Equal(1, await CountEnabledAdministratorsAsync());

            var disableLast = await PostSetEnabledAsync(client, token, target.Id, enabled: false);
            var disableLastMessage = await FollowAndReadAsync(client, disableLast);
            var targetAfterDisable = await GetUserStateAsync(target.Id);
            Assert.True(targetAfterDisable.Enabled);
            Assert.Equal(ApplicationRoles.Administrator, targetAfterDisable.Role);
            Assert.Contains("系統必須至少保留一位啟用中的管理員。", disableLastMessage, StringComparison.Ordinal);

            var demoteLast = await PostEditAsync(client, token, target.Id, "T1 最後管理員", ApplicationRoles.Storekeeper, null);
            var demoteLastHtml = WebUtility.HtmlDecode(await demoteLast.Content.ReadAsStringAsync());
            var targetAfterDemote = await GetUserStateAsync(target.Id);
            Assert.Equal(HttpStatusCode.OK, demoteLast.StatusCode);
            Assert.Contains("系統必須至少保留一位啟用中的管理員。", demoteLastHtml, StringComparison.Ordinal);
            Assert.True(targetAfterDemote.Enabled);
            Assert.Equal(ApplicationRoles.Administrator, targetAfterDemote.Role);

            await SetAdministrativeStatusDirectAsync(actor.Id, enabled: true);
            Assert.Equal(2, await CountEnabledAdministratorsAsync());

            var disableSelf = await PostSetEnabledAsync(client, token, actor.Id, enabled: false);
            var disableSelfMessage = await FollowAndReadAsync(client, disableSelf);
            var actorAfterDisable = await GetUserStateAsync(actor.Id);
            Assert.True(actorAfterDisable.Enabled);
            Assert.Equal(ApplicationRoles.Administrator, actorAfterDisable.Role);
            Assert.Contains("管理員不能停用自己的帳號。", disableSelfMessage, StringComparison.Ordinal);

            var demoteSelf = await PostEditAsync(client, token, actor.Id, "T1 操作者", ApplicationRoles.Storekeeper, null);
            var demoteSelfHtml = WebUtility.HtmlDecode(await demoteSelf.Content.ReadAsStringAsync());
            var actorAfterDemote = await GetUserStateAsync(actor.Id);
            Assert.Equal(HttpStatusCode.OK, demoteSelf.StatusCode);
            Assert.Contains("管理員不能把自己的角色降級", demoteSelfHtml, StringComparison.Ordinal);
            Assert.True(actorAfterDemote.Enabled);
            Assert.Equal(ApplicationRoles.Administrator, actorAfterDemote.Role);

            _output.WriteLine($"T1(a) POST /Users/SetEnabled(last,false): HTTP {(int)disableLast.StatusCode} -> {disableLast.Headers.Location?.OriginalString}; 回應=系統必須至少保留一位啟用中的管理員；DB enabled={targetAfterDisable.Enabled}, role={targetAfterDisable.Role}");
            _output.WriteLine($"T1(b) POST /Users/Edit(last,Storekeeper): HTTP {(int)demoteLast.StatusCode}; 回應=系統必須至少保留一位啟用中的管理員；DB enabled={targetAfterDemote.Enabled}, role={targetAfterDemote.Role}");
            _output.WriteLine($"T1(c) POST /Users/SetEnabled(self,false): HTTP {(int)disableSelf.StatusCode} -> {disableSelf.Headers.Location?.OriginalString}; 回應=管理員不能停用自己的帳號；DB enabled={actorAfterDisable.Enabled}, role={actorAfterDisable.Role}");
            _output.WriteLine($"T1(d) POST /Users/Edit(self,Storekeeper): HTTP {(int)demoteSelf.StatusCode}; 回應=管理員不能把自己的角色降級；DB enabled={actorAfterDemote.Enabled}, role={actorAfterDemote.Role}");
        }
        finally
        {
            await RestoreAdministratorStatusesAsync(originalStatuses);
            await DeleteUserAsync(actor.Id);
            await DeleteUserAsync(target.Id);
        }
    }

    [Fact]
    public async Task T2_requester_and_storekeeper_are_denied_by_every_user_management_endpoint()
    {
        var administratorId = await GetUserIdAsync(TestIdentitySeeder.AdministratorEmail);
        var forbiddenEmail = $"itest-x-t2-{Guid.NewGuid():N}@example.local";
        var endpoints = new (string Method, string Path, IReadOnlyDictionary<string, string>? Form)[]
        {
            ("GET", "/Users", null),
            ("GET", "/Users/Create", null),
            ("GET", $"/Users/Edit/{administratorId}", null),
            ("GET", "/Users/ResetPasswordResult", null),
            ("POST", "/Users/Create", new Dictionary<string, string>
            {
                ["Email"] = forbiddenEmail,
                ["DisplayName"] = "T2 禁止建立",
                ["Role"] = ApplicationRoles.Storekeeper,
                ["InitialPassword"] = TestPassword,
            }),
            ("POST", $"/Users/Edit/{administratorId}", new Dictionary<string, string>
            {
                ["DisplayName"] = "T2 禁止編輯",
                ["Role"] = ApplicationRoles.Storekeeper,
            }),
            ("POST", $"/Users/SetEnabled/{administratorId}", new Dictionary<string, string> { ["enabled"] = "false" }),
            ("POST", $"/Users/ResetPassword/{administratorId}", new Dictionary<string, string>()),
        };

        foreach (var account in new[] { TestIdentitySeeder.RequesterEmail, TestIdentitySeeder.StorekeeperEmail })
        {
            using var client = CreateClient();
            await WebAuthTestHelpers.LoginAsync(client, account);
            var token = await GetTokenAsync(client, "/Account/Login");
            foreach (var endpoint in endpoints)
            {
                using var response = endpoint.Method == "GET"
                    ? await client.GetAsync(endpoint.Path)
                    : await client.PostAsync(endpoint.Path, WithToken(token, endpoint.Form!));
                Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
                Assert.Equal("/Account/AccessDenied", response.Headers.Location?.AbsolutePath);
                _output.WriteLine($"T2 {account,-31} {endpoint.Method,-4} {endpoint.Path,-70} HTTP {(int)response.StatusCode} -> {response.Headers.Location?.AbsolutePath}");
            }
        }

        Assert.Null(await FindUserIdAsync(forbiddenEmail));
    }

    [Fact]
    public async Task T3_disabled_user_gets_generic_login_failure_then_can_login_after_enable()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var email = $"itest-x-t3-{suffix}@example.local";
        var departmentId = await GetDepartmentIdAsync();
        var user = await CreateUserAsync(email, "T3 請領人", ApplicationRoles.Requester, departmentId);

        try
        {
            using var administrator = CreateClient();
            await WebAuthTestHelpers.LoginAsync(administrator, TestIdentitySeeder.AdministratorEmail);
            var token = await GetTokenAsync(administrator, "/Users");
            var disable = await PostSetEnabledAsync(administrator, token, user.Id, enabled: false);
            Assert.Equal(HttpStatusCode.Redirect, disable.StatusCode);

            using var disabledClient = CreateClient();
            var disabledLogin = await PostLoginAsync(disabledClient, email, TestPassword);
            var disabledHtml = WebUtility.HtmlDecode(await disabledLogin.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.OK, disabledLogin.StatusCode);
            Assert.Contains("帳號或密碼不正確。", disabledHtml, StringComparison.Ordinal);
            Assert.DoesNotContain("停用", disabledHtml, StringComparison.Ordinal);
            Assert.DoesNotContain("鎖定", disabledHtml, StringComparison.Ordinal);

            var enable = await PostSetEnabledAsync(administrator, token, user.Id, enabled: true);
            Assert.Equal(HttpStatusCode.Redirect, enable.StatusCode);
            using var enabledClient = CreateClient();
            var enabledLogin = await PostLoginAsync(enabledClient, email, TestPassword);
            Assert.Equal(HttpStatusCode.Redirect, enabledLogin.StatusCode);

            var audits = await GetAuditsAsync(user.Id);
            Assert.Contains(audits, audit => audit.Action == "Disable");
            Assert.Contains(audits, audit => audit.Action == "Enable");
            Assert.DoesNotContain(audits, audit => AuditContains(audit, TestPassword));
            _output.WriteLine("T3 停用後以正確密碼登入：HTTP 200，畫面訊息『帳號或密碼不正確。』；未出現『停用』或『鎖定』");
            _output.WriteLine("T3 啟用後以相同正確密碼登入：HTTP 302（成功）");
        }
        finally
        {
            await DeleteUserAsync(user.Id);
        }
    }

    [Fact]
    public async Task T4_reset_password_is_one_time_invalidates_old_password_and_session_and_audits_no_secret()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var email = $"itest-x-t4-{suffix}@example.local";
        var departmentId = await GetDepartmentIdAsync();
        var user = await CreateUserAsync(email, "T4 請領人", ApplicationRoles.Requester, departmentId);

        try
        {
            using var existingSession = CreateClient();
            await LoginAsync(existingSession, email, TestPassword);
            using var administrator = CreateClient();
            administrator.DefaultRequestHeaders.TryAddWithoutValidation(
                "Cookie",
                ".AspNetCore.Culture=c%3Den%7Cuic%3Den");
            await WebAuthTestHelpers.LoginAsync(administrator, TestIdentitySeeder.AdministratorEmail);
            var token = await GetTokenAsync(administrator, "/Users");

            var reset = await administrator.PostAsync($"/Users/ResetPassword/{user.Id}", WithToken(token));
            Assert.Equal(HttpStatusCode.Redirect, reset.StatusCode);
            Assert.Equal("/Users/ResetPasswordResult", reset.Headers.Location?.OriginalString);
            var result = await administrator.GetAsync(reset.Headers.Location);
            var resultHtml = WebUtility.HtmlDecode(await result.Content.ReadAsStringAsync());
            var passwordMatch = NewPasswordRegex().Match(resultHtml);
            Assert.Equal(HttpStatusCode.OK, result.StatusCode);
            Assert.True(passwordMatch.Success, "重設結果頁必須顯示一次新密碼。");
            Assert.Contains("This new password is shown only once.", resultHtml, StringComparison.Ordinal);
            var newPassword = passwordMatch.Groups[1].Value;
            Assert.NotEqual(TestPassword, newPassword);

            var secondRead = await administrator.GetAsync("/Users/ResetPasswordResult");
            Assert.Equal(HttpStatusCode.Redirect, secondRead.StatusCode);
            Assert.Equal("/Users", secondRead.Headers.Location?.OriginalString);

            using var oldPasswordClient = CreateClient();
            using var newPasswordClient = CreateClient();
            var oldLogin = await PostLoginAsync(oldPasswordClient, email, TestPassword);
            var newLogin = await PostLoginAsync(newPasswordClient, email, newPassword);
            Assert.Equal(HttpStatusCode.OK, oldLogin.StatusCode);
            Assert.Equal(HttpStatusCode.Redirect, newLogin.StatusCode);

            var staleSession = await existingSession.GetAsync("/");
            Assert.Equal(HttpStatusCode.Redirect, staleSession.StatusCode);
            Assert.Equal("/Account/Login", staleSession.Headers.Location?.AbsolutePath);

            var audit = (await GetAuditsAsync(user.Id)).Single(item => item.Action == "ResetPassword");
            Assert.Equal("User", audit.EntityType);
            Assert.False(AuditContains(audit, TestPassword));
            Assert.False(AuditContains(audit, newPassword));
            Assert.DoesNotContain("token", audit.OldValue + audit.NewValue, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Requester", audit.OldValue, StringComparison.Ordinal);
            Assert.Contains(departmentId.ToString(System.Globalization.CultureInfo.InvariantCulture), audit.NewValue, StringComparison.Ordinal);

            _output.WriteLine("T4 舊密碼登入：HTTP 200 留在登入頁（失敗）；新密碼登入：HTTP 302（成功）");
            _output.WriteLine($"T4 原登入工作階段下一請求：HTTP {(int)staleSession.StatusCode} -> {staleSession.Headers.Location?.AbsolutePath}");
            _output.WriteLine($"T4 audit_logs：ENTITY_TYPE={audit.EntityType}, ACTION={audit.Action}, ENTITY_ID={audit.EntityId}, OLD_VALUE={audit.OldValue}, NEW_VALUE={audit.NewValue}；所有欄位均不含舊／新密碼或權杖");
            _output.WriteLine("T4 結果頁第一次 HTTP 200；第二次 HTTP 302 -> /Users（新密碼只顯示一次）");
        }
        finally
        {
            await DeleteUserAsync(user.Id);
        }
    }

    [Fact]
    public async Task T5_requester_without_department_is_rejected_on_create_and_edit()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var createEmail = $"itest-x-t5-create-{suffix}@example.local";
        var editEmail = $"itest-x-t5-edit-{suffix}@example.local";
        var departmentId = await GetDepartmentIdAsync();
        var existing = await CreateUserAsync(editEmail, "T5 既有請領人", ApplicationRoles.Requester, departmentId);

        try
        {
            using var administrator = CreateClient();
            await WebAuthTestHelpers.LoginAsync(administrator, TestIdentitySeeder.AdministratorEmail);
            var token = await GetTokenAsync(administrator, "/Users/Create");
            var create = await administrator.PostAsync("/Users/Create", WithToken(token, new Dictionary<string, string>
            {
                ["Email"] = createEmail,
                ["DisplayName"] = "T5 無科室請領人",
                ["Role"] = ApplicationRoles.Requester,
                ["InitialPassword"] = TestPassword,
            }));
            var createHtml = WebUtility.HtmlDecode(await create.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.OK, create.StatusCode);
            Assert.Contains("請領人必須選擇科室。", createHtml, StringComparison.Ordinal);
            Assert.Null(await FindUserIdAsync(createEmail));

            var edit = await PostEditAsync(administrator, token, existing.Id, "T5 既有請領人", ApplicationRoles.Requester, null);
            var editHtml = WebUtility.HtmlDecode(await edit.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
            Assert.Contains("請領人必須選擇科室。", editHtml, StringComparison.Ordinal);
            var state = await GetUserStateAsync(existing.Id);
            Assert.Equal(departmentId, state.DepartmentId);

            _output.WriteLine("T5 新增 Requester、DepartmentId 空白：HTTP 200，訊息『請領人必須選擇科室。』；DB 未建立帳號");
            _output.WriteLine($"T5 編輯既有 Requester、DepartmentId 空白：HTTP 200，同訊息；DB DepartmentId 仍為 {state.DepartmentId}");
        }
        finally
        {
            await DeleteUserAsync(existing.Id);
        }
    }

    [Fact]
    public async Task Create_edit_and_filters_persist_identity_values_and_audit_role_and_department_without_password()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var email = $"itest-x-lifecycle-{suffix}@example.local";
        var departmentId = await GetDepartmentIdAsync();
        string? userId = null;

        try
        {
            using var administrator = CreateClient();
            await WebAuthTestHelpers.LoginAsync(administrator, TestIdentitySeeder.AdministratorEmail);
            var token = await GetTokenAsync(administrator, "/Users/Create");
            var create = await administrator.PostAsync("/Users/Create", WithToken(token, new Dictionary<string, string>
            {
                ["Email"] = email,
                ["DisplayName"] = "Lifecycle Storekeeper",
                ["Role"] = ApplicationRoles.Storekeeper,
                ["InitialPassword"] = TestPassword,
            }));
            Assert.Equal(HttpStatusCode.Redirect, create.StatusCode);
            userId = await FindUserIdAsync(email);
            Assert.NotNull(userId);

            var filteredList = await administrator.GetAsync("/Users?role=Storekeeper&status=Enabled");
            var filteredHtml = WebUtility.HtmlDecode(await filteredList.Content.ReadAsStringAsync());
            Assert.Contains(email, filteredHtml, StringComparison.Ordinal);

            var edit = await PostEditAsync(
                administrator,
                token,
                userId,
                "Lifecycle Requester",
                ApplicationRoles.Requester,
                departmentId);
            Assert.Equal(HttpStatusCode.Redirect, edit.StatusCode);
            var state = await GetUserStateAsync(userId);
            Assert.Equal(ApplicationRoles.Requester, state.Role);
            Assert.Equal(departmentId, state.DepartmentId);

            var audits = await GetAuditsAsync(userId);
            var createAudit = Assert.Single(audits, audit => audit.Action == "Create");
            var updateAudit = Assert.Single(audits, audit => audit.Action == "Update");
            Assert.Contains("Storekeeper", createAudit.NewValue, StringComparison.Ordinal);
            Assert.Contains("Storekeeper", updateAudit.OldValue, StringComparison.Ordinal);
            Assert.Contains("Requester", updateAudit.NewValue, StringComparison.Ordinal);
            Assert.Contains(departmentId.ToString(System.Globalization.CultureInfo.InvariantCulture), updateAudit.NewValue, StringComparison.Ordinal);
            Assert.DoesNotContain(audits, audit => AuditContains(audit, TestPassword));
        }
        finally
        {
            if (userId is not null)
            {
                await DeleteUserAsync(userId);
            }
        }
    }

    private HttpClient CreateClient()
        => _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private async Task<ApplicationUser> CreateUserAsync(
        string email,
        string displayName,
        string role,
        long? departmentId = null)
    {
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = displayName,
            DepartmentId = departmentId,
            LockoutEnabled = true,
        };
        EnsureSucceeded(await userManager.CreateAsync(user, TestPassword), $"建立 {email}");
        EnsureSucceeded(await userManager.AddToRoleAsync(user, role), $"指派 {role}");
        return user;
    }

    private static async Task LoginAsync(HttpClient client, string email, string password)
    {
        var response = await PostLoginAsync(client, email, password);
        if (response.StatusCode != HttpStatusCode.Redirect)
        {
            throw new InvalidOperationException($"登入 {email} 失敗：HTTP {(int)response.StatusCode}");
        }
    }

    private static async Task<HttpResponseMessage> PostLoginAsync(HttpClient client, string email, string password)
    {
        var token = await GetTokenAsync(client, "/Account/Login");
        return await client.PostAsync("/Account/Login", WithToken(token, new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = password,
        }));
    }

    private static async Task<HttpResponseMessage> PostSetEnabledAsync(
        HttpClient client,
        string token,
        string userId,
        bool enabled)
        => await client.PostAsync($"/Users/SetEnabled/{userId}", WithToken(token, new Dictionary<string, string>
        {
            ["enabled"] = enabled ? "true" : "false",
        }));

    private static async Task<HttpResponseMessage> PostEditAsync(
        HttpClient client,
        string token,
        string userId,
        string displayName,
        string role,
        long? departmentId)
    {
        var values = new Dictionary<string, string>
        {
            ["DisplayName"] = displayName,
            ["Role"] = role,
        };
        if (departmentId is not null)
        {
            values["DepartmentId"] = departmentId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return await client.PostAsync($"/Users/Edit/{userId}", WithToken(token, values));
    }

    private static async Task<string> FollowAndReadAsync(HttpClient client, HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var followed = await client.GetAsync(response.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, followed.StatusCode);
        return WebUtility.HtmlDecode(await followed.Content.ReadAsStringAsync());
    }

    private static async Task<string> GetTokenAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        var html = await response.Content.ReadAsStringAsync();
        var match = AntiforgeryTokenRegex().Match(html);
        Assert.True(match.Success, $"{path} 必須包含 AntiForgery request token。");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static FormUrlEncodedContent WithToken(
        string token,
        IReadOnlyDictionary<string, string>? values = null)
    {
        var form = values is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(values, StringComparer.Ordinal);
        form["__RequestVerificationToken"] = token;
        return new FormUrlEncodedContent(form);
    }

    private static async Task<long> GetDepartmentIdAsync()
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        return await connection.QuerySingleAsync<long>(
            "SELECT MIN(department_id) FROM departments WHERE is_active = 1 AND is_deleted = 0");
    }

    private static async Task<string> GetUserIdAsync(string email)
        => await FindUserIdAsync(email) ?? throw new InvalidOperationException($"找不到 {email}");

    private static async Task<string?> FindUserIdAsync(string email)
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        return await connection.QuerySingleOrDefaultAsync<string>(
            "SELECT id FROM identity_users WHERE normalized_email = :email",
            new { email = email.ToUpperInvariant() });
    }

    private static async Task<UserStateRow> GetUserStateAsync(string userId)
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        var row = await connection.QuerySingleAsync<UserStateDatabaseRow>(
            """
            SELECT u.department_id AS DepartmentId,
                   u.lockout_enabled AS LockoutEnabled,
                   u.lockout_end AS LockoutEnd,
                   r.name AS Role
            FROM identity_users u
            JOIN identity_user_roles ur ON ur.user_id = u.id
            JOIN identity_roles r ON r.id = ur.role_id
            WHERE u.id = :userId
            """,
            new { userId });
        var enabled = row.LockoutEnabled == 0 || row.LockoutEnd is null || row.LockoutEnd.Value.Year < 9999;
        var departmentId = row.DepartmentId is decimal value ? decimal.ToInt64(value) : (long?)null;
        return new UserStateRow(row.Role, departmentId, enabled);
    }

    private static async Task<List<AdministratorStatusRow>> GetAdministratorStatusesAsync()
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        var rows = await connection.QueryAsync<AdministratorStatusRow>(
            """
            SELECT u.id AS Id, u.lockout_enabled AS LockoutEnabled, u.lockout_end AS LockoutEnd
            FROM identity_users u
            JOIN identity_user_roles ur ON ur.user_id = u.id
            JOIN identity_roles r ON r.id = ur.role_id
            WHERE r.normalized_name = 'ADMINISTRATOR'
            """);
        return rows.AsList();
    }

    private static async Task DisableEveryAdministratorExceptAsync(string enabledUserId)
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync(
            """
            UPDATE identity_users
            SET lockout_enabled = 1, lockout_end = :disabledUntil
            WHERE id IN (
                SELECT ur.user_id
                FROM identity_user_roles ur
                JOIN identity_roles r ON r.id = ur.role_id
                WHERE r.normalized_name = 'ADMINISTRATOR'
            )
              AND id <> :enabledUserId
            """,
            new { disabledUntil = DisabledUntil, enabledUserId });
        await connection.ExecuteAsync("COMMIT");
    }

    private static async Task SetAdministrativeStatusDirectAsync(string userId, bool enabled)
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync(
            "UPDATE identity_users SET lockout_enabled = 1, lockout_end = :lockoutEnd WHERE id = :userId",
            new { lockoutEnd = enabled ? (DateTimeOffset?)null : DisabledUntil, userId });
        await connection.ExecuteAsync("COMMIT");
    }

    private static async Task RestoreAdministratorStatusesAsync(IEnumerable<AdministratorStatusRow> statuses)
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.OpenAsync();
        foreach (var status in statuses)
        {
            await connection.ExecuteAsync(
                "UPDATE identity_users SET lockout_enabled = :lockoutEnabled, lockout_end = :lockoutEnd WHERE id = :id",
                new { status.LockoutEnabled, status.LockoutEnd, status.Id });
        }

        await connection.ExecuteAsync("COMMIT");
    }

    private static async Task<int> CountEnabledAdministratorsAsync()
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        return await connection.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM identity_users u
            JOIN identity_user_roles ur ON ur.user_id = u.id
            JOIN identity_roles r ON r.id = ur.role_id
            WHERE r.normalized_name = 'ADMINISTRATOR'
              AND (u.lockout_enabled = 0 OR u.lockout_end IS NULL OR EXTRACT(YEAR FROM u.lockout_end) < 9999)
            """);
    }

    private static async Task<List<UserAuditRow>> GetAuditsAsync(string userId)
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        var rows = await connection.QueryAsync<UserAuditRow>(
            """
            SELECT entity_type AS EntityType,
                   entity_id AS EntityId,
                   action AS Action,
                   actor AS Actor,
                   NVL(DBMS_LOB.SUBSTR(old_value, 4000, 1), '') AS OldValue,
                   NVL(DBMS_LOB.SUBSTR(new_value, 4000, 1), '') AS NewValue
            FROM audit_logs
            WHERE entity_type = 'User' AND entity_id = :userId
            ORDER BY audit_log_id
            """,
            new { userId });
        return rows.AsList();
    }

    private static bool AuditContains(UserAuditRow audit, string value)
        => string.Join('|', audit.EntityType, audit.EntityId, audit.Action, audit.Actor, audit.OldValue, audit.NewValue)
            .Contains(value, StringComparison.Ordinal);

    private static async Task DeleteUserAsync(string userId)
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync("DELETE FROM audit_logs WHERE entity_type = 'User' AND entity_id = :userId", new { userId });
        await connection.ExecuteAsync("DELETE FROM identity_user_tokens WHERE user_id = :userId", new { userId });
        await connection.ExecuteAsync("DELETE FROM identity_user_logins WHERE user_id = :userId", new { userId });
        await connection.ExecuteAsync("DELETE FROM identity_user_claims WHERE user_id = :userId", new { userId });
        await connection.ExecuteAsync("DELETE FROM identity_user_roles WHERE user_id = :userId", new { userId });
        await connection.ExecuteAsync("DELETE FROM identity_users WHERE id = :userId", new { userId });
        await connection.ExecuteAsync("COMMIT");
    }

    private static void EnsureSucceeded(IdentityResult result, string action)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"{action}失敗：{string.Join("；", result.Errors.Select(error => error.Description))}");
        }
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryTokenRegex();

    [GeneratedRegex("data-testid=\"new-password\">([^<]+)</code>")]
    private static partial Regex NewPasswordRegex();

    private sealed class AdministratorStatusRow
    {
        public string Id { get; init; } = string.Empty;

        public short LockoutEnabled { get; init; }

        public DateTimeOffset? LockoutEnd { get; init; }
    }

    private sealed class UserStateDatabaseRow
    {
        public decimal? DepartmentId { get; init; }

        public short LockoutEnabled { get; init; }

        public DateTimeOffset? LockoutEnd { get; init; }

        public string Role { get; init; } = string.Empty;
    }

    private sealed record UserStateRow(string Role, long? DepartmentId, bool Enabled);

    private sealed record UserAuditRow(
        string EntityType,
        string EntityId,
        string Action,
        string Actor,
        string OldValue,
        string NewValue);
}
