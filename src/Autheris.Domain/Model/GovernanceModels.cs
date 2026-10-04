namespace Autheris.Domain.Model;

public sealed class Role
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string RoleName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
}

public sealed class RoleMember
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid RoleId { get; init; }
    public string MemberType { get; init; } = "USER"; // USER, AD_GROUP
    public Sid MemberSid { get; init; }
}

public sealed class DataOwner
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Sid AdSid { get; init; }
    public string AdAccount { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public bool IsActive { get; init; } = true;
}

public sealed class TableOwner
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid TableId { get; init; }
    public Guid DataOwnerId { get; init; }
    public string OwnerRole { get; init; } = "PRIMARY"; // PRIMARY, SECONDARY, STEWARD
}

public sealed class DataOwnerDelegation
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid DataOwnerId { get; init; }
    public Sid DelegateSid { get; init; }
    public DateTimeOffset ValidFrom { get; init; }
    public DateTimeOffset ValidTo { get; init; }
    public string Reason { get; init; } = string.Empty;

    public bool IsActive(DateTimeOffset atTime) =>
        ValidFrom <= atTime && atTime < ValidTo;
}

public sealed class ConsentRequest
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid TableId { get; init; }
    public TableIdentifier TableIdentifier { get; init; }
    public Sid RequesterSid { get; init; }
    public GranteeType RequestedGranteeType { get; init; } = GranteeType.User;
    public string RequestedGranteeRef { get; init; } = string.Empty;
    public string BusinessJustification { get; init; } = string.Empty;
    public string Status { get; set; } = "PENDING"; // PENDING, APPROVED, REJECTED, EXPIRED, ESCALATED, PENDING_EXTERNAL_APPROVAL
    public DateTimeOffset RequestedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset RequestedValidTo { get; init; }
    public string? ItsmTicketId { get; set; }
    public TenantId TenantId { get; set; } = TenantId.LegacySingleTenant;
    public List<ApprovalStep> ApprovalSteps { get; init; } = new();
}

public sealed class ApprovalStep
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid ConsentRequestId { get; init; }
    public int StepNumber { get; init; }
    public Sid ApproverSid { get; init; }
    public string Decision { get; set; } = "PENDING"; // APPROVED, REJECTED, PENDING
    public string? RejectionReason { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
}

public sealed class PolicyEpoch
{
    public Guid TableId { get; init; }
    public TableIdentifier TableIdentifier { get; init; }
    public long Epoch { get; set; } = 1;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AuditLogEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public TenantId TenantId { get; init; } = TenantId.LegacySingleTenant;
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
    public string EventType { get; init; } = string.Empty;
    public Sid ActorSid { get; init; }
    public string TargetTable { get; init; } = string.Empty;
    public string? TargetColumn { get; init; }
    public string Decision { get; init; } = "ALLOW"; // ALLOW, DENY
    public string TraceId { get; init; } = string.Empty;
    public string DetailsJson { get; init; } = "{}";
    public string PrevHash { get; set; } = string.Empty;
    public string EntryHash { get; set; } = string.Empty;
}
