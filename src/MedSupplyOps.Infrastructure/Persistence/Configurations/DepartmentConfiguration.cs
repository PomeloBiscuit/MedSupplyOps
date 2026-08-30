using System;
using MedSupplyOps.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MedSupplyOps.Infrastructure.Persistence.Configurations;

/// <summary>DEPARTMENTS 表對映。識別項全大寫明確指定，理由見 <see cref="ItemConfiguration"/>。</summary>
internal sealed class DepartmentConfiguration : IEntityTypeConfiguration<Department>
{
    public void Configure(EntityTypeBuilder<Department> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("DEPARTMENTS");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("DEPARTMENT_ID").ValueGeneratedOnAdd();

        builder.Property(x => x.Code).HasColumnName("DEPARTMENT_CODE").HasMaxLength(32).IsRequired();
        builder.Property(x => x.Name).HasColumnName("DEPARTMENT_NAME").HasMaxLength(200).IsRequired();
        builder.Property(x => x.IsActive).HasColumnName("IS_ACTIVE").IsRequired();

        builder.Property(x => x.IsDeleted).HasColumnName("IS_DELETED").IsRequired();
        builder.Property(x => x.DeletedAt).HasColumnName("DELETED_AT").HasColumnType("TIMESTAMP(6)");
        builder.Property(x => x.DeletedBy).HasColumnName("DELETED_BY").HasMaxLength(100);

        builder.Property(x => x.CreatedAt).HasColumnName("CREATED_AT").HasColumnType("TIMESTAMP(6)").IsRequired();
        builder.Property(x => x.CreatedBy).HasColumnName("CREATED_BY").HasMaxLength(100).IsRequired();
        builder.Property(x => x.UpdatedAt).HasColumnName("UPDATED_AT").HasColumnType("TIMESTAMP(6)");
        builder.Property(x => x.UpdatedBy).HasColumnName("UPDATED_BY").HasMaxLength(100);
    }
}
