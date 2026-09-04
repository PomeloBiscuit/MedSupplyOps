using MedSupplyOps.Domain.Requisitions;
using MedSupplyOps.Infrastructure.Identity;
using MedSupplyOps.Infrastructure.Persistence;
using MedSupplyOps.Infrastructure.Queries;
using MedSupplyOps.Infrastructure.Services;
using MedSupplyOps.Web.Models.Requisitions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MedSupplyOps.Web.Controllers;

public sealed class RequisitionsController : Controller
{
    private const string ConcurrentReviewMessage = "此單已被他人處理，請重新整理後再試。";
    private readonly MedSupplyOpsDbContext _dbContext;
    private readonly InventoryQueries _inventoryQueries;
    private readonly StockIssueService _stockIssueService;
    private readonly ICurrentUser _currentUser;

    public RequisitionsController(
        MedSupplyOpsDbContext dbContext,
        InventoryQueries inventoryQueries,
        StockIssueService stockIssueService,
        ICurrentUser currentUser)
    {
        _dbContext = dbContext;
        _inventoryQueries = inventoryQueries;
        _stockIssueService = stockIssueService;
        _currentUser = currentUser;
    }

    [HttpGet]
    public async Task<IActionResult> Index(
        RequisitionStatus? status,
        long? departmentId,
        DateOnly? createdFrom,
        DateOnly? createdTo,
        CancellationToken cancellationToken)
    {
        if (createdFrom.HasValue && createdTo.HasValue && createdFrom > createdTo)
        {
            ModelState.AddModelError(nameof(createdTo), "建立日期的結束日不得早於開始日。");
        }

        var query = _dbContext.Requisitions.AsNoTracking();
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

        var requisitions = await (
            from requisition in query
            join department in _dbContext.Departments.AsNoTracking()
                on requisition.DepartmentId equals department.Id
            orderby EF.Property<DateTime>(requisition, "CreatedAt") descending, requisition.Id descending
            select new RequisitionListItemViewModel(
                requisition.Id,
                EF.Property<string>(requisition, "RequisitionNo"),
                department.Name,
                requisition.Status,
                EF.Property<DateTime>(requisition, "CreatedAt"),
                requisition.Lines.Count))
            .ToListAsync(cancellationToken);

        var departments = await GetDepartmentOptionsAsync(cancellationToken);
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
    public async Task<IActionResult> Create(DateOnly? asOf, CancellationToken cancellationToken)
    {
        var effectiveAsOf = asOf ?? DateOnly.FromDateTime(DateTime.Today);
        var model = new CreateRequisitionViewModel
        {
            AsOf = effectiveAsOf,
            Lines = [new CreateRequisitionLineViewModel { Quantity = 1 }],
        };
        await PopulateCreateOptionsAsync(model, cancellationToken);
        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreateRequisitionViewModel model, CancellationToken cancellationToken)
    {
        if (model.AsOf == default)
        {
            ModelState.AddModelError(nameof(model.AsOf), "查詢基準日不正確，請重新載入頁面。");
        }

        if (!ModelState.IsValid)
        {
            await PopulateCreateOptionsAsync(model, cancellationToken);
            return View(model);
        }

        var departmentExists = await _dbContext.Departments.AsNoTracking()
            .AnyAsync(
                department => department.Id == model.DepartmentId && department.IsActive && !department.IsDeleted,
                cancellationToken);
        if (!departmentExists)
        {
            ModelState.AddModelError(nameof(model.DepartmentId), "選擇的科室不存在或已停用。");
        }

        var requestedItemIds = model.Lines.Select(line => line.ItemId).Distinct().ToList();
        var validItemCount = await _dbContext.Items.AsNoTracking()
            .CountAsync(item => requestedItemIds.Contains(item.Id) && !item.IsDeleted, cancellationToken);
        if (validItemCount != requestedItemIds.Count)
        {
            ModelState.AddModelError(nameof(model.Lines), "明細包含不存在或已停用的品項。");
        }

        await PopulateCreateOptionsAsync(model, cancellationToken);
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

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (ContainsConstraint(exception, "UQ_REQ_LINES_ITEM"))
        {
            ModelState.AddModelError(nameof(model.Lines), "同一張請領單不可重複加入相同品項，請刪除重複明細。");
            return View(model);
        }
        catch (DbUpdateException exception) when (ContainsConstraint(exception, "UQ_REQUISITIONS_NO"))
        {
            ModelState.AddModelError(string.Empty, "單號產生衝突，請重新送出。");
            return View(model);
        }

        TempData["SuccessMessage"] = $"請領單 {GetShadowValue<string>(requisition, "RequisitionNo")} 已送審。";
        return RedirectToAction(nameof(Details), new { id = requisition.Id });
    }

    [HttpGet]
    public async Task<IActionResult> Details(long id, CancellationToken cancellationToken)
    {
        var header = await (
            from requisition in _dbContext.Requisitions.AsNoTracking()
            join department in _dbContext.Departments.AsNoTracking()
                on requisition.DepartmentId equals department.Id
            where requisition.Id == id
            select new
            {
                requisition.Id,
                RequisitionNo = EF.Property<string>(requisition, "RequisitionNo"),
                DepartmentName = department.Name,
                requisition.Status,
                requisition.RejectionReason,
                RowVersion = EF.Property<long>(requisition, "RowVersion"),
                CreatedAt = EF.Property<DateTime>(requisition, "CreatedAt"),
                SubmittedAt = EF.Property<DateTime?>(requisition, "SubmittedAt"),
                ApprovedAt = EF.Property<DateTime?>(requisition, "ApprovedAt"),
            }).SingleOrDefaultAsync(cancellationToken);

        if (header is null)
        {
            return NotFound();
        }

        var lines = await (
            from line in _dbContext.RequisitionLines.AsNoTracking()
            join item in _dbContext.Items.AsNoTracking() on line.ItemId equals item.Id
            where EF.Property<long>(line, "RequisitionId") == id
            orderby EF.Property<int>(line, "LineNo")
            select new RequisitionDetailsLineViewModel(
                EF.Property<int>(line, "LineNo"),
                item.Code,
                item.Name,
                item.UnitOfMeasure,
                line.Quantity))
            .ToListAsync(cancellationToken);

        var issueAllocations = await _inventoryQueries
            .GetRequisitionIssueAllocationsAsync(id, cancellationToken);

        return View(new RequisitionDetailsViewModel
        {
            Id = header.Id,
            RequisitionNo = header.RequisitionNo,
            DepartmentName = header.DepartmentName,
            Status = header.Status,
            RejectionReason = header.RejectionReason,
            RowVersion = header.RowVersion,
            CreatedAt = header.CreatedAt,
            SubmittedAt = header.SubmittedAt,
            ApprovedAt = header.ApprovedAt,
            Lines = lines,
            IssueAllocations = issueAllocations
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
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Issue(long id, CancellationToken cancellationToken)
    {
        var asOf = DateOnly.FromDateTime(DateTime.Today);
        var result = await _stockIssueService.IssueRequisitionAsync(id, asOf, _currentUser.Actor, cancellationToken);

        if (result.IsSuccess)
        {
            TempData["SuccessMessage"] = "請領單已發料，請核對下列配批明細。";
            return RedirectToAction(nameof(Details), new { id });
        }

        switch (result.FailureReason)
        {
            case RequisitionIssueFailureReason.NotFound:
                return NotFound();
            case RequisitionIssueFailureReason.InsufficientStock:
                var item = await _dbContext.Items.AsNoTracking()
                    .Where(candidate => candidate.Id == result.FailedItemId)
                    .Select(candidate => new { candidate.Code, candidate.Name })
                    .SingleOrDefaultAsync(cancellationToken);
                var itemText = item is null
                    ? $"品項 ID {result.FailedItemId}"
                    : $"品項 {item.Code}（{item.Name}）";
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
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(long id, long rowVersion, CancellationToken cancellationToken)
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
            requisition.Approve();
            _dbContext.Entry(requisition).Property("ApprovedAt").CurrentValue = DateTime.UtcNow;
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
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reject(
        long id,
        long rowVersion,
        string? rejectionReason,
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
            requisition.Reject(rejectionReason);
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
        return RedirectToAction(nameof(Details), new { id });
    }

    private async Task PopulateCreateOptionsAsync(CreateRequisitionViewModel model, CancellationToken cancellationToken)
    {
        model.Departments = await GetDepartmentOptionsAsync(cancellationToken);
        model.Items = await _dbContext.Items.AsNoTracking()
            .Where(item => !item.IsDeleted)
            .OrderBy(item => item.Code)
            .Select(item => new RequisitionItemOptionViewModel(item.Id, item.Code, item.Name, item.UnitOfMeasure))
            .ToListAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<RequisitionOptionViewModel>> GetDepartmentOptionsAsync(CancellationToken cancellationToken)
        => await _dbContext.Departments.AsNoTracking()
            .Where(department => department.IsActive && !department.IsDeleted)
            .OrderBy(department => department.Code)
            .Select(department => new RequisitionOptionViewModel(department.Id, department.Name))
            .ToListAsync(cancellationToken);

    private T GetShadowValue<T>(Requisition requisition, string propertyName)
        => (T)_dbContext.Entry(requisition).Property(propertyName).CurrentValue!;

    private static string GenerateRequisitionNo()
        => $"REQ-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid():N}"[..29].ToUpperInvariant();

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
}
