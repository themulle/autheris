namespace Autheris.Domain.Model;

public enum ConsentWorkflowMode
{
    Internal = 0,
    External = 1
}

public enum ConsentRequestKind
{
    Data = 0,
    ProfileAssignment = 1,
    Unmask = 2,
    Catalog = 3
}

public enum PermissionGrantKind
{
    Unknown = 0,
    Data = 1,
    Unmask = 2,
    Catalog = 3
}

public enum ApproverCapacity
{
    DataOwner = 0,
    Governance = 1,
    ItAdmin = 2,
    SelfGrant = 3
}

public enum ConsentGrantSource
{
    ConsentRequest = 0,
    AccessProfile = 1,
    Bulk = 3,
    Declarative = 4,
    AccessPlan = 5,
    SelfGrant = 6
}

/// <summary>
/// Scope of a workflow route. <see cref="Unknown"/> is what a stored value with a removed (data owner, data source) or unrecognised
/// scope parses to; such routes are inert and never match.
/// </summary>
public enum WorkflowRouteScopeKind
{
    Unknown = -1,
    Tenant = 0,
    Domain = 1
}

public static class ConsentWorkflowSqlMapping
{
    public static string ToSqlString(this ConsentGrantSource source) => source switch
    {
        ConsentGrantSource.ConsentRequest => "CONSENT_REQUEST",
        ConsentGrantSource.AccessProfile => "ACCESS_PROFILE",
        ConsentGrantSource.Bulk => "BULK",
        ConsentGrantSource.Declarative => "DECLARATIVE",
        ConsentGrantSource.AccessPlan => "ACCESS_PLAN",
        ConsentGrantSource.SelfGrant => "SELF_GRANT",
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Unsupported consent grant source.")
    };

    public static bool TryParseGrantSource(string? value, out ConsentGrantSource source)
    {
        switch (value?.Trim().ToUpperInvariant())
        {
            case "CONSENT_REQUEST":
                source = ConsentGrantSource.ConsentRequest;
                return true;
            case "ACCESS_PROFILE":
                source = ConsentGrantSource.AccessProfile;
                return true;
            case "BULK":
                source = ConsentGrantSource.Bulk;
                return true;
            case "DECLARATIVE":
                source = ConsentGrantSource.Declarative;
                return true;
            case "ACCESS_PLAN":
                source = ConsentGrantSource.AccessPlan;
                return true;
            case "SELF_GRANT":
                source = ConsentGrantSource.SelfGrant;
                return true;
            default:
                source = ConsentGrantSource.ConsentRequest;
                return false;
        }
    }

    public static ConsentGrantSource ParseGrantSourceOrDefault(
        string? value,
        ConsentGrantSource fallback = ConsentGrantSource.ConsentRequest) =>
        TryParseGrantSource(value, out var parsed) ? parsed : fallback;

    public static string ToSqlString(this ConsentWorkflowMode mode) => mode switch
    {
        ConsentWorkflowMode.Internal => "INTERNAL",
        ConsentWorkflowMode.External => "EXTERNAL",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported consent workflow mode.")
    };

    public static bool TryParseWorkflowMode(string? value, out ConsentWorkflowMode mode)
    {
        switch (value?.Trim().ToUpperInvariant())
        {
            case "INTERNAL":
                mode = ConsentWorkflowMode.Internal;
                return true;
            case "EXTERNAL":
                mode = ConsentWorkflowMode.External;
                return true;
            default:
                mode = ConsentWorkflowMode.Internal;
                return false;
        }
    }

    public static ConsentWorkflowMode ParseWorkflowModeOrDefault(
        string? value,
        ConsentWorkflowMode fallback = ConsentWorkflowMode.Internal) =>
        TryParseWorkflowMode(value, out var parsed) ? parsed : fallback;

    public static string ToSqlString(this ConsentRequestKind kind) => kind switch
    {
        ConsentRequestKind.Data => "DATA",
        ConsentRequestKind.ProfileAssignment => "PROFILE_ASSIGNMENT",
        ConsentRequestKind.Unmask => "UNMASK",
        ConsentRequestKind.Catalog => "CATALOG",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported consent request kind.")
    };

    public static bool TryParseRequestKind(string? value, out ConsentRequestKind kind)
    {
        switch (value?.Trim().ToUpperInvariant())
        {
            case "DATA":
            case "CONSENT":
                kind = ConsentRequestKind.Data;
                return true;
            case "PROFILE_ASSIGNMENT":
                kind = ConsentRequestKind.ProfileAssignment;
                return true;
            case "UNMASK":
                kind = ConsentRequestKind.Unmask;
                return true;
            case "CATALOG":
                kind = ConsentRequestKind.Catalog;
                return true;
            default:
                kind = ConsentRequestKind.Data;
                return false;
        }
    }

