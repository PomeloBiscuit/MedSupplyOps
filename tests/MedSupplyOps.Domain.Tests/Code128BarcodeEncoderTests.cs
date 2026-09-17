using MedSupplyOps.Domain.Barcodes;

namespace MedSupplyOps.Domain.Tests;

public sealed class Code128BarcodeEncoderTests
{
    [Fact]
    public void All_107_patterns_obey_Code_128_structural_invariants()
    {
        Assert.Equal(107, Code128BarcodeEncoder.SymbolPatterns.Count);

        for (var symbol = 0; symbol < Code128BarcodeEncoder.SymbolPatterns.Count; symbol++)
        {
            var pattern = Code128BarcodeEncoder.SymbolPatterns[symbol];
            var isStop = symbol == 106;
            Assert.Equal(isStop ? 7 : 6, pattern.Length);
            Assert.All(pattern, width => Assert.InRange(width - '0', 1, 4));
            Assert.Equal(isStop ? 13 : 11, pattern.Sum(width => width - '0'));
            Assert.Equal(0, pattern.Where((_, index) => index % 2 == 0).Sum(width => width - '0') % 2);

            if (!isStop)
            {
                Assert.Equal(3, pattern.Where((_, index) => index % 2 == 0).Count());
                Assert.Equal(3, pattern.Where((_, index) => index % 2 != 0).Count());
            }
        }
    }

    [Theory]
    [InlineData("AB", new[] { 104, 33, 34, 102, 106 })]
    [InlineData("123456", new[] { 105, 12, 34, 56, 44, 106 })]
    [InlineData("A1234B", new[] { 104, 33, 99, 12, 34, 100, 34, 78, 106 })]
    public void Checksum_examples_match_the_modulo_103_formula(string value, int[] expected)
    {
        Assert.True(Code128BarcodeEncoder.TryBuildCodewords(value, out var codewords));
        Assert.Equal(expected, codewords);
        Assert.Equal(expected[^2], Code128BarcodeEncoder.CalculateChecksum(expected[..^2]));
    }

    [Fact]
    public void Encoder_selects_and_switches_between_A_B_and_C()
    {
        Assert.True(Code128BarcodeEncoder.TryBuildCodewords("123456", out var numeric));
        Assert.Equal(105, numeric[0]);

        Assert.True(Code128BarcodeEncoder.TryBuildCodewords("AB1234CD", out var mixed));
        Assert.Contains(99, mixed);
        Assert.Contains(100, mixed);

        Assert.True(Code128BarcodeEncoder.TryBuildCodewords("\u0001AB\u007f\u007f", out var controls));
        Assert.Equal(103, controls[0]);
        Assert.Contains(100, controls);

        Assert.True(Code128BarcodeEncoder.TryBuildCodewords("AB\u0001CD", out var shifted));
        Assert.Contains(98, shifted);
    }

    [Fact]
    public void Encoded_modules_include_ten_module_quiet_zones_and_a_thirteen_module_stop()
    {
        Assert.True(Code128BarcodeEncoder.TryBuildCodewords("AB-1234", out var codewords));
        Assert.True(Code128BarcodeEncoder.TryEncode("AB-1234", out var modules));

        Assert.All(modules[..10], Assert.False);
        Assert.All(modules[^10..], Assert.False);
        Assert.Equal(20 + ((codewords.Count - 1) * 11) + 13, modules.Length);
    }

    [Theory]
    [InlineData("AB", "00000000001101001000010100011000100010110001111010111011000111010110000000000")]
    [InlineData("123456", "0000000000110100111001011001110010001011000111000101101000110111011000111010110000000000")]
    [InlineData("AB-1234", "0000000000110100100001010001100010001011000100110111001011101111010110011100100010110001010011110011000111010110000000000")]
    public void Independently_verified_golden_module_sequences_remain_frozen(string value, string expected)
    {
        Assert.True(Code128BarcodeEncoder.TryEncode(value, out var modules));

        Assert.Equal(expected, string.Concat(modules.Select(module => module ? '1' : '0')));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("中文條碼")]
    [InlineData("ASCII-and-中文")]
    public void Unsupported_or_empty_values_return_false_without_throwing(string? value)
    {
        var encoded = Code128BarcodeEncoder.TryEncode(value, out var modules);

        Assert.False(encoded);
        Assert.Empty(modules);
    }
}
