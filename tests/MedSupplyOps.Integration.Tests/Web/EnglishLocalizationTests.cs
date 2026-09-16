using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc.Testing;
using Oracle.ManagedDataAccess.Client;
using Xunit.Abstractions;

namespace MedSupplyOps.Integration.Tests.Web;

/// <summary>
/// 英文版防退化關卡：以 <c>en</c> 文化取回清單列出的所有頁面，斷言頁面上不得出現
/// 一份指定的中文 UI 字串清單。清單刻意留在測試裡、可增補 —— 翻譯這種工作永遠會漏，
/// 而漏掉的地方只有切到英文才看得見；沒有這道關卡，下一個人新增一個頁面就又漏一批。
///
/// 只驗 UI 字串（頁面標題、區塊標題、表頭、欄位標籤、按鈕、空狀態文字、說明文字），
/// 不驗資料內容（品名、規格、科室名、批號、儲位、駁回原因等使用者輸入或種子資料）——
/// 種子品名（無菌手套…）本來就應該維持中文，不在這份清單裡。
/// </summary>
public sealed partial class EnglishLocalizationTests : IClassFixture<RequisitionFlowTests.RequisitionWebApplicationFactory>
{
    private static readonly string EnglishCultureCookie =
        $"{CookieRequestCultureProvider.DefaultCookieName}={Uri.EscapeDataString(CookieRequestCultureProvider.MakeCookieValue(new RequestCulture("en")))}";

    /// <summary>至少涵蓋實機檢查點名的例子；發現新的漏翻可以直接加進來。</summary>
    public static readonly string[] BannedChineseUiStrings =
    [
        // ★ 這三條是「整頁掃中日韓字元」抓到的，不是靠人工列清單想出來的。
        //   頁尾那條每一頁都有，卻在清單式斷言下活了下來 —— 見 L-038。
        "．版權所有", "個人資料為唯讀；如需修改請聯絡系統管理員。",
        "更新後，目前裝置會重新簽入，其他裝置的既有登入會失效。",
        "庫存總覽", "料號", "品名", "規格", "單位", "可用量", "安全存量",
        "展開批次明細（", "請選擇品項", "尚未選擇品項。", "查詢基準日", "基準日：",
        "近效期批次", "批號", "效期", "儲位", "N 天內到期", "此條件下沒有仍有數量的近效期批次。",
        "依狀態、科室與建立日期查詢請領單。", "請領單列表", "建立日起", "建立日迄",
        "依目前篩選條件顯示", "沒有符合條件的請領單。",
        "送審時間", "核准時間", "駁回原因", "行號", "品項代碼", "請領數量",
        "發料配批明細", "明細行", "發料數量", "發料動作", "審核動作",
        "品項主檔", "料號建立後不可修改", "目前沒有可管理的品項。",
        "維護可供請領與入庫使用的品項主檔。", "更新品項名稱、規格與安全存量。",
        "建立可供請領與入庫使用的品項主檔。", "計量單位建立後不可修改",
        "編輯品項", "停用品項",
        "批號與儲位會自動去除首尾空白並轉成大寫。",
        "帳號尚未設定角色或科室，請聯絡管理員。",
        "院內帳號登入後依角色顯示對應的工作儀表板。", "示範帳號", "關閉示範帳號提示", "填入",
        "密碼皆為", "忘記密碼請聯絡系統管理員。",
    ];

    private readonly RequisitionFlowTests.RequisitionWebApplicationFactory _factory;
    private readonly ITestOutputHelper _output;