    public static ConsentRequestKind ParseRequestKindOrDefault(
        string? value,
        ConsentRequestKind fallback = ConsentRequestKind.Data) =>
        TryParseRequestKind(value, out var parsed) ? parsed : fallback;

    public static string ToSqlString(this PermissionGrantKind grantKind) => grantKind switch
    {
        PermissionGrantKind.Data => "DATA",
        PermissionGrantKind.Unmask => "UNMASK",
        PermissionGrantKind.Catalog => "CATALOG",
        PermissionGrantKind.Unknown => "UNKNOWN",
        _ => throw new ArgumentOutOfRangeException(nameof(grantKind), grantKind, "Unsupported permission grant kind.")
    };

    /// <summary>
    /// Strict parser for persisted grant kinds: unknown or NULL values never fall back to <see cref="PermissionGrantKind.Data"/>.
    /// </summary>
    public static bool TryParseGrantKind(string? value, out PermissionGrantKind grantKind)
    {
        switch (value?.Trim().ToUpperInvariant())
        {
            case "DATA":
                grantKind = PermissionGrantKind.Data;
                return true;
            case "UNMASK":
                grantKind = PermissionGrantKind.Unmask;
                return true;
            case "CATALOG":
                grantKind = PermissionGrantKind.Catalog;
                return true;
            default:
                grantKind = PermissionGrantKind.Unknown;
                return false;
        }
    }

    public static PermissionGrantKind ParseGrantKindOrUnknown(string? value) =>
        TryParseGrantKind(value, out var parsed) ? parsed : PermissionGrantKind.Unknown;

    public static string ToSqlString(this ApproverCapacity capacity) => capacity switch
    {
        ApproverCapacity.DataOwner => "DATA_OWNER",
        ApproverCapacity.Governance => "GOVERNANCE",
        ApproverCapacity.ItAdmin => "IT_ADMIN",
        ApproverCapacity.SelfGrant => "SELF_GRANT",
        _ => throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Unsupported approver capacity.")
    };

    public static bool TryParseApproverCapacity(string? value, out ApproverCapacity capacity)
    {
        switch (value?.Trim().ToUpperInvariant())
        {
            case "DATA_OWNER":
                capacity = ApproverCapacity.DataOwner;
                return true;
            case "GOVERNANCE":
                capacity = ApproverCapacity.Governance;
                return true;
            case "IT_ADMIN":
                capacity = ApproverCapacity.ItAdmin;
                return true;
            case "SELF_GRANT":
                capacity = ApproverCapacity.SelfGrant;
                return true;
            default:
                capacity = default;
                return false;
        }
    }

    public static string ToSqlString(this ContainerKind containerKind) => containerKind switch
    {
        ContainerKind.Domain => "DOMAIN",
        ContainerKind.DataProduct => "DATA_PRODUCT",
        _ => throw new ArgumentOutOfRangeException(nameof(containerKind), containerKind, "Unsupported container kind.")
    };

    public static bool TryParseContainerKind(string? value, out ContainerKind containerKind)
    {
        switch (value?.Trim().ToUpperInvariant())
        {
            case "DOMAIN":
                containerKind = ContainerKind.Domain;
                return true;
            case "DATA_PRODUCT":
                containerKind = ContainerKind.DataProduct;
                return true;
            default:
                containerKind = default;
                return false;
        }
    }

    public static string ToSqlString(this WorkflowRouteScopeKind scopeKind) => scopeKind switch
    {
        WorkflowRouteScopeKind.Tenant => "TENANT",
        WorkflowRouteScopeKind.Domain => "DOMAIN",
        _ => throw new ArgumentOutOfRangeException(nameof(scopeKind), scopeKind, "Unsupported workflow route scope kind.")
    };

    public static bool TryParseRouteScopeKind(string? value, out WorkflowRouteScopeKind scopeKind)
    {
        switch (value?.Trim().ToUpperInvariant())
        {
            case "TENANT":
                scopeKind = WorkflowRouteScopeKind.Tenant;
                return true;
            case "DOMAIN":
                scopeKind = WorkflowRouteScopeKind.Domain;
                return true;
            default:
                scopeKind = WorkflowRouteScopeKind.Unknown;
                return false;
        }
    }
}

