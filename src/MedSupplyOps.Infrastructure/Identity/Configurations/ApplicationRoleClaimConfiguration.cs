using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MedSupplyOps.Infrastructure.Identity.Configurations;

/// <summary>IDENTITY_ROLE_CLAIMS 表對映。本系統目前未使用，保留是為了讓 Identity 的標準模型完整。</summary>
internal sealed class ApplicationRoleClaimConfiguration : IEntityTypeConfiguration<IdentityRoleClaim<string>>
{
    public void Configure(EntityTypeBuilder<IdentityRoleClaim<string>> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("IDENTITY_ROLE_CLAIMS");

        builder.Property(x => x.Id).HasColumnName("ID");
        builder.Property(x => x.RoleId).HasColumnName("ROLE_ID").HasMaxLength(450);
        builder.Property(x => x.ClaimType).HasColumnName("CLAIM_TYPE").HasMaxLength(256);
        builder.Property(x => x.ClaimValue).HasColumnName("CLAIM_VALUE").HasMaxLength(1000);
    }
}
