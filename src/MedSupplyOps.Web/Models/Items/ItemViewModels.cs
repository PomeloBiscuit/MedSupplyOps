using System.ComponentModel.DataAnnotations;

namespace MedSupplyOps.Web.Models.Items;

public sealed class ItemIndexViewModel
{
    public string? Search { get; init; }

    public IReadOnlyList<ItemListRowViewModel> Items { get; init; } = [];
}

public sealed record ItemListRowViewModel(
    long Id,
    string Code,
    string? Barcode,
    string Name,
    string? Specification,
    string UnitOfMeasure,
    int SafetyStockQty);

public sealed class CreateItemViewModel
{
    [Display(Name = "料號")]
    [Required(ErrorMessage = "料號為必填。")]
    [RegularExpression("^[A-Z0-9-]{1,32}$", ErrorMessage = "料號只能包含大寫英文字母、數字與連字號，且不可超過 32 個字。")]
    public string Code { get; set; } = string.Empty;

    [Display(Name = "條碼")]
    [StringLength(64, ErrorMessage = "條碼不可超過 64 個字。")]
    public string? Barcode { get; set; }

    [Display(Name = "名稱")]
    [Required(ErrorMessage = "名稱為必填。")]
    [StringLength(200, ErrorMessage = "名稱不可超過 200 個字。")]
    public string Name { get; set; } = string.Empty;

    [Display(Name = "英文品名")]
    [StringLength(200, ErrorMessage = "英文品名不可超過 200 個字。")]
    public string? EnglishName { get; set; }

    [Display(Name = "規格")]
    [StringLength(400, ErrorMessage = "規格不可超過 400 個字。")]
    public string? Specification { get; set; }

    [Display(Name = "英文規格")]
    [StringLength(400, ErrorMessage = "英文規格不可超過 400 個字。")]
    public string? EnglishSpecification { get; set; }

    [Display(Name = "計量單位")]
    [Required(ErrorMessage = "計量單位為必填。")]
    [StringLength(20, ErrorMessage = "計量單位不可超過 20 個字。")]
    public string UnitOfMeasure { get; set; } = string.Empty;

    [Display(Name = "英文計量單位")]
    [StringLength(20, ErrorMessage = "英文計量單位不可超過 20 個字。")]
    public string? EnglishUnitOfMeasure { get; set; }

    [Display(Name = "安全存量")]
    [Required(ErrorMessage = "安全存量為必填。")]
    [Range(0, 1_000_000, ErrorMessage = "安全存量必須是 0 到 1,000,000 的整數。")]
    public int SafetyStockQty { get; set; }
}

public sealed class EditItemViewModel
{
    public long Id { get; set; }

    [Display(Name = "料號")]
    public string Code { get; set; } = string.Empty;

    [Display(Name = "條碼")]
    [StringLength(64, ErrorMessage = "條碼不可超過 64 個字。")]
    public string? Barcode { get; set; }

    [Display(Name = "名稱")]
    [Required(ErrorMessage = "名稱為必填。")]
    [StringLength(200, ErrorMessage = "名稱不可超過 200 個字。")]
    public string Name { get; set; } = string.Empty;

    [Display(Name = "英文品名")]
    [StringLength(200, ErrorMessage = "英文品名不可超過 200 個字。")]
    public string? EnglishName { get; set; }

    [Display(Name = "規格")]
    [StringLength(400, ErrorMessage = "規格不可超過 400 個字。")]
    public string? Specification { get; set; }

    [Display(Name = "英文規格")]
    [StringLength(400, ErrorMessage = "英文規格不可超過 400 個字。")]
    public string? EnglishSpecification { get; set; }

    [Display(Name = "計量單位")]
    public string UnitOfMeasure { get; set; } = string.Empty;

    [Display(Name = "英文計量單位")]
    [StringLength(20, ErrorMessage = "英文計量單位不可超過 20 個字。")]
    public string? EnglishUnitOfMeasure { get; set; }

    [Display(Name = "安全存量")]
    [Required(ErrorMessage = "安全存量為必填。")]
    [Range(0, 1_000_000, ErrorMessage = "安全存量必須是 0 到 1,000,000 的整數。")]
    public int SafetyStockQty { get; set; }
}
