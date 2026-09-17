using System.Globalization;

namespace MedSupplyOps.Domain.Barcodes;

/// <summary>
/// 掃描入庫流程採用的 GS1 子集結果。只有 AI (01)、(17)、(10) 會被接受；
/// 任一段無法辨識時，解析器整筆失敗，不回傳部分結果。
/// </summary>
public sealed record Gs1BarcodeData(string Gtin, DateOnly? ExpiryDate, string? LotNumber);

/// <summary>
/// 解析醫材入庫所需的最小 GS1 子集，零套件依賴。
/// 支援括號式 HRI（例如 (01)...(17)...(10)...）及掃描器常見的原始 AI 字串；
/// 原始字串的變動長度 AI (10) 若不是最後一段，必須以 ASCII 29（FNC1）終止。
/// </summary>
public static class Gs1BarcodeParser
{
    private const char GroupSeparator = (char)29;

    /// <summary>辨識應以嚴格 GS1 規則處理、不得退回一般條碼查詢的輸入。</summary>
    public static bool IsGs1Candidate(string? value)
    {
        var input = value?.Trim();
        return !string.IsNullOrEmpty(input) &&
            (input.StartsWith('(') || (input.Length > 16 && input.StartsWith("01", StringComparison.Ordinal)));
    }

    /// <summary>
    /// 將 GS1 的兩位數年份轉成固定世紀：00–49 是 2000–2049，50–99 是 1950–1999。
    /// 固定樞紐不依賴執行當下年份，確保同一個醫材條碼永遠得到相同效期。
    /// </summary>
    public static bool TryParse(string? value, out Gs1BarcodeData? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var input = value.Trim();
        var elements = input.StartsWith('(')
            ? ParseHumanReadable(input)
            : ParseRaw(input);
        if (elements is null)
        {
            return false;
        }

        string? gtin = null;
        DateOnly? expiryDate = null;
        string? lotNumber = null;

        foreach (var element in elements)
        {
            switch (element.ApplicationIdentifier)
            {
                case "01":
                    if (gtin is not null || element.Value.Length != 14 || !IsAsciiDigits(element.Value))
                    {
                        return false;
                    }

                    gtin = element.Value;
                    break;
                case "17":
                    if (expiryDate is not null || !TryParseExpiry(element.Value, out var parsedExpiry))
                    {
                        return false;
                    }

                    expiryDate = parsedExpiry;
                    break;
                case "10":
                    if (lotNumber is not null || !IsValidLotNumber(element.Value))
                    {
                        return false;
                    }

                    lotNumber = element.Value;
                    break;
                default:
                    return false;
            }
        }

        if (gtin is null)
        {
            return false;
        }

        result = new Gs1BarcodeData(gtin, expiryDate, lotNumber);
        return true;
    }

    private static List<Gs1Element>? ParseHumanReadable(string input)
    {
        var elements = new List<Gs1Element>();
        var position = 0;
        while (position < input.Length)
        {
            if (position + 4 > input.Length || input[position] != '(' || input[position + 3] != ')' ||
                !char.IsAsciiDigit(input[position + 1]) || !char.IsAsciiDigit(input[position + 2]))
            {
                return null;
            }

            var applicationIdentifier = input.Substring(position + 1, 2);
            position += 4;
            var nextElement = input.IndexOf('(', position);
            var end = nextElement < 0 ? input.Length : nextElement;
            var elementValue = input[position..end];
            if (elementValue.Length == 0 || elementValue.Contains(')'))
            {
                return null;
            }

            elements.Add(new Gs1Element(applicationIdentifier, elementValue));
            position = end;
        }

        return elements;
    }

    private static List<Gs1Element>? ParseRaw(string input)
    {
        var elements = new List<Gs1Element>();
        var position = 0;
        while (position < input.Length)
        {
            if (input[position] == GroupSeparator)
            {
                return null;
            }

            if (position + 2 > input.Length || !char.IsAsciiDigit(input[position]) || !char.IsAsciiDigit(input[position + 1]))
            {
                return null;
            }

            var applicationIdentifier = input.Substring(position, 2);
            position += 2;
            switch (applicationIdentifier)
            {
                case "01":
                    if (!TryReadFixedLength(input, ref position, 14, out var gtin))
                    {
                        return null;
                    }

                    elements.Add(new Gs1Element(applicationIdentifier, gtin));
                    break;
                case "17":
                    if (!TryReadFixedLength(input, ref position, 6, out var expiry))
                    {
                        return null;
                    }

                    elements.Add(new Gs1Element(applicationIdentifier, expiry));
                    break;
                case "10":
                    var separator = input.IndexOf(GroupSeparator, position);
                    var end = separator < 0 ? input.Length : separator;
                    elements.Add(new Gs1Element(applicationIdentifier, input[position..end]));
                    position = separator < 0 ? input.Length : separator + 1;
                    break;
                default:
                    return null;
            }
        }

        return elements;
    }

    private static bool TryReadFixedLength(string input, ref int position, int length, out string value)
    {
        value = string.Empty;
        if (position + length > input.Length)
        {
            return false;
        }

        value = input.Substring(position, length);
        position += length;
        return true;
    }

    private static bool TryParseExpiry(string value, out DateOnly expiryDate)
    {
        expiryDate = default;
        if (value.Length != 6 || !IsAsciiDigits(value))
        {
            return false;
        }

        var twoDigitYear = int.Parse(value.AsSpan(0, 2), CultureInfo.InvariantCulture);
        var year = twoDigitYear <= 49 ? 2000 + twoDigitYear : 1900 + twoDigitYear;
        var month = int.Parse(value.AsSpan(2, 2), CultureInfo.InvariantCulture);
        var day = int.Parse(value.AsSpan(4, 2), CultureInfo.InvariantCulture);
        return DateOnly.TryParseExact(
            $"{year:D4}{month:D2}{day:D2}",
            "yyyyMMdd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out expiryDate);
    }

    private static bool IsAsciiDigits(string value) => value.All(char.IsAsciiDigit);

    private static bool IsValidLotNumber(string value)
        => value.Length is >= 1 and <= 20 && value.All(character => character is >= '!' and <= '~' and not '(' and not ')');

    private sealed record Gs1Element(string ApplicationIdentifier, string Value);
}
