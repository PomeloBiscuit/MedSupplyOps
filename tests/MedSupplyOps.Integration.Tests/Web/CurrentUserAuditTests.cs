using System.Globalization;
using System.Net;
using Dapper;
using MedSupplyOps.Web.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Oracle.ManagedDataAccess.Client;
using Xunit.Abstractions;

namespace MedSupplyOps.Integration.Tests.Web;

/// <summary>
/// ★ 設計裁定 D4 的整合測試：CREATED_BY 不可以有靜默預設值。
///
/// 兩件事一起證明：
///   1. 沒有登入時，授權層先拒絕；底層 Actor 取不到時仍會丟例外，不會填預設值。
///   2. 登入之後，created_by 是那個登入者的帳號，不是任何寫死的常數（過去是 "web"／"system"）。
/// </summary>
public sealed class CurrentUserAuditTests : IClassFixture<RequisitionFlowTests.RequisitionWebApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly ITestOutputHelper _output;

    public CurrentUserAuditTests(RequisitionFlowTests.RequisitionWebApplicationFactory factory, ITestOutputHelper output)
    {
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        _output = output;
    }

    [Fact]
    public async Task Anonymous_create_is_redirected_to_login_and_persists_nothing()
    {
        var before = await CountRequisitionsAsync();
        var response = await _client.GetAsync("/Requisitions/Create?asOf=2026-09-01");
        var after = await CountRequisitionsAsync();

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Account/Login", response.Headers.Location?.AbsolutePath);
        Assert.Equal(before, after);
        _output.WriteLine($"HTTP={(int)response.StatusCode} LOCATION={response.Headers.Location}；REQUISITIONS {before}->{after}");
    }

    [Fact]
    public void Missing_authenticated_user_throws_instead_of_using_a_default_actor()
    {
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var currentUser = new HttpContextCurrentUser(accessor);

        var exception = Assert.Throws<InvalidOperationException>(() => currentUser.Actor);
        Assert.Contains("無法取得目前登入使用者", exception.Message, StringComparison.Ordinal);
        _output.WriteLine($"ICurrentUser.Actor -> {exception.GetType().Name}: {exception.Message}");
    }

    [Fact]
    public async Task Logged_in_create_records_the_actual_login_user_as_created_by()
    {
        const string email = TestIdentitySeeder.RequesterEmail;
        await WebAuthTestHelpers.LoginAsync(_client, email);

        var createPage = await _client.GetAsync("/Requisitions/Create?asOf=2026-09-01");
        var token = ExtractToken(await createPage.Content.ReadAsStringAsync());
        var (departmentId, itemId) = await GetSeedIdsAsync();

        var response = await _client.PostAsync("/Requisitions/Create", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["AsOf"] = "2026-09-01",
                ["DepartmentId"] = departmentId.ToString(CultureInfo.InvariantCulture),
                ["Lines[0].ItemId"] = itemId.ToString(CultureInfo.InvariantCulture),
                ["Lines[0].Quantity"] = "1",
            }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var id = long.Parse(response.Headers.Location!.OriginalString.Split('/').Last(), CultureInfo.InvariantCulture);

        try
        {
            var createdBy = await GetCreatedByAsync(id);
            Assert.Equal(email, createdBy);
            _output.WriteLine($"T3 DB: REQUISITION_ID={id}, CREATED_BY={createdBy}（不是 \"system\" 也不是 \"web\"）");
        }
        finally
        {
            await DeleteRequisitionAsync(id);
        }
    }

    private static async Task<int> CountRequisitionsAsync()
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        return await connection.QuerySingleAsync<int>("SELECT COUNT(*) FROM requisitions");
    }

    private static async Task<string> GetCreatedByAsync(long id)
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        return await connection.QuerySingleAsync<string>(
            "SELECT created_by FROM requisitions WHERE requisition_id = :id", new { id });
    }

    private static async Task DeleteRequisitionAsync(long id)
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync(
            "DELETE FROM audit_logs WHERE entity_type = 'Requisition' AND entity_id = TO_CHAR(:id)", new { id });
        await connection.ExecuteAsync("DELETE FROM requisition_lines WHERE requisition_id = :id", new { id });
        await connection.ExecuteAsync("DELETE FROM requisitions WHERE requisition_id = :id", new { id });
        await connection.ExecuteAsync("COMMIT");
    }

    private static async Task<(long DepartmentId, long ItemId)> GetSeedIdsAsync()
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        var departmentId = await connection.QuerySingleAsync<long>(
            "SELECT department_id FROM identity_users WHERE normalized_user_name = :name",
            new { name = TestIdentitySeeder.RequesterEmail.ToUpperInvariant() });
        var itemId = await connection.QuerySingleAsync<long>(
            "SELECT MIN(item_id) FROM items WHERE is_deleted = 0");
        return (departmentId, itemId);
    }

    private static string ExtractToken(string html)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");
        Assert.True(match.Success, "頁面必須包含 AntiForgery request token。");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }
}
