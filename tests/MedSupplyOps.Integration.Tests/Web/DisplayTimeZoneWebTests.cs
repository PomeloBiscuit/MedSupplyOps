using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Dapper;
using MedSupplyOps.Web.Localization;
using Microsoft.AspNetCore.Mvc.Testing;
using Oracle.ManagedDataAccess.Client;
using Xunit.Abstractions;

namespace MedSupplyOps.Integration.Tests.Web;

/// <summary>
/// 顯示時區清單擴充後的驗證。
/// 業務日期（FEFO、請領單號）刻意不跟著這個偏好走，見 <see cref="MedSupplyOps.Infrastructure.Time.BusinessCalendar"/>；
/// 這裡用同一個固定時鐘（<see cref="TestBusinessCalendar.DefaultInstant"/>：UTC 停在台北日期已跨零點、
/// 紐約日期還沒跨的那個邊界時段）證明「換顯示時區只改呈現，不改單號日期」。
/// </summary>
public sealed partial class DisplayTimeZoneWebTests : IClassFixture<RequisitionFlowTests.RequisitionWebApplicationFactory>
{
    private readonly RequisitionFlowTests.RequisitionWebApplicationFactory _factory;
    private readonly ITestOutputHelper _output;

    public DisplayTimeZoneWebTests(RequisitionFlowTests.RequisitionWebApplicationFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [Fact]
    public void Supported_display_time_zone_ids_all_resolve_via_FindSystemTimeZoneById()
    {
        Assert.True(DisplayTimeZone.SupportedIds.Count > 100, "顯示時區必須由系統資料動態提供全球清單。");
        foreach (var id in DisplayTimeZone.SupportedIds)
        {
            var resolved = TimeZoneInfo.FindSystemTimeZoneById(id);
            Assert.NotNull(resolved);
            Assert.True(id == "UTC" || id.Contains('/'), $"{id} 不是 IANA ID。");
        }

        _output.WriteLine($"Y-T3 dynamically discovered IANA ids: {DisplayTimeZone.SupportedIds.Count}");
    }

    [Fact]
    public void Daylight_saving_zone_has_different_winter_and_summer_offsets()
    {
        var london = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
        var winter = london.GetUtcOffset(new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero));
        var summer = london.GetUtcOffset(new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero));

