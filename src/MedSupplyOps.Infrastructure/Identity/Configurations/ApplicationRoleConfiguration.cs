using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MedSupplyOps.Infrastructure.Identity.Configurations;

/// <summary>IDENTITY_ROLES 表對映。角色本身用 Identity 內建的 <see cref="IdentityRole"/>，不另外自訂型別。</summary>
internal sealed class ApplicationRoleConfiguration : IEntityTypeConfiguration<IdentityRole>
{
    public void Configure(EntityTypeBuilder<IdentityRole> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("IDENTITY_ROLES");

        builder.Property(x => x.Id).HasColumnName("ID").HasMaxLength(450);
        builder.Property(x => x.Name).HasColumnName("NAME").HasMaxLength(256);
        builder.Property(x => x.NormalizedName).HasColumnName("NORMALIZED_NAME").HasMaxLength(256);
        builder.Property(x => x.ConcurrencyStamp).HasColumnName("CONCURRENCY_STAMP").HasMaxLength(100).IsConcurrencyToken();

        builder.HasIndex(x => x.NormalizedName).HasDatabaseName("UX_IDENTITY_ROLES_NAME").IsUnique();
    }
}
