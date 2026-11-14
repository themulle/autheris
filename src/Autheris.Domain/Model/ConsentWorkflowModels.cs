namespace Autheris.Domain.Model;

public enum ConsentWorkflowMode
{
    Internal = 0,
    External = 1
}

public enum ConsentRequestKind
{
    Consent = 0,
    ProfileAssignment = 1
}

public enum ConsentGrantSource
{
    ConsentRequest = 0,
    AccessProfile = 1,
    Owner = 2,
    Bulk = 3,
    Declarative = 4,
    AccessPlan = 5
}

public enum WorkflowRouteScopeKind
{
    Tenant = 0,
    DataOwner = 1,
    DataSource = 2
}

public static class ConsentWorkflowSqlMapping
{
    public static string ToSqlString(this ConsentGrantSource source) => source switch
    {
        ConsentGrantSource.ConsentRequest => "CONSENT_REQUEST",
        ConsentGrantSource.AccessProfile => "ACCESS_PROFILE",
        ConsentGrantSource.Owner => "OWNER",
        ConsentGrantSource.Bulk => "BULK",
        ConsentGrantSource.Declarative => "DECLARATIVE",
        ConsentGrantSource.AccessPlan => "ACCESS_PLAN",
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
            case "OWNER":
                source = ConsentGrantSource.Owner;
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
        ConsentRequestKind.Consent => "CONSENT",
        ConsentRequestKind.ProfileAssignment => "PROFILE_ASSIGNMENT",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported consent request kind.")
    };

    public static bool TryParseRequestKind(string? value, out ConsentRequestKind kind)
    {
        switch (value?.Trim().ToUpperInvariant())
        {
            case "CONSENT":
                kind = ConsentRequestKind.Consent;
                return true;
            case "PROFILE_ASSIGNMENT":
                kind = ConsentRequestKind.ProfileAssignment;
                return true;
            default:
                kind = ConsentRequestKind.Consent;
                return false;
        }
    }

    public static ConsentRequestKind ParseRequestKindOrDefault(
        string? value,
        ConsentRequestKind fallback = ConsentRequestKind.Consent) =>
        TryParseRequestKind(value, out var parsed) ? parsed : fallback;

    public static string ToSqlString(this WorkflowRouteScopeKind scopeKind) => scopeKind switch
    {
        WorkflowRouteScopeKind.Tenant => "TENANT",
        WorkflowRouteScopeKind.DataOwner => "DATA_OWNER",
        WorkflowRouteScopeKind.DataSource => "DATA_SOURCE",
        _ => throw new ArgumentOutOfRangeException(nameof(scopeKind), scopeKind, "Unsupported workflow route scope kind.")
    };

    public static bool TryParseRouteScopeKind(string? value, out WorkflowRouteScopeKind scopeKind)
    {
        switch (value?.Trim().ToUpperInvariant())
        {
            case "TENANT":
                scopeKind = WorkflowRouteScopeKind.Tenant;
                return true;
            case "DATA_OWNER":
                scopeKind = WorkflowRouteScopeKind.DataOwner;
                return true;
            case "DATA_SOURCE":
                scopeKind = WorkflowRouteScopeKind.DataSource;
                return true;
            default:
                scopeKind = WorkflowRouteScopeKind.Tenant;
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
    int ExpiryNoticeLeadHours,
    bool RequireResolvableApprover,
    string UpdatedBy,
    DateTimeOffset UpdatedAt);

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
    DateTimeOffset? DisabledAt);

public sealed record AssertedApprover(
    string Subject,
    string? Email = null,
    string? DisplayName = null,
    string? ResolvedSid = null);

public sealed record ExternalDecisionAttribution(
    Guid ExternalSystemId,
    string ExternalReference,
    string Decision,
    AssertedApprover Approver,
    DateTimeOffset DecidedAt,
    string? Justification = null,
    string? CorrelationId = null);
