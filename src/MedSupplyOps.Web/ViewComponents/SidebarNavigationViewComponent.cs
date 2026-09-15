using MedSupplyOps.Domain.Requisitions;
using MedSupplyOps.Infrastructure.Identity;
using MedSupplyOps.Infrastructure.Persistence;
using MedSupplyOps.Infrastructure.Queries;
using MedSupplyOps.Infrastructure.Time;
using MedSupplyOps.Web.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.EntityFrameworkCore;

namespace MedSupplyOps.Web.ViewComponents;

/// <summary>左側導覽的單一請求讀取模型；角標只在這裡計算一次，避免展開／收折版重複查詢。</summary>
public sealed class SidebarNavigationViewComponent : ViewComponent
{
    private readonly MedSupplyOpsDbContext _dbContext;
    private readonly InventoryQueries _inventoryQueries;
    private readonly BusinessCalendar _businessCalendar;
    private readonly DepartmentScopeResolver _departmentScopeResolver;
    private readonly UserManager<ApplicationUser> _userManager;

    public SidebarNavigationViewComponent(
        MedSupplyOpsDbContext dbContext,
        InventoryQueries inventoryQueries,
        BusinessCalendar businessCalendar,
        DepartmentScopeResolver departmentScopeResolver,
        UserManager<ApplicationUser> userManager)
    {
        _dbContext = dbContext;
        _inventoryQueries = inventoryQueries;
        _businessCalendar = businessCalendar;
        _departmentScopeResolver = departmentScopeResolver;
        _userManager = userManager;
    }

    public async Task<IViewComponentResult> InvokeAsync(string state)
    {
        var principal = HttpContext.User;
        var normalizedState = state is "expanded" or "compact" or "hidden" ? state : "expanded";
        var hasRole = principal.IsInRole(ApplicationRoles.Requester) ||
            principal.IsInRole(ApplicationRoles.Storekeeper) ||
            principal.IsInRole(ApplicationRoles.Administrator);
        var actor = principal.Identity?.Name ?? string.Empty;
        var user = string.IsNullOrWhiteSpace(actor) ? null : await _userManager.FindByNameAsync(actor);
        var model = new SidebarNavigationViewModel(
            normalizedState,
            hasRole,
            user?.DisplayName ?? actor,
            user?.Email ?? actor,
            RoleName(principal),
            Initial(user?.DisplayName ?? actor));

        if (!hasRole)
        {
            return View(model);
        }

        var scope = await _departmentScopeResolver.ResolveAsync(principal, actor);
        if (scope.Kind == DepartmentScopeKind.NoScope)
        {
            return View(model);
        }

        // 與首頁待審核卡使用相同的狀態條件；請領人必須由既有 ScopeResolver 限制科室。
        model.PendingRequisitionCount = await _dbContext.Requisitions.AsNoTracking()
            .Where(requisition => requisition.Status == RequisitionStatus.PendingApproval)
            .Where(requisition => !scope.IsRestricted || requisition.DepartmentId == scope.DepartmentId)
            .CountAsync(HttpContext.RequestAborted);

        if (scope.Kind == DepartmentScopeKind.Unrestricted)
        {
            // 沿用效期預警頁與首頁卡片的既有 InventoryQueries，不另寫 SQL。
            model.ExpiringLotCount = (await _inventoryQueries.GetExpiringLotsAsync(
                30,
                _businessCalendar.Today,
                cancellationToken: HttpContext.RequestAborted)).Count;
        }

        return View(model);
    }

    private static string RoleName(System.Security.Claims.ClaimsPrincipal user)
        => user.IsInRole(ApplicationRoles.Administrator) ? "管理員"
            : user.IsInRole(ApplicationRoles.Storekeeper) ? "庫管員"
            : user.IsInRole(ApplicationRoles.Requester) ? "請領人"
            : "未指派角色";

    private static string Initial(string value)
        => string.IsNullOrWhiteSpace(value) ? "?" : value[..1].ToUpperInvariant();
}

public sealed class SidebarNavigationViewModel
{
    public SidebarNavigationViewModel(string state, bool hasRole, string displayName, string email, string roleName, string initial)
    {
        State = state;
        HasRole = hasRole;
        DisplayName = displayName;
        Email = email;
        RoleName = roleName;
        Initial = initial;
    }

    public string State { get; }
    public bool HasRole { get; }
    public string DisplayName { get; }
    public string Email { get; }
    public string RoleName { get; }
    public string Initial { get; }
    public int PendingRequisitionCount { get; set; }
    public int ExpiringLotCount { get; set; }
}
