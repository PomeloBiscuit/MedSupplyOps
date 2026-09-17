using System.ComponentModel.DataAnnotations;

namespace MedSupplyOps.Web.Models.StorageLocations;

public sealed class StorageLocationIndexViewModel
{
    public IReadOnlyList<StorageLocationListRowViewModel> Locations { get; init; } = [];
}

public sealed record StorageLocationListRowViewModel(long Id, string Code, string Name);

public sealed class CreateStorageLocationViewModel
{
    [Display(Name = "儲藏位置代碼")]
    [Required(ErrorMessage = "儲藏位置代碼為必填。")]
    [RegularExpression("^[\\x21-\\x7E]{1,32}$", ErrorMessage = "儲藏位置代碼只能包含 ASCII 非空白字元，且不可超過 32 個字。")]
    public string Code { get; set; } = string.Empty;

    [Display(Name = "儲藏位置名稱")]
    [Required(ErrorMessage = "儲藏位置名稱為必填。")]
    [StringLength(64, ErrorMessage = "儲藏位置名稱不可超過 64 個字。")]
    public string Name { get; set; } = string.Empty;

    [Display(Name = "英文名稱")]
    [StringLength(200, ErrorMessage = "英文名稱不可超過 200 個字。")]
    public string? EnglishName { get; set; }
}

public sealed class EditStorageLocationViewModel
{
    public long Id { get; set; }

    [Display(Name = "儲藏位置代碼")]
    public string Code { get; set; } = string.Empty;

    [Display(Name = "儲藏位置名稱")]
    public string Name { get; set; } = string.Empty;

    [Display(Name = "英文名稱")]
    [StringLength(200, ErrorMessage = "英文名稱不可超過 200 個字。")]
    public string? EnglishName { get; set; }
}
