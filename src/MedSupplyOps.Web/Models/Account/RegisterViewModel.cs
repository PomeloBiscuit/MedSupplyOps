using System.ComponentModel.DataAnnotations;

namespace MedSupplyOps.Web.Models.Account;

public sealed class RegisterViewModel
{
    [Required(ErrorMessage = "請輸入姓名。")]
    [StringLength(100, ErrorMessage = "姓名不可超過 100 個字。")]
    public string DisplayName { get; set; } = string.Empty;

    [Required(ErrorMessage = "請輸入帳號。")]
    [EmailAddress(ErrorMessage = "帳號格式不正確。")]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "請選擇角色。")]
    public string Role { get; set; } = string.Empty;

    [Required(ErrorMessage = "請輸入密碼。")]
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    [Required(ErrorMessage = "請再輸入一次密碼。")]
    [DataType(DataType.Password)]
    [Compare(nameof(Password), ErrorMessage = "兩次輸入的密碼不一致。")]
    public string ConfirmPassword { get; set; } = string.Empty;

    public IReadOnlyList<string> AvailableRoles { get; set; } = [];
}
