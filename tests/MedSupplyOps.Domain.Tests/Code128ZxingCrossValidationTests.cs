using MedSupplyOps.Domain.Barcodes;
using Xunit.Abstractions;
using ZXing;
using ZXing.Common;
using ZXing.OneD;

namespace MedSupplyOps.Domain.Tests;

public sealed class Code128ZxingCrossValidationTests(ITestOutputHelper output)
{
    public static TheoryData<string> Corpus =>
    [
        "0",
        "A",
        "z",
        "-",
        "12",
        "123",
        "1234",
        "12345",
        "123456",
        "00000000",
        "987654321",
        "04712345678901",
        "ABCDEFGHIJKLMNOPQRSTUVWXYZ",
        "abcdefghijklmnopqrstuvwxyz",
        "AbCdEfGhIjKlMnOpQrStUvWxYz",
        "ITEM-0001",
        "LOT-2026-09-17",
        "ABC!@#$%^&*()_+-=[]{}",
        "Mixed-1234-CASE",
        "12AB34CD56EF",
        "AB1234CD",
        "1234AB5678",
        "A12345B",
        "A123456B",
        "12345678901234567890123456789012345678901",
        "The-quick-brown-fox-jumps-over-13-lazy-dogs.",
        " !\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~",
        "\u0000",
        "\u0001AB",
        "AB\u0001CD",
        "\u001fCONTROL",
        "CONTROL\u007f",
        "\u0001AB\u007f\u007f",
        "AB12\u00013456\u007fCD",
    ];

    [Theory]
    [MemberData(nameof(Corpus))]
    public void Our_modules_round_trip_through_ZXing_Code128Reader(string value)
    {
        Assert.True(Code128BarcodeEncoder.TryEncode(value, out var modules));

        var decoded = DecodeWithZxing(modules);

        Assert.Equal(value, decoded);
        output.WriteLine($"PASS {Display(value)} | modules={modules.Length}");
    }

    private static string DecodeWithZxing(bool[] modules)
    {
        const int scale = 3;
        const int height = 60;
        var width = modules.Length * scale;
        var luminances = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var module = 0; module < modules.Length; module++)
            {
                var luminance = modules[module] ? (byte)0 : (byte)255;
                for (var x = 0; x < scale; x++)
                {
                    luminances[(y * width) + (module * scale) + x] = luminance;
                }
            }
        }

        var source = new PlanarYUVLuminanceSource(
            luminances,
            width,
            height,
            0,
            0,
            width,
            height,
            false);
        var bitmap = new BinaryBitmap(new GlobalHistogramBinarizer(source));
        var result = new Code128Reader().decode(bitmap);
        Assert.NotNull(result);
        return result.Text;
    }

    private static string Display(string value)
        => string.Concat(value.Select(character => char.IsControl(character) ? $"\\x{(int)character:X2}" : character.ToString()));
}
