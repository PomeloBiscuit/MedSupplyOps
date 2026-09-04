using System;

namespace MedSupplyOps.Infrastructure.Identity;

/// <summary>
/// <see cref="ICurrentUser"/> 的固定值實作，給不在 HTTP 請求內的情境使用
/// （種子資料、背景工作、測試）。呼叫端必須明確傳入 actor —— 這就是 D4
/// 要求的「由呼叫端明確指定」，而不是由 <see cref="Persistence.MedSupplyOpsDbContext"/> 悄悄補一個預設值。
/// </summary>
public sealed class FixedCurrentUser : ICurrentUser
{
    public FixedCurrentUser(string actor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        Actor = actor;
    }

    public string Actor { get; }
}