        Assert.NotEqual(winter, summer);
        _output.WriteLine($"Y-T3 Europe/London winter offset={winter}, summer offset={summer}；選單只能顯示現在的偏移。");
    }

    [Fact]
    public async Task Invalid_time_zone_cookie_falls_back_to_default_without_500()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", $"{DisplayTimeZone.CookieName}=Mars/Olympus");
        await WebAuthTestHelpers.LoginAsync(client, TestIdentitySeeder.AdministratorEmail);

        var response = await client.GetAsync("/");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Matches("value=\"Asia/Taipei\"[^>]*selected", html);
        _output.WriteLine("Y-T4 cookie Mars/Olympus -> HTTP 200；畫面選取 Asia/Taipei。");
    }

    [Fact]
    public void Supported_display_time_zones_cover_the_design_decision_D3_set()
    {
        var required = new[]
        {
            "Asia/Taipei", "UTC", "Asia/Tokyo", "Asia/Shanghai", "Asia/Singapore",
            "Asia/Seoul", "Europe/London", "America/New_York", "America/Los_Angeles",
        };

        foreach (var id in required)
        {
            Assert.Contains(id, DisplayTimeZone.SupportedIds);
        }
    }

    /// <summary>
    /// T3 驗收核心：切到 America/New_York 後，清單顯示時間改變，
    /// 但請領單號日期（由 BusinessCalendar 固定 Asia/Taipei 決定）不變。
    /// </summary>
    [Fact]
    public async Task Switching_display_time_zone_changes_rendered_time_but_not_the_requisition_number_date()
    {
        // 兩個獨立 client，各自在登入前就釘死顯示時區 cookie —— 避免中途改 Cookie 標頭
        // 和 HttpClient 自己用 CookieContainer 追蹤的登入 Cookie 互相覆寫。
        using var taipeiClient = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        taipeiClient.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", "mso-display-time-zone=Asia/Taipei");
        await WebAuthTestHelpers.LoginAsync(taipeiClient, TestIdentitySeeder.AdministratorEmail);

        using var losAngelesClient = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        losAngelesClient.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", "mso-display-time-zone=America/Los_Angeles");
        await WebAuthTestHelpers.LoginAsync(losAngelesClient, TestIdentitySeeder.AdministratorEmail);

        var expectedTaipeiDate = TestBusinessCalendar.Today.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        long id = 0;
        try
        {
            id = await CreatePendingRequisitionAsync(taipeiClient);
            var requisitionNo = await GetRequisitionNoAsync(id);

            Assert.StartsWith($"REQ-{expectedTaipeiDate}-", requisitionNo, StringComparison.Ordinal);

            var taipeiHtml = await GetHtmlAsync(taipeiClient, "/Requisitions");
            var losAngelesHtml = await GetHtmlAsync(losAngelesClient, "/Requisitions");
            var expiryHtml = await GetHtmlAsync(losAngelesClient, "/Inventory/Expiring");

            // 單號本身（含日期）在兩種顯示時區下必須完全一致：業務日期不跟著偏好走。
            Assert.Contains(requisitionNo, taipeiHtml, StringComparison.Ordinal);
            Assert.Contains(requisitionNo, losAngelesHtml, StringComparison.Ordinal);
            Assert.Contains(TestBusinessCalendar.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), expiryHtml, StringComparison.Ordinal);

            var taipeiTime = TimeCellRegex().Match(Fragment(taipeiHtml, requisitionNo)).Groups[1].Value;
            var losAngelesTime = TimeCellRegex().Match(Fragment(losAngelesHtml, requisitionNo)).Groups[1].Value;
            Assert.NotEqual(taipeiTime, losAngelesTime);

            _output.WriteLine($"T3 requisition no.（兩種顯示時區皆同）: {requisitionNo}");
            _output.WriteLine($"T3 Asia/Taipei 顯示時間: {taipeiTime}");
            _output.WriteLine($"Y-T5 America/Los_Angeles 顯示時間: {losAngelesTime}");
            _output.WriteLine($"Y-T5 請領單號日期={expectedTaipeiDate}；效期預警基準日={TestBusinessCalendar.Today:yyyy-MM-dd}（皆固定台北業務日）。");
        }
        finally
        {
            if (id != 0)
            {
                await DeleteRequisitionAsync(id);
            }
        }
    }

    private static async Task<long> CreatePendingRequisitionAsync(HttpClient client)
    {
        var createPage = await client.GetAsync("/Requisitions/Create?asOf=2026-09-01");
        var token = ExtractToken(await createPage.Content.ReadAsStringAsync());
        var (departmentId, itemId) = await GetSeedIdsAsync();
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["AsOf"] = "2026-09-01",
            ["DepartmentId"] = departmentId.ToString(CultureInfo.InvariantCulture),
            ["Lines[0].ItemId"] = itemId.ToString(CultureInfo.InvariantCulture),
            ["Lines[0].Quantity"] = "1",
        });
        var response = await client.PostAsync("/Requisitions/Create", form);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return long.Parse(response.Headers.Location!.OriginalString.Split('/').Last(), CultureInfo.InvariantCulture);
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

    private static async Task<string> GetRequisitionNoAsync(long id)
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        return await connection.QuerySingleAsync<string>(
            "SELECT requisition_no FROM requisitions WHERE requisition_id = :id", new { id });
    }

    private static async Task DeleteRequisitionAsync(long id)
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.ExecuteAsync(
            "DELETE FROM audit_logs WHERE entity_type = 'Requisition' AND entity_id = :entityId",
            new { entityId = id.ToString(CultureInfo.InvariantCulture) });
        await connection.ExecuteAsync("DELETE FROM requisition_lines WHERE requisition_id = :id", new { id });
        await connection.ExecuteAsync("DELETE FROM requisitions WHERE requisition_id = :id", new { id });
    }

    private static async Task<string> GetHtmlAsync(HttpClient client, string path)
        => WebUtility.HtmlDecode(await client.GetStringAsync(path));

    private static string Fragment(string html, string marker)
    {
        var start = Math.Max(0, html.IndexOf(marker, StringComparison.Ordinal) - 40);
        var end = Math.Min(html.Length, html.IndexOf(marker, StringComparison.Ordinal) + 400);
        return html[start..end];
    }

    private static string ExtractToken(string html)
    {
        var match = AntiforgeryTokenRegex().Match(html);
        Assert.True(match.Success, "頁面必須包含 AntiForgery request token。");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryTokenRegex();

    [GeneratedRegex(@"<td>(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})</td>")]
    private static partial Regex TimeCellRegex();
}
