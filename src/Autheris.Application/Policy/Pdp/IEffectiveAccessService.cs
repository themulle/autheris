namespace Autheris.Application.Policy.Pdp;

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

/// <summary>Paging and validation constants for effective-access reporting.</summary>
public static class EffectiveAccessDefaults
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;
}

/// <summary>Outcome of an effective-access report request.</summary>
public enum EffectiveAccessOutcome
{
    Ok,
    BadRequest,
    Forbidden,
    NotFound
}

/// <summary>Query parameters for <see cref="IEffectiveAccessService.GetEffectiveAccessAsync"/>.</summary>
public sealed record EffectiveAccessReportQuery(
    string? SubjectSid,
    string? ObjectPattern = null,
    string? Cursor = null,
    int? PageSize = null);

/// <summary>Summary of a single row-scope policy attachment on a grant (names and counts only, never parameter values).</summary>
public sealed record EffectiveRowScopeSummary(
    string Policy,
    int ParameterCount,
    [property: JsonConverter(typeof(JsonStringEnumConverter<UncoveredPolicy>))] UncoveredPolicy Uncovered);

/// <summary>Contributing grant provenance entry on an object in the effective-access report.</summary>
public sealed record EffectiveContributingGrant(
    Guid GrantId,
    string Via,
    IReadOnlyList<EffectiveRowScopeSummary> RowScope);

/// <summary>Hard row-policy reference applying to the subject on an object.</summary>
public sealed record EffectiveHardPolicyRef(
    string Policy,
    string Via);

/// <summary>Structural redundancy finding where <see cref="GrantId"/> is covered by broader <see cref="CoveredBy"/>.</summary>
public sealed record RedundantGrantFlag(
    Guid GrantId,
    Guid CoveredBy);

/// <summary>Effective column access state projected for the report (<c>Clear</c>, <c>Masked</c>, <c>RowDependent</c>, <c>NoAccess</c>).</summary>
public sealed record EffectiveColumnState(
    string ColumnName,
    [property: JsonConverter(typeof(JsonStringEnumConverter<ColumnAccessLevel>))] ColumnAccessLevel Level,
    string State,
    string? MaskType = null);

/// <summary>Per-object effective-access summary with provenance and structural flags.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Effective access report DTO uses canonical Object target name.")]
public sealed record EffectiveAccessObjectEntry(
    string Object,
    bool Allowed,
    IReadOnlyList<EffectiveColumnState> Columns,
    IReadOnlyList<EffectiveContributingGrant> ContributingGrants,
    IReadOnlyList<EffectiveHardPolicyRef> HardPolicies,
    IReadOnlyList<RedundantGrantFlag> RedundantGrants,
    bool EmptyHardIntersection,
    long PolicyEpoch,
    long? CatalogEpoch = null);

/// <summary>Paged effective-access report for one subject within a tenant.</summary>
public sealed record EffectiveAccessReport(
    string SubjectSid,
    IReadOnlyList<EffectiveAccessObjectEntry> Objects,
    string? NextCursor,
    bool Truncated,
    bool SubjectResolved = true);

/// <summary>Service response pairing HTTP-mappable outcome with the redacted report payload.</summary>
public sealed record EffectiveAccessReportResult(
    EffectiveAccessOutcome Outcome,
    EffectiveAccessReport? Report = null);

/// <summary>Builds per-subject effective-access reports with provenance and structural redundancy/intersection flags.</summary>
public interface IEffectiveAccessService
{
    Task<EffectiveAccessReportResult> GetEffectiveAccessAsync(
        SecurityPrincipalContext caller,
        EffectiveAccessReportQuery query,
        CancellationToken ct = default);
}
