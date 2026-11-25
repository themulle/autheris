namespace Autheris.Domain.Model;

public sealed class Role
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string RoleName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
}

/// <summary>
/// The owner of a table as resolved from its governance container (domain or product): the container owner group with the container
/// contact as display name. A read-only projection for display and approver lists; it never grants anything by itself.
/// </summary>
public sealed class ContainerOwnerEntry
{
    public Sid AdSid { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public bool IsActive { get; init; } = true;
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
    public string Status { get; set; } = "PENDING"; // PENDING, PENDING_SECOND_APPROVAL, APPROVED, REJECTED, EXPIRED, ESCALATED, PENDING_EXTERNAL_APPROVAL
    public DateTimeOffset RequestedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset RequestedValidTo { get; init; }
    public TenantId TenantId { get; set; }
    public string? ContextJson { get; set; }
    public List<string> RequesterIdentifiers { get; init; } = new();
    public List<ApprovalStep> ApprovalSteps { get; init; } = new();
    public IReadOnlyList<WriteRule> RequestedWriteRules { get; init; } = [];
    public bool IncludeReadAccess { get; init; }
    public int Version { get; init; } = 1;
    public ConsentWorkflowMode WorkflowMode { get; init; } = ConsentWorkflowMode.Internal;
    public Guid? ExternalSystemId { get; init; }
    public string? ExternalReference { get; init; }
    public DateTimeOffset? RequestedValidFrom { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public ConsentRequestKind RequestKind { get; init; } = ConsentRequestKind.Data;
    public PermissionGrantKind GrantKind { get; init; } = PermissionGrantKind.Data;
    public Guid? ProfileId { get; init; }
    public IReadOnlyList<string>? TargetColumns { get; init; }
    public ContainerKind? ContainerKind { get; init; }
    public Guid? ContainerId { get; init; }
    public ApproverCapacity? ApproverCapacity { get; init; }
    public bool SelfGrant { get; init; }
    public Guid? BundleId { get; init; }
    public Sid? DecidedBy { get; init; }
    public DateTimeOffset? DecidedAt { get; init; }

    public bool ShouldGrantReadColumns => RequestedWriteRules.Count == 0 || IncludeReadAccess;
    public bool IsWriteOnlyRequest => RequestedWriteRules.Count > 0 && !IncludeReadAccess;
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

    /// <summary>Issue #1121: every personal identifier form of the approver, so a second approval by the same person is detected in any form.</summary>
    public List<string> ApproverIdentifiers { get; init; } = new();
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
    private string _detailsJson = "{}";

    public Guid Id { get; init; } = Guid.NewGuid();
    public TenantId TenantId { get; init; }
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
    public string EventType { get; init; } = string.Empty;
    public Sid ActorSid { get; init; }
    public string TargetTable { get; init; } = string.Empty;
    public string? TargetColumn { get; init; }
    public string? Query { get; init; }
    public string Decision { get; init; } = "ALLOW"; // ALLOW, DENY
    public string TraceId { get; init; } = string.Empty;

    public string DetailsJson
    {
        get => EffectiveDetailsJson;
        init => _detailsJson = value;
    }

    public Autheris.Domain.Security.CallerContext? Caller { get; set; }

    public string EffectiveDetailsJson
    {
        get
        {
            if (Caller == null || Caller.IsEmpty)
            {
                return _detailsJson;
            }

            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(string.IsNullOrWhiteSpace(_detailsJson) ? "{}" : _detailsJson);
                var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    dict[prop.Name] = prop.Value.Clone();
                }

                dict["caller"] = new
                {
                    app = Caller.Application,
                    ver = Caller.Version,
                    query = Caller.QueryName,
                    asserted = Caller.IsClientAsserted,
                    clientId = Caller.ClientId
                };

                return System.Text.Json.JsonSerializer.Serialize(dict);
            }
            catch
            {
                return _detailsJson;
            }
        }
    }

    public long? ChainSequence { get; set; }
    public string PrevHash { get; set; } = string.Empty;
    public string EntryHash { get; set; } = string.Empty;
}
