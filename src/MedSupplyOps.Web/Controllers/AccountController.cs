using MedSupplyOps.Infrastructure.Identity;
using MedSupplyOps.Web.Models.Account;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace MedSupplyOps.Web.Controllers;

/// <summary>
/// 登入／註冊／登出。刻意手寫三個頁面，不用 Identity 的 scaffold UI 套件
/// （<c>Microsoft.AspNetCore.Identity.UI</c>）——設計裁定 D7：scaffold 會塞進
/// 2FA、外部登入、個人資料下載等一整組用不到、也沒被測試或授權保護的端點。
///
/// ★ 目前刻意不做授權（見 README 的說明 D6）：這個 Controller 沒有任何
/// [Authorize]，所有 action 目前都能匿名呼叫。
/// </summary>
public sealed class AccountController : Controller
{
    private static readonly string[] AvailableRoles = ["Requester", "Storekeeper", "Administrator"];

    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly UserManager<ApplicationUser> _userManager;

    public AccountController(SignInManager<ApplicationUser> signInManager, UserManager<ApplicationUser> userManager)
    {
        _signInManager = signInManager;
        _userManager = userManager;
    }

    [HttpGet]
    public IActionResult Login(string? returnUrl = null)
        => View(new LoginViewModel { ReturnUrl = returnUrl });

    [HttpPost]
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
    public IActionResult Register()
        => View(new RegisterViewModel { AvailableRoles = AvailableRoles });

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Register(RegisterViewModel model, CancellationToken cancellationToken)
    {
        if (!AvailableRoles.Contains(model.Role, StringComparer.Ordinal))
        {
            ModelState.AddModelError(nameof(model.Role), "請選擇有效的角色。");
        }

        if (!ModelState.IsValid)
        {
            model.AvailableRoles = AvailableRoles;
            return View(model);
        }

        var user = new ApplicationUser
        {
            UserName = model.Email,
            Email = model.Email,
            DisplayName = model.DisplayName,
        };

        var createResult = await _userManager.CreateAsync(user, model.Password);
        if (!createResult.Succeeded)
        {
            foreach (var error in createResult.Errors)
            {
                ModelState.AddModelError(string.Empty, error.Description);
            }

            model.AvailableRoles = AvailableRoles;
            return View(model);
        }

        await _userManager.AddToRoleAsync(user, model.Role);
        await _signInManager.SignInAsync(user, isPersistent: false);

        return RedirectToAction("Index", "Home");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await _signInManager.SignOutAsync();
        return RedirectToAction("Index", "Home");
    }

    [HttpGet]
    public IActionResult AccessDenied() => View();
}
