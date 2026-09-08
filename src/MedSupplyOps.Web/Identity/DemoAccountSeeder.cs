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

    private static readonly (string Role, string Email, string DisplayName)[] Accounts =
    [
        (ApplicationRoles.Requester, "requester@example.local", "王小明"),
        (ApplicationRoles.Storekeeper, "keeper@example.local", "陳庫管"),
        (ApplicationRoles.Administrator, "admin@example.local", "林大同"),
    ];

    public static async Task SeedAsync(IServiceProvider services)
    {
        var roleManager = services.GetRequiredService<RoleManager<IdentityRole>>();
        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
        var dbContext = services.GetRequiredService<MedSupplyOpsDbContext>();
        var requesterDepartmentId = await dbContext.Departments.AsNoTracking()
            .Where(department => department.IsActive && !department.IsDeleted)
            .OrderBy(department => department.Code)
            .Select(department => department.Id)
            .FirstAsync();

        foreach (var (role, _, _) in Accounts)
        {
            if (!await roleManager.RoleExistsAsync(role))
            {
                var roleResult = await roleManager.CreateAsync(new IdentityRole(role));
                ThrowIfFailed(roleResult, $"建立角色 {role}");
            }
        }

        foreach (var (role, email, displayName) in Accounts)
        {
            var existing = await userManager.FindByEmailAsync(email);
            if (existing is not null)
            {
                if (role == ApplicationRoles.Requester && existing.DepartmentId != requesterDepartmentId)
                {
                    existing.DepartmentId = requesterDepartmentId;
                    ThrowIfFailed(await userManager.UpdateAsync(existing), $"更新示範帳號 {email} 的科室");
                }

                continue;
            }

            var user = new ApplicationUser
            {
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                DisplayName = displayName,
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
