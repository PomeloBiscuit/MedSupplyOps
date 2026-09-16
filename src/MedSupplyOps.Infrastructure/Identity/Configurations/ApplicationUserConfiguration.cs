using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MedSupplyOps.Infrastructure.Identity.Configurations;

/// <summary>IDENTITY_USERS 表對映。命名與大小寫風格比照 Persistence.Configurations（見 ItemConfiguration）。</summary>
internal sealed class ApplicationUserConfiguration : IEntityTypeConfiguration<ApplicationUser>
{
    public void Configure(EntityTypeBuilder<ApplicationUser> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("IDENTITY_USERS");

        builder.Property(x => x.Id).HasColumnName("ID").HasMaxLength(450);
        builder.Property(x => x.UserName).HasColumnName("USER_NAME").HasMaxLength(256);
        builder.Property(x => x.NormalizedUserName).HasColumnName("NORMALIZED_USER_NAME").HasMaxLength(256);
        builder.Property(x => x.Email).HasColumnName("EMAIL").HasMaxLength(256);
        builder.Property(x => x.NormalizedEmail).HasColumnName("NORMALIZED_EMAIL").HasMaxLength(256);
        builder.Property(x => x.EmailConfirmed).HasColumnName("EMAIL_CONFIRMED").IsRequired();
        builder.Property(x => x.PasswordHash).HasColumnName("PASSWORD_HASH").HasMaxLength(256);
        builder.Property(x => x.SecurityStamp).HasColumnName("SECURITY_STAMP").HasMaxLength(100);
        builder.Property(x => x.ConcurrencyStamp).HasColumnName("CONCURRENCY_STAMP").HasMaxLength(100).IsConcurrencyToken();
        builder.Property(x => x.PhoneNumber).HasColumnName("PHONE_NUMBER").HasMaxLength(32);
        builder.Property(x => x.PhoneNumberConfirmed).HasColumnName("PHONE_NUMBER_CONFIRMED").IsRequired();
        builder.Property(x => x.TwoFactorEnabled).HasColumnName("TWO_FACTOR_ENABLED").IsRequired();
        builder.Property(x => x.LockoutEnd).HasColumnName("LOCKOUT_END");
        builder.Property(x => x.LockoutEnabled).HasColumnName("LOCKOUT_ENABLED").IsRequired();
        builder.Property(x => x.AccessFailedCount).HasColumnName("ACCESS_FAILED_COUNT").IsRequired();

        // 自訂欄位：畫面顯示用的姓名（登入帳號是 email，不適合直接顯示）。
        builder.Property(x => x.DisplayName).HasColumnName("DISPLAY_NAME").HasMaxLength(100).IsRequired();
        builder.Property(x => x.EmployeeNo).HasColumnName("EMPLOYEE_NO").HasMaxLength(32);
        builder.Property(x => x.DepartmentId).HasColumnName("DEPARTMENT_ID");

        builder.HasIndex(x => x.NormalizedUserName).HasDatabaseName("UX_IDENTITY_USERS_USERNAME").IsUnique();
        builder.HasIndex(x => x.NormalizedEmail).HasDatabaseName("IX_IDENTITY_USERS_EMAIL");
        builder.HasIndex(x => x.EmployeeNo).HasDatabaseName("UX_IDENTITY_USERS_EMPLOYEE_NO").IsUnique();
    }
}
