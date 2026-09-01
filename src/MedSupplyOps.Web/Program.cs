using System.Text.Json;
using MedSupplyOps.Infrastructure.Persistence;
using MedSupplyOps.Infrastructure.Queries;
using MedSupplyOps.Web;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services
    .AddControllersWithViews()
    .AddJsonOptions(options => options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);

// ─────────────────────────────────────────────────────────────────────────────
// 資料庫連線字串一律來自組態，不寫進 appsettings.json（需求 SEC-5）：
//   開發：repo 根目錄的 .env（與 docker compose 共用同一份，見下方說明）
//         或 User Secrets：dotnet user-secrets set "ConnectionStrings:MedSupplyOps" "..."
//   其他：環境變數 ConnectionStrings__MedSupplyOps
//
// ★ 這一段原本寫成「沒設定就不註冊 DbContext，App 仍應能啟動」。
//   那句話在只有預設樣板頁的時候是對的，加了實際功能之後就不對了 ——
//   而它不對的方式非常糟：App 照常啟動、首頁照常顯示，
//   然後**每一個功能頁都 500**，錯誤訊息是
//   「Unable to resolve service for type InventoryQueries」。
//   那個訊息指向 DI 註冊，但真正的原因是**組態沒讀到** —— 完全指錯方向。
//   現在改成啟動時就失敗，訊息直接說明要怎麼修。
//
// ★ 為什麼 Development 會去讀 .env：
//   docker compose 用 .env 的 APP_DB_PASSWORD 建立資料庫帳號。
//   如果 App 另外要求開發者再設一次 User Secrets，同一個密碼就有兩份來源，
//   而兩份不一致的症狀是「容器起得來、App 連不上」，看起來像網路問題。
//   共用同一份 .env 讓那種漂移不可能發生。
// ─────────────────────────────────────────────────────────────────────────────
var medSupplyConnection = builder.Configuration.GetConnectionString("MedSupplyOps");

if (string.IsNullOrWhiteSpace(medSupplyConnection) && builder.Environment.IsDevelopment())
{
    medSupplyConnection = DevelopmentConnectionString.TryComposeFromDotEnv(builder.Environment.ContentRootPath);
}

if (string.IsNullOrWhiteSpace(medSupplyConnection))
{
    throw new InvalidOperationException(
        """
        找不到資料庫連線字串，App 無法啟動。

        本機開發（擇一）：
          1. 在 repo 根目錄建立 .env（可從 .env.example 複製），設定 APP_DB_PASSWORD=
             這與 docker compose 用的是同一份檔案，不會有兩份密碼不一致的問題。
          2. dotnet user-secrets set "ConnectionStrings:MedSupplyOps" "User Id=medsupply;Password=...;Data Source=//localhost:1521/FREEPDB1" --project src/MedSupplyOps.Web

        其他環境：設定環境變數 ConnectionStrings__MedSupplyOps

        （刻意在啟動時就失敗：讓 App 帶著缺失的組態啟動，會變成每個功能頁都 500，
        而錯誤訊息會指向 DI 註冊而不是組態，把人帶往完全錯誤的方向。）
        """);
}

builder.Services.AddDbContext<MedSupplyOpsDbContext>(options =>
    options.UseOracle(medSupplyConnection));
builder.Services.AddScoped<InventoryQueries>(serviceProvider =>
    new InventoryQueries(serviceProvider.GetRequiredService<MedSupplyOpsDbContext>().Database.GetDbConnection()));

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();

app.UseAuthorization();

app.MapStaticAssets();

app.MapControllers();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();


app.Run();

public partial class Program;
