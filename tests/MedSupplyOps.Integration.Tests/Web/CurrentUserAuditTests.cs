using System.Globalization;
using System.Net;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Oracle.ManagedDataAccess.Client;
using Xunit.Abstractions;

namespace MedSupplyOps.Integration.Tests.Web;

/// <summary>
/// ★ 設計裁定 D4 的整合測試：CREATED_BY 不可以有靜默預設值。
///
/// 兩件事一起證明：
///   1. 沒有登入時，會寫 created_by 的操作要整個失敗——不是「填個預設值後照樣成功」。
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
    public async Task Anonymous_create_fails_and_persists_nothing()
    {
        var before = await CountRequisitionsAsync();

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

        var after = await CountRequisitionsAsync();

        // ★ T3：沒有靜默預設值——匿名要求會整個失敗（500），不是「填個 system 照樣成功」。
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(before, after);
        _output.WriteLine($"T3 HTTP={(int)response.StatusCode}；REQUISITIONS 筆數 {before} -> {after}（未增加）");
    }

    [Fact]
    public async Task Logged_in_create_records_the_actual_login_user_as_created_by()
    {
        const string email = "requester@example.local";
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
        await connection.ExecuteAsync("DELETE FROM requisition_lines WHERE requisition_id = :id", new { id });
        await connection.ExecuteAsync("DELETE FROM requisitions WHERE requisition_id = :id", new { id });
        await connection.ExecuteAsync("COMMIT");
    }

    private static async Task<(long DepartmentId, long ItemId)> GetSeedIdsAsync()
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        var departmentId = await connection.QuerySingleAsync<long>(
            "SELECT MIN(department_id) FROM departments WHERE is_active = 1 AND is_deleted = 0");
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
