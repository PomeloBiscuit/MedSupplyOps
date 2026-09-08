using MedSupplyOps.Infrastructure.Identity;
using MedSupplyOps.Infrastructure.Persistence;
using MedSupplyOps.Web.Authorization;
using MedSupplyOps.Web.Models.Account;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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

    public AccountController(
        SignInManager<ApplicationUser> signInManager,
        UserManager<ApplicationUser> userManager,
        MedSupplyOpsDbContext dbContext)
    {
        _signInManager = signInManager;
        _userManager = userManager;
        _dbContext = dbContext;
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
    [AllowAnonymous]
    public IActionResult AccessDenied() => View();

    private async Task PopulateDepartmentsAsync(RegisterViewModel model, CancellationToken cancellationToken)
    {
        model.Departments = await _dbContext.Departments.AsNoTracking()
            .Where(department => department.IsActive && !department.IsDeleted)
            .OrderBy(department => department.Code)
            .Select(department => new RegisterDepartmentOption(department.Id, department.Name))
            .ToListAsync(cancellationToken);
    }
}
