using System.Globalization;
using MedSupplyOps.Domain.Inventory;

namespace MedSupplyOps.Domain.Tests;

public sealed class StockLotTests
{
    private static StockLot Lot(int quantity, string expiry = "2026-12-31")
        => new(1, 1, "L001", DateOnly.Parse(expiry, CultureInfo.InvariantCulture), quantity, "A-01");

    [Fact]
    public void Deduct_ReducesQuantity()
    {
        var lot = Lot(10);
        lot.Deduct(4);
        Assert.Equal(6, lot.Quantity);
    }

    [Fact]
    public void Deduct_MoreThanAvailable_IsRejectedAndQuantityIsUnchanged()
    {
        var lot = Lot(10);

        Assert.Throws<InvalidOperationException>(() => lot.Deduct(11));
        Assert.Equal(10, lot.Quantity);
    }

    [Fact]
    public void Deduct_ExactlyAllAvailable_IsAllowed()
    {
        var lot = Lot(10);
        lot.Deduct(10);
        Assert.Equal(0, lot.Quantity);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Deduct_NonPositive_IsRejected(int quantity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Lot(10).Deduct(quantity));
    }

    [Fact]
    public void NegativeInitialQuantity_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Lot(-1));
    }

    // ★ 差一天的邊界。這三格是整個庫存模型最容易寫錯、也最不可能被人工發現的地方：
    //   把 < 寫成 <= 只會讓每一批醫材少用一天，畫面完全正常，不會有任何人回報。
    [Theory]
    [InlineData("2026-08-25", false)] // 效期在基準日之後 -> 未過期
    [InlineData("2026-08-24", false)] // 效期正好是基準日當天 -> 仍可用
    [InlineData("2026-08-23", true)]  // 效期在基準日前一天 -> 已過期
    public void IsExpiredOn_TreatsTheExpiryDateItselfAsStillUsable(string expiry, bool expectedExpired)
    {
        var lot = Lot(10, expiry);
        Assert.Equal(expectedExpired, lot.IsExpiredOn(new DateOnly(2026, 8, 24)));
    }
}
