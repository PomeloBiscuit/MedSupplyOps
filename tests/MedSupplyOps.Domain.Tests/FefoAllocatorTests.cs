using System.Globalization;
using MedSupplyOps.Domain.Inventory;

namespace MedSupplyOps.Domain.Tests;

/// <summary>
/// FEFO 配批（FR-401）的測試。
///
/// 這組測試存在的理由：配批做錯時，數量正確、庫存扣得剛好、頁面毫無異狀，
/// 唯一的差別是「發出去的是哪一批」。肉眼與煙霧測試都抓不到，只有斷言抓得到。
/// </summary>
public sealed class FefoAllocatorTests
{
    private static readonly DateOnly Today = new(2026, 8, 24);

    private static StockLot Lot(long id, string lotNumber, string expiry, int quantity)
        => new(id, itemId: 1, lotNumber, DateOnly.Parse(expiry, CultureInfo.InvariantCulture), quantity, storageLocation: "A-01");

    [Fact]
    public void SingleLotWithEnoughStock_TakesAllFromThatLot()
    {
        var result = FefoAllocator.Allocate([Lot(1, "L001", "2026-12-31", 100)], 30, Today);

        Assert.True(result.IsSuccess);
        var only = Assert.Single(result.Allocations);
        Assert.Equal("L001", only.LotNumber);
        Assert.Equal(30, only.Quantity);
    }

    [Fact]
    public void MultipleLots_TakesEarliestExpiryFirst()
    {
        // 刻意把最早效期放在集合的最後一個，確保通過的原因是排序，而不是輸入順序。
        StockLot[] lots =
        [
            Lot(1, "LATE", "2027-01-31", 100),
            Lot(2, "MID", "2026-11-30", 100),
            Lot(3, "EARLY", "2026-09-30", 100),
        ];

        var result = FefoAllocator.Allocate(lots, 50, Today);

        Assert.True(result.IsSuccess);
        var only = Assert.Single(result.Allocations);
        Assert.Equal("EARLY", only.LotNumber);
    }

    [Fact]
    public void WhenFirstLotIsNotEnough_SpillsOverToNextLotsInExpiryOrder()
    {
        StockLot[] lots =
        [
            Lot(1, "EARLY", "2026-09-30", 10),
            Lot(2, "MID", "2026-11-30", 20),
            Lot(3, "LATE", "2027-01-31", 100),
        ];

        var result = FefoAllocator.Allocate(lots, 25, Today);

        Assert.True(result.IsSuccess);

        // 只該動用兩批：EARLY 全拿 10，MID 補 15，剩下的 LATE 不該被碰。
        // 「不該被碰的批次沒有出現在結果裡」這件事必須斷言 —— 多配一批的話總量仍然是 25，
        // 畫面與庫存都正常，只有這條斷言看得出來。
        Assert.Equal(2, result.Allocations.Count);
        Assert.Equal(["EARLY", "MID"], result.Allocations.Select(a => a.LotNumber));
        Assert.Equal([10, 15], result.Allocations.Select(a => a.Quantity));
        Assert.DoesNotContain(result.Allocations, a => a.LotNumber == "LATE");
    }

    [Fact]
    public void AllocatedQuantities_AlwaysSumToRequestedQuantity()
    {
        StockLot[] lots =
        [
            Lot(1, "A", "2026-09-01", 7),
            Lot(2, "B", "2026-09-02", 3),
            Lot(3, "C", "2026-09-03", 40),
        ];

        var result = FefoAllocator.Allocate(lots, 25, Today);

        Assert.True(result.IsSuccess);
        Assert.Equal(25, result.Allocations.Sum(a => a.Quantity));
    }

    [Fact]
    public void WhenTotalAvailableIsLess_FailsEntirelyAndAllocatesNothing()
    {
        // FR-401：不做部分發料。不足即整筆失敗。
        StockLot[] lots = [Lot(1, "A", "2026-09-30", 10), Lot(2, "B", "2026-10-31", 5)];

        var result = FefoAllocator.Allocate(lots, 20, Today);

        Assert.False(result.IsSuccess);
        Assert.Equal(AllocationFailureReason.InsufficientStock, result.FailureReason);
        Assert.Empty(result.Allocations);
        Assert.Equal(15, result.AvailableQuantity);
        Assert.Equal(20, result.RequestedQuantity);
    }

    [Fact]
    public void ExpiredLot_IsNeverAllocated_EvenWhenItIsTheOnlyLotWithStock()
    {
        // 這是 FR-401 最重要的一條：寧可發不出來，也不能發過期品。
        StockLot[] lots = [Lot(1, "EXPIRED", "2026-08-01", 999)];

        var result = FefoAllocator.Allocate(lots, 1, Today);

        Assert.False(result.IsSuccess);
        Assert.Equal(AllocationFailureReason.InsufficientStock, result.FailureReason);
        Assert.Equal(0, result.AvailableQuantity);
    }

