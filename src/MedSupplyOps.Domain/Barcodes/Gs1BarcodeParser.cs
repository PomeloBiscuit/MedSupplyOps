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
    /// 解析 GS1 的 (01)／(17)／(10)。
    ///
    /// ★ (17) 效期是 YYMMDD，沒有世紀。GS1 General Specifications §7.12 規定的是
    ///   「以當年為基準、往前 49 年到往後 50 年」的滑動窗口，**不是**固定在某一年的樞紐。
    ///
    ///   前一版用固定樞紐（00–49 → 2000–2049、50–99 → 1950–1999），理由是
    ///   「不依賴執行當下年份，同一個條碼永遠得到相同效期」。但那個規則只在西元 2000 年
    ///   剛好等於規範；在 2026 年，YY = 50–76 會被解成 1950–1976，差一百年 ——
    ///   一批 2050 年到期的貨會被判成已過期而拒收。而且錯的範圍每年都在擴大。
    ///   滑動窗口對一個條碼在其產品壽命內（前後約 50 年）的解讀是穩定的，
    ///   「決定性」的顧慮在實務上不成立。
    ///
    ///   基準日由呼叫端傳入（取自 BusinessCalendar），這個函式本身仍是純函式、不讀時鐘。
    /// </summary>
    public static bool TryParse(string? value, DateOnly referenceDate, out Gs1BarcodeData? result)
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
                    if (expiryDate is not null || !TryParseExpiry(element.Value, referenceDate.Year, out var parsedExpiry))
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

    private static bool TryParseExpiry(string value, int referenceYear, out DateOnly expiryDate)
    {
        expiryDate = default;
        if (value.Length != 6 || !IsAsciiDigits(value))
        {
            return false;
        }

        var twoDigitYear = int.Parse(value.AsSpan(0, 2), CultureInfo.InvariantCulture);
        var year = ResolveCentury(twoDigitYear, referenceYear);
        var month = int.Parse(value.AsSpan(2, 2), CultureInfo.InvariantCulture);
        var day = int.Parse(value.AsSpan(4, 2), CultureInfo.InvariantCulture);
        return DateOnly.TryParseExact(
            $"{year:D4}{month:D2}{day:D2}",
            "yyyyMMdd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out expiryDate);
    }

    /// <summary>
    /// GS1 §7.12：年份必須落在 [基準年 − 49, 基準年 + 50]。
    /// 先放進基準年的世紀，超出上界就退一個世紀、低於下界就進一個世紀。
    /// </summary>
    private static int ResolveCentury(int twoDigitYear, int referenceYear)
    {
        var candidate = referenceYear / 100 * 100 + twoDigitYear;
        if (candidate > referenceYear + 50)
        {
            return candidate - 100;
        }

        if (candidate < referenceYear - 49)
        {
            return candidate + 100;
        }

        return candidate;
    }

    private static bool IsAsciiDigits(string value) => value.All(char.IsAsciiDigit);

    private static bool IsValidLotNumber(string value)
        => value.Length is >= 1 and <= 20 && value.All(character => character is >= '!' and <= '~' and not '(' and not ')');

    private sealed record Gs1Element(string ApplicationIdentifier, string Value);
}
