using System;
using MedSupplyOps.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MedSupplyOps.Infrastructure.Persistence.Configurations;

/// <summary>AUDIT_LOGS 表對映。OLD_VALUE / NEW_VALUE 是 CLOB。</summary>
internal sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("AUDIT_LOGS");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("AUDIT_LOG_ID").ValueGeneratedOnAdd();

        builder.Property(x => x.EntityType).HasColumnName("ENTITY_TYPE").HasMaxLength(64).IsRequired();
        builder.Property(x => x.EntityId).HasColumnName("ENTITY_ID").HasMaxLength(64).IsRequired();
        builder.Property(x => x.Action).HasColumnName("ACTION").HasMaxLength(32).IsRequired();
        builder.Property(x => x.Actor).HasColumnName("ACTOR").HasMaxLength(100).IsRequired();
        builder.Property(x => x.OccurredAt).HasColumnName("OCCURRED_AT").HasColumnType("TIMESTAMP(6)").IsRequired();

        builder.Property(x => x.OldValue).HasColumnName("OLD_VALUE").HasColumnType("CLOB");
        builder.Property(x => x.NewValue).HasColumnName("NEW_VALUE").HasColumnType("CLOB");
    }
}
