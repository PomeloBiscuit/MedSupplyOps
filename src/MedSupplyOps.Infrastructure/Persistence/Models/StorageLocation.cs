namespace MedSupplyOps.Infrastructure.Persistence.Models;

/// <summary>儲藏位置主檔。批次仍保存位置名稱文字，本型別不建立 StockLot 導覽屬性。</summary>
public sealed class StorageLocation
{
    public long Id { get; set; }

    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? NameEn { get; set; }

    public bool IsDeleted { get; set; }

    public DateTime? DeletedAt { get; set; }

    public string? DeletedBy { get; set; }

    public DateTime CreatedAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;
}
