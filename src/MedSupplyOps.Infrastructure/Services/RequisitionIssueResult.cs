using MedSupplyOps.Domain.Inventory;
using MedSupplyOps.Domain.Requisitions;

namespace MedSupplyOps.Infrastructure.Services;

/// <summary>整張請領單發料失敗的原因。每一種都必須讓呼叫端知道「接下來該做什麼」。</summary>
public enum RequisitionIssueFailureReason
{
    None = 0,

    /// <summary>找不到該請領單。</summary>
    NotFound = 1,

    /// <summary>
    /// 目前狀態不允許發料（例如還在待審核，或已經發過了）。
    /// 這與「庫存不足」是完全不同的問題：庫存充足也可能因為流程沒走到而不能發。
    /// </summary>
    IllegalStatusTransition = 2,

    /// <summary>某一筆明細的可用量不足。<see cref="RequisitionIssueResult.FailedItemId"/> 指出是哪一個品項。</summary>
    InsufficientStock = 3,

    /// <summary>等待列鎖逾時。庫存可能足夠，只是現在拿不到鎖 —— 呼叫端應該重試。</summary>
    LockTimeout = 4,

    /// <summary>沒有任何明細的單不可發料。</summary>
    NoLines = 5,
}

/// <summary>一筆明細的發料結果。</summary>
public sealed record IssuedRequisitionLine(
    long RequisitionLineId,
    long ItemId,
    int RequestedQuantity,
    IReadOnlyList<LotAllocation> Allocations);

/// <summary>
/// 整張請領單發料的結果。
///
/// ★ 這個型別的設計重點：**失敗時必須指出「是哪一筆明細」**。
/// 一張單有五個品項，只回一個「庫存不足」對使用者毫無幫助 ——
/// 他得自己一個一個去查是哪個品項不夠。
/// </summary>
public sealed class RequisitionIssueResult
{
    private static readonly IReadOnlyList<IssuedRequisitionLine> Empty = [];

    private RequisitionIssueResult(
        bool isSuccess,
        RequisitionIssueFailureReason failureReason,
        IReadOnlyList<IssuedRequisitionLine> issuedLines,
        RequisitionStatus? statusAtFailure,
        long? failedItemId,
        int requestedQuantity,
        int availableQuantity)
    {
        IsSuccess = isSuccess;
        FailureReason = failureReason;
        IssuedLines = issuedLines;
        StatusAtFailure = statusAtFailure;
        FailedItemId = failedItemId;
        RequestedQuantity = requestedQuantity;
        AvailableQuantity = availableQuantity;
    }

    public bool IsSuccess { get; }
    public RequisitionIssueFailureReason FailureReason { get; }

    /// <summary>成功時每一筆明細的配批明細。失敗時為空集合 —— **不做部分發料**。</summary>
    public IReadOnlyList<IssuedRequisitionLine> IssuedLines { get; }

    /// <summary>因狀態不允許而失敗時，當下的實際狀態。讓 UI 能顯示「此單目前為『已駁回』，無法發料」。</summary>
    public RequisitionStatus? StatusAtFailure { get; }

    /// <summary>因庫存不足而失敗時，是哪一個品項不夠。</summary>
    public long? FailedItemId { get; }

    /// <summary>失敗那一筆明細的請求量。</summary>
    public int RequestedQuantity { get; }

    /// <summary>失敗那一筆明細鎖定當下的可用量。逾時為 -1（沒讀到，不是 0）。</summary>
    public int AvailableQuantity { get; }

    public static RequisitionIssueResult Success(IReadOnlyList<IssuedRequisitionLine> lines)
        => new(true, RequisitionIssueFailureReason.None, lines, null, null, 0, 0);

    public static RequisitionIssueResult NotFound()
        => new(false, RequisitionIssueFailureReason.NotFound, Empty, null, null, 0, 0);

    public static RequisitionIssueResult NoLines()
        => new(false, RequisitionIssueFailureReason.NoLines, Empty, null, null, 0, 0);

    public static RequisitionIssueResult IllegalStatus(RequisitionStatus current)
        => new(false, RequisitionIssueFailureReason.IllegalStatusTransition, Empty, current, null, 0, 0);

    public static RequisitionIssueResult InsufficientStock(long itemId, int requested, int available)
        => new(false, RequisitionIssueFailureReason.InsufficientStock, Empty, null, itemId, requested, available);

    public static RequisitionIssueResult LockTimeout()
        => new(false, RequisitionIssueFailureReason.LockTimeout, Empty, null, null, 0, -1);
}
