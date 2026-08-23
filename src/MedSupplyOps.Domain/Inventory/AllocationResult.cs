namespace MedSupplyOps.Domain.Inventory;

/// <summary>
/// FEFO 配批的結果。
///
/// 失敗時一律回報 <see cref="AvailableQuantity"/>（實際可用量），不是只回一個 false。
/// 理由：呼叫端與使用者需要知道「差多少」，而「不知道」與「剛好是 0」是兩件事。
/// </summary>
public sealed class AllocationResult
{
    private static readonly IReadOnlyList<LotAllocation> Empty = Array.Empty<LotAllocation>();

    private AllocationResult(
        bool isSuccess,
        IReadOnlyList<LotAllocation> allocations,
        AllocationFailureReason failureReason,
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

    /// <summary>成功時的配批明細，依效期由早到晚排列。失敗時為空集合（絕不做部分發料，見 FR-401）。</summary>
    public IReadOnlyList<LotAllocation> Allocations { get; }

    public AllocationFailureReason FailureReason { get; }
    public int RequestedQuantity { get; }

    /// <summary>在 asOf 當下「未過期且數量大於 0」的批次總量。</summary>
    public int AvailableQuantity { get; }

    public static AllocationResult Success(IReadOnlyList<LotAllocation> allocations, int requestedQuantity, int availableQuantity)
        => new(true, allocations, AllocationFailureReason.None, requestedQuantity, availableQuantity);

    public static AllocationResult InvalidQuantity(int requestedQuantity)
        => new(false, Empty, AllocationFailureReason.InvalidQuantity, requestedQuantity, 0);

    public static AllocationResult InsufficientStock(int requestedQuantity, int availableQuantity)
        => new(false, Empty, AllocationFailureReason.InsufficientStock, requestedQuantity, availableQuantity);
}
