namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;

/// <summary>
/// Access grant definition for a specific principal (user, group, or service principal).
/// </summary>
public sealed record PrincipalAccessGrant(
    string Principal,
    IReadOnlyDictionary<string, string> Columns,
    string? RowFilter = null,
    DateTimeOffset? ValidUntil = null);

/// <summary>
/// Request to generate a two-phase access plan for a target dataset.
/// </summary>
public sealed record AdminPlanAccessRequest(
    string DatasetId,
    IReadOnlyList<PrincipalAccessGrant> Grants,
    string Reason);

/// <summary>
/// Individual diff item in an access plan comparing before and after access levels.
/// </summary>
public sealed record AccessPlanDiffItem(
    string Principal,
    string Column,
    string BeforeState,
    string AfterState,
    bool IsPii);

/// <summary>
/// Result of planning access changes: preview diffs and security warnings without state mutations.
/// </summary>
public sealed record AdminPlanAccessResult(
    string PlanId,
    string DatasetId,
    IReadOnlyList<AccessPlanDiffItem> Diffs,
    IReadOnlyList<string> Warnings,
    DateTimeOffset ExpiresAt);

/// <summary>
/// Request to confirm a pending access plan using a TOTP 2FA code.
/// </summary>
public sealed record AdminConfirmPlanRequest(
    string PlanId,
    string TotpCode);

/// <summary>
/// Result of confirming an access plan: issues an HMAC-signed confirmation token with TTL.
/// </summary>
public sealed record AdminConfirmPlanResult(
    string ConfirmationToken,
    DateTimeOffset ExpiresAt);

/// <summary>
/// Request to apply an approved access plan with a valid confirmation token.
/// </summary>
public sealed record AdminApplyAccessRequest(
    string PlanId,
    string ConfirmationToken);

/// <summary>
/// Result of applying an access plan to ReBAC and metadata stores.
/// </summary>
public sealed record AdminApplyAccessResult(
    bool Success,
    string PlanId,
    int AppliedTuplesCount,
    string? Message = null);


/// <summary>
/// Issue #107 (SEC-RF107-14): request to plan a change of the row filter key-set cache settings. Exactly one of
/// <paramref name="RowFilterId"/> (rule level) or <paramref name="DatasetId"/> (object level) is set. All values are replaced as given:
/// a missing value means "inherit"; an object-level request with all values missing clears the object level.
/// <paramref name="Strategy"/> is accepted only as the strings <c>Exists</c> or <c>KeySetCache</c> (no numeric enum values).
/// </summary>
public sealed record AdminPlanRowFilterEvaluationRequest(
    string? RowFilterId,
    string? DatasetId,
    string? Strategy,
    int? DurationSeconds,
    int? MaxKeys,
    string? Reason = null);

/// <summary>Row filter evaluation settings as shown in plan and apply results (strategy as its name, never a number).</summary>
public sealed record RowFilterEvaluationSettingsDto(string? Strategy, int? DurationSeconds, int? MaxKeys);

/// <summary>Result of planning a row filter evaluation change: before/after settings and warnings, no state mutation.</summary>
public sealed record AdminPlanRowFilterEvaluationResult(
    string PlanId,
    string Level,
    string? RowFilterId,
    string DatasetId,
    RowFilterEvaluationSettingsDto Before,
    RowFilterEvaluationSettingsDto After,
    IReadOnlyList<string> Warnings,
    DateTimeOffset ExpiresAt);

/// <summary>Result of applying a row filter evaluation plan (the epoch of the target table was bumped).</summary>
public sealed record AdminApplyRowFilterEvaluationResult(
    bool Success,
    string PlanId,
    string Level,
    string DatasetId,
    long NewEpoch,
    string? Message = null);

/// <summary>
/// Request to register a new datasource (OpenAPI/Swagger/SQL/etc.).
/// </summary>
public sealed record AdminRegisterDatasourceRequest(
    string Name,
    string Domain,
    string? SpecContent = null,
    string? SpecUrl = null,
    string? BaseUrl = null,
    DatasourceAuthDto? Auth = null,
    bool DryRun = false);

/// <summary>
/// Result of registering a datasource (always starts inactive per SEC M-30).
/// </summary>
public sealed record AdminRegisterDatasourceResult(
    string DatasourceId,
    string Name,
    string Domain,
    string Status,
    bool IsConfigured,
    string? SecretRef = null,
    string? Message = null);

/// <summary>
/// Request to change the lifecycle state of a dataset (active, quarantined, deprecated, inactive).
/// </summary>
public sealed record AdminSetDatasetStateRequest(
    string DatasetId,
    string State,
    string? Reason = null);

/// <summary>
/// Result of changing dataset lifecycle state.
/// </summary>
public sealed record AdminSetDatasetStateResult(
    string DatasetId,
    string PreviousState,
    string NewState,
    bool Success,
    string? Message = null);


/// <summary>
/// Request to resolve a principal by unsharp query.
/// </summary>
public sealed record AdminResolvePrincipalRequest(
    string Query);

/// <summary>
/// Result of resolving principal query.
/// </summary>
public sealed record AdminResolvePrincipalResult(
    IReadOnlyList<PrincipalResolutionItem> Matches);
