using System.Globalization;
using MedSupplyOps.Domain.Barcodes;
using MedSupplyOps.Infrastructure.Localization;
using MedSupplyOps.Infrastructure.Persistence;
using MedSupplyOps.Infrastructure.Services;
using MedSupplyOps.Infrastructure.Time;
using MedSupplyOps.Web.Authorization;
using MedSupplyOps.Web.Models.Receiving;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace MedSupplyOps.Web.Controllers;

public sealed class ReceivingController : Controller
{
    private readonly MedSupplyOpsDbContext _dbContext;
    private readonly StockReceivingService _stockReceivingService;
    private readonly BusinessCalendar _businessCalendar;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public ReceivingController(
        MedSupplyOpsDbContext dbContext,
        StockReceivingService stockReceivingService,
        BusinessCalendar businessCalendar,
        IStringLocalizer<SharedResource> localizer)
    {
        _dbContext = dbContext;
        _stockReceivingService = stockReceivingService;
        _businessCalendar = businessCalendar;
        _localizer = localizer;
    }

    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.StockReceive)]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var model = new ReceivingViewModel
        {
            ExpiryDate = _businessCalendar.Today,
            Quantity = 1,
        };
        await PopulateItemsAsync(model, cancellationToken);
        return View(model);
    }

    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.StockReceive)]
    public async Task<IActionResult> LookupBarcode(string? value, CancellationToken cancellationToken)
    {
        var scannedValue = value?.Trim();
        if (string.IsNullOrEmpty(scannedValue))
        {
            return BadRequest(new { message = _localizer["無法解析條碼。"].Value });
        }

        Gs1BarcodeData? gs1 = null;
        if (Gs1BarcodeParser.IsGs1Candidate(scannedValue))
        {
            if (!Gs1BarcodeParser.TryParse(scannedValue, _businessCalendar.Today, out gs1))
            {
                return BadRequest(new { message = _localizer["無法解析條碼。"].Value });
            }
        }

        var itemBarcode = gs1?.Gtin ?? scannedValue;
        var item = await _dbContext.Items.AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => !candidate.IsDeleted && candidate.Barcode == itemBarcode,
                cancellationToken);
        if (item is null)
        {
            return NotFound(new { message = _localizer["這個條碼沒有對應的品項"].Value });
        }

        return Json(new
        {
            itemId = item.Id,
            itemLabel = $"{item.Code} {BilingualText.Option(item.Name, item.NameEn)}",
            expiryDate = gs1?.ExpiryDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            lotNumber = gs1?.LotNumber,
        });
    }

    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.StockReceive)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Index(ReceivingViewModel model, CancellationToken cancellationToken)
    {
        model.LotNumber = NormalizeKey(model.LotNumber);
        model.StorageLocation = (model.StorageLocation ?? string.Empty).Trim();

        // ★ 只清掉「被正規化過的欄位」的舊驗證結果，不可以 ModelState.Clear()：
        //   那會連同綁定錯誤一起清掉 —— 數量送空白時屬性維持預設值 1，重新驗證又合法，
        //   於是入庫一個使用者沒有輸入的數量，畫面顯示成功。見 L-028。
        ModelState.Remove(nameof(model.LotNumber));
        ModelState.Remove(nameof(model.StorageLocation));
        TryValidateModel(model);
        if (model.ExpiryDate == default)
        {
            ModelState.AddModelError(nameof(model.ExpiryDate), "請輸入效期。");
        }

        if (ModelState.IsValid && !await _dbContext.StorageLocations.AsNoTracking()
            .AnyAsync(
                location => !location.IsDeleted && location.Name == model.StorageLocation,
                cancellationToken))
        {
            ModelState.AddModelError(
                nameof(model.StorageLocation),
                _localizer["儲藏位置不存在或已停用。"].Value);
        }

        if (!ModelState.IsValid)
        {
            await PopulateItemsAsync(model, cancellationToken);
            return View(model);
        }

        var asOf = _businessCalendar.Today;
        var result = await _stockReceivingService.ReceiveAsync(
            model.ItemId,
            model.LotNumber,
            model.ExpiryDate,
            model.Quantity,
            model.StorageLocation,
            asOf,
            cancellationToken: cancellationToken);

        if (result.IsSuccess)
        {
            TempData["SuccessMessage"] = $"已入庫：批號 {model.LotNumber}，入庫後數量 {result.QuantityAfter}。";
            return RedirectToAction(nameof(Index));
        }

        var message = result.FailureReason switch
        {
            ReceiveFailureReason.Expired =>
                $"此批次效期 {model.ExpiryDate:yyyy-MM-dd} 已過期（今天是 {asOf:yyyy-MM-dd}），不可入庫。",
            ReceiveFailureReason.ExpiryMismatch =>
                $"批號 {model.LotNumber} 在儲藏位置 {model.StorageLocation} 已登記效期 {result.ExistingExpiry:yyyy-MM-dd}，與本次輸入不同，請確認標籤。",
            ReceiveFailureReason.ItemNotFound => "品項不存在或已停用。",
            ReceiveFailureReason.LockTimeout => "目前有其他人正在異動這個品項的庫存，請稍後再試。",
            _ => "入庫未完成，請重新整理後再試。",
        };
        ModelState.AddModelError(string.Empty, message);
        await PopulateItemsAsync(model, cancellationToken);
        return View(model);
    }

    private async Task PopulateItemsAsync(ReceivingViewModel model, CancellationToken cancellationToken)
    {
        var items = await _dbContext.Items.AsNoTracking()
            .Where(item => !item.IsDeleted)
            .OrderBy(item => item.Code)
            .ToListAsync(cancellationToken);
        model.Items = items.Select(item => new ReceivingItemOptionViewModel(
            item.Id,
            item.Code,
            BilingualText.Option(item.Name, item.NameEn),
            BilingualText.Option(item.UnitOfMeasure, item.UnitOfMeasureEn))).ToList();

        var locations = await _dbContext.StorageLocations.AsNoTracking()
            .Where(location => !location.IsDeleted)
            .OrderBy(location => location.Code)
            .ToListAsync(cancellationToken);
        model.StorageLocations = locations.Select(location => new ReceivingStorageLocationOptionViewModel(
            location.Name,
            BilingualText.Option(location.Name, location.NameEn))).ToList();
    }

    private static string NormalizeKey(string? value) => (value ?? string.Empty).Trim().ToUpperInvariant();
}
