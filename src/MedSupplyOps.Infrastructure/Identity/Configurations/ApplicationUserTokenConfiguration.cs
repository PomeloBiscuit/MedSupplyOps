using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MedSupplyOps.Infrastructure.Identity.Configurations;

/// <summary>
/// IDENTITY_USER_TOKENS 表對映。本系統目前未使用（例如 2FA 恢復碼），
/// 保留是為了讓 Identity 的標準模型完整，避免日後啟用相關功能時要重新補表。
/// </summary>
internal sealed class ApplicationUserTokenConfiguration : IEntityTypeConfiguration<IdentityUserToken<string>>
{
    public void Configure(EntityTypeBuilder<IdentityUserToken<string>> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("IDENTITY_USER_TOKENS");

        builder.Property(x => x.UserId).HasColumnName("USER_ID").HasMaxLength(450);
        builder.Property(x => x.LoginProvider).HasColumnName("LOGIN_PROVIDER").HasMaxLength(128);
        builder.Property(x => x.Name).HasColumnName("NAME").HasMaxLength(128);
        builder.Property(x => x.Value).HasColumnName("VALUE").HasMaxLength(2000);
    }
}
