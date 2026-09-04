using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace MedSupplyOps.Infrastructure.Identity;

/// <summary>
/// Identity 專用的 DbContext，與 <see cref="Persistence.MedSupplyOpsDbContext"/> 分開（身分模組的設計決策）。
///
/// 分開的理由：<see cref="Persistence.MedSupplyOpsDbContext"/> 的 <c>SaveChanges</c> 會替
/// 每個實體簿記 CREATED_BY / UPDATED_BY 等欄位（見該類別的 StampBookkeepingColumns），
/// 那套簿記邏輯是為 Domain 實體設計的，Identity 的表（AspNetUsers 等）有自己的欄位集合
/// （SecurityStamp、ConcurrencyStamp…），硬塞進同一個 DbContext 只會讓兩邊的假設互相污染。
///
/// 表名與欄位名比照 V001 的風格改成小寫（Oracle 會折成大寫），而不是用 Identity
/// 預設的 AspNetUsers / AspNetRoles 等 PascalCase 名稱。
/// </summary>
public sealed class MedSupplyOpsIdentityDbContext : IdentityDbContext<ApplicationUser>
{
    public MedSupplyOpsIdentityDbContext(DbContextOptions<MedSupplyOpsIdentityDbContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // ★ 只掃描 Identity 自己的設定類別所在的命名空間，不能用不篩選命名空間的
        //   ApplyConfigurationsFromAssembly(assembly) —— 那會連同 Persistence.Configurations
        //   底下 Item / Department 等 Domain 對映一起套用，而 ApplyConfiguration<TEntity>()
        //   只要被呼叫就會把 TEntity 加進「這個」模型，等於把 Domain 實體錯誤地混進
        //   Identity 專用的 DbContext。
        builder.ApplyConfigurationsFromAssembly(
            typeof(MedSupplyOpsIdentityDbContext).Assembly,
            type => type.Namespace == typeof(Configurations.ApplicationUserConfiguration).Namespace);
    }
}
