using Microsoft.AspNetCore.Identity;

namespace MedSupplyOps.Infrastructure.Identity;

/// <summary>
/// 應用程式的 Identity 使用者。只加了 <see cref="DisplayName"/> 一個欄位 ——
/// 畫面需要顯示「誰在操作」的人類可讀姓名，登入帳號本身是 email，不適合直接顯示。
/// </summary>
public sealed class ApplicationUser : IdentityUser
{
    public string DisplayName { get; set; } = string.Empty;
}
