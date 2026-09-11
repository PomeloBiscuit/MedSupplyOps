using System.Net;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using Oracle.ManagedDataAccess.Client;

namespace MedSupplyOps.Integration.Tests.Web;

/// <summary>
/// ★ 用**正式的 DI 圖**把 App 跑起來，實際打每一個頁面與 API。
///
/// 這個檔案存在的原因（血淚）：
/// 曾經有一段時間，`dotnet build`、82 條測試、格式檢查、7 支鑑別力探針、
/// ER 圖漂移檢查 —— **五道關卡全部綠燈**，而網站的每一個功能頁都回 HTTP 500。
///
/// 原因是 `Program.cs` 的服務註冊包在
/// `if (!string.IsNullOrWhiteSpace(connectionString))` 裡，而連線字串永遠是空的
/// （`csproj` 連 `UserSecretsId` 都沒有，`appsettings.json` 是空字串）。
/// 於是 `InventoryQueries` 從來沒有被註冊。
///
/// 而既有的 Web 測試偵測不到，因為它們做了：
/// <code>
/// services.RemoveAll&lt;InventoryQueries&gt;();
/// services.AddScoped&lt;InventoryQueries&gt;(...);   // 測試自己補了一份
/// </code>
/// **測試把它要驗證的那個東西自己提供了** —— 那是覆寫服務的標準寫法，
/// 用在「換成假的相依」時完全正確，但副作用是
/// **它永遠不可能發現正式碼漏了註冊**。
///
/// 所以這個類別的鐵則是：**不覆寫任何服務**。
/// 它唯一的職責是回答一個問題 ——「照使用者的方式啟動，網站真的能用嗎？」
/// </summary>
public sealed class ApplicationStartupSmokeTests : IClassFixture<ApplicationStartupSmokeTests.ProductionLikeFactory>, IAsyncLifetime
{
    private readonly HttpClient _client;

    public ApplicationStartupSmokeTests(ProductionLikeFactory factory)
    {
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
    }

    public Task InitializeAsync() => WebAuthTestHelpers.LoginAsync(_client, TestIdentitySeeder.AdministratorEmail);

    public Task DisposeAsync() => Task.CompletedTask;

    public static TheoryData<string> Pages =>
    [
        "/",
        "/Inventory",
        "/Inventory/Expiring",
        "/Items",
        "/Items/Create",
        "/Receiving",
        "/Requisitions",
        "/Requisitions/Create",
    ];

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task Every_page_renders_with_the_production_service_graph(string path)
    {
        var response = await _client.GetAsync(path);

        // 明確斷言 200，不是「不是 5xx」——
        // 302 導向登入頁之類的也不該悄悄通過。
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();
        Assert.False(string.IsNullOrWhiteSpace(html), $"{path} 回傳 200 但內容是空的。");

        // 200 但內容其實是錯誤頁的情況也要擋掉。
        Assert.DoesNotContain("Unable to resolve service", html, StringComparison.Ordinal);
        Assert.DoesNotContain("An unhandled exception", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Expiring_lots_api_responds_with_the_production_service_graph()
    {
        var response = await _client.GetAsync("/api/inventory/expiring?withinDays=30");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.False(string.IsNullOrWhiteSpace(body), "回傳 200 但內容是空的。");
        Assert.DoesNotContain("Unable to resolve service", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Item_availability_api_responds_with_the_production_service_graph()
    {
        // 品項 id 是 identity 產生的，不可以寫死。向資料庫要一個真實存在的 id ——
        // 寫死 1 的話，任何一次 `docker compose down -v` 重建之後，
        // 這條測試就變成在測 404，而它「還是紅的」這件事會被誤讀成別的原因。
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.OpenAsync();
        var itemId = await connection.ExecuteScalarAsync<decimal>(
            "SELECT MIN(item_id) FROM items WHERE is_deleted = 0");

        var response = await _client.GetAsync(
            FormattableString.Invariant($"/api/items/{decimal.ToInt64(itemId)}/availability"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.False(string.IsNullOrWhiteSpace(body), "回傳 200 但內容是空的。");
        Assert.DoesNotContain("Unable to resolve service", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// ★ 這個 factory **刻意不覆寫任何服務**。
    ///
    /// 它與 <c>InventoryApiTests</c> / <c>RequisitionFlowTests</c> 的 factory 是兩種不同的東西：
    /// 那兩個覆寫服務是為了控制測試資料，是正確的做法；
    /// 這一個不覆寫，是為了驗證「正式的組裝方式本身沒有洞」。
    /// **兩者缺一不可，而且不能合併** —— 一旦這裡加了任何 <c>RemoveAll</c> 或
    /// <c>AddScoped</c>，它就失去存在的意義了。
    /// </summary>
    public sealed class ProductionLikeFactory : WebApplicationFactory<Program>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            // 只設定環境，不動任何服務註冊。
            // Development 讓 Program.cs 走 .env 的連線字串路徑 ——
            // 那正是文件教使用者的做法，所以也正是應該被測試的那條路。
            builder.UseEnvironment("Development");
            var host = base.CreateHost(builder);
            TestIdentitySeeder.SeedAsync(host.Services).GetAwaiter().GetResult();
            return host;
        }
    }
}
