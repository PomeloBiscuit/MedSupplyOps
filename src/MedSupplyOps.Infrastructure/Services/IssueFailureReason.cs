namespace MedSupplyOps.Infrastructure.Services;

/// <summary>發料失敗的原因。每一種都必須是呼叫端能據以行動的明確結果，不可以只回傳 false。</summary>
public enum IssueFailureReason
{
    /// <summary>沒有失敗。</summary>
    None = 0,

    /// <summary>請求數量非正整數。</summary>
    InvalidQuantity = 1,

    /// <summary>
    /// 未過期批次的可用總量不足。
    /// 注意：在並發情況下，這個結果可能是「等到鎖之後重讀，發現別人先領走了」——
    /// 對呼叫端而言語意相同（就是不夠），不需要區分。
    /// </summary>
    InsufficientStock = 2,

    /// <summary>
    /// 等待列鎖逾時（Oracle ORA-30006）。
    /// 這與 InsufficientStock 是不同的事：庫存可能夠，只是現在拿不到鎖。
    /// 呼叫端應該重試，而不是告訴使用者「庫存不足」——
    /// 把兩者混為一談，會讓使用者看到一個假的缺貨訊息。
    /// </summary>
    LockTimeout = 3,
}
