using System;
using MedSupplyOps.Domain.Requisitions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MedSupplyOps.Infrastructure.Persistence.Configurations;

/// <summary>
/// REQUISITION_LINES 表對映到 Domain 的 <see cref="RequisitionLine"/>。
///
/// Domain 的 <see cref="RequisitionLine"/> 只有 ItemId 與 Quantity —— 沒有主鍵、沒有 FK、沒有行號。
/// 這是刻意的：明細在領域模型裡只是聚合內的值，識別由聚合負責。
/// 對映層用 shadow property 補齊資料表要求的欄位，Domain 型別一個字都不動（設計裁定 D5）：
///   - REQUISITION_LINE_ID：shadow 主鍵，資料庫 IDENTITY 產生。
///   - REQUISITION_ID：shadow FK，由 <see cref="RequisitionConfiguration"/> 的關聯建立。
///   - LINE_NO：shadow，於 DbContext.SaveChanges 依聚合內順序給 1..n（序號簿記，非併發控制）。
/// </summary>
internal sealed class RequisitionLineConfiguration : IEntityTypeConfiguration<RequisitionLine>
{
    public void Configure(EntityTypeBuilder<RequisitionLine> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("REQUISITION_LINES");

        builder.Property<long>("RequisitionLineId").HasColumnName("REQUISITION_LINE_ID").ValueGeneratedOnAdd();
        builder.HasKey("RequisitionLineId");

        builder.Property<long>("RequisitionId").HasColumnName("REQUISITION_ID").IsRequired();
        builder.Property<int>("LineNo").HasColumnName("LINE_NO").IsRequired();

        builder.Property(x => x.ItemId).HasColumnName("ITEM_ID").IsRequired();
        builder.Property(x => x.Quantity).HasColumnName("QUANTITY").IsRequired();

        builder.HasIndex("RequisitionId", "LineNo").IsUnique().HasDatabaseName("UQ_REQ_LINES_LINE_NO");
        builder.HasIndex("RequisitionId", "ItemId").IsUnique().HasDatabaseName("UQ_REQ_LINES_ITEM");
    }
}
