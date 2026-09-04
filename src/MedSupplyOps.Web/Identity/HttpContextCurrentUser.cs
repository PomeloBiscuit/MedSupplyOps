using MedSupplyOps.Infrastructure.Identity;

namespace MedSupplyOps.Web.Identity;

/// <summary>
/// <see cref="ICurrentUser"/> 在 HTTP 請求中的實作：回傳登入者的帳號名稱。
///
/// ★ 設計裁定 D4：沒有登入使用者時，<see cref="Actor"/> 必須丟例外，不可以回傳
/// 任何預設值（例如空字串或 "anonymous"）。一筆「看起來完整、實際上無法追責」的
/// 稽核紀錄，比整個要求失敗更糟——目前刻意不做授權（見 README／D6），
/// 所以匿名使用者確實能打到會寫入的 action，這裡就是那個情境被擋下來的地方。
/// </summary>
public sealed class HttpContextCurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpContextCurrentUser(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public string Actor
    {
        get
        {
            var name = _httpContextAccessor.HttpContext?.User?.Identity?.Name;
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException(
                    "無法取得目前登入使用者：這個要求沒有已通過驗證的使用者。" +
                    "依設計裁定 D4，稽核欄位（CREATED_BY／UPDATED_BY）不可以有靜默預設值，" +
                    "所以這個寫入操作在送進資料庫之前就整個失敗，不會留下任何一列資料。" +
                    "目前刻意尚未套用授權（見 README 的說明），" +
                    "匿名使用者仍能打到這個 action——這正是預期中會被擋下的地方。" +
                    "請先登入；授權接上後會直接拒絕未登入的請求。");
            }

            return name;
        }
    }
}
