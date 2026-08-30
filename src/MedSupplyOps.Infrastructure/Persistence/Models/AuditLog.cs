using System;

namespace MedSupplyOps.Infrastructure.Persistence.Models;

/// <summary>稽核軌跡（AUDIT_LOGS）的持久化模型。只增不改不刪。</summary>
public sealed class AuditLog
{
    public long Id { get; set; }

    public string EntityType { get; set; } = string.Empty;

    public string EntityId { get; set; } = string.Empty;

    public string Action { get; set; } = string.Empty;

    public string Actor { get; set; } = string.Empty;

    public DateTime OccurredAt { get; set; }

    public string? OldValue { get; set; }

    public string? NewValue { get; set; }
}
