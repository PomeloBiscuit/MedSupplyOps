using System.Data;
using System.Globalization;
using Dapper;
using MedSupplyOps.Infrastructure.Auditing;
using MedSupplyOps.Infrastructure.Identity;
using MedSupplyOps.Infrastructure.Localization;
using MedSupplyOps.Infrastructure.Persistence;
using MedSupplyOps.Infrastructure.Persistence.Models;
using MedSupplyOps.Web.Authorization;
using MedSupplyOps.Web.Models.StorageLocations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Localization;
using Oracle.ManagedDataAccess.Client;

namespace MedSupplyOps.Web.Controllers;

public sealed class StorageLocationsController : Controller
{
    private const int DefaultLockWaitSeconds = 5;
    private const int OraLockWaitTimeout = 30006;
    private const int OraResourceBusy = 54;

    private readonly MedSupplyOpsDbContext _dbContext;
    private readonly ICurrentUser _currentUser;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public StorageLocationsController(
        MedSupplyOpsDbContext dbContext,
        ICurrentUser currentUser,
        IStringLocalizer<SharedResource> localizer)
    {
        _dbContext = dbContext;
        _currentUser = currentUser;
        _localizer = localizer;
    }

    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.StorageLocationManage)]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var locations = await _dbContext.StorageLocations.AsNoTracking()
            .Where(location => !location.IsDeleted)
            .OrderBy(location => location.Code)
            .ToListAsync(cancellationToken);

        return View(new StorageLocationIndexViewModel
        {
            Locations = locations.Select(location => new StorageLocationListRowViewModel(
                location.Id,
                location.Code,
                BilingualText.Resolve(location.Name, location.NameEn) ?? location.Name)).ToList(),
        });
    }

    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.StorageLocationManage)]
    public IActionResult Create() => View(new CreateStorageLocationViewModel());

    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.StorageLocationManage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreateStorageLocationViewModel model, CancellationToken cancellationToken)
    {
        Normalize(model);
        ModelState.Remove(nameof(model.Code));
        ModelState.Remove(nameof(model.Name));
        ModelState.Remove(nameof(model.EnglishName));
        TryValidateModel(model);
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        if (await _dbContext.StorageLocations.AsNoTracking()
            .AnyAsync(location => location.Code == model.Code, cancellationToken))
        {
            ModelState.AddModelError(nameof(model.Code), _localizer["儲藏位置代碼 {0} 已存在。", model.Code]);
            return View(model);
        }

        if (await _dbContext.StorageLocations.AsNoTracking()
            .AnyAsync(location => location.Name == model.Name, cancellationToken))
        {
            ModelState.AddModelError(nameof(model.Name), _localizer["儲藏位置名稱 {0} 已存在。", model.Name]);
            return View(model);
        }

        var location = new StorageLocation
        {
            Code = model.Code,
            Name = model.Name,
            NameEn = model.EnglishName,
        };

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            _dbContext.StorageLocations.Add(location);
            await _dbContext.SaveChangesAsync(cancellationToken);
            AddAudit(location, AuditValues.CreateAction, oldValue: null, DateTime.UtcNow);
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (ContainsConstraint(exception, "UQ_STORAGE_LOCATIONS_CODE"))
        {
            await transaction.RollbackAsync(cancellationToken);
            ModelState.AddModelError(nameof(model.Code), _localizer["儲藏位置代碼 {0} 已存在。", model.Code]);
            return View(model);
        }
        catch (DbUpdateException exception) when (ContainsConstraint(exception, "UQ_STORAGE_LOCATIONS_NAME"))
        {
            await transaction.RollbackAsync(cancellationToken);
            ModelState.AddModelError(nameof(model.Name), _localizer["儲藏位置名稱 {0} 已存在。", model.Name]);
            return View(model);
        }

        TempData["SuccessMessage"] = _localizer["儲藏位置 {0} 已新增。", location.Name].Value;
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.StorageLocationManage)]
    public async Task<IActionResult> Edit(long id, CancellationToken cancellationToken)
    {
        var model = await _dbContext.StorageLocations.AsNoTracking()
            .Where(location => location.Id == id && !location.IsDeleted)
            .Select(location => new EditStorageLocationViewModel
            {
                Id = location.Id,
                Code = location.Code,
                Name = location.Name,
                EnglishName = location.NameEn,
            })
            .SingleOrDefaultAsync(cancellationToken);

        return model is null ? NotFound() : View(model);
    }

    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.StorageLocationManage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(
        long id,
        [Bind("EnglishName")] EditStorageLocationViewModel model,
        CancellationToken cancellationToken)
    {
        model.EnglishName = NormalizeOptional(model.EnglishName);
        ModelState.Remove(nameof(model.EnglishName));
        TryValidateModel(model);

        var location = await _dbContext.StorageLocations
            .SingleOrDefaultAsync(candidate => candidate.Id == id && !candidate.IsDeleted, cancellationToken);
        if (location is null)
        {
            return NotFound();
        }

        // Code 與 Name 刻意不在 allow-list；偽造欄位會被忽略，畫面值一律從持久層重載。
        model.Id = location.Id;
        model.Code = location.Code;
        model.Name = location.Name;
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var oldValue = StorageLocationAuditJson(location);
        location.NameEn = model.EnglishName;

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        AddAudit(location, AuditValues.UpdateAction, oldValue, DateTime.UtcNow);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        TempData["SuccessMessage"] = _localizer["儲藏位置 {0} 已更新。", location.Name].Value;
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.StorageLocationDeactivate)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Disable(long id, CancellationToken cancellationToken)
    {
        await using var transaction = await _dbContext.Database
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var connection = _dbContext.Database.GetDbConnection();
        var dbTransaction = transaction.GetDbTransaction();

        try
        {
            var locked = await connection.QuerySingleOrDefaultAsync<LockedStorageLocationRow>(new CommandDefinition(
                LockStorageLocationSql + " " + DefaultLockWaitSeconds.ToString(CultureInfo.InvariantCulture),
                new { locationId = id },
                dbTransaction,
                cancellationToken: cancellationToken));
            if (locked is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return NotFound();
            }

            var stock = await connection.QuerySingleAsync<PositiveStockRow>(new CommandDefinition(
                PositiveStockSql,
                new { name = locked.Name },
                dbTransaction,
                cancellationToken: cancellationToken));
            var quantity = decimal.ToInt32(stock.Quantity);
            if (quantity > 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                TempData["ErrorMessage"] = _localizer["儲藏位置 {0} 仍有庫存 {1}，請先清空後再停用。", locked.Name, quantity].Value;
                return RedirectToAction(nameof(Index));
            }

            var location = await _dbContext.StorageLocations.SingleAsync(candidate => candidate.Id == id, cancellationToken);
            var oldValue = StorageLocationAuditJson(location);
            var now = DateTime.UtcNow;
            location.IsDeleted = true;
            location.DeletedAt = now;
            location.DeletedBy = _currentUser.Actor;
            AddAudit(location, AuditValues.DeleteAction, oldValue, now);
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            TempData["SuccessMessage"] = _localizer["儲藏位置 {0} 已停用。", location.Name].Value;
            return RedirectToAction(nameof(Index));
        }
        catch (OracleException exception) when (exception.Number is OraLockWaitTimeout or OraResourceBusy)
        {
            await transaction.RollbackAsync(cancellationToken);
            TempData["ErrorMessage"] = _localizer["目前有其他人正在異動這個儲藏位置，請稍後再試。"].Value;
            return RedirectToAction(nameof(Index));
        }
    }

    private void AddAudit(StorageLocation location, string action, string? oldValue, DateTime occurredAt)
        => _dbContext.AuditLogs.Add(new AuditLog
        {
            EntityType = AuditValues.StorageLocationEntity,
            EntityId = location.Id.ToString(CultureInfo.InvariantCulture),
            Action = action,
            Actor = _currentUser.Actor,
            OccurredAt = occurredAt,
            OldValue = oldValue,
            NewValue = StorageLocationAuditJson(location),
        });

    private static void Normalize(CreateStorageLocationViewModel model)
    {
        model.Code = (model.Code ?? string.Empty).Trim().ToUpperInvariant();
        model.Name = (model.Name ?? string.Empty).Trim();
        model.EnglishName = NormalizeOptional(model.EnglishName);
    }

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string StorageLocationAuditJson(StorageLocation location) => AuditValues.ToJson(new
    {
        location.Code,
        location.Name,
        location.NameEn,
        location.IsDeleted,
        location.DeletedAt,
        location.DeletedBy,
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

    private const string LockStorageLocationSql = """
        SELECT location_id AS LocationId,
               name AS Name
        FROM storage_locations
        WHERE location_id = :locationId
          AND is_deleted = 0
        FOR UPDATE WAIT
        """;

    private const string PositiveStockSql = """
        SELECT NVL(SUM(quantity), 0) AS Quantity
        FROM stock_lots
        WHERE storage_location = :name
          AND quantity > 0
        """;

    private sealed class LockedStorageLocationRow
    {
        public decimal LocationId { get; init; }

        public string Name { get; init; } = string.Empty;
    }

    private sealed class PositiveStockRow
    {
        public decimal Quantity { get; init; }
    }
}
