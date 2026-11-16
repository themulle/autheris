namespace Autheris.Api.Endpoints;

using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using Autheris.Api.Errors;
using Autheris.Api.Extensions;
using Autheris.Api.Security;
using Autheris.Application.Audit;
using Autheris.Application.Usage;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Options.DataSources;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

public static partial class UsageEndpoints
{
    public sealed record CreateTariffVersionHttpRequest(
        DateOnly ValidFrom,
        decimal BasePerQuery = 0m,
        decimal PerRow = 0m,
        decimal PerMib = 0m,
        decimal PerComputeSecond = 0m,
        decimal PerAnalysisCost = 0m,
        decimal SourceCostFactor = 0m,
        string? SourceCostUnit = null,
        string UnitLabel = "credits",
        short ComputeBasis = 0,
        string Reason = "",
        string? TenantId = null,
        DateOnly? ValidUntil = null);

    private static void MapTariffEndpoints(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/usage/tariffs", (
            HttpContext httpContext,
            [FromServices] IUsageTariffService tariffService,
            string? dataSource = null,
            string? at = null,
            string? tenant = null,
            CancellationToken ct = default) =>
            HandleGetTariffsAsync(httpContext, tariffService, dataSource, at, tenant, ct))
        .RequireAuthorization(GatewayPolicies.UsageAdmin)
        .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead)
        .WithIngressTransport(DataSourceTransportFlags.Rest)
        .WithTags("Usage");

        app.MapGet("/api/v1/usage/tariffs/{dataSource}/versions", (
            string dataSource,
            HttpContext httpContext,
            [FromServices] IUsageTariffService tariffService,
            string? tenant = null,
            CancellationToken ct = default) =>
            HandleGetTariffVersionsAsync(dataSource, httpContext, tariffService, tenant, ct))
        .RequireAuthorization(GatewayPolicies.UsageAdmin)
        .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead)
        .WithIngressTransport(DataSourceTransportFlags.Rest)
        .WithTags("Usage");

        app.MapPost("/api/v1/usage/tariffs/{dataSource}/versions", (
            string dataSource,
            CreateTariffVersionHttpRequest? request,
            HttpContext httpContext,
            [FromServices] IUsageTariffService tariffService,
            string? tenant = null,
            CancellationToken ct = default) =>
            HandleAddTariffVersionAsync(dataSource, request, httpContext, tariffService, tenant, ct))
        .RequireAuthorization(GatewayPolicies.UsageAdmin)
        .WithAudit(AuditLevel.Full, AuditEventTypes.UsageTariffVersionAdded)
        .WithIngressTransport(DataSourceTransportFlags.Rest)
        .WithTags("Usage");
    }

    internal static async Task<IResult> HandleGetTariffsAsync(
        HttpContext httpContext,
        IUsageTariffService tariffService,
        string? dataSource = null,
        string? at = null,
        string? tenant = null,
        CancellationToken ct = default)
    {
        var callerTenant = EndpointSecurity.GetRequestTenant(httpContext);
        var targetTenant = callerTenant;
        bool filterWildcardOnly = false;

        if (!string.IsNullOrWhiteSpace(tenant))
        {
            string trimmedTenant = tenant.Trim();
            bool isOther = !string.Equals(trimmedTenant, callerTenant.Value, StringComparison.OrdinalIgnoreCase);
            if (isOther && !EndpointSecurity.IsCanonicalClusterAdmin(httpContext.User))
            {
                return ApiProblems.Forbidden(
                    ApiErrorCodes.CrossTenantAccessForbidden,
                    "Only a canonical ClusterAdmin may inspect another tenant's tariffs.",
                    httpContext);
            }

            if (string.Equals(trimmedTenant, "*", StringComparison.Ordinal))
            {
                targetTenant = TenantId.Platform;
                filterWildcardOnly = true;
            }
            else if (TenantId.TryParse(trimmedTenant, out var parsedTenant))
            {
                targetTenant = parsedTenant;
            }
            else
            {
                return ApiProblems.BadRequest(ApiErrorCodes.InvalidTenantId, "Invalid tenant identifier.", httpContext);
            }
        }

        var timeProvider = httpContext.RequestServices?.GetService<TimeProvider>() ?? TimeProvider.System;
        DateOnly atDate;
        if (string.IsNullOrWhiteSpace(at))
        {
            atDate = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        }
        else if (!DateOnly.TryParseExact(at.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out atDate))
        {
            return ApiProblems.BadRequest(ApiErrorCodes.InvalidQuery, "Query parameter 'at' must be a UTC date in yyyy-MM-dd format.", httpContext);
        }

        var tariffs = await tariffService.GetEffectiveTariffsAsync(targetTenant, atDate, dataSource, ct).ConfigureAwait(false);
        if (filterWildcardOnly)
        {
            tariffs = tariffs.Where(t => string.Equals(t.TenantId, "*", StringComparison.Ordinal)).ToList();
        }

        httpContext.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new { effectiveAt = atDate, tariffs });
    }

    internal static async Task<IResult> HandleGetTariffVersionsAsync(
        string dataSource,
        HttpContext httpContext,
        IUsageTariffService tariffService,
        string? tenant = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(dataSource))
        {
            return ApiProblems.BadRequest(ApiErrorCodes.InvalidQuery, "DataSource is required.", httpContext);
        }

        var callerTenant = EndpointSecurity.GetRequestTenant(httpContext);
        string targetTenant = string.IsNullOrWhiteSpace(tenant) ? callerTenant.Value : tenant.Trim();
        bool isOtherOrWildcard = string.Equals(targetTenant, "*", StringComparison.Ordinal)
            || !string.Equals(targetTenant, callerTenant.Value, StringComparison.OrdinalIgnoreCase);

        if (isOtherOrWildcard && !EndpointSecurity.IsCanonicalClusterAdmin(httpContext.User))
        {
            return ApiProblems.Forbidden(
                ApiErrorCodes.CrossTenantAccessForbidden,
                "Only a canonical ClusterAdmin may inspect another tenant's or wildcard tariff versions.",
                httpContext);
        }

        if (!string.Equals(targetTenant, "*", StringComparison.Ordinal) && !TenantId.TryParse(targetTenant, out _))
        {
            return ApiProblems.BadRequest(ApiErrorCodes.InvalidTenantId, "Invalid tenant identifier.", httpContext);
        }

        var versions = await tariffService.GetVersionsAsync(targetTenant, dataSource, ct).ConfigureAwait(false);
        httpContext.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new { dataSource = dataSource.Trim(), versions });
    }

    internal static async Task<IResult> HandleAddTariffVersionAsync(
        string dataSource,
        CreateTariffVersionHttpRequest? request,
        HttpContext httpContext,
        IUsageTariffService tariffService,
        string? tenant = null,
        CancellationToken ct = default)
    {
        if (request is null)
        {
            return ApiProblems.BadRequest(ApiErrorCodes.InvalidJson, "A JSON tariff version payload is required.", httpContext);
        }

        string? callerIdentity = EndpointSecurity.GetCallerIdentity(httpContext);
        if (string.IsNullOrWhiteSpace(callerIdentity))
        {
            return ApiProblems.Unauthorized(detail: "An authenticated caller identity is required.", httpContext: httpContext);
        }

        var callerTenant = EndpointSecurity.GetRequestTenant(httpContext);
        string targetTenant = !string.IsNullOrWhiteSpace(request.TenantId)
            ? request.TenantId.Trim()
            : !string.IsNullOrWhiteSpace(tenant)
                ? tenant.Trim()
                : callerTenant.Value;

        bool isWildcard = string.Equals(targetTenant, "*", StringComparison.Ordinal)
            || string.Equals(dataSource.Trim(), "*", StringComparison.Ordinal);
        bool isOtherTenant = !string.Equals(targetTenant, "*", StringComparison.Ordinal)
            && !string.Equals(targetTenant, callerTenant.Value, StringComparison.OrdinalIgnoreCase);

        if ((isWildcard || isOtherTenant) && !EndpointSecurity.IsCanonicalClusterAdmin(httpContext.User))
        {
            return ApiProblems.Forbidden(
                ApiErrorCodes.AccessDenied,
                "Only a canonical ClusterAdmin may add wildcard or cross-tenant tariff versions.",
                httpContext);
        }

        try
        {
            var command = new CreateUsageTariffVersionRequest(
                TenantId: targetTenant,
                DataSource: dataSource,
                ValidFrom: request.ValidFrom,
                BasePerQuery: request.BasePerQuery,
                PerRow: request.PerRow,
                PerMib: request.PerMib,
                PerComputeSecond: request.PerComputeSecond,
                PerAnalysisCost: request.PerAnalysisCost,
                SourceCostFactor: request.SourceCostFactor,
                SourceCostUnit: request.SourceCostUnit,
                UnitLabel: request.UnitLabel,
                ComputeBasis: request.ComputeBasis,
                CreatedBy: callerIdentity,
                Reason: request.Reason,
                ValidUntil: request.ValidUntil,
                TraceId: httpContext.TraceIdentifier);

            var created = await tariffService.AddVersionAsync(command, ct).ConfigureAwait(false);
            return Results.Created($"/api/v1/usage/tariffs/{Uri.EscapeDataString(created.DataSource)}/versions", created);
        }
        catch (UsageTariffValidationException ex)
        {
            var logger = (httpContext.RequestServices?.GetService(typeof(Microsoft.Extensions.Logging.ILoggerFactory)) as Microsoft.Extensions.Logging.ILoggerFactory)
                ?.CreateLogger("Autheris.Api.Endpoints.UsageEndpoints");
            if (logger is not null) Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(logger, ex, "Usage tariff request validation failed.");
            return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "The usage tariff request is invalid.", httpContext);
        }
        catch (AuditUnavailableException)
        {
            return ApiProblems.ServiceUnavailable(
                ApiErrorCodes.UsageAuditUnavailable,
                "The audit store is temporarily unavailable. Retry later.",
                httpContext);
        }
    }
}
