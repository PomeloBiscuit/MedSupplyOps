using MedSupplyOps.Infrastructure.Identity;
using MedSupplyOps.Infrastructure.Persistence;
using MedSupplyOps.Web.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MedSupplyOps.Web.Identity;

/// <summary>
/// 啟動時建立三個角色與各一個示範帳號（設計裁定 D5）。
///
/// 密碼經過 <see cref="UserManager{TUser}"/>／<see cref="PasswordHasher{TUser}"/>
/// 雜湊後才寫入資料庫（SEC-1），這裡只是明文常數的唯一合法出處——
/// 這是示範帳號，密碼刻意公開在 README，讓 clone 下來的人第一步就能登入看畫面。
///
/// 冪等：每次啟動都會呼叫，用「先查有沒有」擋重複建立，
/// 不能用 try/catch 唯一鍵衝突來冪等——那樣失敗時看到的錯誤會指向錯誤的原因。
/// </summary>
internal static class DemoAccountSeeder
{
    public const string DemoPassword = "Demo#2026pass";

    /// <summary>
    /// 示範請領人固定屬於急診（V002 種子資料的 <c>DEP-ER</c>），README 也是這樣寫的。
    ///
    /// ★ 必須**指名**，不可以用「代碼排第一的科室」。
    ///   這個種子每次啟動都會執行、而且會重綁；資料庫是共用的，
    ///   任何人新增一個代碼排在前面的科室（例如整合測試暫時建立的 <c>D1EE0F72409</c>），
    ///   下一次啟動就會把示範帳號綁過去。
    /// </summary>
    public const string RequesterDepartmentCode = "DEP-ER";

    private static readonly (string Role, string Email, string DisplayName, string DisplayNameEn)[] Accounts =
    [
        (ApplicationRoles.Requester, "requester@example.local", "王小明", "Xiaoming Wang"),
        (ApplicationRoles.Storekeeper, "keeper@example.local", "陳庫管", "Storekeeper Chen"),
        (ApplicationRoles.Administrator, "admin@example.local", "林大同", "Datong Lin"),
    ];

    public static async Task SeedAsync(IServiceProvider services)
    {
        var roleManager = services.GetRequiredService<RoleManager<IdentityRole>>();
        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
        var dbContext = services.GetRequiredService<MedSupplyOpsDbContext>();
        // 找不到就停止啟動，不退而求其次改綁別的科室 —— 綁錯科室比啟動失敗更難被發現。
        var requesterDepartmentId = await dbContext.Departments.AsNoTracking()
            .Where(department => department.Code == RequesterDepartmentCode && department.IsActive && !department.IsDeleted)
            .Select(department => (long?)department.Id)
            .SingleOrDefaultAsync()
            ?? throw new InvalidOperationException(
                $"找不到示範請領人的科室 {RequesterDepartmentCode}（種子資料 db/schema/V002__seed_data.sql）。");

        foreach (var (role, _, _, _) in Accounts)
        {
            if (!await roleManager.RoleExistsAsync(role))
            {
                var roleResult = await roleManager.CreateAsync(new IdentityRole(role));
                ThrowIfFailed(roleResult, $"建立角色 {role}");
            }
        }

        foreach (var (role, email, displayName, displayNameEn) in Accounts)
        {
            var existing = await userManager.FindByEmailAsync(email);
            if (existing is not null)
            {
                var changed = false;
                if (role == ApplicationRoles.Requester && existing.DepartmentId != requesterDepartmentId)
                {
                    existing.DepartmentId = requesterDepartmentId;
                    changed = true;
                }

                if (!string.Equals(existing.DisplayNameEn, displayNameEn, StringComparison.Ordinal))
                {
                    existing.DisplayNameEn = displayNameEn;
                    changed = true;
                }

                if (changed)
                {
                    ThrowIfFailed(await userManager.UpdateAsync(existing), $"更新示範帳號 {email} 的雙語主檔");
                }

                continue;
            }

            var user = new ApplicationUser
            {
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                DisplayName = displayName,
                DisplayNameEn = displayNameEn,
                DepartmentId = role == ApplicationRoles.Requester ? requesterDepartmentId : null,
            };

            var createResult = await userManager.CreateAsync(user, DemoPassword);
            ThrowIfFailed(createResult, $"建立示範帳號 {email}");

            var roleResult = await userManager.AddToRoleAsync(user, role);
            ThrowIfFailed(roleResult, $"將 {email} 加入角色 {role}");
        }
    }

    private static void ThrowIfFailed(IdentityResult result, string action)
    {
        if (!result.Succeeded)
        {
            var errors = string.Join("; ", result.Errors.Select(e => $"{e.Code}: {e.Description}"));
            throw new InvalidOperationException($"{action} 失敗：{errors}");
        }
    }
}
