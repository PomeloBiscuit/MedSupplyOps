using System.Diagnostics;
using MedSupplyOps.Domain.Requisitions;
using MedSupplyOps.Infrastructure.Identity;
using MedSupplyOps.Infrastructure.Localization;
using MedSupplyOps.Infrastructure.Persistence;
using MedSupplyOps.Infrastructure.Queries;
using MedSupplyOps.Infrastructure.Time;
using MedSupplyOps.Web.Authorization;
using MedSupplyOps.Web.Models;
using MedSupplyOps.Web.Models.Home;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MedSupplyOps.Web.Controllers;

public sealed class HomeController : Controller
{
    private const int RecentItemCount = 5;

    private readonly MedSupplyOpsDbContext _dbContext;
    private readonly InventoryQueries _inventoryQueries;
    private readonly DashboardQueries _dashboardQueries;
    private readonly BusinessCalendar _businessCalendar;
    private readonly ICurrentUser _currentUser;
    private readonly DepartmentScopeResolver _departmentScopeResolver;
    private readonly IAuthorizationService _authorizationService;

    public HomeController(
        MedSupplyOpsDbContext dbContext,
        InventoryQueries inventoryQueries,
        DashboardQueries dashboardQueries,
        BusinessCalendar businessCalendar,
        ICurrentUser currentUser,
        DepartmentScopeResolver departmentScopeResolver,
        IAuthorizationService authorizationService)
    {
        _dbContext = dbContext;
        _inventoryQueries = inventoryQueries;
        _dashboardQueries = dashboardQueries;
        _businessCalendar = businessCalendar;
        _currentUser = currentUser;
        _departmentScopeResolver = departmentScopeResolver;
        _authorizationService = authorizationService;
    }

    /// <summary>
    /// 依角色的工作儀表板。範圍規則見 <see cref="DepartmentScopeResolver"/>：
    /// 不受限（庫管員／管理員）→ 營運儀表板；限科室（請領人）→ 請領人儀表板；
    /// 沒有範圍（沒有任何角色）→ 只顯示提示，不顯示任何數字。
    /// </summary>
    [Authorize(Policy = AuthorizationPolicies.Authenticated)]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var scope = await _departmentScopeResolver.ResolveAsync(User, _currentUser.Actor);

        if (scope.Kind == DepartmentScopeKind.NoScope)
        {
            return View("NoRoleDashboard");
        }

        var canCreateRequisition = (await _authorizationService.AuthorizeAsync(User, AuthorizationPolicies.RequisitionCreate)).Succeeded;
        var today = _businessCalendar.Today;

        if (scope.Kind == DepartmentScopeKind.Unrestricted)
        {
            return View("OperationsDashboard", await BuildOperationsDashboardAsync(canCreateRequisition, today, cancellationToken));
        }

        // 限科室（請領人）。科室為 null 代表帳號沒有配到科室：與 RequisitionsController 一致，直接拒絕。
        if (scope.DepartmentId is not { } departmentId)
        {
            return Forbid();
        }

