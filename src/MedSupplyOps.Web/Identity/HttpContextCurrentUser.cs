using MedSupplyOps.Infrastructure.Identity;

namespace MedSupplyOps.Web.Identity;

/// <summary>
/// <see cref="ICurrentUser"/> 在 HTTP 請求中的實作：回傳登入者的帳號名稱。
///
/// ★ 設計裁定 D4：沒有登入使用者時，<see cref="Actor"/> 必須丟例外，不可以回傳
/// 任何預設值（例如空字串或 "anonymous"）。一筆「看起來完整、實際上無法追責」的
/// 稽核紀錄，比整個要求失敗更糟。授權層會先拒絕匿名 HTTP 要求；這裡仍保留
/// fail-closed 防線，避免任何非標準呼叫路徑在沒有 actor 時寫入。
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
                    "所以這個寫入操作在送進資料庫之前就整個失敗，不會留下任何一列資料。");
            }

            return name;
        }
    }
}
