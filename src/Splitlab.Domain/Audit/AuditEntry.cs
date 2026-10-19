namespace Splitlab.Domain.Audit;

/// <summary>Who changed what and when. Written in the same transaction as the change (ADR-0004).</summary>
public sealed class AuditEntry
{
    private AuditEntry()
    {
    }

    public Guid Id { get; private set; }

    public Guid ProjectId { get; private set; }

    public string Actor { get; private set; } = "";

    public string Action { get; private set; } = "";

    public string EntityType { get; private set; } = "";

    public Guid EntityId { get; private set; }

    public string? Detail { get; private set; }

    public DateTimeOffset At { get; private set; }

    public static AuditEntry Create(
        Guid projectId, string actor, string action, string entityType, Guid entityId, string? detail, DateTimeOffset at) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId,
        Actor = actor,
        Action = action,
        EntityType = entityType,
        EntityId = entityId,
        Detail = detail,
        At = at,
    };
}
