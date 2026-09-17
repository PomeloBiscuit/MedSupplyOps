using System.ComponentModel.DataAnnotations;

namespace MedSupplyOps.Web.Models.Receiving;

public sealed record ReceivingItemOptionViewModel(
    long Id,
    string Code,
    string Name,
    string UnitOfMeasure);

public sealed record ReceivingStorageLocationOptionViewModel(string Name, string Label);

public sealed class ReceivingViewModel
{
    [Display(Name = "品項")]
    [Required(ErrorMessage = "品項為必填。")]
    [Range(1, long.MaxValue, ErrorMessage = "請選擇品項。")]
    public long ItemId { get; set; }

    [Display(Name = "批號")]
    [Required(ErrorMessage = "批號為必填。")]
    [StringLength(64, ErrorMessage = "批號不可超過 64 個字。")]
    public string LotNumber { get; set; } = string.Empty;

    [Display(Name = "效期")]
    [Required(ErrorMessage = "效期為必填。")]
    [DataType(DataType.Date)]
    public DateOnly ExpiryDate { get; set; }

    [Display(Name = "數量")]
    [Required(ErrorMessage = "數量為必填。")]
    [Range(1, 100_000, ErrorMessage = "數量必須是 1 到 100,000 的整數。")]
    public int Quantity { get; set; } = 1;

    [Display(Name = "儲藏位置")]
    [Required(ErrorMessage = "儲藏位置為必填。")]
    [StringLength(64, ErrorMessage = "儲藏位置不可超過 64 個字。")]
    public string StorageLocation { get; set; } = string.Empty;

    public IReadOnlyList<ReceivingItemOptionViewModel> Items { get; set; } = [];

    public IReadOnlyList<ReceivingStorageLocationOptionViewModel> StorageLocations { get; set; } = [];
}
