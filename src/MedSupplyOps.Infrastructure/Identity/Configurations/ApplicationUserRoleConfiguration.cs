using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MedSupplyOps.Infrastructure.Identity.Configurations;

/// <summary>IDENTITY_USER_ROLES 表對映。使用者與角色的多對多關聯表。</summary>
internal sealed class ApplicationUserRoleConfiguration : IEntityTypeConfiguration<IdentityUserRole<string>>
{
    public void Configure(EntityTypeBuilder<IdentityUserRole<string>> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("IDENTITY_USER_ROLES");

        builder.Property(x => x.UserId).HasColumnName("USER_ID").HasMaxLength(450);
        builder.Property(x => x.RoleId).HasColumnName("ROLE_ID").HasMaxLength(450);
    }
}
