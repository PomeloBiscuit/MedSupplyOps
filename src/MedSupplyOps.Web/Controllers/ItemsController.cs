using System.Data;
using System.Globalization;
using Dapper;
using MedSupplyOps.Infrastructure.Auditing;
using MedSupplyOps.Infrastructure.Identity;
using MedSupplyOps.Infrastructure.Localization;
using MedSupplyOps.Infrastructure.Persistence;
using MedSupplyOps.Infrastructure.Persistence.Models;
using MedSupplyOps.Web.Authorization;
using MedSupplyOps.Web.Models.Items;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Localization;
using Oracle.ManagedDataAccess.Client;

namespace MedSupplyOps.Web.Controllers;

public sealed class ItemsController : Controller
{
    private const int DefaultLockWaitSeconds = 5;
    private const int OraLockWaitTimeout = 30006;
    private const int OraResourceBusy = 54;

    private readonly MedSupplyOpsDbContext _dbContext;
    private readonly ICurrentUser _currentUser;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public ItemsController(
        MedSupplyOpsDbContext dbContext,
        ICurrentUser currentUser,
        IStringLocalizer<SharedResource> localizer)
    {
        _dbContext = dbContext;
        _currentUser = currentUser;
        _localizer = localizer;
    }

    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.ItemManage)]
    public async Task<IActionResult> Index(string? search, CancellationToken cancellationToken)
    {
        var normalizedSearch = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        var query = _dbContext.Items.AsNoTracking().Where(item => !item.IsDeleted);
        if (normalizedSearch is not null)
        {
            query = query.Where(item =>
                item.Code.Contains(normalizedSearch) ||
                (item.Barcode != null && item.Barcode.Contains(normalizedSearch)) ||
                item.Name.Contains(normalizedSearch) ||
                (item.NameEn != null && item.NameEn.Contains(normalizedSearch)) ||
                (item.Specification != null && item.Specification.Contains(normalizedSearch)) ||
                (item.SpecificationEn != null && item.SpecificationEn.Contains(normalizedSearch)) ||
                item.UnitOfMeasure.Contains(normalizedSearch) ||
                (item.UnitOfMeasureEn != null && item.UnitOfMeasureEn.Contains(normalizedSearch)));
        }

        var items = await query
            .OrderBy(item => item.Code)
            .ToListAsync(cancellationToken);

        return View(new ItemIndexViewModel
        {
            Search = normalizedSearch,
            Items = items.Select(item => new ItemListRowViewModel(
                item.Id,
                item.Code,
                item.Barcode,
                BilingualText.Resolve(item.Name, item.NameEn) ?? item.Name,
                BilingualText.Resolve(item.Specification, item.SpecificationEn),
                BilingualText.Resolve(item.UnitOfMeasure, item.UnitOfMeasureEn) ?? item.UnitOfMeasure,
                item.SafetyStockQty)).ToList(),
        });
    }

    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.ItemManage)]
    public IActionResult Create() => View(new CreateItemViewModel());

    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.ItemManage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreateItemViewModel model, CancellationToken cancellationToken)
    {
        // ★ 只重新驗證被正規化過的欄位；不可以 ModelState.Clear()，那會吞掉數字欄的綁定錯誤（L-028）。
        Normalize(model);
        ModelState.Remove(nameof(model.Code));
        ModelState.Remove(nameof(model.Barcode));
        ModelState.Remove(nameof(model.Name));
        ModelState.Remove(nameof(model.EnglishName));
        ModelState.Remove(nameof(model.Specification));
        ModelState.Remove(nameof(model.EnglishSpecification));
        ModelState.Remove(nameof(model.UnitOfMeasure));
        ModelState.Remove(nameof(model.EnglishUnitOfMeasure));
        TryValidateModel(model);

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        if (await _dbContext.Items.AsNoTracking()
            .AnyAsync(item => !item.IsDeleted && item.Code == model.Code, cancellationToken))
        {
            AddDuplicateCodeError(model.Code);
            return View(model);
        }

        if (model.Barcode is not null && await _dbContext.Items.AsNoTracking()
            .AnyAsync(item => item.Barcode == model.Barcode, cancellationToken))
        {
            AddDuplicateBarcodeError(model.Barcode);
            return View(model);
        }

        var item = new Item
        {
            Code = model.Code,
            Barcode = model.Barcode,
            Name = model.Name,
            NameEn = model.EnglishName,
            Specification = model.Specification,
            SpecificationEn = model.EnglishSpecification,
            UnitOfMeasure = model.UnitOfMeasure,
            UnitOfMeasureEn = model.EnglishUnitOfMeasure,
            SafetyStockQty = model.SafetyStockQty,
            TracksLot = true,
            TracksExpiry = true,
        };

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            _dbContext.Items.Add(item);
            await _dbContext.SaveChangesAsync(cancellationToken);
            _dbContext.AuditLogs.Add(new AuditLog
            {
                EntityType = AuditValues.ItemEntity,
                EntityId = item.Id.ToString(CultureInfo.InvariantCulture),
                Action = AuditValues.CreateAction,
                Actor = _currentUser.Actor,
                OccurredAt = DateTime.UtcNow,
                NewValue = ItemAuditJson(item),
            });
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (ContainsConstraint(exception, "UX_ITEMS_CODE_ACTIVE"))
        {
            await transaction.RollbackAsync(cancellationToken);
            AddDuplicateCodeError(model.Code);
            return View(model);
        }
        catch (DbUpdateException exception) when (ContainsConstraint(exception, "UX_ITEMS_BARCODE"))
        {
            await transaction.RollbackAsync(cancellationToken);
            AddDuplicateBarcodeError(model.Barcode!);
            return View(model);
        }

        TempData["SuccessMessage"] = $"品項 {item.Code} 已新增。";
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.ItemManage)]
    public async Task<IActionResult> Edit(long id, CancellationToken cancellationToken)
    {
        var model = await _dbContext.Items.AsNoTracking()
            .Where(item => item.Id == id && !item.IsDeleted)
            .Select(item => new EditItemViewModel
            {
                Id = item.Id,
                Code = item.Code,
                Barcode = item.Barcode,
                PersistedBarcode = item.Barcode,
                Name = item.Name,
                EnglishName = item.NameEn,
                Specification = item.Specification,
                EnglishSpecification = item.SpecificationEn,
                UnitOfMeasure = item.UnitOfMeasure,
                EnglishUnitOfMeasure = item.UnitOfMeasureEn,
                SafetyStockQty = item.SafetyStockQty,
            })
            .SingleOrDefaultAsync(cancellationToken);

        return model is null ? NotFound() : View(model);
    }

    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.ItemManage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(
        long id,
        [Bind("Barcode,Name,EnglishName,Specification,EnglishSpecification,EnglishUnitOfMeasure,SafetyStockQty")] EditItemViewModel model,
        CancellationToken cancellationToken)
    {
        // ★ 同 Create：安全存量送空白或非數字時，Clear() 會讓它變成 0 寫進資料庫（L-028）。
        Normalize(model);
        ModelState.Remove(nameof(model.Barcode));
        ModelState.Remove(nameof(model.Name));
        ModelState.Remove(nameof(model.EnglishName));
        ModelState.Remove(nameof(model.Specification));
        ModelState.Remove(nameof(model.EnglishSpecification));
        ModelState.Remove(nameof(model.EnglishUnitOfMeasure));
        TryValidateModel(model);

        var item = await _dbContext.Items
            .SingleOrDefaultAsync(candidate => candidate.Id == id && !candidate.IsDeleted, cancellationToken);
        if (item is null)
        {
            return NotFound();
        }

        // 料號與計量單位建立後不可修改。POST 採 allow-list binding，
        // 所以即使呼叫端自行送 Code / UnitOfMeasure，這兩欄也完全不會進入更新路徑。
        model.Id = item.Id;
        model.Code = item.Code;
        model.UnitOfMeasure = item.UnitOfMeasure;
        model.PersistedBarcode = item.Barcode;
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        if (model.Barcode is not null && await _dbContext.Items.AsNoTracking()
            .AnyAsync(candidate => candidate.Id != item.Id && candidate.Barcode == model.Barcode, cancellationToken))
        {
            AddDuplicateBarcodeError(model.Barcode);
            return View(model);
        }

        var oldValue = ItemAuditJson(item);
        item.Barcode = model.Barcode;
        item.Name = model.Name;
        item.NameEn = model.EnglishName;
        item.Specification = model.Specification;
        item.SpecificationEn = model.EnglishSpecification;
        item.UnitOfMeasureEn = model.EnglishUnitOfMeasure;
        item.SafetyStockQty = model.SafetyStockQty;

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            _dbContext.AuditLogs.Add(new AuditLog
            {
                EntityType = AuditValues.ItemEntity,
                EntityId = item.Id.ToString(CultureInfo.InvariantCulture),
                Action = AuditValues.UpdateAction,
                Actor = _currentUser.Actor,
                OccurredAt = DateTime.UtcNow,
                OldValue = oldValue,
                NewValue = ItemAuditJson(item),
            });
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (ContainsConstraint(exception, "UX_ITEMS_BARCODE"))
        {
            await transaction.RollbackAsync(cancellationToken);
            AddDuplicateBarcodeError(model.Barcode!);
            return View(model);
        }

        TempData["SuccessMessage"] = $"品項 {item.Code} 已更新。";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.ItemDeactivate)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Disable(long id, CancellationToken cancellationToken)
    {
        await using var transaction = await _dbContext.Database
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var connection = _dbContext.Database.GetDbConnection();
        var dbTransaction = transaction.GetDbTransaction();

        try
        {
            var lockedItem = await connection.QuerySingleOrDefaultAsync<LockedItemRow>(new CommandDefinition(
                LockItemSql + " " + DefaultLockWaitSeconds.ToString(CultureInfo.InvariantCulture),
                new { itemId = id },
                dbTransaction,
                cancellationToken: cancellationToken));
            if (lockedItem is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return NotFound();
            }

            var positiveLots = (await connection.QueryAsync<PositiveStockRow>(new CommandDefinition(
                PositiveStockSql,
                new { itemId = id },
                dbTransaction,
                cancellationToken: cancellationToken))).AsList();
            if (positiveLots.Count > 0)
            {
                var total = positiveLots.Sum(row => decimal.ToInt32(row.Quantity));
                await transaction.RollbackAsync(cancellationToken);
                TempData["ErrorMessage"] = $"仍有庫存 {total}（批號 {positiveLots[0].LotNumber}），請先處理。";
                return RedirectToAction(nameof(Index));
            }

            var openRequisition = await connection.QueryFirstOrDefaultAsync<OpenRequisitionRow>(new CommandDefinition(
                OpenRequisitionSql,
                new { itemId = id },
                dbTransaction,
                cancellationToken: cancellationToken));
            if (openRequisition is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                TempData["ErrorMessage"] = $"有未結案的請領單：單號 {openRequisition.RequisitionNo}。";
                return RedirectToAction(nameof(Index));
            }

            var item = await _dbContext.Items.SingleAsync(candidate => candidate.Id == id, cancellationToken);
            var oldValue = ItemAuditJson(item);
            var now = DateTime.UtcNow;
            item.IsDeleted = true;
            item.DeletedAt = now;
            item.DeletedBy = _currentUser.Actor;
            _dbContext.AuditLogs.Add(new AuditLog
            {
                EntityType = AuditValues.ItemEntity,
                EntityId = item.Id.ToString(CultureInfo.InvariantCulture),
                Action = AuditValues.DeleteAction,
                Actor = _currentUser.Actor,
                OccurredAt = now,
                OldValue = oldValue,
                NewValue = ItemAuditJson(item),
            });

            // 已知限制：請領單建立不鎖品項列，因此它和本停用交易同時發生時，
            // 仍可能建立一張含剛停用品項的請領單。發料會以庫存不足明確失敗；目前不改該路徑。
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            TempData["SuccessMessage"] = $"品項 {item.Code} 已停用。";
            return RedirectToAction(nameof(Index));
        }
        catch (OracleException exception) when (exception.Number is OraLockWaitTimeout or OraResourceBusy)
        {
            await transaction.RollbackAsync(cancellationToken);
            TempData["ErrorMessage"] = "目前有其他人正在異動這個品項，請稍後再試。";
            return RedirectToAction(nameof(Index));
        }
    }

    private void AddDuplicateCodeError(string code)
        => ModelState.AddModelError(nameof(CreateItemViewModel.Code), $"料號 {code} 已存在。");

    private void AddDuplicateBarcodeError(string barcode)
        => ModelState.AddModelError(nameof(CreateItemViewModel.Barcode), _localizer["條碼 {0} 已存在。", barcode]);

    private static void Normalize(CreateItemViewModel model)
    {
        model.Code = NormalizeKey(model.Code);
        model.Barcode = NormalizeOptional(model.Barcode);
        model.Name = (model.Name ?? string.Empty).Trim();
        model.EnglishName = NormalizeOptional(model.EnglishName);
        model.Specification = NormalizeOptional(model.Specification);
        model.EnglishSpecification = NormalizeOptional(model.EnglishSpecification);
        model.UnitOfMeasure = (model.UnitOfMeasure ?? string.Empty).Trim();
        model.EnglishUnitOfMeasure = NormalizeOptional(model.EnglishUnitOfMeasure);
    }

    private static void Normalize(EditItemViewModel model)
    {
        model.Barcode = NormalizeOptional(model.Barcode);
        model.Name = (model.Name ?? string.Empty).Trim();
        model.EnglishName = NormalizeOptional(model.EnglishName);
        model.Specification = NormalizeOptional(model.Specification);
        model.EnglishSpecification = NormalizeOptional(model.EnglishSpecification);
        model.EnglishUnitOfMeasure = NormalizeOptional(model.EnglishUnitOfMeasure);
    }

    private static string NormalizeKey(string? value) => (value ?? string.Empty).Trim().ToUpperInvariant();

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string ItemAuditJson(Item item) => AuditValues.ToJson(new
    {
        item.Code,
        item.Barcode,
        item.Name,
        item.NameEn,
        item.Specification,
        item.SpecificationEn,
        item.UnitOfMeasure,
        item.UnitOfMeasureEn,
        item.SafetyStockQty,
        item.IsDeleted,
        item.DeletedAt,
        item.DeletedBy,
    });

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

    private const string LockItemSql = """
        SELECT item_id AS ItemId
        FROM items
        WHERE item_id = :itemId
          AND is_deleted = 0
        FOR UPDATE WAIT
        """;

    private const string PositiveStockSql = """
        SELECT lot_number AS LotNumber,
               quantity AS Quantity
        FROM stock_lots
        WHERE item_id = :itemId
          AND quantity > 0
        ORDER BY expiry_date, lot_number, stock_lot_id
        """;

    private const string OpenRequisitionSql = """
        SELECT r.requisition_no AS RequisitionNo
        FROM requisitions r
        INNER JOIN requisition_lines rl ON rl.requisition_id = r.requisition_id
        WHERE rl.item_id = :itemId
          AND r.status IN ('Draft', 'PendingApproval', 'Approved')
        ORDER BY r.requisition_no
        FETCH FIRST 1 ROW ONLY
        """;

    private sealed class LockedItemRow
    {
        public decimal ItemId { get; init; }
    }

    private sealed class PositiveStockRow
    {
        public string LotNumber { get; init; } = string.Empty;
        public decimal Quantity { get; init; }
    }

    private sealed class OpenRequisitionRow
    {
        public string RequisitionNo { get; init; } = string.Empty;
    }
}
