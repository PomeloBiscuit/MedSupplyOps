using System;

namespace MedSupplyOps.Infrastructure.Persistence.Models;

/// <summary>
/// 醫材品項主檔（ITEMS）的持久化模型。
///
/// 為什麼是 Infrastructure 的獨立類別、不是 Domain 型別：
///   Domain 目前沒有 Item 聚合（FEFO 與狀態機不需要它）。
///   為了對映一張表而在零依賴的 Domain 裡新增一個貧血類別，只會讓 Domain 的邊界變模糊。
///   等到有品項相關的業務規則時，再把規則放進 Domain，這裡的模型退回純資料載體。
/// </summary>
public sealed class Item
{
    public long Id { get; set; }

    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? NameEn { get; set; }

    public string? Specification { get; set; }

    public string? SpecificationEn { get; set; }

    public string UnitOfMeasure { get; set; } = string.Empty;

    public string? UnitOfMeasureEn { get; set; }

    public bool TracksLot { get; set; } = true;

    public bool TracksExpiry { get; set; } = true;

    public int SafetyStockQty { get; set; }

    public bool IsDeleted { get; set; }

    public DateTime? DeletedAt { get; set; }

    public string? DeletedBy { get; set; }

    public DateTime CreatedAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public DateTime? UpdatedAt { get; set; }

    public string? UpdatedBy { get; set; }
}
