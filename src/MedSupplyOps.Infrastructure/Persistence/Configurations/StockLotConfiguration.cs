using System;
using MedSupplyOps.Domain.Inventory;
using MedSupplyOps.Infrastructure.Persistence.Converters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MedSupplyOps.Infrastructure.Persistence.Configurations;

/// <summary>
/// STOCK_LOTS 表對映到 Domain 的 <see cref="StockLot"/>。
///
/// Domain 型別維持零套件依賴、封裝不放寬（設計裁定 D1 / D5）：
///   - <see cref="StockLot"/> 只有一個「全參數建構子」、Id 等屬性 get-only、Quantity 是 private set。
///     EF Core 用「建構子繫結」（參數名對屬性名）具現化，private setter 它寫得進去，
///     不需要無參數建構子，也不需要把 setter 改 public。
///   - 資料表上 Domain 沒有的欄位（ROW_VERSION 與四個稽核欄位）以 shadow property 對映。
///
/// ROW_VERSION 這裡只當普通欄位（NOT NULL、預設 0），不設成 IsConcurrencyToken ——
/// 樂觀鎖是 FR-402，明確不在這裡處理。
/// </summary>
internal sealed class StockLotConfiguration : IEntityTypeConfiguration<StockLot>
{
    public void Configure(EntityTypeBuilder<StockLot> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("STOCK_LOTS");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("STOCK_LOT_ID").ValueGeneratedOnAdd();

        builder.Property(x => x.ItemId).HasColumnName("ITEM_ID").IsRequired();
        builder.Property(x => x.LotNumber).HasColumnName("LOT_NUMBER").HasMaxLength(64).IsRequired();

        builder.Property(x => x.ExpiryDate)
            .HasColumnName("EXPIRY_DATE")
            .HasColumnType("DATE")
            .HasConversion(DateOnlyConverters.DateOnlyToDateTime)
            .IsRequired();

        builder.Property(x => x.Quantity).HasColumnName("QUANTITY").IsRequired();
        builder.Property(x => x.StorageLocation).HasColumnName("STORAGE_LOCATION").HasMaxLength(64).IsRequired();

        builder.Property<long>("RowVersion").HasColumnName("ROW_VERSION").IsRequired();

        builder.Property<DateTime>("CreatedAt").HasColumnName("CREATED_AT").HasColumnType("TIMESTAMP(6)").IsRequired();
        builder.Property<string>("CreatedBy").HasColumnName("CREATED_BY").HasMaxLength(100).IsRequired();
        builder.Property<DateTime?>("UpdatedAt").HasColumnName("UPDATED_AT").HasColumnType("TIMESTAMP(6)");
        builder.Property<string?>("UpdatedBy").HasColumnName("UPDATED_BY").HasMaxLength(100);
    }
}
