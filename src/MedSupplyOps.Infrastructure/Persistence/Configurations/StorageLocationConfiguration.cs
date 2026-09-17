using MedSupplyOps.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MedSupplyOps.Infrastructure.Persistence.Configurations;

internal sealed class StorageLocationConfiguration : IEntityTypeConfiguration<StorageLocation>
{
    public void Configure(EntityTypeBuilder<StorageLocation> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("STORAGE_LOCATIONS");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("LOCATION_ID").ValueGeneratedOnAdd();
        builder.Property(x => x.Code).HasColumnName("LOCATION_CODE").HasMaxLength(32).IsRequired();
        builder.Property(x => x.Name).HasColumnName("NAME").HasMaxLength(64).IsRequired();
        builder.Property(x => x.NameEn).HasColumnName("NAME_EN").HasMaxLength(200);
        builder.Property(x => x.IsDeleted).HasColumnName("IS_DELETED").IsRequired();
        builder.Property(x => x.DeletedAt).HasColumnName("DELETED_AT").HasColumnType("TIMESTAMP(6)");
        builder.Property(x => x.DeletedBy).HasColumnName("DELETED_BY").HasMaxLength(100);
        builder.Property(x => x.CreatedAt).HasColumnName("CREATED_AT").HasColumnType("TIMESTAMP(6)").IsRequired();
        builder.Property(x => x.CreatedBy).HasColumnName("CREATED_BY").HasMaxLength(100).IsRequired();
    }
}
