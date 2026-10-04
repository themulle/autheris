namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;
using Autheris.Domain.Common;

public sealed record AccessDenialEvent(
    Guid EventId,
    DateTimeOffset Timestamp,
    TenantId TenantId,
    Sid RequesterSid,
    IReadOnlySet<string> Roles,
    TableIdentifier TargetTable,
    IReadOnlyList<string> RequestedColumns,
    string DenialReason,
    string? IntendedPurpose = null
);

public sealed record LeastPrivilegeProposal(
    TableIdentifier TargetTable,
    TenantId TenantId,
    Sid RequesterSid,
    IReadOnlyList<string> MinimalColumns,
    IReadOnlyList<ConsentRowFilter> MinimalRowFilters,
    TimeSpan SuggestedValidityDuration,
    float ConfidenceScore,
    string BusinessJustification
);

public sealed class PolicyRecommendationOptions
{
    public TimeSpan MaxValidityDuration { get; set; } = TimeSpan.FromDays(14);
    public int MaxQueueCapacity { get; set; } = 5000;
    public bool AutoSubmitDrafts { get; set; } = true;
    public bool AllowWildcardRecommendations { get; set; } = false;
}
