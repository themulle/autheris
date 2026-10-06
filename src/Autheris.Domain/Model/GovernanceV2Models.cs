namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;
using System.Net;
using Autheris.Domain.Common;

public sealed record SecurityEvaluationContext(
    Sid UserSid,
    IReadOnlyCollection<Sid> GroupSids,
    TenantId Tenant,
    TableIdentifier TargetTable,
    IReadOnlyCollection<string> RequestedColumns,
    IPAddress ClientIp,
    DateTimeOffset Timestamp,
    string? PurposeId,
    IReadOnlyDictionary<string, object?>? Attributes = null,
    // SQL dialect of the target table; correlated row filters are generated with its quoting rules.
    DatabaseDialect? TargetDialect = null
)
{
    public string? Department => GetAttribute("department") ?? GetAttribute("dept");
    public string? Region => GetAttribute("region") ?? GetAttribute("country");
    public string? ClearanceLevel => GetAttribute("clearance") ?? GetAttribute("clearance_level");

    public string? GetAttribute(string key)
    {
        if (Attributes != null && Attributes.TryGetValue(key, out var val) && val != null)
        {
            return val.ToString();
        }
        return null;
    }
}

public enum LineageNodeType
{
    Table = 0,
    Column = 1,
    Dashboard = 2,
    Pipeline = 3,
    ExternalService = 4
}

public sealed record LineageNode(
    string Id,
    string Name,
    LineageNodeType Type,
    IReadOnlyCollection<string> DownstreamNodeIds,
    string? OwnerTeam = null,
    string? OwnerEmail = null
);

public sealed record AffectedEntity(
    string Id,
    string Name,
    LineageNodeType Type,
    string? OwnerTeam,
    string? OwnerEmail, // Null, sofern Aufrufer nicht autorisiert (Zero-Trust Spaltenautorisierung)
    bool CyclicReferenceDetected = false
);

public sealed record ConsentRevocationImpactReport(
    string Severity, // HIGH, MEDIUM, LOW
    int AffectedDownstreamCount,
    IReadOnlyList<AffectedEntity> AffectedEntities,
    bool ContainsCycles
);

public enum ItsmSystemType
{
    ServiceNow = 1,
    Jira = 2
}

public sealed record ItsmTicketReference(
    ItsmSystemType System,
    string TicketId,
    string TicketUrl
);

public enum JustificationCategory
{
    LegitimateAudit = 1,
    IncidentTriage = 2,
    Unjustified = 3,
    SuspiciousExfiltration = 4,
    Unclassified = 5
}

public sealed record JustificationTriageResult(
    JustificationCategory Category,
    double Confidence,
    string RawModelOutput,
    bool AutoGrantEligible,
    TimeSpan? GrantedDuration
);

public enum DataCatalogProviderType
{
    OpenMetadata = 1,
    MicrosoftPurview = 2,
    Collibra = 3,
    Alation = 4
}

public enum DataCatalogSyncMode
{
    Mirror = 1,      // Ingest/Sync catalog into local SQLite governance store
    Reference = 2    // Referencing/federated lookup on-demand
}

public enum DataSensitivityClassification
{
    Normal = 1,
    Internal = 2,
    Confidential = 3,
    Pii = 4,
    GdprArticle9 = 5 // Special category under GDPR Art. 9 (Health, Biometrics, Political, Religious, etc.)
}

public sealed record DownstreamConsumerEntity(
    string Id,
    string Name,
    LineageNodeType Type,
    string? OwnerTeam,
    string? OwnerEmail, // Null unless caller is authorized approver or admin (Zero-Trust)
    int DistanceFromRoot,
    string? UpstreamDependencyId
);

public sealed record RuntimeConsumerSummary(
    string ActorSid,
    int QueryCount,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    string ClientType // "ServicePrincipal", "InteractiveUser", "DownstreamSystem"
);

public sealed record TableConsumersReport(
    string Table,
    string BreakingChangeRisk, // "CRITICAL", "HIGH", "MEDIUM", "LOW"
    int TotalDownstreamCount,
    int ActiveReadersCount,
    DateTimeOffset? LastAccessedAt,
    IReadOnlyList<DownstreamConsumerEntity> DownstreamConsumers,
    IReadOnlyList<RuntimeConsumerSummary> RuntimeConsumers,
    IReadOnlyList<string> RecommendedMitigations
);

public sealed record GdprRecipientAccessRecord(
    string RecipientSid,
    string RecipientCategory, // "InteractiveUser", "ServicePrincipal", "DownstreamSystem"
    string Purpose,
    DateTimeOffset FirstAccess,
    DateTimeOffset LastAccess,
    int TotalQueries,
    IReadOnlyList<string> AccessedColumns,
    string? MaskingRuleApplied
);

public sealed record GdprDisclosureReport(
    string? TargetTable,
    string? SubjectSid,
    DateTimeOffset GeneratedAt,
    int TimeWindowDays,
    int TotalAccessEvents,
    IReadOnlyList<GdprRecipientAccessRecord> DisclosedRecipients,
    IReadOnlyList<string> SensitivityCategories,
    string LegalBasisNotice
);

