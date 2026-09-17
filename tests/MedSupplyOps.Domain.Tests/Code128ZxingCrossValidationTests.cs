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

    /// <summary>
    /// ★ 全表涵蓋：107 個符號**每一個**都要被 ZXing 實際解碼驗證過。
    ///
    /// 只跑一般語料不夠：覆核時把對照表的 70（f）與 71（g）互換，黃金值、結構不變量、
    /// 檢查碼三種不依賴 ZXing 的測試全部照樣綠——因為黃金值只涵蓋少數符號，
    /// 而兩個「合法」圖案互換騙得過結構檢查。移除 ZXing 之後，那種錯就沒有任何測試抓得到。
    ///
    /// 所以這裡刻意讓編碼器用到全部符號並逐一交給 ZXing 解回來：
    /// 連續的 00～99 涵蓋字元集 C 的 0～99；其餘（三種起始、字元集切換、只會以檢查碼身分出現的值）
    /// 以既有語料補足，仍缺的值就找一個「檢查碼剛好等於它」的短字串。
    /// ZXing 解碼時會自己驗檢查碼，所以檢查碼那一格的圖案錯了也會解不出來。
    /// 這條全綠之後，<see cref="Code128BarcodeEncoderTests"/> 才能把整張表凍結成快照。
    /// </summary>
    [Fact]
    public void Every_symbol_in_the_table_is_verified_by_decoding_through_ZXing()
    {
        var inputs = new List<string>
        {
            string.Concat(Enumerable.Range(0, 100).Select(pair => pair.ToString("00", System.Globalization.CultureInfo.InvariantCulture))),
        };
        inputs.AddRange(Corpus.Cast<string>());

        var covered = new HashSet<int>();
        foreach (var input in inputs)
        {
            Assert.True(Code128BarcodeEncoder.TryBuildCodewords(input, out var codewords));
            covered.UnionWith(codewords);
        }

        // 還沒涵蓋到的值：找一個檢查碼剛好等於它的兩字元字串（字元集 B，可印字元）。
        var printable = Enumerable.Range(33, 94).Select(code => (char)code).ToArray();
        foreach (var missing in Enumerable.Range(0, 107).Where(value => !covered.Contains(value)).ToList())
        {
            var found = (from first in printable
                         from second in printable
                         let candidate = $"{first}{second}"
                         where Code128BarcodeEncoder.TryBuildCodewords(candidate, out var words) && words[^2] == missing
                         select candidate).FirstOrDefault();
            Assert.True(found is not null, $"找不到檢查碼等於 {missing} 的輸入，無法驗證這個符號。");
            inputs.Add(found!);
            Assert.True(Code128BarcodeEncoder.TryBuildCodewords(found, out var foundWords));
            covered.UnionWith(foundWords);
        }

        Assert.Equal(Enumerable.Range(0, 107), covered.Order());

        foreach (var input in inputs)
        {
            Assert.True(Code128BarcodeEncoder.TryEncode(input, out var modules));
            Assert.Equal(input, DecodeWithZxing(modules));
        }

        output.WriteLine($"107 個符號全部經 ZXing 解碼驗證；使用 {inputs.Count} 個輸入。");
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
