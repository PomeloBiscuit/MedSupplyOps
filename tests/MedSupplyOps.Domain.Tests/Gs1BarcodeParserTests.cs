using MedSupplyOps.Domain.Barcodes;
using Xunit.Abstractions;

namespace MedSupplyOps.Domain.Tests;

public sealed class Gs1BarcodeParserTests(ITestOutputHelper output)
{
    [Fact]
    public void T1_valid_01_17_10_combination_returns_all_fields()
    {
        var success = Gs1BarcodeParser.TryParse("(01)04712345678901(17)491231(10)LOT-A1", out var result);

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
            "(01)04712345678901(17)491231(10)LOT-A1(21)SERIAL-1",
            out var result);

        Assert.False(success);
        Assert.Null(result);
        output.WriteLine($"T1(b) success={success}; result={(result is null ? "無法解析" : "unexpected partial result")}");
    }

    [Fact]
    public void T1_impossible_expiry_date_rejects_the_whole_barcode()
    {
        var success = Gs1BarcodeParser.TryParse("(01)04712345678901(17)991332(10)LOT-A1", out var result);

        Assert.False(success);
        Assert.Null(result);
        output.WriteLine($"T1(c) success={success}; result={(result is null ? "無法解析" : "unexpected partial result")}");
    }

    [Fact]
    public void T1_short_gtin_rejects_the_whole_barcode()
    {
        var success = Gs1BarcodeParser.TryParse("(01)0471234567890(17)491231(10)LOT-A1", out var result);

        Assert.False(success);
        Assert.Null(result);
        output.WriteLine($"T1(d) success={success}; result={(result is null ? "無法解析" : "unexpected partial result")}");
    }

    [Fact]
    public void T1_empty_input_is_rejected()
    {
        var success = Gs1BarcodeParser.TryParse(string.Empty, out var result);

        Assert.False(success);
        Assert.Null(result);
        output.WriteLine($"T1(e) success={success}; result={(result is null ? "無法解析" : "unexpected partial result")}");
    }

    [Theory]
    [InlineData("491231", 2049)]
    [InlineData("501231", 1950)]
    public void T2_fixed_century_pivot_is_deterministic(string encodedDate, int expectedYear)
    {
        var success = Gs1BarcodeParser.TryParse($"(01)04712345678901(17){encodedDate}", out var result);

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
        Assert.False(Gs1BarcodeParser.TryParse(barcode, out var result));
        Assert.Null(result);
    }

    [Fact]
    public void Raw_scanner_form_supports_fixed_fields_and_FNC1_terminated_lot()
    {
        var raw = $"01047123456789011749123110LOT-A1{(char)29}";

        var success = Gs1BarcodeParser.TryParse(raw, out var result);

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