        return View("RequesterDashboard", await BuildRequesterDashboardAsync(canCreateRequisition, departmentId, cancellationToken));
    }

    private async Task<OperationsDashboardViewModel> BuildOperationsDashboardAsync(
        bool canCreateRequisition,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        var pendingApprovalCount = await _dbContext.Requisitions.AsNoTracking()
            .CountAsync(requisition => requisition.Status == RequisitionStatus.PendingApproval, cancellationToken);
        var approvedAwaitingIssueCount = await _dbContext.Requisitions.AsNoTracking()
            .CountAsync(requisition => requisition.Status == RequisitionStatus.Approved, cancellationToken);

        // ★ D3：卡片數字必須重用效期預警／庫存查詢頁用的同一個查詢方法，不可另寫一份「差不多」的 SQL。
        var expiringLots = await _inventoryQueries.GetExpiringLotsAsync(30, today, cancellationToken: cancellationToken);
        var belowSafetyStock = await _inventoryQueries.GetItemsBelowSafetyStockAsync(today, cancellationToken: cancellationToken);
        var expiredInStockCount = await _dashboardQueries.GetExpiredInStockLotCountAsync(today, cancellationToken: cancellationToken);

        var auditEntries = await _dashboardQueries.GetRecentAuditEntriesAsync(RecentItemCount, cancellationToken);
        var approvedIssueQueueRows = await (
            from requisition in _dbContext.Requisitions.AsNoTracking()
            join department in _dbContext.Departments.AsNoTracking()
                on requisition.DepartmentId equals department.Id
            where requisition.Status == RequisitionStatus.Approved &&
                  EF.Property<DateTime?>(requisition, "ApprovedAt") != null
            orderby EF.Property<DateTime?>(requisition, "ApprovedAt"), requisition.Id
            select new
            {
                requisition.Id,
                RequisitionNo = EF.Property<string>(requisition, "RequisitionNo"),
                DepartmentName = department.Name,
                DepartmentNameEn = department.NameEn,
                LineCount = requisition.Lines.Count,
                ApprovedAt = EF.Property<DateTime>(requisition, "ApprovedAt"),
            })
            .ToListAsync(cancellationToken);
        var approvedIssueQueue = approvedIssueQueueRows.Select(row => new ApprovedIssueQueueItemViewModel(
            row.Id,
            row.RequisitionNo,
            BilingualText.Resolve(row.DepartmentName, row.DepartmentNameEn) ?? row.DepartmentName,
            row.LineCount,
            row.ApprovedAt)).ToList();

        return new OperationsDashboardViewModel
        {
            CanCreateRequisition = canCreateRequisition,
            PendingApprovalCount = pendingApprovalCount,
            ApprovedAwaitingIssueCount = approvedAwaitingIssueCount,
            ExpiringWithin30DaysCount = expiringLots.Count,
            BelowSafetyStockCount = belowSafetyStock.Count,
            ExpiredInStockCount = expiredInStockCount,
            ApprovedIssueQueue = approvedIssueQueue,
            RecentAudit = auditEntries.Select(AuditFeedItemViewModel.FromEntry).ToList(),
        };
    }

    private async Task<RequesterDashboardViewModel> BuildRequesterDashboardAsync(
        bool canCreateRequisition,
        long departmentId,
        CancellationToken cancellationToken)
    {
        var pendingApprovalCount = await _dbContext.Requisitions.AsNoTracking()
            .CountAsync(
                requisition => requisition.DepartmentId == departmentId && requisition.Status == RequisitionStatus.PendingApproval,
                cancellationToken);
        var approvedAwaitingIssueCount = await _dbContext.Requisitions.AsNoTracking()
            .CountAsync(
                requisition => requisition.DepartmentId == departmentId && requisition.Status == RequisitionStatus.Approved,
                cancellationToken);

        // ★ D5：「本月」用台灣時區的月份邊界，不可用 UTC 月份切（見 BusinessCalendar.CurrentMonthRangeUtc）。
        var (fromUtc, toUtc) = _businessCalendar.CurrentMonthRangeUtc();
        var issuedThisMonthCount = await _dbContext.Requisitions.AsNoTracking()
            .Where(requisition => requisition.DepartmentId == departmentId)
            .Where(requisition =>
                EF.Property<DateTime?>(requisition, "IssuedAt") != null &&
                EF.Property<DateTime?>(requisition, "IssuedAt") >= fromUtc &&
                EF.Property<DateTime?>(requisition, "IssuedAt") < toUtc)
            .CountAsync(cancellationToken);

        var recentRequisitionsQuery =
            from requisition in _dbContext.Requisitions.AsNoTracking()
            where requisition.DepartmentId == departmentId
            orderby EF.Property<DateTime>(requisition, "CreatedAt") descending, requisition.Id descending
            select new RequesterRequisitionItemViewModel(
                requisition.Id,
                EF.Property<string>(requisition, "RequisitionNo"),
                EF.Property<DateTime>(requisition, "CreatedAt"),
                requisition.Lines.Count,
                requisition.Status,
                requisition.RejectionReason);
        var recentRequisitions = await recentRequisitionsQuery.Take(RecentItemCount).ToListAsync(cancellationToken);

        return new RequesterDashboardViewModel
        {
            CanCreateRequisition = canCreateRequisition,
            PendingApprovalCount = pendingApprovalCount,
            ApprovedAwaitingIssueCount = approvedAwaitingIssueCount,
            IssuedThisMonthCount = issuedThisMonthCount,
            RecentRequisitions = recentRequisitions,
        };
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
