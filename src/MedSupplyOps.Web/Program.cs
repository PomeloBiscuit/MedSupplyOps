using System.Text.Json;
using MedSupplyOps.Infrastructure.Identity;
using MedSupplyOps.Infrastructure.Persistence;
using MedSupplyOps.Infrastructure.Queries;
using MedSupplyOps.Infrastructure.Services;
using MedSupplyOps.Infrastructure.Time;
using MedSupplyOps.Web;
using MedSupplyOps.Web.Authorization;
using MedSupplyOps.Web.Fhir;
using MedSupplyOps.Web.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var businessTimeZoneId = builder.Configuration["MedSupplyOps:BusinessTimeZone"] ?? "Asia/Taipei";
TimeZoneInfo businessTimeZone;
try
{
    businessTimeZone = TimeZoneInfo.FindSystemTimeZoneById(businessTimeZoneId);
}
catch (TimeZoneNotFoundException exception)
{
    throw new InvalidOperationException(
        $"找不到業務時區 '{businessTimeZoneId}'，App 無法啟動。請將 MedSupplyOps:BusinessTimeZone 設為有效的 IANA 時區 ID（例如 Asia/Taipei）。",
        exception);
}
catch (InvalidTimeZoneException exception)
{
    throw new InvalidOperationException(
        $"業務時區 '{businessTimeZoneId}' 無效，App 無法啟動。請將 MedSupplyOps:BusinessTimeZone 設為有效的 IANA 時區 ID（例如 Asia/Taipei）。",
        exception);
}

builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton(new BusinessCalendar(TimeProvider.System, businessTimeZone));

// Add services to the container.
builder.Services
    .AddControllersWithViews()
    .AddJsonOptions(options => options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);

builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build())
    .AddPolicy(AuthorizationPolicies.Authenticated, policy => policy.RequireAuthenticatedUser())
    .AddPolicy(
        AuthorizationPolicies.InventoryRead,
        policy => policy.RequireRole(ApplicationRoles.Requester, ApplicationRoles.Storekeeper, ApplicationRoles.Administrator))
    .AddPolicy(
        AuthorizationPolicies.RequisitionRead,
        policy => policy.RequireRole(ApplicationRoles.Requester, ApplicationRoles.Storekeeper, ApplicationRoles.Administrator))
    .AddPolicy(
        AuthorizationPolicies.RequisitionCreate,
        policy => policy.RequireRole(ApplicationRoles.Requester, ApplicationRoles.Administrator))
    .AddPolicy(
        AuthorizationPolicies.RequisitionReview,
        policy => policy.RequireRole(ApplicationRoles.Storekeeper, ApplicationRoles.Administrator))
    .AddPolicy(
        AuthorizationPolicies.RequisitionIssue,
        policy => policy.RequireRole(ApplicationRoles.Storekeeper, ApplicationRoles.Administrator))
    .AddPolicy(
        AuthorizationPolicies.FhirRead,
        policy => policy.RequireRole(ApplicationRoles.Storekeeper, ApplicationRoles.Administrator));

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

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpContextCurrentUser>();

builder.Services.AddDbContext<MedSupplyOpsDbContext>(options =>
    options.UseOracle(medSupplyConnection));
builder.Services.AddScoped<InventoryQueries>(serviceProvider =>
    new InventoryQueries(serviceProvider.GetRequiredService<MedSupplyOpsDbContext>().Database.GetDbConnection()));
builder.Services.AddScoped<FhirQueries>(serviceProvider =>
    new FhirQueries(serviceProvider.GetRequiredService<MedSupplyOpsDbContext>().Database.GetDbConnection()));
builder.Services.AddScoped<StockIssueService>(serviceProvider =>
    new StockIssueService(serviceProvider.GetRequiredService<MedSupplyOpsDbContext>().Database.GetDbConnection()));

// ─────────────────────────────────────────────────────────────────────────────
// Identity 認證與預設拒絕授權。
//
// 用同一條連線字串、另一個 DbContext（設計裁定 D1）：Identity 的表與 Domain 的表
// 是兩個不同的物件圖，硬塞進同一個 DbContext 會讓 MedSupplyOpsDbContext 的稽核簿記
// 邏輯（StampBookkeepingColumns）誤判 Identity 實體也有 CREATED_BY 之類的欄位。
// ─────────────────────────────────────────────────────────────────────────────
builder.Services.AddDbContext<MedSupplyOpsIdentityDbContext>(options =>
    options.UseOracle(medSupplyConnection));

builder.Services
    .AddIdentity<ApplicationUser, IdentityRole>(options =>
    {
        // 登入失敗鎖定（SEC-8）：連續 5 次失敗鎖定 15 分鐘。
        // 數字沒有標準答案，這裡選 5/15 分鐘是「常見到記得住、又不會讓忘記密碼的人等太久」。
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        options.Lockout.AllowedForNewUsers = true;

        options.User.RequireUniqueEmail = true;
    })
    .AddEntityFrameworkStores<MedSupplyOpsIdentityDbContext>()
    .AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.LogoutPath = "/Account/Logout";
    options.AccessDeniedPath = "/Account/AccessDenied";
    options.Events.OnRedirectToLogin = context =>
    {
        if (context.Request.Path.StartsWithSegments("/fhir"))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = FhirResponse.ContentType;
            return context.Response.WriteAsync(FhirResponse.Serialize(FhirResponse.Outcome(
                Hl7.Fhir.Model.OperationOutcome.IssueType.Login,
                "此 FHIR 互動需要先通過驗證。")));
        }

        if (context.Request.Path.StartsWithSegments("/api"))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }

        context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    };
    options.Events.OnRedirectToAccessDenied = context =>
    {
        if (context.Request.Path.StartsWithSegments("/fhir"))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = FhirResponse.ContentType;
            return context.Response.WriteAsync(FhirResponse.Serialize(FhirResponse.Outcome(
                Hl7.Fhir.Model.OperationOutcome.IssueType.Forbidden,
                "目前身分沒有 FHIR 讀取權限。")));
        }

        if (context.Request.Path.StartsWithSegments("/api"))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }

        context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    };
});

var app = builder.Build();

// 建立三個角色與各一個示範帳號（設計裁定 D5）。冪等，每次啟動都跑。
using (var scope = app.Services.CreateScope())
{
    await DemoAccountSeeder.SeedAsync(scope.ServiceProvider);
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets().AllowAnonymous();

app.MapControllers();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();


app.Run();

public partial class Program;
