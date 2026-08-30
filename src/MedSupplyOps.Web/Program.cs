using MedSupplyOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews();

// 資料庫連線字串一律來自組態，不寫進 appsettings.json（需求 SEC-5 / 設計裁定 D4）：
//   開發：User Secrets（dotnet user-secrets set "ConnectionStrings:MedSupplyOps" "...")
//   其他：環境變數 ConnectionStrings__MedSupplyOps
// 尚未設定時不註冊 DbContext —— 目前沒有任何功能用到它，App 仍應能啟動。
var medSupplyConnection = builder.Configuration.GetConnectionString("MedSupplyOps");
if (!string.IsNullOrWhiteSpace(medSupplyConnection))
{
    builder.Services.AddDbContext<MedSupplyOpsDbContext>(options =>
        options.UseOracle(medSupplyConnection));
}

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

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();


app.Run();
