using System;
using MedSupplyOps.Infrastructure.Persistence.Converters;
using MedSupplyOps.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MedSupplyOps.Infrastructure.Persistence.Configurations;

/// <summary>
/// ISSUE_ALLOCATIONS 表對映。
/// FK（REQUISITION_LINE_ID / STOCK_LOT_ID）以純量對映，不宣告導覽關聯 ——
/// 完整性由資料庫的 FK 約束保證，對映層只做欄位進出。
/// </summary>
internal sealed class IssueAllocationConfiguration : IEntityTypeConfiguration<IssueAllocation>
{
    public void Configure(EntityTypeBuilder<IssueAllocation> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ISSUE_ALLOCATIONS");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("ISSUE_ALLOCATION_ID").ValueGeneratedOnAdd();

        builder.Property(x => x.RequisitionLineId).HasColumnName("REQUISITION_LINE_ID").IsRequired();
        builder.Property(x => x.StockLotId).HasColumnName("STOCK_LOT_ID").IsRequired();
        builder.Property(x => x.Quantity).HasColumnName("QUANTITY").IsRequired();

        builder.Property(x => x.ExpiryDateAtIssue)
            .HasColumnName("EXPIRY_DATE_AT_ISSUE")
            .HasColumnType("DATE")
            .HasConversion(DateOnlyConverters.DateOnlyToDateTime)
            .IsRequired();

        builder.Property(x => x.IssuedAt).HasColumnName("ISSUED_AT").HasColumnType("TIMESTAMP(6)").IsRequired();
        builder.Property(x => x.IssuedBy).HasColumnName("ISSUED_BY").HasMaxLength(100).IsRequired();

        builder.HasIndex(x => new { x.RequisitionLineId, x.StockLotId })
            .IsUnique()
            .HasDatabaseName("UQ_ISSUE_ALLOC_LINE_LOT");
    }
}
