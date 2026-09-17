using MedSupplyOps.Domain.Requisitions;
using MedSupplyOps.Infrastructure.Auditing;
using MedSupplyOps.Infrastructure.Identity;
using MedSupplyOps.Infrastructure.Localization;
using MedSupplyOps.Infrastructure.Persistence;
using MedSupplyOps.Infrastructure.Persistence.Models;
using MedSupplyOps.Infrastructure.Queries;
using MedSupplyOps.Infrastructure.Services;
using MedSupplyOps.Infrastructure.Time;
using MedSupplyOps.Web.Authorization;
using MedSupplyOps.Web.Models.Requisitions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace MedSupplyOps.Web.Controllers;

public sealed class RequisitionsController : Controller
{
    private const string ConcurrentReviewMessage = "此單已被他人處理，請重新整理後再試。";
    private readonly MedSupplyOpsDbContext _dbContext;
    private readonly InventoryQueries _inventoryQueries;
    private readonly StockIssueService _stockIssueService;
    private readonly ICurrentUser _currentUser;
    private readonly DepartmentScopeResolver _departmentScopeResolver;
    private readonly BusinessCalendar _businessCalendar;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public RequisitionsController(
        MedSupplyOpsDbContext dbContext,
        InventoryQueries inventoryQueries,
        StockIssueService stockIssueService,
        ICurrentUser currentUser,
        DepartmentScopeResolver departmentScopeResolver,
        BusinessCalendar businessCalendar,
        IStringLocalizer<SharedResource> localizer)
    {
        _dbContext = dbContext;
        _inventoryQueries = inventoryQueries;
        _stockIssueService = stockIssueService;
        _currentUser = currentUser;
        _departmentScopeResolver = departmentScopeResolver;
        _businessCalendar = businessCalendar;
        _localizer = localizer;
    }

    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.RequisitionRead)]
    public async Task<IActionResult> Index(
        RequisitionStatus? status,
        long? departmentId,
        DateOnly? createdFrom,
        DateOnly? createdTo,
        CancellationToken cancellationToken)
    {
        if (createdFrom.HasValue && createdTo.HasValue && createdFrom > createdTo)
        {
            ModelState.AddModelError(nameof(createdTo), _localizer["建立日期的結束日不得早於開始日。"]);
        }

        var departmentScope = await _departmentScopeResolver.ResolveAsync(User, _currentUser.Actor);
        if (departmentScope.IsRestricted && departmentScope.DepartmentId is null)
        {
            return Forbid();
        }

        var query = _dbContext.Requisitions.AsNoTracking();
        if (departmentScope.IsRestricted)
        {
            departmentId = departmentScope.DepartmentId;
            query = query.Where(requisition => requisition.DepartmentId == departmentScope.DepartmentId);
        }
        if (status.HasValue)
        {
            query = query.Where(requisition => requisition.Status == status.Value);
        }

        if (departmentId.HasValue)
        {
            query = query.Where(requisition => requisition.DepartmentId == departmentId.Value);
        }

        if (createdFrom.HasValue)
        {
            var from = createdFrom.Value.ToDateTime(TimeOnly.MinValue);
            query = query.Where(requisition => EF.Property<DateTime>(requisition, "CreatedAt") >= from);
        }

        if (createdTo.HasValue)
        {
            var until = createdTo.Value.AddDays(1).ToDateTime(TimeOnly.MinValue);
            query = query.Where(requisition => EF.Property<DateTime>(requisition, "CreatedAt") < until);
        }

        var requisitionRows = await (
            from requisition in query
            join department in _dbContext.Departments.AsNoTracking()
                on requisition.DepartmentId equals department.Id
            orderby EF.Property<DateTime>(requisition, "CreatedAt") descending, requisition.Id descending
            select new
            {
                requisition.Id,
                RequisitionNo = EF.Property<string>(requisition, "RequisitionNo"),
                DepartmentName = department.Name,
                DepartmentNameEn = department.NameEn,
                requisition.Status,
                CreatedAt = EF.Property<DateTime>(requisition, "CreatedAt"),
                LineCount = requisition.Lines.Count,
            })
            .ToListAsync(cancellationToken);
        var requisitions = requisitionRows.Select(row => new RequisitionListItemViewModel(
            row.Id,
            row.RequisitionNo,
            BilingualText.Resolve(row.DepartmentName, row.DepartmentNameEn) ?? row.DepartmentName,
            row.Status,
            row.CreatedAt,
            row.LineCount)).ToList();

        var departments = await GetDepartmentOptionsAsync(departmentScope, cancellationToken);
        return View(new RequisitionIndexViewModel
        {
            Status = status,
            DepartmentId = departmentId,
            CreatedFrom = createdFrom,
            CreatedTo = createdTo,
            Departments = departments,
            Requisitions = requisitions,
        });
    }

    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.RequisitionCreate)]
    public async Task<IActionResult> Create(DateOnly? asOf, CancellationToken cancellationToken)
    {
        var departmentScope = await _departmentScopeResolver.ResolveAsync(User, _currentUser.Actor);
        if (departmentScope.IsRestricted && departmentScope.DepartmentId is null)
        {
            return Forbid();
        }

        var effectiveAsOf = asOf ?? _businessCalendar.Today;
        var model = new CreateRequisitionViewModel
        {
            AsOf = effectiveAsOf,
            DepartmentId = departmentScope.DepartmentId ?? 0,
            Lines = [new CreateRequisitionLineViewModel { Quantity = 1 }],
        };
        await PopulateCreateOptionsAsync(model, departmentScope, cancellationToken);
        return View(model);
    }

    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.RequisitionCreate)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreateRequisitionViewModel model, CancellationToken cancellationToken)
    {
        var departmentScope = await _departmentScopeResolver.ResolveAsync(User, _currentUser.Actor);
        if (departmentScope.IsRestricted &&
            (departmentScope.DepartmentId is null || model.DepartmentId != departmentScope.DepartmentId.Value))
        {
            return Forbid();
        }

        if (model.AsOf == default)
        {
            ModelState.AddModelError(nameof(model.AsOf), _localizer["查詢基準日不正確，請重新載入頁面。"]);
        }

        if (!ModelState.IsValid)
        {
            await PopulateCreateOptionsAsync(model, departmentScope, cancellationToken);
            return View(model);
        }

        var departmentExists = await _dbContext.Departments.AsNoTracking()
            .AnyAsync(
                department => department.Id == model.DepartmentId && department.IsActive && !department.IsDeleted,
                cancellationToken);
        if (!departmentExists)
        {
            ModelState.AddModelError(nameof(model.DepartmentId), _localizer["選擇的科室不存在或已停用。"]);
        }

        var requestedItemIds = model.Lines.Select(line => line.ItemId).Distinct().ToList();
        var validItemCount = await _dbContext.Items.AsNoTracking()
            .CountAsync(item => requestedItemIds.Contains(item.Id) && !item.IsDeleted, cancellationToken);
        if (validItemCount != requestedItemIds.Count)
        {
            ModelState.AddModelError(nameof(model.Lines), _localizer["明細包含不存在或已停用的品項。"]);
        }

        await PopulateCreateOptionsAsync(model, departmentScope, cancellationToken);
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        Requisition requisition;
        try
        {
            requisition = new Requisition(0, model.DepartmentId);
            foreach (var line in model.Lines)
            {
                requisition.AddLine(new RequisitionLine(line.ItemId, line.Quantity));
            }

            requisition.Submit();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            ModelState.AddModelError(nameof(model.Lines), exception.Message);
            return View(model);
        }

        _dbContext.Requisitions.Add(requisition);
        _dbContext.Entry(requisition).Property("RequisitionNo").CurrentValue = GenerateRequisitionNo();
        _dbContext.Entry(requisition).Property("SubmittedAt").CurrentValue = DateTime.UtcNow;

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            _dbContext.AuditLogs.Add(new AuditLog
            {
                EntityType = AuditValues.RequisitionEntity,
                EntityId = requisition.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Action = AuditValues.CreateAction,
                Actor = _currentUser.Actor,
                OccurredAt = DateTime.UtcNow,
                NewValue = AuditValues.ToJson(new
                {
                    requisitionNo = GetShadowValue<string>(requisition, "RequisitionNo"),
                    departmentId = requisition.DepartmentId,
                    status = requisition.Status.ToString(),
                    lines = requisition.Lines.Select(line => new { line.ItemId, line.Quantity }),
                }),
            });
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (ContainsConstraint(exception, "UQ_REQ_LINES_ITEM"))
        {
            ModelState.AddModelError(nameof(model.Lines), _localizer["同一張請領單不可重複加入相同品項，請刪除重複明細。"]);
            return View(model);
        }
        catch (DbUpdateException exception) when (ContainsConstraint(exception, "UQ_REQUISITIONS_NO"))
        {
            ModelState.AddModelError(string.Empty, _localizer["單號產生衝突，請重新送出。"]);
            return View(model);
        }

        TempData["SuccessMessage"] = $"請領單 {GetShadowValue<string>(requisition, "RequisitionNo")} 已送審。";
        return RedirectToAction(nameof(Details), new { id = requisition.Id });
    }

    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.RequisitionRead)]
    public async Task<IActionResult> Details(long id, CancellationToken cancellationToken)
    {
        var result = await LoadRequisitionDetailsAsync(id, returnUrl: null, cancellationToken);
        if (result.IsForbidden)
        {
            return Forbid();
        }

        return result.Model is null ? NotFound() : View(result.Model);
    }

    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.RequisitionRead)]
    public async Task<IActionResult> DetailsPanel(
        long id,
        string? returnUrl,
        CancellationToken cancellationToken)
    {
        var safeReturnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl : Url.Action(nameof(Index));
        var result = await LoadRequisitionDetailsAsync(id, safeReturnUrl, cancellationToken);
        if (result.IsForbidden)
        {
            return Forbid();
        }

        return result.Model is null ? NotFound() : PartialView("_DetailsPanel", result.Model);
    }

    private async Task<RequisitionDetailsLoadResult> LoadRequisitionDetailsAsync(
        long id,
        string? returnUrl,
        CancellationToken cancellationToken)
    {
        var departmentScope = await _departmentScopeResolver.ResolveAsync(User, _currentUser.Actor);
        if (departmentScope.IsRestricted && departmentScope.DepartmentId is null)
        {
            return new RequisitionDetailsLoadResult(IsForbidden: true, Model: null);
        }

        var header = await (
            from requisition in _dbContext.Requisitions.AsNoTracking()
            join department in _dbContext.Departments.AsNoTracking()
                on requisition.DepartmentId equals department.Id
            where requisition.Id == id &&
                  (!departmentScope.IsRestricted || requisition.DepartmentId == departmentScope.DepartmentId)
            select new
            {
                requisition.Id,
                RequisitionNo = EF.Property<string>(requisition, "RequisitionNo"),
                DepartmentName = department.Name,
                DepartmentNameEn = department.NameEn,
                requisition.Status,
                requisition.RejectionReason,
                RowVersion = EF.Property<long>(requisition, "RowVersion"),
                CreatedAt = EF.Property<DateTime>(requisition, "CreatedAt"),
                SubmittedAt = EF.Property<DateTime?>(requisition, "SubmittedAt"),
                ApprovedAt = EF.Property<DateTime?>(requisition, "ApprovedAt"),
            }).SingleOrDefaultAsync(cancellationToken);

        if (header is null)
        {
            return new RequisitionDetailsLoadResult(IsForbidden: false, Model: null);
        }

        var lineRows = await (
            from line in _dbContext.RequisitionLines.AsNoTracking()
            join item in _dbContext.Items.AsNoTracking() on line.ItemId equals item.Id
            where EF.Property<long>(line, "RequisitionId") == id
            orderby EF.Property<int>(line, "LineNo")
            select new
            {
                LineNo = EF.Property<int>(line, "LineNo"),
                item.Code,
                item.Name,
                item.NameEn,
                item.UnitOfMeasure,
                item.UnitOfMeasureEn,
                line.Quantity,
            })
            .ToListAsync(cancellationToken);
        var lines = lineRows.Select(row => new RequisitionDetailsLineViewModel(
            row.LineNo,
            row.Code,
            BilingualText.Resolve(row.Name, row.NameEn) ?? row.Name,
            BilingualText.Resolve(row.UnitOfMeasure, row.UnitOfMeasureEn) ?? row.UnitOfMeasure,
            row.Quantity)).ToList();

        var issueAllocations = await _inventoryQueries
            .GetRequisitionIssueAllocationsAsync(id, cancellationToken);

        return new RequisitionDetailsLoadResult(IsForbidden: false, new RequisitionDetailsViewModel
        {
            Id = header.Id,
            RequisitionNo = header.RequisitionNo,
            DepartmentName = BilingualText.Resolve(header.DepartmentName, header.DepartmentNameEn) ?? header.DepartmentName,
            Status = header.Status,
            RejectionReason = header.RejectionReason,
            RowVersion = header.RowVersion,
            CreatedAt = header.CreatedAt,
            SubmittedAt = header.SubmittedAt,
            ApprovedAt = header.ApprovedAt,
            Lines = lines,
            IssueAllocations = issueAllocations
                .Select(allocation => allocation.ForCulture(System.Globalization.CultureInfo.CurrentUICulture))
                .Select(allocation => new RequisitionIssueAllocationViewModel(
                    allocation.LineNo,
                    allocation.ItemCode,
                    allocation.ItemName,
                    allocation.UnitOfMeasure,
                    allocation.LotNumber,
                    allocation.ExpiryDate,
                    allocation.Quantity))
                .ToList(),
            CanRetryIssue = TempData["IssueRetryAvailable"] is true,
            ReturnUrl = returnUrl,
        });
    }

    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.RequisitionIssue)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Issue(long id, string? returnUrl, CancellationToken cancellationToken)
    {
        var asOf = _businessCalendar.Today;
        var result = await _stockIssueService.IssueRequisitionAsync(id, asOf, _currentUser.Actor, cancellationToken);

        if (result.IsSuccess)
        {
            TempData["SuccessMessage"] = "請領單已發料，請核對下列配批明細。";
            return RedirectAfterSuccessfulAction(id, returnUrl);
        }

        switch (result.FailureReason)
        {
            case RequisitionIssueFailureReason.NotFound:
                return NotFound();
            case RequisitionIssueFailureReason.InsufficientStock:
                var item = await _dbContext.Items.AsNoTracking()
                    .Where(candidate => candidate.Id == result.FailedItemId)
                    .Select(candidate => new { candidate.Code, candidate.Name, candidate.NameEn })
                    .SingleOrDefaultAsync(cancellationToken);
                var itemText = item is null
                    ? $"品項 ID {result.FailedItemId}"
                    : $"品項 {item.Code}（{BilingualText.Resolve(item.Name, item.NameEn) ?? item.Name}）";
                TempData["ErrorMessage"] =
                    $"{itemText} 庫存不足：需要 {result.RequestedQuantity}、目前可用 {result.AvailableQuantity}。";
                break;
            case RequisitionIssueFailureReason.IllegalStatusTransition:
                TempData["ErrorMessage"] = result.StatusAtFailure.HasValue
                    ? $"此單目前為「{RequisitionStatusText.Get(result.StatusAtFailure.Value)}」，無法發料。"
                    : "此單目前狀態不允許發料。";
                break;
            case RequisitionIssueFailureReason.LockTimeout:
                TempData["ErrorMessage"] = "系統忙碌中，請稍後再試。";
                TempData["IssueRetryAvailable"] = true;
                break;
            case RequisitionIssueFailureReason.NoLines:
                TempData["ErrorMessage"] = "此單沒有明細，無法發料。";
                break;
            default:
                TempData["ErrorMessage"] = "發料未完成，請重新整理後再試。";
                break;
        }

        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.RequisitionReview)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(
        long id,
        long rowVersion,
        string? returnUrl,
        CancellationToken cancellationToken)
    {
        var requisition = await _dbContext.Requisitions.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (requisition is null)
        {
            return NotFound();
        }

        if (GetShadowValue<long>(requisition, "RowVersion") != rowVersion)
        {
            TempData["ErrorMessage"] = ConcurrentReviewMessage;
            return RedirectToAction(nameof(Details), new { id });
        }

        try
        {
            var oldStatus = requisition.Status.ToString();
            requisition.Approve();
            _dbContext.Entry(requisition).Property("ApprovedAt").CurrentValue = DateTime.UtcNow;
            _dbContext.AuditLogs.Add(new AuditLog
            {
                EntityType = AuditValues.RequisitionEntity,
                EntityId = id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Action = AuditValues.ApproveAction,
                Actor = _currentUser.Actor,
                OccurredAt = DateTime.UtcNow,
                OldValue = AuditValues.ToJson(new { status = oldStatus }),
                NewValue = AuditValues.ToJson(new { status = requisition.Status.ToString() }),
            });
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (InvalidOperationException)
        {
            TempData["ErrorMessage"] = $"此單目前為「{RequisitionStatusText.Get(requisition.Status)}」，無法核准。";
            return RedirectToAction(nameof(Details), new { id });
        }
        catch (DbUpdateConcurrencyException)
        {
            TempData["ErrorMessage"] = ConcurrentReviewMessage;
            return RedirectToAction(nameof(Details), new { id });
        }

        TempData["SuccessMessage"] = "請領單已核准。";
        return RedirectAfterSuccessfulAction(id, returnUrl);
    }

    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.RequisitionReview)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reject(
        long id,
        long rowVersion,
        string? rejectionReason,
        string? returnUrl,
        CancellationToken cancellationToken)
    {
        var requisition = await _dbContext.Requisitions.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (requisition is null)
        {
            return NotFound();
        }

        if (GetShadowValue<long>(requisition, "RowVersion") != rowVersion)
        {
            TempData["ErrorMessage"] = ConcurrentReviewMessage;
            return RedirectToAction(nameof(Details), new { id });
        }

        if (string.IsNullOrWhiteSpace(rejectionReason))
        {
            TempData["ErrorMessage"] = "駁回必須填寫原因。";
            return RedirectToAction(nameof(Details), new { id });
        }

        if (rejectionReason.Length > 500)
        {
            TempData["ErrorMessage"] = "駁回原因不可超過 500 個字。";
            return RedirectToAction(nameof(Details), new { id });
        }

        try
        {
            var oldStatus = requisition.Status.ToString();
            requisition.Reject(rejectionReason);
            _dbContext.AuditLogs.Add(new AuditLog
            {
                EntityType = AuditValues.RequisitionEntity,
                EntityId = id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Action = AuditValues.RejectAction,
                Actor = _currentUser.Actor,
                OccurredAt = DateTime.UtcNow,
                OldValue = AuditValues.ToJson(new { status = oldStatus }),
                NewValue = AuditValues.ToJson(new
                {
                    status = requisition.Status.ToString(),
                    rejectionReason = requisition.RejectionReason,
                }),
            });
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (InvalidOperationException)
        {
            TempData["ErrorMessage"] = $"此單目前為「{RequisitionStatusText.Get(requisition.Status)}」，無法駁回。";
            return RedirectToAction(nameof(Details), new { id });
        }
        catch (DbUpdateConcurrencyException)
        {
            TempData["ErrorMessage"] = ConcurrentReviewMessage;
            return RedirectToAction(nameof(Details), new { id });
        }

        TempData["SuccessMessage"] = "請領單已駁回。";
        return RedirectAfterSuccessfulAction(id, returnUrl);
    }

    private IActionResult RedirectAfterSuccessfulAction(long id, string? returnUrl)
    {
        if (returnUrl is null)
        {
            return RedirectToAction(nameof(Details), new { id });
        }

        return Url.IsLocalUrl(returnUrl)
            ? LocalRedirect(returnUrl)
            : RedirectToAction(nameof(Index));
    }

    private async Task PopulateCreateOptionsAsync(
        CreateRequisitionViewModel model,
        DepartmentScope departmentScope,
        CancellationToken cancellationToken)
    {
        model.Departments = await GetDepartmentOptionsAsync(departmentScope, cancellationToken);
        var items = await _dbContext.Items.AsNoTracking()
            .Where(item => !item.IsDeleted)
            .OrderBy(item => item.Code)
            .ToListAsync(cancellationToken);
        model.Items = items.Select(item => new RequisitionItemOptionViewModel(
            item.Id,
            item.Code,
            BilingualText.Option(item.Name, item.NameEn),
            BilingualText.Option(item.UnitOfMeasure, item.UnitOfMeasureEn))).ToList();
    }

    private async Task<IReadOnlyList<RequisitionOptionViewModel>> GetDepartmentOptionsAsync(
        DepartmentScope departmentScope,
        CancellationToken cancellationToken)
    {
        var query = _dbContext.Departments.AsNoTracking()
            .Where(department => department.IsActive && !department.IsDeleted);
        if (departmentScope.IsRestricted)
        {
            query = query.Where(department => department.Id == departmentScope.DepartmentId);
        }

        var departments = await query
            .OrderBy(department => department.Code)
            .ToListAsync(cancellationToken);
        return departments.Select(department => new RequisitionOptionViewModel(
            department.Id,
            BilingualText.Option(department.Name, department.NameEn))).ToList();
    }

    private T GetShadowValue<T>(Requisition requisition, string propertyName)
        => (T)_dbContext.Entry(requisition).Property(propertyName).CurrentValue!;

    private string GenerateRequisitionNo()
        => $"REQ-{_businessCalendar.Today:yyyyMMdd}-{Guid.NewGuid():N}"[..29].ToUpperInvariant();

    private static bool ContainsConstraint(Exception exception, string constraintName)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current.Message.Contains(constraintName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private sealed record RequisitionDetailsLoadResult(bool IsForbidden, RequisitionDetailsViewModel? Model);

}
