namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using Autheris.Domain.Common;

public sealed record CatalogDatasetSummary(
    string DatasetId,
    string Domain,
    string Schema,
    string Table,
    string SourceType,
    string Sensitivity,
    string? Description,
    bool IsActive);

public sealed record CatalogColumnDetail(
    string Name,
    string Type,
    string Sensitivity,
    string MaskingState,
    bool IsPrimaryKey,
    bool IsPiiIndicator);

public sealed record CatalogDatasetDetail(
    string DatasetId,
    string Domain,
    string Schema,
    string Table,
    IReadOnlyList<CatalogColumnDetail> Columns,
    IReadOnlyList<string> PrimaryKeys,
    string Sensitivity,
    bool IsActive);

public sealed record CatalogDatasourceSummary(
    string Id,
    string Name,
    string Domain,
    string Type,
    string? BaseUrl,
    bool IsConfigured,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt = null);

public sealed record PrincipalResolutionItem(
    string Sid,
    string DisplayName,
    string PrincipalType,
    bool ExactMatch,
    IReadOnlyList<string> Groups);

public sealed record DatasourceAuthDto(
    string Type,
    string? Scheme = null,
    string? In = null,
    string? Name = null,
    string? Value = null,
    string? Username = null,
    string? TokenUrl = null,
    string? ClientId = null,
    string? ClientSecret = null,
    string? Scope = null,
    string? SecretRef = null);

public sealed record DatasourceRegistrationRequest(
    string Name,
    string Domain,
    string? SpecContent = null,
    string? SpecUrl = null,
    string? BaseUrl = null,
    DatasourceAuthDto? Auth = null,
    bool DryRun = false);

public sealed record DatasourceRegistrationResult(
    string DatasourceId,
    string Name,
    string Domain,
    bool Success,
    bool DryRun,
    int CreatedDatasetsCount,
    IReadOnlyList<string> CreatedDatasets,
    IReadOnlyList<string> SkippedDatasets,
    IReadOnlyList<string> Warnings,
    bool IsConfigured,
    string? ErrorMessage = null);

public sealed record RequestContext(
    TenantId TenantId,
    Sid SubjectId,
    IReadOnlyCollection<string>? Roles = null,
    string? CorrelationId = null,
    ClaimsPrincipal? User = null)
{
    public static RequestContext FromCaller(CallerSecurityContext caller, string? correlationId = null) =>
        new(caller.Tenant, caller.UserSid, caller.Roles, correlationId);
}
