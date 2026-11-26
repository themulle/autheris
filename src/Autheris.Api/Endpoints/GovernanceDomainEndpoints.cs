using System.Globalization;
using Autheris.Api.Errors;
using Autheris.Api.Extensions;
using Autheris.Api.Security;
using Autheris.Application.Governance.Domains;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options.DataSources;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Autheris.Api.Endpoints;

/// <summary>Governance domain, owner-group, table-assignment, and object protection endpoints.</summary>
public static class GovernanceDomainEndpoints
{
    public const string DomainsRoute = "/api/v1/governance/domains";
    public const string DomainByIdRoute = "/api/v1/governance/domains/{id:guid}";
    public const string DomainOwnerGroupRoute = "/api/v1/governance/domains/{id:guid}/owner-group";
    public const string DomainTablesRoute = "/api/v1/governance/domains/{id:guid}/tables";
    public const string ObjectProtectionRoute = "/api/v1/governance/objects/{datasetId}/protection";
    public const string ColumnProtectionRoute = "/api/v1/governance/objects/{datasetId}/columns/{column}/protection";
    public const string ImportSamplesRoute = "/api/v1/governance/objects/{datasetId}/import-samples";

    public static IEndpointRouteBuilder MapGovernanceDomainEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(DomainsRoute, ListDomainsAsync)
            .RequireAuthorization(GatewayPolicies.GovernanceAdmin)
            .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead)
            .WithIngressTransport(DataSourceTransportFlags.Rest)
            .WithTags("Governance");

        app.MapPost(DomainsRoute, CreateDomainAsync)
            .RequireAuthorization(GatewayPolicies.AuthenticatedCaller)
            .WithAudit(AuditLevel.Full, AuditEventTypes.GovernanceDomainChanged)
            .WithIngressTransport(DataSourceTransportFlags.Rest)
            .WithTags("Governance");

        app.MapGet(DomainByIdRoute, GetDomainAsync)
            .RequireAuthorization(GatewayPolicies.GovernanceAdmin)
            .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead)
            .WithIngressTransport(DataSourceTransportFlags.Rest)
            .WithTags("Governance");

        app.MapPut(DomainByIdRoute, UpdateDomainAsync)
            .RequireAuthorization(GatewayPolicies.AuthenticatedCaller)
            .WithAudit(AuditLevel.Full, AuditEventTypes.GovernanceDomainChanged)
            .WithIngressTransport(DataSourceTransportFlags.Rest)
            .WithTags("Governance");

        app.MapDelete(DomainByIdRoute, DeleteDomainAsync)
            .RequireAuthorization(GatewayPolicies.AuthenticatedCaller)
            .WithAudit(AuditLevel.Full, AuditEventTypes.GovernanceDomainChanged)
            .WithIngressTransport(DataSourceTransportFlags.Rest)
            .WithTags("Governance");

        app.MapPut(DomainOwnerGroupRoute, AssignOwnerGroupAsync)
            .RequireAuthorization(GatewayPolicies.AuthenticatedCaller)
            .WithAudit(AuditLevel.Full, AuditEventTypes.OwnerGroupAssigned)
            .WithIngressTransport(DataSourceTransportFlags.Rest)
            .WithTags("Governance");

        app.MapPut(DomainTablesRoute, AssignTablesAsync)
            .RequireAuthorization(GatewayPolicies.AuthenticatedCaller)
            .WithAudit(AuditLevel.Full, AuditEventTypes.GovernanceDomainChanged)
            .WithIngressTransport(DataSourceTransportFlags.Rest)
            .WithTags("Governance");

        app.MapPut(ObjectProtectionRoute, SetTableProtectionAsync)
            .RequireAuthorization(GatewayPolicies.AuthenticatedCaller)
            .WithAudit(AuditLevel.Full, AuditEventTypes.ProtectionClassChanged)
            .WithIngressTransport(DataSourceTransportFlags.Rest)
            .WithTags("Governance");

        app.MapPut(ColumnProtectionRoute, SetColumnProtectionAsync)
            .RequireAuthorization(GatewayPolicies.AuthenticatedCaller)
            .WithAudit(AuditLevel.Full, AuditEventTypes.ProtectionClassChanged)
            .WithIngressTransport(DataSourceTransportFlags.Rest)
            .WithTags("Governance");

        app.MapDelete(ImportSamplesRoute, PurgeImportSamplesAsync)
            .RequireAuthorization(GatewayPolicies.AuthenticatedCaller)
            .WithAudit(AuditLevel.Full, AuditEventTypes.ImportSamplesPurged)
            .WithIngressTransport(DataSourceTransportFlags.Rest)
            .WithTags("Governance");

        return app;
    }

    internal static async Task<IResult> ListDomainsAsync(
        HttpContext http,
        int? limit,
        int? offset,
        [FromServices] GovernanceDomainService service,
        CancellationToken ct)
    {
        var sec = http.GetSecurityContext();
        if (!sec.IsAuthenticated || sec.TenantClaimInvalid)
        {
            return Results.Unauthorized();
        }

        var result = await service.ListDomainsAsync(sec, sec.TenantId, limit, offset, ct).ConfigureAwait(false);
        return ToResult(result, http, list => Results.Ok(new { items = list.Select(ToDomainDto).ToList() }));
    }

    internal static async Task<IResult> GetDomainAsync(
        HttpContext http,
        Guid id,
        [FromServices] GovernanceDomainService service,
        CancellationToken ct)
    {
        var sec = http.GetSecurityContext();
        if (!sec.IsAuthenticated || sec.TenantClaimInvalid)
        {
            return Results.Unauthorized();
        }

        var result = await service.GetDomainAsync(sec, sec.TenantId, id, ct).ConfigureAwait(false);
        if (result.Status is not (DomainServiceStatus.Ok or DomainServiceStatus.Created or DomainServiceStatus.Deleted) || result.Value is null)
        {
            return ToResult(result, http, _ => Results.Ok());
        }

        var tablesResult = await service.ListDomainTablesAsync(sec, sec.TenantId, id, ct: ct).ConfigureAwait(false);
        var (tables, truncated) = tablesResult.Status == DomainServiceStatus.Ok
            ? tablesResult.Value
            : ((IReadOnlyList<string>)Array.Empty<string>(), false);

        var d = result.Value;
        http.Response.Headers.ETag = FormatETag(d.Version);
        return Results.Ok(ToDomainDetailDto(d, tables, truncated));
    }

    internal static async Task<IResult> CreateDomainAsync(
        HttpContext http,
        [FromBody] CreateDomainRequest? request,
        [FromServices] GovernanceDomainService service,
        CancellationToken ct)
    {
        var sec = http.GetSecurityContext();
        if (!sec.IsAuthenticated || sec.TenantClaimInvalid)
        {
            return Results.Unauthorized();
        }

        if (request is null)
        {
            return ApiProblems.Unprocessable("INVALID_DOMAIN", "Invalid governance domain request.", http);
        }

        if (!TryParseOptionalClass(request.DefaultClass, out var defaultClass, out var err)
            || !TryParseOptionalVisibility(request.Visibility, out var visibility, out err))
        {
            return ApiProblems.Unprocessable("INVALID_DOMAIN", err ?? "Invalid governance domain request.", http);
        }

        var result = await service.CreateDomainAsync(
            sec,
            sec.TenantId,
            new CreateGovernanceDomainInput(request.Name, request.Description, request.Contact, request.OwnerGroupSid, defaultClass, visibility),
            ct).ConfigureAwait(false);

        return ToResult(result, http, d =>
        {
            http.Response.Headers.ETag = FormatETag(d.Version);
            return Results.Created($"{DomainsRoute}/{d.Id:D}", ToDomainDto(d));
        });
    }

    internal static async Task<IResult> UpdateDomainAsync(
        HttpContext http,
        Guid id,
        [FromBody] UpdateDomainRequest? request,
        [FromServices] GovernanceDomainService service,
        CancellationToken ct)
    {
        var sec = http.GetSecurityContext();
        if (!sec.IsAuthenticated || sec.TenantClaimInvalid)
        {
            return Results.Unauthorized();
        }

        if (!AccessPolicyEndpoints.TryParseIfMatchVersion(http, allowZero: false, out var expectedVersion, out var ifMatchErr))
        {
            return ifMatchErr!;
        }

        if (request is null)
        {
            return ApiProblems.Unprocessable("INVALID_DOMAIN", "Invalid governance domain request.", http);
        }

        if (!TryParseOptionalClass(request.DefaultClass, out var defaultClass, out var err)
            || !TryParseOptionalVisibility(request.Visibility, out var visibility, out err))
        {
            return ApiProblems.Unprocessable("INVALID_DOMAIN", err ?? "Invalid governance domain request.", http);
        }

        var result = await service.UpdateDomainAsync(
            sec,
            sec.TenantId,
            id,
            expectedVersion,
            new UpdateGovernanceDomainInput(request.Name, request.Description, request.Contact, defaultClass, visibility),
            ct).ConfigureAwait(false);

        return ToResult(result, http, d =>
        {
            http.Response.Headers.ETag = FormatETag(d.Version);
            return Results.Ok(ToDomainDto(d));
        });
    }

    internal static async Task<IResult> DeleteDomainAsync(
        HttpContext http,
        Guid id,
        [FromServices] GovernanceDomainService service,
        CancellationToken ct)
    {
        var sec = http.GetSecurityContext();
        if (!sec.IsAuthenticated || sec.TenantClaimInvalid)
        {
            return Results.Unauthorized();
        }

        if (!AccessPolicyEndpoints.TryParseIfMatchVersion(http, allowZero: false, out var expectedVersion, out var ifMatchErr))
        {
            return ifMatchErr!;
        }

        var result = await service.DeleteDomainAsync(sec, sec.TenantId, id, expectedVersion, ct).ConfigureAwait(false);
        return ToResult(result, http, _ => Results.NoContent());
    }

    internal static async Task<IResult> AssignOwnerGroupAsync(
        HttpContext http,
        Guid id,
        [FromBody] AssignOwnerGroupRequest? request,
        [FromServices] GovernanceDomainService service,
        CancellationToken ct)
    {
        var sec = http.GetSecurityContext();
        if (!sec.IsAuthenticated || sec.TenantClaimInvalid)
        {
            return Results.Unauthorized();
        }

        int? expectedVersion = null;
        if (http.Request.Headers.ContainsKey("If-Match"))
        {
            if (!AccessPolicyEndpoints.TryParseIfMatchVersion(http, allowZero: false, out var parsedVersion, out var ifMatchErr))
            {
                return ifMatchErr!;
            }

            expectedVersion = parsedVersion;
        }

        var result = await service.AssignOwnerGroupAsync(sec, sec.TenantId, id, request?.OwnerGroupSid, expectedVersion, ct).ConfigureAwait(false);
        return ToResult(result, http, d =>
        {
            http.Response.Headers.ETag = FormatETag(d.Version);
            return Results.Ok(ToDomainDto(d));
        });
    }

    internal static async Task<IResult> AssignTablesAsync(
        HttpContext http,
        Guid id,
        [FromBody] AssignTablesRequest? request,
        [FromServices] GovernanceDomainService service,
        CancellationToken ct)
    {
        var sec = http.GetSecurityContext();
        if (!sec.IsAuthenticated || sec.TenantClaimInvalid)
        {
            return Results.Unauthorized();
        }

        var result = await service.AssignTablesAsync(sec, sec.TenantId, id, request?.TableIds, request?.Datasets, ct).ConfigureAwait(false);
        return ToResult(result, http, count => Results.Ok(new { domainId = id, assignedCount = count }));
    }

    internal static async Task<IResult> SetTableProtectionAsync(
        HttpContext http,
        string datasetId,
        [FromBody] SetProtectionRequest? request,
        [FromServices] GovernanceDomainService service,
        CancellationToken ct)
    {
        var sec = http.GetSecurityContext();
        if (!sec.IsAuthenticated || sec.TenantClaimInvalid)
        {
            return Results.Unauthorized();
        }

        if (request is null)
        {
            return ApiProblems.Unprocessable("INVALID_PROTECTION_CLASS", "Protection payload is required.", http);
        }

        if (!TryParseOptionalClass(request.ProtectionClass, out var parsedClass, out var err))
        {
            return ApiProblems.Unprocessable("INVALID_PROTECTION_CLASS", err ?? "Protection payload is required.", http);
        }

        var decodedDataset = Uri.UnescapeDataString(datasetId);
        var result = await service.SetTableProtectionAsync(
            sec, sec.TenantId, decodedDataset, parsedClass, request.MaskCategory, ct).ConfigureAwait(false);

        return ToResult(result, http, o => Results.Ok(ToProtectionDto(o)));
    }

    internal static async Task<IResult> SetColumnProtectionAsync(
        HttpContext http,
        string datasetId,
        string column,
        [FromBody] SetProtectionRequest? request,
        [FromServices] GovernanceDomainService service,
        CancellationToken ct)
    {
        var sec = http.GetSecurityContext();
        if (!sec.IsAuthenticated || sec.TenantClaimInvalid)
        {
            return Results.Unauthorized();
        }

        if (request is null)
        {
            return ApiProblems.Unprocessable("INVALID_PROTECTION_CLASS", "Protection payload is required.", http);
        }

        if (!TryParseOptionalClass(request.ProtectionClass, out var parsedClass, out var err))
        {
            return ApiProblems.Unprocessable("INVALID_PROTECTION_CLASS", err ?? "Protection payload is required.", http);
        }

        var decodedDataset = Uri.UnescapeDataString(datasetId);
        var decodedColumn = Uri.UnescapeDataString(column);
        var result = await service.SetColumnProtectionAsync(
            sec, sec.TenantId, decodedDataset, decodedColumn, parsedClass, request.MaskCategory, ct).ConfigureAwait(false);

        return ToResult(result, http, o => Results.Ok(ToProtectionDto(o)));
    }

    internal static async Task<IResult> PurgeImportSamplesAsync(
        HttpContext http,
        string datasetId,
        [FromServices] GovernanceDomainService service,
        [FromServices] Autheris.Application.Catalog.Discovery.IDiscoveryArtifactStore artifactStore,
        CancellationToken ct)
    {
        var sec = http.GetSecurityContext();
        if (!sec.IsAuthenticated || sec.TenantClaimInvalid)
        {
            return Results.Unauthorized();
        }

        var decodedDataset = Uri.UnescapeDataString(datasetId);
        var result = await service.PurgeImportSamplesAsync(sec, sec.TenantId, decodedDataset, artifactStore, ct).ConfigureAwait(false);
        return ToResult(result, http, _ => Results.NoContent());
    }

    private static IResult ToResult<T>(DomainServiceResult<T> result, HttpContext http, Func<T, IResult> onSuccess) =>
        result.Status switch
        {
            DomainServiceStatus.Ok or DomainServiceStatus.Created or DomainServiceStatus.Deleted => onSuccess(result.Value!),
            DomainServiceStatus.Forbidden => ApiProblems.Forbidden(result.ErrorCode ?? ApiErrorCodes.AccessDenied, result.ErrorMessage ?? "Forbidden.", http),
            DomainServiceStatus.NotFound => ApiProblems.NotFound(result.ErrorCode ?? ApiErrorCodes.ResourceNotFound, result.ErrorMessage ?? "Not found.", http),
            DomainServiceStatus.Conflict => ApiProblems.Conflict(result.ErrorCode ?? ApiErrorCodes.Conflict, result.ErrorMessage ?? "Conflict.", http),
            DomainServiceStatus.VersionMismatch => ApiProblems.Problem(StatusCodes.Status412PreconditionFailed, ApiErrorCodes.VersionMismatch, result.ErrorMessage ?? "Version mismatch.", http, title: "Precondition Failed"),
            _ => ApiProblems.Unprocessable(result.ErrorCode ?? ApiErrorCodes.UnprocessableEntity, result.ErrorMessage ?? "Validation failed.", http)
        };

    private static string FormatETag(int version) => string.Create(CultureInfo.InvariantCulture, $"\"{Math.Max(1, version)}\"");

    private static bool TryParseOptionalClass(string? raw, out ProtectionClass? parsed, out string? error)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            parsed = null;
            error = null;
            return true;
        }

        if (ProtectionClassExtensions.TryParse(raw, out var value))
        {
            parsed = value;
            error = null;
            return true;
        }

        parsed = null;
        error = $"Unknown protection class '{raw}'.";
        return false;
    }

    private static bool TryParseOptionalVisibility(string? raw, out CatalogVisibilityMode? parsed, out string? error)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            parsed = null;
            error = null;
            return true;
        }

        var trimmed = raw.Trim();
        if (string.Equals(trimmed, "OPEN", StringComparison.OrdinalIgnoreCase))
        {
            parsed = CatalogVisibilityMode.Open;
            error = null;
            return true;
        }

        if (string.Equals(trimmed, "ON_REQUEST", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "OnRequest", StringComparison.OrdinalIgnoreCase))
        {
            parsed = CatalogVisibilityMode.OnRequest;
            error = null;
            return true;
        }

        parsed = null;
        error = $"Unknown visibility mode '{raw}'.";
        return false;
    }

    private static object ToDomainDto(GovernanceDomain d) => new
    {
        id = d.Id,
        tenantId = d.Tenant.Value,
        name = d.Name,
        description = d.Description,
        contact = d.Contact,
        ownerGroupSid = d.OwnerGroupSid?.Value,
        defaultClass = d.DefaultClass?.ToCanonicalName(),
        visibility = d.Visibility.ToCanonicalName(),
        isSystemUnassigned = d.IsSystemUnassigned,
        version = d.Version,
        createdAt = d.CreatedAt,
        updatedAt = d.UpdatedAt
    };

    private static object ToDomainDetailDto(GovernanceDomain d, IReadOnlyList<string> tables, bool tablesTruncated) => new
    {
        id = d.Id,
        tenantId = d.Tenant.Value,
        name = d.Name,
        description = d.Description,
        contact = d.Contact,
        ownerGroupSid = d.OwnerGroupSid?.Value,
        defaultClass = d.DefaultClass?.ToCanonicalName(),
        visibility = d.Visibility.ToCanonicalName(),
        isSystemUnassigned = d.IsSystemUnassigned,
        version = d.Version,
        createdAt = d.CreatedAt,
        updatedAt = d.UpdatedAt,
        tables,
        tablesTruncated
    };

    private static object ToProtectionDto(ProtectionWriteOutcome o) => new
    {
        datasetId = o.DatasetId,
        tableId = o.TableId,
        column = o.Column,
        container = new
        {
            kind = o.Effective.Container.Kind.ToString(),
            id = o.Effective.Container.Id
        },
        ownerGroupSid = o.Effective.OwnerGroupSid?.Value,
        declaredTableClass = o.Effective.DeclaredTableClass.ToCanonicalName(),
        tableClass = o.Effective.TableClass.ToCanonicalName(),
        tableMaskCategory = o.Effective.TableMaskCategory,
        visibility = o.Effective.Visibility.ToCanonicalName(),
        coveringGrantsCount = o.CoveringGrantsCount,
        looserThanContainer = o.LooserThanContainer,
        columns = o.Effective.Columns.ToDictionary(
            kvp => kvp.Key,
            kvp => new
            {
                effectiveClass = kvp.Value.EffectiveClass.ToCanonicalName(),
                effectiveMaskCategory = kvp.Value.EffectiveMaskCategory
            },
            StringComparer.OrdinalIgnoreCase)
    };

    public sealed record CreateDomainRequest(
        string Name,
        string? Description = null,
        string? Contact = null,
        string? OwnerGroupSid = null,
        string? DefaultClass = null,
        string? Visibility = null);

    public sealed record UpdateDomainRequest(
        string Name,
        string? Description = null,
        string? Contact = null,
        string? DefaultClass = null,
        string? Visibility = null);

    public sealed record AssignOwnerGroupRequest(string? OwnerGroupSid = null);
    public sealed record AssignTablesRequest(IReadOnlyList<Guid>? TableIds = null, IReadOnlyList<string>? Datasets = null);
    public sealed record SetProtectionRequest(string? ProtectionClass = null, string? MaskCategory = null);
}