    public EnglishLocalizationTests(RequisitionFlowTests.RequisitionWebApplicationFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    public static TheoryData<string, string> PagesByRole => new()
    {
        { "/Inventory", TestIdentitySeeder.StorekeeperEmail },
        { "/Inventory/Expiring", TestIdentitySeeder.StorekeeperEmail },
        { "/Requisitions", TestIdentitySeeder.StorekeeperEmail },
        { "/Requisitions/Create", TestIdentitySeeder.RequesterEmail },
        { "/Items", TestIdentitySeeder.AdministratorEmail },
        { "/Receiving", TestIdentitySeeder.StorekeeperEmail },
        { "/", TestIdentitySeeder.AdministratorEmail }, // 營運儀表板
        { "/", TestIdentitySeeder.RequesterEmail }, // 我的儀表板
        { "/", TestIdentitySeeder.NoRoleEmail }, // 未指派角色
        { "/Account/Profile", TestIdentitySeeder.RequesterEmail },
        { "/Account/ChangePassword", TestIdentitySeeder.RequesterEmail },
    };

    [Theory]
    [MemberData(nameof(PagesByRole))]
    public async Task Page_rendered_in_English_does_not_leak_untranslated_Chinese_UI_strings(string path, string email)
    {
        using var client = CreateEnglishClient();
        await WebAuthTestHelpers.LoginAsync(client, email);

        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        AssertNoLeakedChinese(path, html);
        _output.WriteLine($"T4 {path}（{email}）: 無未翻譯字串");
    }

    [Fact]
    public async Task Login_page_in_English_does_not_leak_untranslated_Chinese_UI_strings()
    {
        using var client = CreateEnglishClient();

        var html = await (await client.GetAsync("/Account/Login")).Content.ReadAsStringAsync();

        AssertNoLeakedChinese("/Account/Login", html);
        _output.WriteLine("T4 /Account/Login: 無未翻譯字串");
    }

    [Fact]
    public async Task Item_edit_in_English_does_not_leak_untranslated_Chinese_UI_strings()
    {
        using var client = CreateEnglishClient();
        await WebAuthTestHelpers.LoginAsync(client, TestIdentitySeeder.AdministratorEmail);

        var itemId = await GetSeedItemIdAsync();
        var html = await (await client.GetAsync($"/Items/Edit/{itemId}")).Content.ReadAsStringAsync();

        AssertNoLeakedChinese($"/Items/Edit/{itemId}", html);
        _output.WriteLine($"T4 /Items/Edit/{itemId}: 無未翻譯字串");
    }

    [Fact]
    public async Task Requisition_details_in_English_does_not_leak_untranslated_Chinese_UI_strings()
    {
        using var client = CreateEnglishClient();
        await WebAuthTestHelpers.LoginAsync(client, TestIdentitySeeder.AdministratorEmail);

        long id = 0;
        try
        {
            id = await CreatePendingRequisitionAsync(client);
            var html = await (await client.GetAsync($"/Requisitions/Details/{id}")).Content.ReadAsStringAsync();

            AssertNoLeakedChinese($"/Requisitions/Details/{id}", html);
            _output.WriteLine($"T4 /Requisitions/Details/{id}: 無未翻譯字串");
        }
        finally
        {
            if (id != 0)
            {
                await DeleteRequisitionAsync(id);
            }
        }
    }

    /// <summary>
    /// T4 鑑別力：把某個已翻譯的字串故意改回中文字面量（不包 @L），這條斷言必須變紅並指名那一頁。
    /// 這個測試本身不做突變，只是把斷言邏輯集中在一處，方便手動示範時重複使用同一份清單。
    /// </summary>
    private static void AssertNoLeakedChinese(string path, string html)
    {
        var decoded = WebUtility.HtmlDecode(html);
        var leaked = BannedChineseUiStrings.Where(s => decoded.Contains(s, StringComparison.Ordinal)).ToList();
        Assert.True(leaked.Count == 0, $"{path} 在英文文化下仍出現未翻譯字串：{string.Join("、", leaked)}");
    }

    private HttpClient CreateEnglishClient()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", EnglishCultureCookie);
        return client;
    }

    private static async Task<long> GetSeedItemIdAsync()
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        return await connection.QuerySingleAsync<long>("SELECT MIN(item_id) FROM items WHERE is_deleted = 0");
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

    private static async Task DeleteRequisitionAsync(long id)
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.ExecuteAsync(
            "DELETE FROM audit_logs WHERE entity_type = 'Requisition' AND entity_id = :entityId",
            new { entityId = id.ToString(CultureInfo.InvariantCulture) });
        await connection.ExecuteAsync("DELETE FROM requisition_lines WHERE requisition_id = :id", new { id });
        await connection.ExecuteAsync("DELETE FROM requisitions WHERE requisition_id = :id", new { id });
    }

    private static string ExtractToken(string html)
    {
        var match = AntiforgeryTokenRegex().Match(html);
        Assert.True(match.Success, "頁面必須包含 AntiForgery request token。");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryTokenRegex();
}
