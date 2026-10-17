namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;

/// <summary>
/// Issue #40: Declarative GitOps policy synchronization manifest and rule models.
/// </summary>
public sealed record DeclarativeConsentManifest(
    string Version,
    IReadOnlyList<DeclarativeConsentPolicy> Policies,
    IReadOnlyList<DeclarativeDatasetCatalog>? Datasets = null);

/// <summary>
/// Issue #111: declarative dataset catalog settings. A null field is "unchanged" (never a reset); an empty <c>Tags</c> list
/// replaces the admin tags with none. <c>Visibility</c> is parsed by name only (<see cref="DatasetTagRules.TryParseVisibility"/>).
/// </summary>
public sealed record DeclarativeDatasetCatalog(
    string Domain,
    string Schema,
    string TableName,
    IReadOnlyList<string>? Tags = null,
    string? Visibility = null);

/// <remarks>
/// WP-SC-16: <c>ValidUntil</c> is an absolute expiry and is mandatory for individual (User / ServicePrincipal) grantees; group and role
/// grantees use <c>ValidityDays</c> (default 365). Re-applying a manifest never extends an existing identical grant.
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
    DateTimeOffset? ValidUntil = null);

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

public sealed record DeclarativeApplyResult(
    int TotalEvaluated,
    int Applied,
    int Unchanged,
    int Revoked,
    bool DryRun,
    IReadOnlyList<string> Details);
