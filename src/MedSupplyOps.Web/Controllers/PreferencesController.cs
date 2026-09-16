using MedSupplyOps.Web.Authorization;
using MedSupplyOps.Web.Localization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;

namespace MedSupplyOps.Web.Controllers;

[Authorize(Policy = AuthorizationPolicies.Authenticated)]
public sealed class PreferencesController : Controller
{
    private static readonly HashSet<string> SupportedCultures = new(StringComparer.OrdinalIgnoreCase)
    {
        "zh-Hant",
        "en",
    };

    [HttpPost]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public IActionResult Culture(string culture, string? returnUrl)
    {
        if (!SupportedCultures.Contains(culture))
        {
            return BadRequest();
        }

        Response.Cookies.Append(
            CookieRequestCultureProvider.DefaultCookieName,
            CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
            PreferenceCookieOptions());

        return RedirectBack(returnUrl);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult TimeZone(string timeZone, string? returnUrl)
    {
        if (!DisplayTimeZone.IsSupported(timeZone))
        {
            return BadRequest();
        }

        Response.Cookies.Append(DisplayTimeZone.CookieName, timeZone, PreferenceCookieOptions());
        return RedirectBack(returnUrl);
    }

    private IActionResult RedirectBack(string? returnUrl) =>
        !string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)
            ? LocalRedirect(returnUrl)
            : RedirectToAction("Index", "Home");

    private static CookieOptions PreferenceCookieOptions() => new()
    {
        HttpOnly = true,
        IsEssential = true,
        SameSite = SameSiteMode.Lax,
        Secure = true,
        Expires = DateTimeOffset.UtcNow.AddYears(1),
    };
}