public sealed record ConsentWorkflowRoute(
    string TenantId,
    WorkflowRouteScopeKind ScopeKind,
    string ScopeRef,
    ConsentWorkflowMode Mode,
    Guid? ExternalSystemId,
    int MaxDurationDays,
    int RequestTimeoutDays,
    bool RequireResolvableApprover,
    string UpdatedBy,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Role of an external workflow system (issue #1161). <see cref="Workflow"/> systems can be route targets and decide routed requests;
/// <see cref="Observer"/> systems receive every consent event of their tenant and read requests, but never decide or act.
/// <see cref="Unknown"/> is what an unrecognised stored value parses to; such a system receives nothing and reads nothing (fail closed).
/// </summary>
public enum ExternalWorkflowSystemRole
{
    Unknown = -1,
    Workflow = 0,
    Observer = 1
}

public static class ExternalWorkflowSystemRoleMapping
{
    public const string WorkflowSql = "WORKFLOW";
    public const string ObserverSql = "OBSERVER";

    public static string ToSqlString(this ExternalWorkflowSystemRole role) => role switch
    {
        ExternalWorkflowSystemRole.Workflow => WorkflowSql,
        ExternalWorkflowSystemRole.Observer => ObserverSql,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Only known roles can be stored.")
    };

    /// <summary>Parses a stored value; anything but the two known values (case-sensitive, as written) is <see cref="ExternalWorkflowSystemRole.Unknown"/>.</summary>
    public static ExternalWorkflowSystemRole FromSqlString(string? value) => value switch
    {
        WorkflowSql => ExternalWorkflowSystemRole.Workflow,
        ObserverSql => ExternalWorkflowSystemRole.Observer,
        _ => ExternalWorkflowSystemRole.Unknown
    };

    /// <summary>Parses an API value (<c>Workflow</c> or <c>Observer</c>, case-insensitive); returns false for anything else.</summary>
    public static bool TryParseApi(string? value, out ExternalWorkflowSystemRole role)
    {
        var trimmed = value?.Trim();
        if (string.Equals(trimmed, WorkflowApi, StringComparison.OrdinalIgnoreCase))
        {
            role = ExternalWorkflowSystemRole.Workflow;
            return true;
        }

        if (string.Equals(trimmed, ObserverApi, StringComparison.OrdinalIgnoreCase))
        {
            role = ExternalWorkflowSystemRole.Observer;
            return true;
        }

        role = ExternalWorkflowSystemRole.Unknown;
        return false;
    }

    public const string WorkflowApi = "Workflow";
    public const string ObserverApi = "Observer";

    /// <summary>The API value of a role; an unrecognised stored role is reported as <c>Unknown</c>.</summary>
    public static string ToApiString(this ExternalWorkflowSystemRole role) => role switch
    {
        ExternalWorkflowSystemRole.Workflow => WorkflowApi,
        ExternalWorkflowSystemRole.Observer => ObserverApi,
        _ => "Unknown"
    };
}

public sealed record ExternalWorkflowSystem(
    Guid Id,
    string TenantId,
    string Name,
    string ClientId,
    string WebhookUrl,
    string WebhookSecretProtected,
    bool Enabled,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DisabledAt,
    ExternalWorkflowSystemRole Role = ExternalWorkflowSystemRole.Workflow)
{
    /// <summary>True for an enabled observer (issue #1161).</summary>
    public bool IsActiveObserver => Enabled && Role == ExternalWorkflowSystemRole.Observer;

    /// <summary>True for an enabled workflow system; an observer or an unknown role never acts as a workflow system.</summary>
    public bool IsActiveWorkflow => Enabled && Role == ExternalWorkflowSystemRole.Workflow;
}

public sealed record AssertedApprover(
    string Subject,
    string? Email = null,
    string? DisplayName = null,
    string? ResolvedSid = null);

public sealed record ExternalDecisionAttribution(
    Guid ExternalSystemId,
    string ExternalReference,
    string Decision,
    AssertedApprover? Approver,
    DateTimeOffset DecidedAt,
    string? Justification = null,
    string? CorrelationId = null,
    IReadOnlyList<AssertedApprover>? AdditionalApprovers = null)
{
    public IReadOnlyList<AssertedApprover> EffectiveApprovers
    {
        get
        {
            if (AdditionalApprovers is not { Count: > 0 })
            {
                return Approver is not null ? [Approver] : Array.Empty<AssertedApprover>();
            }

            var list = new List<AssertedApprover>(AdditionalApprovers.Count + (Approver is not null ? 1 : 0));
            if (Approver is not null)
            {
                list.Add(Approver);
            }

            list.AddRange(AdditionalApprovers);
            return list;
        }
    }
}
