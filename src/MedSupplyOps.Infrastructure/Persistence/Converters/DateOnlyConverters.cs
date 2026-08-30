using System;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace MedSupplyOps.Infrastructure.Persistence.Converters;

/// <summary>
/// DateOnly 對映到 Oracle DATE。
///
/// 為什麼要自己寫轉換器而不倚賴 provider 內建：
///   Domain 的效期欄位刻意用 DateOnly（見 StockLot.ExpiryDate 的註解，
///   「純日期、沒有時分秒」是它的語意）。Oracle 的 DATE 型別其實含時分秒，
///   Oracle.EntityFrameworkCore 10 對 DateOnly 的原生支援不保證存在。
///   明確寫一個「補上 00:00:00 / 讀回時砍掉時間」的轉換器，
///   比賭 provider 版本行為穩定，而且測試看得懂它在做什麼。
/// </summary>
internal static class DateOnlyConverters
{
    public static readonly ValueConverter<DateOnly, DateTime> DateOnlyToDateTime =
        new(
            d => d.ToDateTime(TimeOnly.MinValue),
            dt => DateOnly.FromDateTime(dt));
}
