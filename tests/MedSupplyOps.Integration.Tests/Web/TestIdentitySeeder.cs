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
        var requesterDepartmentId = await dbContext.Departments.AsNoTracking()
            .Where(department => department.IsActive && !department.IsDeleted)
            .OrderBy(department => department.Code)
            .Select(department => department.Id)
            .FirstAsync();

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
