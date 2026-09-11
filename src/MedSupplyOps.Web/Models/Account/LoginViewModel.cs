using System.ComponentModel.DataAnnotations;

namespace MedSupplyOps.Web.Models.Account;

public sealed class LoginViewModel
{
    [Required(ErrorMessage = "請輸入帳號。")]
    [EmailAddress(ErrorMessage = "帳號格式不正確。")]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "請輸入密碼。")]
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    [Required(ErrorMessage = "記住我為必填。")]
    public bool RememberMe { get; set; }

    public string? ReturnUrl { get; set; }
}
