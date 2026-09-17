using MedSupplyOps.Domain.Barcodes;
using Xunit.Abstractions;

namespace MedSupplyOps.Domain.Tests;

public sealed class Gs1BarcodeParserTests(ITestOutputHelper output)
{
    /// <summary>純函式測試的固定基準日。解析器不讀時鐘，基準日一律由呼叫端傳入。</summary>
    private static readonly DateOnly Reference = new(2026, 9, 17);

    [Fact]
    public void T1_valid_01_17_10_combination_returns_all_fields()
    {
        var success = Gs1BarcodeParser.TryParse("(01)04712345678901(17)491231(10)LOT-A1", Reference, out var result);

        Assert.True(success);
        Assert.NotNull(result);
        Assert.Equal("04712345678901", result.Gtin);
        Assert.Equal(new DateOnly(2049, 12, 31), result.ExpiryDate);
        Assert.Equal("LOT-A1", result.LotNumber);
        output.WriteLine($"T1(a) success={success}; GTIN={result.Gtin}; expiry={result.ExpiryDate:yyyy-MM-dd}; lot={result.LotNumber}");
    }

    [Fact]
    public void T1_unknown_application_identifier_rejects_the_whole_barcode()
    {
        var success = Gs1BarcodeParser.TryParse(
            "(01)04712345678901(17)491231(10)LOT-A1(21)SERIAL-1", Reference,
            out var result);

        Assert.False(success);
        Assert.Null(result);
        output.WriteLine($"T1(b) success={success}; result={(result is null ? "無法解析" : "unexpected partial result")}");
    }

    [Fact]
    public void T1_impossible_expiry_date_rejects_the_whole_barcode()
    {
        var success = Gs1BarcodeParser.TryParse("(01)04712345678901(17)991332(10)LOT-A1", Reference, out var result);

        Assert.False(success);
        Assert.Null(result);
        output.WriteLine($"T1(c) success={success}; result={(result is null ? "無法解析" : "unexpected partial result")}");
    }

    [Fact]
    public void T1_short_gtin_rejects_the_whole_barcode()
    {
        var success = Gs1BarcodeParser.TryParse("(01)0471234567890(17)491231(10)LOT-A1", Reference, out var result);

        Assert.False(success);
        Assert.Null(result);
        output.WriteLine($"T1(d) success={success}; result={(result is null ? "無法解析" : "unexpected partial result")}");
    }

    [Fact]
    public void T1_empty_input_is_rejected()
    {
        var success = Gs1BarcodeParser.TryParse(string.Empty, Reference, out var result);

        Assert.False(success);
        Assert.Null(result);
        output.WriteLine($"T1(e) success={success}; result={(result is null ? "無法解析" : "unexpected partial result")}");
    }

    /// <summary>
    /// ★ GS1 General Specifications §7.12：年份落在 [基準年 − 49, 基準年 + 50]。
    ///   前一版是固定樞紐（50–99 → 1950–1999），只在西元 2000 年剛好等於規範。
    ///   第一列就是那個規則會答錯的案例：2026 年讀到 (17)500101，應該是 2050，不是 1950。
    /// </summary>
    [Theory]
    [InlineData(2026, "500101", 2050)] // 舊規則答 1950：一批 2050 年到期的貨會被判成已過期
    [InlineData(2026, "761231", 2076)] // 上界：基準年 + 50
    [InlineData(2026, "770101", 1977)] // 超過上界一年 → 退一個世紀（基準年 − 49 = 下界）
    [InlineData(2026, "491231", 2049)]
    [InlineData(2026, "000101", 2000)]
    [InlineData(2080, "300101", 2130)] // 窗口會隨基準年滑動：2080 年的下界是 2031
    [InlineData(2080, "310101", 2031)]
    public void T2_century_follows_the_GS1_sliding_window_around_the_reference_year(int referenceYear, string encodedDate, int expectedYear)
    {
        var success = Gs1BarcodeParser.TryParse($"(01)04712345678901(17){encodedDate}", new DateOnly(referenceYear, 6, 30), out var result);

        Assert.True(success);
        Assert.NotNull(result);
        Assert.Equal(expectedYear, result.ExpiryDate?.Year);
        output.WriteLine($"T2 (17){encodedDate} -> {result.ExpiryDate:yyyy-MM-dd}");
    }

    [Theory]
    [InlineData("(01)04712345678901(17)250229")]
    [InlineData("(01)04712345678901(17)240000")]
    [InlineData("(01)04712345678901(10)")]
    [InlineData("(01)04712345678901(10)ABCDEFGHIJKLMNOPQRSTU")]
    [InlineData("(01)04712345678901(01)14712345678908")]
    [InlineData("(17)491231(10)LOT-A1")]
    public void Invalid_lengths_dates_duplicates_and_missing_gtin_are_rejected(string barcode)
    {
        Assert.False(Gs1BarcodeParser.TryParse(barcode, Reference, out var result));
        Assert.Null(result);
    }

    [Fact]
    public void Raw_scanner_form_supports_fixed_fields_and_FNC1_terminated_lot()
    {
        var raw = $"01047123456789011749123110LOT-A1{(char)29}";

        var success = Gs1BarcodeParser.TryParse(raw, Reference, out var result);

        Assert.True(success);
        Assert.NotNull(result);
        Assert.Equal("04712345678901", result.Gtin);
        Assert.Equal(new DateOnly(2049, 12, 31), result.ExpiryDate);
        Assert.Equal("LOT-A1", result.LotNumber);
    }

    [Theory]
    [InlineData("(01)04712345678901(17)491231", true)]
    [InlineData("010471234567890117491231", true)]
    [InlineData("04712345678901", false)]
    [InlineData("LOCAL-ITEM-BARCODE", false)]
    public void Candidate_detection_keeps_plain_item_barcodes_out_of_GS1_rejection(string barcode, bool expected)
        => Assert.Equal(expected, Gs1BarcodeParser.IsGs1Candidate(barcode));
}
