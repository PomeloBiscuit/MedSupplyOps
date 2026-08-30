using System;
using MedSupplyOps.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MedSupplyOps.Infrastructure.Persistence.Configurations;

/// <summary>
/// ITEMS 表對映。
///
/// ★ 每一個資料表與欄位名稱都「明確、且全大寫」指定（設計裁定 D3）。
///   Oracle 未加引號的識別項會被折成大寫，schema 裡的表實際叫 ITEMS；
///   EF Core 產生 SQL 時一律替識別項加雙引號，"ITEMS" 能對到 ITEMS，
///   但 "Items"（沿用 C# 型別名的預設慣例）對不到，會噴 ORA-00942。
///   所以這裡不依賴任何命名慣例，逐一寫死大寫名稱。
/// </summary>
internal sealed class ItemConfiguration : IEntityTypeConfiguration<Item>
{
    public void Configure(EntityTypeBuilder<Item> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ITEMS");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("ITEM_ID").ValueGeneratedOnAdd();

        builder.Property(x => x.Code).HasColumnName("ITEM_CODE").HasMaxLength(32).IsRequired();
        builder.Property(x => x.Name).HasColumnName("ITEM_NAME").HasMaxLength(200).IsRequired();
        builder.Property(x => x.Specification).HasColumnName("SPECIFICATION").HasMaxLength(400);
        builder.Property(x => x.UnitOfMeasure).HasColumnName("UNIT_OF_MEASURE").HasMaxLength(20).IsRequired();

        builder.Property(x => x.TracksLot).HasColumnName("TRACKS_LOT").IsRequired();
        builder.Property(x => x.TracksExpiry).HasColumnName("TRACKS_EXPIRY").IsRequired();
        builder.Property(x => x.SafetyStockQty).HasColumnName("SAFETY_STOCK_QTY").IsRequired();

        builder.Property(x => x.IsDeleted).HasColumnName("IS_DELETED").IsRequired();
        builder.Property(x => x.DeletedAt).HasColumnName("DELETED_AT").HasColumnType("TIMESTAMP(6)");
        builder.Property(x => x.DeletedBy).HasColumnName("DELETED_BY").HasMaxLength(100);

        builder.Property(x => x.CreatedAt).HasColumnName("CREATED_AT").HasColumnType("TIMESTAMP(6)").IsRequired();
        builder.Property(x => x.CreatedBy).HasColumnName("CREATED_BY").HasMaxLength(100).IsRequired();
        builder.Property(x => x.UpdatedAt).HasColumnName("UPDATED_AT").HasColumnType("TIMESTAMP(6)");
        builder.Property(x => x.UpdatedBy).HasColumnName("UPDATED_BY").HasMaxLength(100);
    }
}
