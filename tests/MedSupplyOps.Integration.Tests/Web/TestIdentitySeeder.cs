using MedSupplyOps.Infrastructure.Identity;
using MedSupplyOps.Infrastructure.Persistence;
using MedSupplyOps.Web.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MedSupplyOps.Integration.Tests.Web;

internal static class TestIdentitySeeder
{
    public const string RequesterEmail = "itest-requester@example.local";
    public const string StorekeeperEmail = "itest-keeper@example.local";
    public const string AdministratorEmail = "itest-admin@example.local";

    /// <summary>沒有任何角色、沒有科室的帳號（首頁儀表板 T2）：驗證首頁對「沒有範圍」的處理。</summary>
    public const string NoRoleEmail = "itest-norole@example.local";
    public const string RequesterDepartmentCode = "DEP-ER";

    public static string Password { get; } = $"ITest#{Guid.NewGuid():N}aA1";

    private static readonly (string Role, string Email, string DisplayName)[] Accounts =
    [
        (ApplicationRoles.Requester, RequesterEmail, "測試請領員"),
        (ApplicationRoles.Storekeeper, StorekeeperEmail, "測試庫管員"),
        (ApplicationRoles.Administrator, AdministratorEmail, "測試管理員"),
    ];

    public static async Task SeedAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var dbContext = scope.ServiceProvider.GetRequiredService<MedSupplyOpsDbContext>();
        // ★ 指名種子科室，與示範帳號同一條規則。
        //   曾經用「代碼排第一、且 created_by 不是 itest」—— 那只擋得住一個字串，
        //   任何其他前綴建立的科室照樣會把測試帳號綁走。
        var requesterDepartmentId = await dbContext.Departments.AsNoTracking()
            .Where(department => department.Code == RequesterDepartmentCode && department.IsActive && !department.IsDeleted)
            .Select(department => department.Id)
            .SingleAsync();

        foreach (var (role, email, displayName) in Accounts)
        {
            if (!await roleManager.RoleExistsAsync(role))
            {
                EnsureSucceeded(await roleManager.CreateAsync(new IdentityRole(role)), $"建立角色 {role}");
            }

            var user = await userManager.FindByEmailAsync(email);
            if (user is null)
            {
                user = new ApplicationUser
                {
                    UserName = email,
                    Email = email,
                    EmailConfirmed = true,
                    DisplayName = displayName,
                    DepartmentId = role == ApplicationRoles.Requester ? requesterDepartmentId : null,
                };
                EnsureSucceeded(await userManager.CreateAsync(user, Password), $"建立測試帳號 {email}");
            }
            else
            {
                user.DisplayName = displayName;
                user.DepartmentId = role == ApplicationRoles.Requester ? requesterDepartmentId : null;
                // 測試帳號會跨 dotnet test process 留在共用資料庫；密碼每個 process 都是新的。
                // 只重設密碼不會解除前一輪錯誤登入留下的鎖定，下一輪所有 Web 測試便會首跑失敗。
                user.AccessFailedCount = 0;
                user.LockoutEnd = null;
                EnsureSucceeded(await userManager.UpdateAsync(user), $"更新測試帳號 {email}");
                if (await userManager.HasPasswordAsync(user))
                {
                    EnsureSucceeded(await userManager.RemovePasswordAsync(user), $"重設測試帳號 {email} 的密碼");
                }

                EnsureSucceeded(await userManager.AddPasswordAsync(user, Password), $"設定測試帳號 {email} 的密碼");
            }

            if (!await userManager.IsInRoleAsync(user, role))
            {
                EnsureSucceeded(await userManager.AddToRoleAsync(user, role), $"將測試帳號 {email} 加入 {role}");
            }
        }

        await SeedNoRoleAccountAsync(userManager);
    }

    /// <summary>
    /// 沒有任何角色、沒有科室的帳號。刻意不進 <see cref="Accounts"/> 的迴圈——
    /// 那個迴圈的每一筆都會被加進一個角色，這個帳號的重點正是「不屬於任何角色」。
    /// </summary>
    private static async Task SeedNoRoleAccountAsync(UserManager<ApplicationUser> userManager)
    {
        var user = await userManager.FindByEmailAsync(NoRoleEmail);
        if (user is null)
        {
            user = new ApplicationUser
            {
                UserName = NoRoleEmail,
                Email = NoRoleEmail,
                EmailConfirmed = true,
                DisplayName = "測試無角色帳號",
                DepartmentId = null,
            };
            EnsureSucceeded(await userManager.CreateAsync(user, Password), $"建立測試帳號 {NoRoleEmail}");
        }
        else
        {
            // 同樣清除跨 process 的 Identity lockout 狀態；此帳號雖沒有角色，仍會走真實登入端點。
            user.AccessFailedCount = 0;
            user.LockoutEnd = null;
            EnsureSucceeded(await userManager.UpdateAsync(user), $"更新測試帳號 {NoRoleEmail}");

            if (await userManager.HasPasswordAsync(user))
            {
                EnsureSucceeded(await userManager.RemovePasswordAsync(user), $"重設測試帳號 {NoRoleEmail} 的密碼");
            }

            EnsureSucceeded(await userManager.AddPasswordAsync(user, Password), $"設定測試帳號 {NoRoleEmail} 的密碼");
        }
    }

    private static void EnsureSucceeded(IdentityResult result, string action)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"{action} 失敗：{string.Join("; ", result.Errors.Select(error => error.Description))}");
        }
    }
}
