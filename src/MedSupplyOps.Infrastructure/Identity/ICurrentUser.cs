namespace MedSupplyOps.Infrastructure.Identity;

/// <summary>
/// 「目前是誰在操作」的抽象，供 <see cref="Persistence.MedSupplyOpsDbContext"/> 的稽核欄位簿記使用。
///
/// ★ 設計裁定 D4：取不到登入使用者時不可以有靜默預設值。
///   實作 <see cref="Actor"/> 時，取不到就必須丟例外，不可以回傳像 "system" 這樣的佔位字串 ——
///   一筆「看起來完整、實際上無法追責」的稽核紀錄，比寫入失敗更糟。
///
/// 兩種情境對應兩種實作：
///   1. Web 請求中：<c>HttpContextCurrentUser</c>，回傳登入者的帳號名稱；沒有登入就丟例外。
///   2. 不在請求內（種子資料、背景工作、測試）：由呼叫端明確建立一個固定值的實作
///      （例如測試用的 <c>OracleTestDatabase</c>），不是由 DbContext 悄悄補一個預設值。
/// </summary>
public interface ICurrentUser
{
    /// <summary>目前操作者的識別字串（登入帳號）。取不到時必須丟例外。</summary>
    string Actor { get; }
}
