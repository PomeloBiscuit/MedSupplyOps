using MedSupplyOps.Domain.Inventory;

namespace MedSupplyOps.Infrastructure.Services;

/// <summary>
/// 發料的結果。
///
/// 失敗時一律回報 <see cref="AvailableQuantity"/>（鎖定當下實際看到的可用量），
/// 不是只回一個 false —— 呼叫端與使用者需要知道「差多少」，
/// 而「不知道」與「剛好是 0」是兩件事。
/// </summary>
public sealed class IssueResult
{
    private static readonly IReadOnlyList<LotAllocation> Empty = [];

    private IssueResult(
        bool isSuccess,
        IReadOnlyList<LotAllocation> allocations,
        IssueFailureReason failureReason,
        int requestedQuantity,
        int availableQuantity)
    {
        IsSuccess = isSuccess;
        Allocations = allocations;
        FailureReason = failureReason;
        RequestedQuantity = requestedQuantity;
        AvailableQuantity = availableQuantity;
    }

    public bool IsSuccess { get; }

    /// <summary>成功時實際扣減的批次明細，依 FEFO 順序。失敗時為空集合（不做部分發料，FR-401）。</summary>
    public IReadOnlyList<LotAllocation> Allocations { get; }

    public IssueFailureReason FailureReason { get; }
    public int RequestedQuantity { get; }

    /// <summary>取得列鎖之後重讀到的可用量。逾時失敗時為 -1，表示「沒讀到，不是 0」。</summary>
    public int AvailableQuantity { get; }

    public static IssueResult Success(IReadOnlyList<LotAllocation> allocations, int requested, int available)
        => new(true, allocations, IssueFailureReason.None, requested, available);

    public static IssueResult InvalidQuantity(int requested)
        => new(false, Empty, IssueFailureReason.InvalidQuantity, requested, 0);

    public static IssueResult InsufficientStock(int requested, int available)
        => new(false, Empty, IssueFailureReason.InsufficientStock, requested, available);

    /// <summary>
    /// 逾時時 <see cref="AvailableQuantity"/> 給 -1 而不是 0。
    /// 因為我們根本沒讀到資料 —— 回 0 會讓呼叫端以為「確實查過，就是沒貨」。
    /// 「查不到」與「值是 0」永遠不要混為一談。
    /// </summary>
    public static IssueResult LockTimeout(int requested)
        => new(false, Empty, IssueFailureReason.LockTimeout, requested, -1);
}
