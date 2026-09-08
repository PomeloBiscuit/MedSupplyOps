using System.Diagnostics;
using MedSupplyOps.Web.Authorization;
using MedSupplyOps.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MedSupplyOps.Web.Controllers;

public class HomeController : Controller
{
    [Authorize(Policy = AuthorizationPolicies.Authenticated)]
    public IActionResult Index()
    {
        return View();
    }

    [Authorize(Policy = AuthorizationPolicies.Authenticated)]
    public IActionResult Privacy()
    {
        return View();
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    [Authorize(Policy = AuthorizationPolicies.Authenticated)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
