using MedSupplyOps.Infrastructure.Identity;
using MedSupplyOps.Infrastructure.Persistence;
using MedSupplyOps.Infrastructure.Persistence.Models;
using MedSupplyOps.Web.Authorization;
using MedSupplyOps.Web.Models.Account;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MedSupplyOps.Web.Controllers;

/// <summary>
/// 登入／註冊／登出。刻意手寫三個頁面，不用 Identity 的 scaffold UI 套件
/// （<c>Microsoft.AspNetCore.Identity.UI</c>）——設計裁定 D7：scaffold 會塞進
/// 2FA、外部登入、個人資料下載等一整組用不到、也沒被測試或授權保護的端點。
/// </summary>
public sealed class AccountController : Controller
{
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly MedSupplyOpsDbContext _dbContext;
    private readonly PasswordOptions _passwordOptions;

    public AccountController(
        SignInManager<ApplicationUser> signInManager,
        UserManager<ApplicationUser> userManager,
        MedSupplyOpsDbContext dbContext,
        IOptions<IdentityOptions> identityOptions)
    {
        _signInManager = signInManager;
        _userManager = userManager;
        _dbContext = dbContext;
        _passwordOptions = identityOptions.Value.Password;
    }

    [AcceptVerbs("GET", "HEAD")]
    [AllowAnonymous]
    public IActionResult Login(string? returnUrl = null)
        => View(new LoginViewModel { ReturnUrl = returnUrl });

    [HttpPost]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginViewModel model, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        // lockoutOnFailure: true —— 失敗次數計進 AccessFailedCount，達到門檻由
        // AddIdentity 設定的 Lockout 選項鎖定帳號（SEC-8，見 Program.cs）。
        var result = await _signInManager.PasswordSignInAsync(
            model.Email, model.Password, model.RememberMe, lockoutOnFailure: true);

        if (result.Succeeded)
        {
            if (Url.IsLocalUrl(model.ReturnUrl))
            {
                return Redirect(model.ReturnUrl);
            }

            return RedirectToAction("Index", "Home");
        }

        if (result.IsLockedOut)
        {
            ModelState.AddModelError(string.Empty, "登入失敗次數過多，帳號已被暫時鎖定，請稍後再試。");
            return View(model);
        }

