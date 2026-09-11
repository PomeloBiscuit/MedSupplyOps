using System.ComponentModel.DataAnnotations;

namespace MedSupplyOps.Web.Models.Items;

public sealed record ItemListRowViewModel(
    long Id,
    string Code,
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

    [Display(Name = "名稱")]
    [Required(ErrorMessage = "名稱為必填。")]
    [StringLength(200, ErrorMessage = "名稱不可超過 200 個字。")]
    public string Name { get; set; } = string.Empty;

    [Display(Name = "規格")]
    [StringLength(400, ErrorMessage = "規格不可超過 400 個字。")]
    public string? Specification { get; set; }

    [Display(Name = "計量單位")]
    [Required(ErrorMessage = "計量單位為必填。")]
    [StringLength(20, ErrorMessage = "計量單位不可超過 20 個字。")]
    public string UnitOfMeasure { get; set; } = string.Empty;

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

    [Display(Name = "名稱")]
    [Required(ErrorMessage = "名稱為必填。")]
    [StringLength(200, ErrorMessage = "名稱不可超過 200 個字。")]
    public string Name { get; set; } = string.Empty;

    [Display(Name = "規格")]
    [StringLength(400, ErrorMessage = "規格不可超過 400 個字。")]
    public string? Specification { get; set; }

    [Display(Name = "計量單位")]
    public string UnitOfMeasure { get; set; } = string.Empty;

    [Display(Name = "安全存量")]
    [Required(ErrorMessage = "安全存量為必填。")]
    [Range(0, 1_000_000, ErrorMessage = "安全存量必須是 0 到 1,000,000 的整數。")]
    public int SafetyStockQty { get; set; }
}
