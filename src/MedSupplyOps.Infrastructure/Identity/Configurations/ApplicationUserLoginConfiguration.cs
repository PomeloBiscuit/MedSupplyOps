using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MedSupplyOps.Infrastructure.Identity.Configurations;

/// <summary>IDENTITY_USER_LOGINS 表對映。本系統目前未使用外部登入，保留是為了讓 Identity 的標準模型完整。</summary>
internal sealed class ApplicationUserLoginConfiguration : IEntityTypeConfiguration<IdentityUserLogin<string>>
{
    public void Configure(EntityTypeBuilder<IdentityUserLogin<string>> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("IDENTITY_USER_LOGINS");

        builder.Property(x => x.LoginProvider).HasColumnName("LOGIN_PROVIDER").HasMaxLength(128);
        builder.Property(x => x.ProviderKey).HasColumnName("PROVIDER_KEY").HasMaxLength(256);
        builder.Property(x => x.ProviderDisplayName).HasColumnName("PROVIDER_DISPLAY_NAME").HasMaxLength(256);
        builder.Property(x => x.UserId).HasColumnName("USER_ID").HasMaxLength(450);
    }
}
