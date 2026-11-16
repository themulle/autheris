namespace Autheris.Api.Endpoints;

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Errors;
using Autheris.Api.Extensions;
using Autheris.Api.Security;
using Autheris.Application.Catalog.External;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Options.DataSources;
using Autheris.Domain.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

public static class ExternalCatalogApiEndpoints
{
    private const string OpenApiResourceName = "Autheris.Api.Contracts.autheris-external-v1.openapi.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Lazy<string> CachedOpenApiJson = new(LoadEmbeddedOpenApiJson);

    public static IEndpointRouteBuilder MapExternalCatalogApiEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var catalogGroup = app.MapGroup("/api/v1/catalog")
            .WithIngressTransport(DataSourceTransportFlags.Rest);

        catalogGroup.MapGet("/objects", ListObjectsAsync)
            .RequireAuthorization(GatewayPolicies.CatalogRead)
            .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        catalogGroup.MapGet("/objects/{id}", GetObjectByIdAsync)
            .RequireAuthorization(GatewayPolicies.CatalogRead)
            .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        catalogGroup.MapGet("/changes", GetChangesAsync)
            .RequireAuthorization(GatewayPolicies.CatalogRead)
            .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        var openApiGroup = app.MapGroup("/api/v1/openapi")
            .WithIngressTransport(DataSourceTransportFlags.Rest);

        openApiGroup.MapGet("/external.json", GetExternalOpenApiSpec)
            .WithName("GetExternalCatalogAndConsentOpenApiSpec")
            .RequireAuthorization()
            .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        return app;
    }

    internal static async Task<IResult> ListObjectsAsync(
        HttpContext context,
        ExternalCatalogReader reader,
        string? kind = null,
        string? domain = null,
        string? schema = null,
        string? owner = null,
        string? tag = null,
        string? sensitivity = null,
        bool? pii = null,
        long? changedSince = null,
        string? cursor = null,
        int? pageSize = null,
        CancellationToken ct = default)
    {
        if (!TryResolveTenant(context, out var tenantId, out var denied))
        {
            return denied!;
        }

        var query = new ExternalCatalogListQuery(
            Kind: kind,
            Domain: domain,
            Schema: schema,
            Owner: owner,
            Tag: tag,
            Sensitivity: sensitivity,
            Pii: pii,
            ChangedSince: changedSince,
            Cursor: cursor,
            PageSize: pageSize ?? ExternalCatalogReader.DefaultPageSize);

        var result = await reader.ListObjectsAsync(context.User, tenantId, query, ct).ConfigureAwait(false);
        return ToJsonOrProblemResult(context, result);
    }

    internal static async Task<IResult> GetObjectByIdAsync(
        string id,
        HttpContext context,
        ExternalCatalogReader reader,
        CancellationToken ct = default)
    {
        if (!TryResolveTenant(context, out var tenantId, out var denied))
        {
            return denied!;
        }

        var result = await reader.GetObjectAsync(context.User, tenantId, id, ct).ConfigureAwait(false);
        return ToJsonOrProblemResult(context, result);
    }

    internal static async Task<IResult> GetChangesAsync(
        HttpContext context,
        ExternalCatalogReader reader,
        long since = 0,
        int limit = 100,
        CancellationToken ct = default)
    {
        if (!TryResolveTenant(context, out var tenantId, out var denied))
        {
            return denied!;
        }

        var result = await reader.GetChangesAsync(context.User, tenantId, since, limit, ct).ConfigureAwait(false);
        return ToJsonOrProblemResult(context, result);
    }

    internal static IResult GetExternalOpenApiSpec(HttpContext context) =>
        RespondWithETag(context, CachedOpenApiJson.Value);

    private static bool TryResolveTenant(HttpContext context, out TenantId tenantId, out IResult? error)
    {
        var claim = TenantClaimReader.Read(context.User);
        if (claim.Kind == ClaimTenantKind.Valid)
        {
            tenantId = claim.Tenant;
            error = null;
            return true;
        }

        if (claim.Kind == ClaimTenantKind.Invalid)
        {
            tenantId = default;
            error = ApiProblems.Forbidden(ApiErrorCodes.TenantClaimInvalid, "The caller tenant claim is invalid.", context);
            return false;
        }

        if (context.GetSecurityContext()?.ResolvedTenant is { Value: { Length: > 0 } } resolved)
        {
            tenantId = resolved;
            error = null;
            return true;
        }

        tenantId = default;
        error = ApiProblems.Forbidden(ApiErrorCodes.TenantUnresolved, "A valid tenant context is required for external catalog access.", context);
        return false;
    }

    private static IResult ToJsonOrProblemResult<T>(HttpContext context, ExternalCatalogResult<T> result)
    {
        if (!result.IsSuccess)
        {
            var err = result.Error!;
            return ApiProblems.Problem(err.StatusCode, err.Code, err.Detail, context);
        }

        var json = JsonSerializer.Serialize(result.Value!, JsonOptions);
        return RespondWithETag(context, json);
    }

    private static IResult RespondWithETag(HttpContext context, string json)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
        var eTag = $"\"{hash}\"";
        context.Response.Headers.ETag = eTag;
        context.Response.Headers.CacheControl = "private, no-cache";

        if (IfNoneMatchMatches(context.Request.Headers.IfNoneMatch.ToString(), eTag))
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        return Results.Content(json, "application/json; charset=utf-8");
    }

    private static bool IfNoneMatchMatches(string? headerValue, string eTag)
    {
        if (string.IsNullOrWhiteSpace(headerValue))
        {
            return false;
        }

        foreach (var rawCandidate in headerValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.Equals(rawCandidate, "*", StringComparison.Ordinal))
            {
                return true;
            }

            var candidate = rawCandidate.StartsWith("W/", StringComparison.OrdinalIgnoreCase)
                ? rawCandidate[2..].Trim()
                : rawCandidate;
            if (string.Equals(candidate, eTag, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string LoadEmbeddedOpenApiJson()
    {
        var assembly = typeof(ExternalCatalogApiEndpoints).Assembly;
        using var stream = assembly.GetManifestResourceStream(OpenApiResourceName)
            ?? throw new InvalidOperationException($"Embedded OpenAPI contract '{OpenApiResourceName}' not found.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