        ModelState.AddModelError(string.Empty, "帳號或密碼不正確。");
        return View(model);
    }

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> Register(CancellationToken cancellationToken)
    {
        var model = new RegisterViewModel();
        await PopulateDepartmentsAsync(model, cancellationToken);
        return View(model);
    }

    [HttpPost]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Register(RegisterViewModel model, CancellationToken cancellationToken)
    {
        var departmentExists = await _dbContext.Departments.AsNoTracking()
            .AnyAsync(
                department => department.Id == model.DepartmentId && department.IsActive && !department.IsDeleted,
                cancellationToken);
        if (!departmentExists)
        {
            ModelState.AddModelError(nameof(model.DepartmentId), "選擇的科室不存在或已停用。");
        }

        if (!ModelState.IsValid)
        {
            await PopulateDepartmentsAsync(model, cancellationToken);
            return View(model);
        }

        var user = new ApplicationUser
        {
            UserName = model.Email,
            Email = model.Email,
            DisplayName = model.DisplayName,
            DepartmentId = model.DepartmentId,
        };

        var createResult = await _userManager.CreateAsync(user, model.Password);
        if (!createResult.Succeeded)
        {
            foreach (var error in createResult.Errors)
            {
                ModelState.AddModelError(string.Empty, error.Description);
            }

            await PopulateDepartmentsAsync(model, cancellationToken);
            return View(model);
        }

        var roleResult = await _userManager.AddToRoleAsync(user, ApplicationRoles.Requester);
        if (!roleResult.Succeeded)
        {
            foreach (var error in roleResult.Errors)
            {
                ModelState.AddModelError(string.Empty, error.Description);
            }

            await PopulateDepartmentsAsync(model, cancellationToken);
            return View(model);
        }
        await _signInManager.SignInAsync(user, isPersistent: false);

        return RedirectToAction("Index", "Home");
    }

    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.Authenticated)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await _signInManager.SignOutAsync();
        return RedirectToAction("Index", "Home");
    }

    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.Authenticated)]
    public async Task<IActionResult> Profile(CancellationToken cancellationToken)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        var roles = await _userManager.GetRolesAsync(user);
        var departmentName = user.DepartmentId is long departmentId
            ? await _dbContext.Departments.AsNoTracking()
                .Where(department => department.Id == departmentId && !department.IsDeleted)
                .Select(department => department.Name)
                .SingleOrDefaultAsync(cancellationToken) ?? "未指定"
            : "未指定";
        var displayName = string.IsNullOrWhiteSpace(user.DisplayName) ? user.Email ?? user.UserName ?? "使用者" : user.DisplayName;

        return View(new ProfileViewModel(
            displayName,
            user.Email ?? user.UserName ?? string.Empty,
            DisplayRoles(roles),
            departmentName,
            displayName[..1].ToUpperInvariant()));
    }

    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.Authenticated)]
    public IActionResult ChangePassword()
        => View(CreateChangePasswordModel());

    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.Authenticated)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangePassword(ChangePasswordViewModel model, CancellationToken cancellationToken)
    {
        model.PasswordPolicy = PasswordPolicyViewModel.From(_passwordOptions);
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        var result = await _userManager.ChangePasswordAsync(user, model.CurrentPassword, model.NewPassword);
        if (!result.Succeeded)
        {
            AddChangePasswordErrors(result);
            return View(model);
        }

        // ChangePasswordAsync 已由 Identity 驗證並更新雜湊；再明確輪替安全戳記，
        // 然後只替目前工作階段重發 Cookie，使其他工作階段沿用的 Cookie 失效。
        var stampResult = await _userManager.UpdateSecurityStampAsync(user);
        if (!stampResult.Succeeded)
        {
            throw new InvalidOperationException("密碼已更新，但重新產生安全戳記失敗。");
        }

        await _signInManager.RefreshSignInAsync(user);

        _dbContext.AuditLogs.Add(new AuditLog
        {
            EntityType = "User",
            EntityId = user.Id,
            Action = "ChangePassword",
            Actor = user.Email ?? user.UserName ?? user.Id,
            OccurredAt = DateTime.UtcNow,
            // 密碼與雜湊都不屬於可稽核內容；兩欄刻意保持 null。
            OldValue = null,
            NewValue = null,
        });
        await _dbContext.SaveChangesAsync(cancellationToken);

        if (string.Equals(Request.Headers["X-Requested-With"], "XMLHttpRequest", StringComparison.Ordinal))
        {
            return Ok(new { succeeded = true, message = "密碼已更新；其他裝置的登入已失效。" });
        }

        TempData["Success"] = "密碼已更新；其他裝置的登入已失效。";
        return RedirectToAction("Index", "Home");
    }

    [HttpGet]
    [AllowAnonymous]
    public IActionResult AccessDenied() => View();

    private ChangePasswordViewModel CreateChangePasswordModel()
        => new() { PasswordPolicy = PasswordPolicyViewModel.From(_passwordOptions) };

    private void AddChangePasswordErrors(IdentityResult result)
    {
        foreach (var error in result.Errors)
        {
            var message = error.Code switch
            {
                "PasswordMismatch" => "目前密碼不正確，密碼未變更。",
                "PasswordTooShort" => $"新密碼必須至少 {_passwordOptions.RequiredLength} 個字元。",
                "PasswordRequiresUniqueChars" => $"新密碼必須至少包含 {_passwordOptions.RequiredUniqueChars} 個不同字元。",
                "PasswordRequiresNonAlphanumeric" => "新密碼必須至少包含一個符號。",
                "PasswordRequiresDigit" => "新密碼必須至少包含一個數字。",
                "PasswordRequiresLower" => "新密碼必須至少包含一個小寫英文字母。",
                "PasswordRequiresUpper" => "新密碼必須至少包含一個大寫英文字母。",
                _ => "密碼無法更新，請確認輸入後再試。",
            };
            ModelState.AddModelError(string.Empty, message);
        }
    }

    private static string DisplayRoles(IEnumerable<string> roles)
    {
        var displayNames = roles.Select(role => role switch
        {
            ApplicationRoles.Administrator => "管理員",
            ApplicationRoles.Storekeeper => "庫管員",
            ApplicationRoles.Requester => "請領人",
            _ => role,
        }).ToList();

        return displayNames.Count == 0 ? "未指派角色" : string.Join("、", displayNames);
    }

    private async Task PopulateDepartmentsAsync(RegisterViewModel model, CancellationToken cancellationToken)
    {
        model.Departments = await _dbContext.Departments.AsNoTracking()
            .Where(department => department.IsActive && !department.IsDeleted)
            .OrderBy(department => department.Code)
            .Select(department => new RegisterDepartmentOption(department.Id, department.Name))
            .ToListAsync(cancellationToken);
    }
}