    [Fact]
    public void ExpiredLot_IsSkippedButLaterLotsAreStillUsable()
    {
        StockLot[] lots =
        [
            Lot(1, "EXPIRED", "2026-08-01", 100),
            Lot(2, "GOOD", "2026-12-31", 50),
        ];

        var result = FefoAllocator.Allocate(lots, 30, Today);

        Assert.True(result.IsSuccess);
        var only = Assert.Single(result.Allocations);
        Assert.Equal("GOOD", only.LotNumber);
        Assert.Equal(50, result.AvailableQuantity);
    }

    // ── ★ 差一天的邊界。這兩條是這個檔案裡最容易寫錯、也最不可能被人工發現的地方 ──

    [Fact]
    public void LotExpiringExactlyOnAsOfDate_IsStillUsable()
    {
        // 「有效期限 2026-08-24」= 8/24 當天仍可用。
        // 若把過期判定寫成 <=，這一條會紅。
        StockLot[] lots = [Lot(1, "TODAY", "2026-08-24", 10)];

        var result = FefoAllocator.Allocate(lots, 10, Today);

        Assert.True(result.IsSuccess);
        Assert.Equal(10, Assert.Single(result.Allocations).Quantity);
    }

    [Fact]
    public void LotExpiringOneDayBeforeAsOfDate_IsExpired()
    {
        // 若把過期判定寫成 <（少了等於），或整個判定寫反，這一條會紅。
        StockLot[] lots = [Lot(1, "YESTERDAY", "2026-08-23", 10)];

        var result = FefoAllocator.Allocate(lots, 1, Today);

        Assert.False(result.IsSuccess);
        Assert.Equal(0, result.AvailableQuantity);
    }

    [Fact]
    public void LotsWithSameExpiry_AreOrderedByLotNumberSoResultIsDeterministic()
    {
        // ★ 這組測試資料的 Id 順序與批號順序是「刻意相反」的，請勿整理成一致。
        //
        //   Id 順序：C-9001(1) → A-9003(2) → B-9002(3)
        //   批號順序：A-9003(2) → B-9002(3) → C-9001(1)
        //
        //   第一版的測試資料讓兩者剛好一致（批號 A/B/C 對應 Id 1/2/3），結果是：
        //   把 .ThenBy(LotNumber) 整條拿掉，48 條測試依然全綠 —— 這條測試從頭到尾
        //   都沒有在測它宣稱要測的東西。是鑑別力探針（改壞→看紅）抓到的，不是讀出來的。
        StockLot[] lots =
        [
            Lot(1, "C-9001", "2026-09-30", 5),
            Lot(2, "A-9003", "2026-09-30", 5),
            Lot(3, "B-9002", "2026-09-30", 5),
        ];

        var result = FefoAllocator.Allocate(lots, 12, Today);

        Assert.True(result.IsSuccess);
        Assert.Equal(["A-9003", "B-9002", "C-9001"], result.Allocations.Select(a => a.LotNumber));
        Assert.Equal([5, 5, 2], result.Allocations.Select(a => a.Quantity));
    }

    [Fact]
    public void ZeroQuantityLots_AreSkipped()
    {
        StockLot[] lots =
        [
            Lot(1, "EMPTY", "2026-09-01", 0),
            Lot(2, "STOCKED", "2026-10-01", 10),
        ];

        var result = FefoAllocator.Allocate(lots, 5, Today);

        Assert.True(result.IsSuccess);
        Assert.Equal("STOCKED", Assert.Single(result.Allocations).LotNumber);
    }

    [Fact]
    public void WhenRequestExactlyDrainsAllLots_Succeeds()
    {
        StockLot[] lots = [Lot(1, "A", "2026-09-01", 10), Lot(2, "B", "2026-10-01", 5)];

        var result = FefoAllocator.Allocate(lots, 15, Today);

        Assert.True(result.IsSuccess);
        Assert.Equal(15, result.Allocations.Sum(a => a.Quantity));
        Assert.Equal(2, result.Allocations.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void NonPositiveRequestedQuantity_IsRejected(int requested)
    {
        var result = FefoAllocator.Allocate([Lot(1, "A", "2026-12-31", 100)], requested, Today);

        Assert.False(result.IsSuccess);
        Assert.Equal(AllocationFailureReason.InvalidQuantity, result.FailureReason);
        Assert.Empty(result.Allocations);
    }

    [Fact]
    public void EmptyLotCollection_FailsWithZeroAvailable()
    {
        var result = FefoAllocator.Allocate([], 1, Today);

        Assert.False(result.IsSuccess);
        Assert.Equal(AllocationFailureReason.InsufficientStock, result.FailureReason);
        Assert.Equal(0, result.AvailableQuantity);
    }
}
