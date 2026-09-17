namespace MedSupplyOps.Domain.Barcodes;

/// <summary>
/// Code 128 encoder implemented from ISO/IEC 15417 and public descriptions of the
/// Code 128 symbol table, character sets, checksum, and quiet-zone requirements.
/// Clean-room note: no ZXing or other barcode-library source code was consulted.
/// </summary>
public static class Code128BarcodeEncoder
{
    private const int CodeShift = 98;
    private const int CodeC = 99;
    private const int CodeB = 100;
    private const int CodeA = 101;
    private const int StartA = 103;
    private const int StartB = 104;
    private const int StartC = 105;
    private const int Stop = 106;
    private const int QuietZoneModules = 10;

    // Each digit is the width (1-4 modules) of alternating black/white elements.
    // Symbols 0-105 contain six elements and occupy 11 modules; stop contains
    // seven elements and occupies 13 modules.
    internal static IReadOnlyList<string> SymbolPatterns { get; } =
    [
        "212222", "222122", "222221", "121223", "121322", "131222", "122213", "122312", "132212", "221213",
        "221312", "231212", "112232", "122132", "122231", "113222", "123122", "123221", "223211", "221132",
        "221231", "213212", "223112", "312131", "311222", "321122", "321221", "312212", "322112", "322211",
        "212123", "212321", "232121", "111323", "131123", "131321", "112313", "132113", "132311", "211313",
        "231113", "231311", "112133", "112331", "132131", "113123", "113321", "133121", "313121", "211331",
        "231131", "213113", "213311", "213131", "311123", "311321", "331121", "312113", "312311", "332111",
        "314111", "221411", "431111", "111224", "111422", "121124", "121421", "141122", "141221", "112214",
        "112412", "122114", "122411", "142112", "142211", "241211", "221114", "413111", "241112", "134111",
        "111242", "121142", "121241", "114212", "124112", "124211", "411212", "421112", "421211", "212141",
        "214121", "412121", "111143", "111341", "131141", "114113", "114311", "411113", "411311", "113141",
        "114131", "311141", "411131", "211412", "211214", "211232", "2331112",
    ];

    /// <summary>
    /// Encodes a non-empty ASCII value as black/white modules. The returned array
    /// includes ten white modules before and after the Code 128 symbol.
    /// </summary>
    public static bool TryEncode(string? value, out bool[] modules)
    {
        modules = [];
        if (!TryBuildCodewords(value, out var codewords))
        {
            return false;
        }

        var moduleList = new List<bool>(
            (QuietZoneModules * 2) + ((codewords.Count - 1) * 11) + 13);
        moduleList.AddRange(Enumerable.Repeat(false, QuietZoneModules));
        foreach (var codeword in codewords)
        {
            AppendPattern(moduleList, SymbolPatterns[codeword]);
        }

        moduleList.AddRange(Enumerable.Repeat(false, QuietZoneModules));
        modules = [.. moduleList];
        return true;
    }

    internal static bool TryBuildCodewords(string? value, out IReadOnlyList<int> codewords)
    {
        codewords = [];
        if (string.IsNullOrEmpty(value) || value.Any(character => character > 127))
        {
            return false;
        }

        var activeSet = SelectInitialSet(value);
        var result = new List<int> { StartCode(activeSet) };
        var index = 0;
        while (index < value.Length)
        {
            if (activeSet == CodeSet.C)
            {
                if (HasDigitPair(value, index))
                {
                    result.Add(((value[index] - '0') * 10) + value[index + 1] - '0');
                    index += 2;
                    continue;
                }

                activeSet = PreferredAlphabeticSet(value[index]);
                result.Add(activeSet == CodeSet.A ? CodeA : CodeB);
                continue;
            }

            var digitRunLength = CountDigitRun(value, index);
            if (digitRunLength >= 4)
            {
                // Enter C on an even boundary. For an odd run, encode the first
                // digit in the current set and let C consume the remaining pairs.
                if ((digitRunLength & 1) != 0)
                {
                    result.Add(EncodeInSet(value[index], activeSet));
                    index++;
                }

                result.Add(CodeC);
                activeSet = CodeSet.C;
                continue;
            }

            if (CanEncode(value[index], activeSet))
            {
                result.Add(EncodeInSet(value[index], activeSet));
                index++;
                continue;
            }

            var otherSet = activeSet == CodeSet.A ? CodeSet.B : CodeSet.A;
            var canShiftBack = index + 1 < value.Length && CanEncode(value[index + 1], activeSet);
            if (canShiftBack)
            {
                result.Add(CodeShift);
                result.Add(EncodeInSet(value[index], otherSet));
                index++;
                continue;
            }

            result.Add(otherSet == CodeSet.A ? CodeA : CodeB);
            activeSet = otherSet;
        }

        result.Add(CalculateChecksum(result));
        result.Add(Stop);
        codewords = result;
        return true;
    }

    internal static int CalculateChecksum(IReadOnlyList<int> codewordsWithoutChecksum)
    {
        var checksum = codewordsWithoutChecksum[0];
        for (var index = 1; index < codewordsWithoutChecksum.Count; index++)
        {
            checksum += codewordsWithoutChecksum[index] * index;
        }

        return checksum % 103;
    }

    private static CodeSet SelectInitialSet(string value)
    {
        if (CountDigitRun(value, 0) >= 4)
        {
            return CodeSet.C;
        }

        return PreferredAlphabeticSet(value[0]);
    }

    private static CodeSet PreferredAlphabeticSet(char character)
        => character < 32 ? CodeSet.A : CodeSet.B;

    private static int StartCode(CodeSet set)
        => set switch
        {
            CodeSet.A => StartA,
            CodeSet.B => StartB,
            CodeSet.C => StartC,
            _ => throw new ArgumentOutOfRangeException(nameof(set)),
        };

    private static bool CanEncode(char character, CodeSet set)
        => set switch
        {
            CodeSet.A => character <= 95,
            CodeSet.B => character is >= (char)32 and <= (char)127,
            _ => false,
        };

    private static int EncodeInSet(char character, CodeSet set)
        => set switch
        {
            CodeSet.A when character < 32 => character + 64,
            CodeSet.A => character - 32,
            CodeSet.B => character - 32,
            _ => throw new ArgumentOutOfRangeException(nameof(set)),
        };

    private static int CountDigitRun(string value, int start)
    {
        var index = start;
        while (index < value.Length && value[index] is >= '0' and <= '9')
        {
            index++;
        }

        return index - start;
    }

    private static bool HasDigitPair(string value, int index)
        => index + 1 < value.Length &&
           value[index] is >= '0' and <= '9' &&
           value[index + 1] is >= '0' and <= '9';

    private static void AppendPattern(List<bool> modules, string widths)
    {
        var isBlack = true;
        foreach (var widthCharacter in widths)
        {
            var width = widthCharacter - '0';
            for (var module = 0; module < width; module++)
            {
                modules.Add(isBlack);
            }

            isBlack = !isBlack;
        }
    }

    private enum CodeSet
    {
        A,
        B,
        C,
    }
}
