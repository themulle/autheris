namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

/// <summary>
/// Issue #40: Declarative GitOps policy synchronization manifest and rule models.
/// <c>Name</c> is the manifest scope: a newer proposal for the same tenant and name supersedes a pending one (Amendment 6).
/// </summary>
public sealed record DeclarativeConsentManifest(
    string Version,
    IReadOnlyList<DeclarativeConsentPolicy> Policies,
    IReadOnlyList<DeclarativeDatasetCatalog>? Datasets = null,
    string? TenantId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name = null);

/// <summary>
/// Issue #111: declarative dataset catalog settings. A null field is "unchanged" (never a reset); an empty <c>Tags</c> list
/// replaces the admin tags with none.
/// </summary>
public sealed record DeclarativeDatasetCatalog(
    string Domain,
    string Schema,
    string TableName,
    IReadOnlyList<string>? Tags = null,
    string? TenantId = null);

/// <remarks>
/// <c>ValidUntil</c> is an absolute expiry and is mandatory for every policy (bounded by the declarative maximum); role grantees
/// are refused. <c>ValidityDays</c> is still bound for compatibility but has no effect. Re-applying a manifest never extends an
/// existing identical grant.
/// </remarks>
public sealed record DeclarativeConsentPolicy(
    string Domain,
    string Schema,
    string TableName,
    string GranteeType,
    string GranteeRef,
    string Effect,
    IReadOnlyList<DeclarativeColumnRule>? ColumnRules = null,
    IReadOnlyList<DeclarativeRowFilter>? RowFilters = null,
    int? ValidityDays = null,
    string? Description = null,
    DateTimeOffset? ValidUntil = null,
    string? TenantId = null);

public sealed record DeclarativeColumnRule(
    string ColumnName,
    string AccessLevel,
    IReadOnlyList<string>? ApplicableScopes = null);

public sealed record DeclarativeRowFilter(
    string ColumnName,
    string Operator,
    string ValueType,
    string ValueJson,
    string? ValueSource = "LITERAL",
    string? UserAttribute = null);

public enum DeclarativePruneMode
{
    False = 0,
    True = 1,
    Force = 2
}

public sealed record DeclarativePrunedConsentDto(
    Guid ConsentId,
    string Grantee,
    string Domain,
    string Schema,
    string Table);

public sealed record DeclarativeApplyResult(
    int TotalEvaluated,
    int Applied,
    int Unchanged,
    int Revoked,
    bool DryRun,
    IReadOnlyList<string> Details,
    int Updated = 0,
    int PrunedCount = 0,
    IReadOnlyList<DeclarativePrunedConsentDto>? Pruned = null);
