using System;
using MedSupplyOps.Domain.Requisitions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MedSupplyOps.Infrastructure.Persistence.Configurations;

/// <summary>
/// REQUISITIONS 表對映到 Domain 的 <see cref="Requisition"/>（聚合根）。
///
/// - STATUS 存字串（'Draft'…'Closed'），與 schema 的 CHECK 約束一致，也與稽核可讀性的設計一致。
///   用 <c>HasConversion&lt;string&gt;()</c>，列舉名即字面值。
/// - 明細集合 <c>Lines</c> 是唯讀視圖，真正的欄位是 <c>_lines</c>。
///   以 backing field + <see cref="PropertyAccessMode.Field"/> 對映，不需要 public 的 add 介面。
/// - Domain 沒有的欄位（REQUISITION_NO、四個時間戳、ROW_VERSION、稽核欄位）走 shadow property。
///   REQUISITION_NO 是 NOT NULL + UNIQUE 且無預設，必須由呼叫端指定；
///   單號的產生規則屬於「建立請領單」的流程，不在這裡處理。
/// </summary>
internal sealed class RequisitionConfiguration : IEntityTypeConfiguration<Requisition>
{
    public void Configure(EntityTypeBuilder<Requisition> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("REQUISITIONS");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("REQUISITION_ID").ValueGeneratedOnAdd();

        builder.Property(x => x.DepartmentId).HasColumnName("DEPARTMENT_ID").IsRequired();

        builder.Property(x => x.Status)
            .HasColumnName("STATUS")
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(x => x.RejectionReason).HasColumnName("REJECTION_REASON").HasMaxLength(500);

        builder.Property<string>("RequisitionNo").HasColumnName("REQUISITION_NO").HasMaxLength(32).IsRequired();
        builder.HasIndex("RequisitionNo").IsUnique().HasDatabaseName("UQ_REQUISITIONS_NO");

        builder.Property<DateTime?>("SubmittedAt").HasColumnName("SUBMITTED_AT").HasColumnType("TIMESTAMP(6)");
        builder.Property<DateTime?>("ApprovedAt").HasColumnName("APPROVED_AT").HasColumnType("TIMESTAMP(6)");
        builder.Property<DateTime?>("IssuedAt").HasColumnName("ISSUED_AT").HasColumnType("TIMESTAMP(6)");
        builder.Property<DateTime?>("ClosedAt").HasColumnName("CLOSED_AT").HasColumnType("TIMESTAMP(6)");

        builder.Property<long>("RowVersion").HasColumnName("ROW_VERSION").IsRequired();

        builder.Property<DateTime>("CreatedAt").HasColumnName("CREATED_AT").HasColumnType("TIMESTAMP(6)").IsRequired();
        builder.Property<string>("CreatedBy").HasColumnName("CREATED_BY").HasMaxLength(100).IsRequired();
        builder.Property<DateTime?>("UpdatedAt").HasColumnName("UPDATED_AT").HasColumnType("TIMESTAMP(6)");
        builder.Property<string?>("UpdatedBy").HasColumnName("UPDATED_BY").HasMaxLength(100);

        builder.HasMany(x => x.Lines)
            .WithOne()
            .HasForeignKey("RequisitionId")
            .OnDelete(DeleteBehavior.Restrict);

        builder.Navigation(x => x.Lines)
            .HasField("_lines")
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
