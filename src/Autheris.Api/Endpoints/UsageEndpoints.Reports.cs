namespace Autheris.Api.Endpoints;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Errors;
using Autheris.Api.Extensions;
using Autheris.Api.Security;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Usage;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Options.DataSources;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

public static partial class UsageEndpoints
{
    private const int DefaultReportLimit = 100;

    private static void MapReportEndpoints(IEndpointRouteBuilder app)
    {
        ConfigureReportEndpoint(
            app.MapGet("/api/v1/usage/me", (
                HttpContext httpContext,
                [FromServices] IUsageReportService reportService,
                string? from = null,
                string? to = null,
                string? by = null,
                string? tenant = null,
                int? limit = null,
                string? cursor = null,
                string? format = null,
                CancellationToken ct = default) =>
                HandleMeReportAsync(httpContext, reportService, from, to, by, tenant, limit, cursor, format, ct)),
            GatewayPolicies.AuthenticatedCaller,
            AuditLevel.Summarized,
            AuditEventTypes.CatalogRead);

        ConfigureReportEndpoint(
            app.MapGet("/api/v1/usage/users", (
                HttpContext httpContext,
                [FromServices] IUsageReportService reportService,
                string? from = null,
                string? to = null,
                string? tenant = null,
                int? limit = null,
                string? cursor = null,
                string? format = null,
                CancellationToken ct = default) =>
                HandleSimpleAdminReportAsync(httpContext, reportService, "users", UsageGroupBy.User, false, from, to, tenant, limit, cursor, format, ct)),
            GatewayPolicies.UsageAdmin,
            AuditLevel.Delegated,
            AuditEventTypes.UsageReportRead);

        ConfigureReportEndpoint(
            app.MapGet("/api/v1/usage/groups", (
                HttpContext httpContext,
                [FromServices] IUsageReportService reportService,
                string? from = null,
                string? to = null,
                string? by = null,
                string? tenant = null,
                int? limit = null,
                string? cursor = null,
                string? format = null,
                CancellationToken ct = default) =>
            {
                if (!TryParseGroupBy(by, UsageGroupBy.Group, ["group", "billing_group"], out var groupBy))
                {
                    return Task.FromResult(ApiProblems.BadRequest(ApiErrorCodes.InvalidQuery, "Parameter 'by' must be 'group' or 'billing_group'.", httpContext));
                }

                return HandleSimpleAdminReportAsync(httpContext, reportService, "groups", groupBy, true, from, to, tenant, limit, cursor, format, ct);
            }),
            GatewayPolicies.UsageAdmin,
            AuditLevel.Delegated,
            AuditEventTypes.UsageReportRead);

        ConfigureReportEndpoint(
            app.MapGet("/api/v1/usage/tenant", (
                HttpContext httpContext,
                [FromServices] IUsageReportService reportService,
                string? from = null,
                string? to = null,
                string? by = null,
                string? tenant = null,
                int? limit = null,
                string? cursor = null,
                string? format = null,
                CancellationToken ct = default) =>
            {
                if (!TryParseGroupBy(by, UsageGroupBy.Day, ["day", "source", "table", "transport", "application", "client", "group", "billing_group", "user"], out var groupBy))
                {
                    return Task.FromResult(ApiProblems.BadRequest(ApiErrorCodes.InvalidQuery, "Invalid 'by' grouping for tenant usage report.", httpContext));
                }

                return HandleSimpleAdminReportAsync(httpContext, reportService, "tenant", groupBy, true, from, to, tenant, limit, cursor, format, ct);
            }),
            GatewayPolicies.UsageAdmin,
            AuditLevel.Delegated,
            AuditEventTypes.UsageReportRead);

        ConfigureReportEndpoint(
            app.MapGet("/api/v1/usage/tables/{tableId}", (
                string tableId,
                HttpContext httpContext,
                [FromServices] IUsageReportService reportService,
                [FromServices] IOptions<UsageAccountingOptions> options,
                string? from = null,
                string? to = null,
                string? by = null,
                string? tenant = null,
                int? limit = null,
                string? cursor = null,
                string? format = null,
                CancellationToken ct = default) =>
                HandleTableReportAsync(tableId, httpContext, reportService, options, from, to, by, tenant, limit, cursor, format, ct)),
            GatewayPolicies.AuthenticatedCaller,
            AuditLevel.Delegated,
            AuditEventTypes.UsageReportRead);

        ConfigureReportEndpoint(
            app.MapGet("/api/v1/usage/sources", (
                HttpContext httpContext,
                [FromServices] IUsageReportService reportService,
                string? from = null,
                string? to = null,
                string? tenant = null,
                int? limit = null,
                string? cursor = null,
                string? format = null,
                CancellationToken ct = default) =>
                HandleSimpleAdminReportAsync(httpContext, reportService, "sources", UsageGroupBy.DataSource, false, from, to, tenant, limit, cursor, format, ct)),
            GatewayPolicies.UsageAdmin,
            AuditLevel.Delegated,
            AuditEventTypes.UsageReportRead);

        ConfigureReportEndpoint(
            app.MapGet("/api/v1/usage/applications", (
                HttpContext httpContext,
                [FromServices] IUsageReportService reportService,
                string? from = null,
                string? to = null,
                string? by = null,
                string? tenant = null,
                int? limit = null,
                string? cursor = null,
                string? format = null,
                CancellationToken ct = default) =>
            {
                if (!TryParseGroupBy(by, UsageGroupBy.Application, ["application", "client"], out var groupBy))
                {
                    return Task.FromResult(ApiProblems.BadRequest(ApiErrorCodes.InvalidQuery, "Parameter 'by' must be 'application' or 'client'.", httpContext));
                }

                return HandleSimpleAdminReportAsync(httpContext, reportService, "applications", groupBy, false, from, to, tenant, limit, cursor, format, ct);
            }),
            GatewayPolicies.UsageAdmin,
            AuditLevel.Delegated,
            AuditEventTypes.UsageReportRead);

        ConfigureReportEndpoint(
            app.MapGet("/api/v1/usage/grants/{grantId}", (
                string grantId,
                HttpContext httpContext,
                [FromServices] IUsageReportService reportService,
                string? from = null,
                string? to = null,
                string? tenant = null,
                int? limit = null,
                string? cursor = null,
                string? format = null,
                CancellationToken ct = default) =>
                HandleGrantReportAsync(grantId, httpContext, reportService, from, to, tenant, limit, cursor, format, ct)),
            GatewayPolicies.AuthenticatedCaller,
            AuditLevel.Delegated,
            AuditEventTypes.UsageReportRead);

        ConfigureReportEndpoint(
            app.MapDelete("/api/v1/usage/users/{userSid}", (
                string userSid,
                HttpContext httpContext,
                [FromServices] IUsageReportService reportService,
                string? tenant = null,
                CancellationToken ct = default) =>
                HandleEraseUserAsync(userSid, httpContext, reportService, tenant, ct)),
            GatewayPolicies.UsageAdmin,
            AuditLevel.Delegated,
            AuditEventTypes.UsageDataErased);
    }

    private static void ConfigureReportEndpoint(
        RouteHandlerBuilder builder,
        string policy,
        AuditLevel auditLevel,
        string auditEventType) =>
        builder
            .RequireAuthorization(policy)
            .WithAudit(auditLevel, auditEventType)
            .WithIngressTransport(DataSourceTransportFlags.Rest)
            .WithTags("Usage");

    internal static async Task<IResult> HandleMeReportAsync(
        HttpContext httpContext,
        IUsageReportService reportService,
        string? from,
        string? to,
        string? by,
        string? tenant,
        int? limit,
        string? cursor,
        string? format,
        CancellationToken ct)
    {
        if (!TryGetAuthenticatedCaller(httpContext, out var secContext, out var authErr))
        {
            return authErr!;
        }

        if (!string.IsNullOrWhiteSpace(tenant)
            && !string.Equals(tenant.Trim(), secContext.TenantId.Value, StringComparison.OrdinalIgnoreCase))
        {
            return ApiProblems.Forbidden(ApiErrorCodes.CrossTenantAccessForbidden, "Cross-tenant queries are not permitted on /usage/me.", httpContext);
        }

        if (!TryParseGroupBy(by, UsageGroupBy.Day, ["day", "source", "table", "application", "client"], out var groupBy))
        {
            return ApiProblems.BadRequest(ApiErrorCodes.InvalidQuery, "Parameter 'by' must be one of: day, source, table, application, client.", httpContext);
        }

        return await ExecuteReportAsync(
            httpContext,
            reportService,
            "me",
            from,
            to,
            limit,
            format,
            (fromUtc, toUtc, effLimit, queryFilters) =>
            {
                string userKey = reportService.ComputeUserKey(secContext.UserSid);
                var filter = new UsageReportFilter(
                    Tenant: secContext.TenantId,
                    FromUtc: fromUtc,
                    ToUtc: toUtc,
                    GroupBy: groupBy,
                    UserKey: userKey,
                    DataSource: queryFilters.DataSource,
                    TableId: queryFilters.TableId,
                    Transport: queryFilters.Transport,
                    Application: queryFilters.Application,
                    ClientId: queryFilters.ClientId,
                    Limit: effLimit,
                    Cursor: cursor,
                    IncludeTenantTotal: false);
                return Task.FromResult<(UsageReportFilter, UsageReportAuditContext?)>((filter, null));
            },
            ct).ConfigureAwait(false);
    }

    internal static async Task<IResult> HandleSimpleAdminReportAsync(
        HttpContext httpContext,
        IUsageReportService reportService,
        string reportSlug,
        UsageGroupBy groupBy,
        bool includeTenantTotal,
        string? from,
        string? to,
        string? tenant,
        int? limit,
        string? cursor,
        string? format,
        CancellationToken ct)
    {
        if (!TryResolveTargetTenant(httpContext, tenant, clusterAdminRequiresTenant: true, out var targetTenant, out var tenantErr))
        {
            return tenantErr!;
        }

        string? caller = EndpointSecurity.GetCallerIdentity(httpContext);
        if (string.IsNullOrWhiteSpace(caller))
        {
            return ApiProblems.Unauthorized(detail: "An authenticated caller identity is required.", httpContext: httpContext);
        }

        return await ExecuteReportAsync(
            httpContext,
            reportService,
            reportSlug,
            from,
            to,
            limit,
            format,
            (fromUtc, toUtc, effLimit, queryFilters) =>
            {
                string? userKeyFilter = queryFilters.UserSid is null
                    ? null
                    : SidFormat.IsCanonical(queryFilters.UserSid)
                        ? reportService.ComputeUserKey(new Sid(queryFilters.UserSid))
                        : queryFilters.UserSid;
                var filter = new UsageReportFilter(
                    Tenant: targetTenant,
                    FromUtc: fromUtc,
                    ToUtc: toUtc,
                    GroupBy: groupBy,
                    UserKey: userKeyFilter,
                    GroupSid: queryFilters.GroupSid,
                    BillingGroup: queryFilters.BillingGroup,
                    DataSource: queryFilters.DataSource,
                    TableId: queryFilters.TableId,
                    Transport: queryFilters.Transport,
                    Application: queryFilters.Application,
                    ClientId: queryFilters.ClientId,
                    Limit: effLimit,
                    Cursor: cursor,
                    IncludeTenantTotal: includeTenantTotal);
                var audit = new UsageReportAuditContext(caller, reportSlug, null, httpContext.TraceIdentifier);
                return Task.FromResult<(UsageReportFilter, UsageReportAuditContext?)>((filter, audit));
            },
            ct).ConfigureAwait(false);
    }

    internal static async Task<IResult> HandleTableReportAsync(
        string tableId,
        HttpContext httpContext,
        IUsageReportService reportService,
        IOptions<UsageAccountingOptions> options,
        string? from,
        string? to,
        string? by,
        string? tenant,
        int? limit,
        string? cursor,
        string? format,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tableId))
        {
            return ApiProblems.BadRequest(ApiErrorCodes.InvalidQuery, "tableId is required.", httpContext);
        }

        if (!TryGetAuthenticatedCaller(httpContext, out var secContext, out var authErr))
        {
            return authErr!;
        }

        if (!TryResolveTargetTenant(httpContext, tenant, clusterAdminRequiresTenant: true, out var targetTenant, out var tenantErr))
        {
            return tenantErr!;
        }

        bool isUsageAdmin = IsUsageAdmin(httpContext.User);
        if (!isUsageAdmin)
        {
            if (!GatewayPolicies.HasRole(httpContext.User, GatewayRole.DataOwner)
                || !TableIdentifier.TryParse(tableId.Trim(), out var parsedTable))
            {
                return ApiProblems.Forbidden(ApiErrorCodes.AccessDenied, "Usage report for this table requires UsageAdmin or table ownership.", httpContext);
            }

            var ownershipRepo = httpContext.RequestServices?.GetService<IDataOwnershipRepository>();
            if (ownershipRepo is null
                || !await ownershipRepo.IsAuthorizedApproverForTableAsync(parsedTable, secContext.UserSid, ct).ConfigureAwait(false))
            {
                return ApiProblems.Forbidden(ApiErrorCodes.AccessDenied, "Caller is not an owner or delegate for the requested table.", httpContext);
            }
        }

        if (!TryParseGroupBy(by, UsageGroupBy.Day, ["day", "source", "transport", "application", "client", "user"], out var groupBy))
        {
            return ApiProblems.BadRequest(ApiErrorCodes.InvalidQuery, "Invalid 'by' grouping for table usage report.", httpContext);
        }

        if (!isUsageAdmin && groupBy == UsageGroupBy.User && !options.Value.DataOwnersSeeUsers)
        {
            return ApiProblems.Forbidden(ApiErrorCodes.AccessDenied, "Per-user attribution on table usage reports is disabled for non-administrators.", httpContext);
        }

        string trimmedTable = tableId.Trim();
        return await ExecuteReportAsync(
            httpContext,
            reportService,
            "table",
            from,
            to,
            limit,
            format,
            (fromUtc, toUtc, effLimit, queryFilters) =>
            {
                var filter = new UsageReportFilter(
                    Tenant: targetTenant,
                    FromUtc: fromUtc,
                    ToUtc: toUtc,
                    GroupBy: groupBy,
                    DataSource: queryFilters.DataSource,
                    TableId: trimmedTable,
                    Transport: queryFilters.Transport,
                    Application: queryFilters.Application,
                    ClientId: queryFilters.ClientId,
                    Limit: effLimit,
                    Cursor: cursor,
                    IncludeTenantTotal: false);
                var audit = new UsageReportAuditContext(secContext.UserSid.Value, "table", trimmedTable, httpContext.TraceIdentifier);
                return Task.FromResult<(UsageReportFilter, UsageReportAuditContext?)>((filter, audit));
            },
            ct).ConfigureAwait(false);
    }

    internal static async Task<IResult> HandleGrantReportAsync(
        string grantId,
        HttpContext httpContext,
        IUsageReportService reportService,
        string? from,
        string? to,
        string? tenant,
        int? limit,
        string? cursor,
        string? format,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(grantId))
        {
            return ApiProblems.BadRequest(ApiErrorCodes.InvalidQuery, "grantId is required.", httpContext);
        }

        if (!TryGetAuthenticatedCaller(httpContext, out var secContext, out var authErr))
        {
            return authErr!;
        }

        if (!TryResolveTargetTenant(httpContext, tenant, clusterAdminRequiresTenant: true, out var targetTenant, out var tenantErr))
        {
            return tenantErr!;
        }

        string trimmedGrantId = grantId.Trim();
        if (!IsUsageAdmin(httpContext.User))
        {
            bool authorized = await IsAuthorizedForGrantReportAsync(httpContext, secContext, targetTenant, trimmedGrantId, ct).ConfigureAwait(false);
            if (!authorized)
            {
                return ApiProblems.Forbidden(ApiErrorCodes.AccessDenied, "Caller is not authorized to view usage for the specified grant.", httpContext);
            }
        }

        return await ExecuteReportAsync(
            httpContext,
            reportService,
            "grant",
            from,
            to,
            limit,
            format,
            (fromUtc, toUtc, effLimit, queryFilters) =>
            {
                var filter = new UsageReportFilter(
                    Tenant: targetTenant,
                    FromUtc: fromUtc,
                    ToUtc: toUtc,
                    GroupBy: UsageGroupBy.Grant,
                    DataSource: queryFilters.DataSource,
                    TableId: queryFilters.TableId,
                    GrantId: trimmedGrantId,
                    Limit: effLimit,
                    Cursor: cursor,
                    IncludeTenantTotal: false);
                var audit = new UsageReportAuditContext(secContext.UserSid.Value, "grant", trimmedGrantId, httpContext.TraceIdentifier);
                return Task.FromResult<(UsageReportFilter, UsageReportAuditContext?)>((filter, audit));
            },
            ct).ConfigureAwait(false);
    }

    internal static async Task<IResult> HandleEraseUserAsync(
        string userSid,
        HttpContext httpContext,
        IUsageReportService reportService,
        string? tenant,
        CancellationToken ct)
    {
        if (!TryResolveTargetTenant(httpContext, tenant, clusterAdminRequiresTenant: true, out var targetTenant, out var tenantErr))
        {
            return tenantErr!;
        }

        string? caller = EndpointSecurity.GetCallerIdentity(httpContext);
        if (string.IsNullOrWhiteSpace(caller))
        {
            return ApiProblems.Unauthorized(detail: "An authenticated caller identity is required.", httpContext: httpContext);
        }

        try
        {
            int rowsAffected = await reportService.EraseUserAsync(targetTenant, userSid, caller, httpContext.TraceIdentifier, ct).ConfigureAwait(false);
            httpContext.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new
            {
                tenantId = targetTenant.Value,
                userSid,
                erased = true,
                rowsAffected
            });
        }
        catch (UsageReportValidationException ex)
        {
            return ApiProblems.BadRequest(ex.ErrorCode, ex.Message, httpContext);
        }
        catch (AuditUnavailableException)
        {
            return ApiProblems.ServiceUnavailable(ApiErrorCodes.UsageAuditUnavailable, "The audit store is temporarily unavailable. Retry later.", httpContext);
        }
    }

    private static async Task<bool> IsAuthorizedForGrantReportAsync(
        HttpContext httpContext,
        SecurityPrincipalContext secContext,
        TenantId targetTenant,
        string grantId,
        CancellationToken ct)
    {
        if (!Guid.TryParse(grantId, out var consentId))
        {
            return false;
        }

        var consentRepo = httpContext.RequestServices?.GetService<IConsentRepository>();
        if (consentRepo is null)
        {
            return false;
        }

        var consent = await consentRepo.GetConsentByIdAsync(consentId, ct).ConfigureAwait(false);
        if (consent is null || consent.TenantId != targetTenant)
        {
            return false;
        }

        if (GatewayPolicies.HasRole(httpContext.User, GatewayRole.DataOwner))
        {
            var ownershipRepo = httpContext.RequestServices?.GetService<IDataOwnershipRepository>();
            if (ownershipRepo is not null
                && await ownershipRepo.IsAuthorizedApproverForTableAsync(consent.TableIdentifier, secContext.UserSid, ct).ConfigureAwait(false))
            {
                return true;
            }
        }

        if (consent.GranteeSid is not { } granteeSid)
        {
            return false;
        }

        return consent.GranteeType switch
        {
            GranteeType.User => granteeSid == secContext.UserSid,
            GranteeType.Group => secContext.GroupSids.Contains(granteeSid),
            _ => false
        };
    }

    private static async Task<IResult> ExecuteReportAsync(
        HttpContext httpContext,
        IUsageReportService reportService,
        string reportSlug,
        string? from,
        string? to,
        int? limit,
        string? format,
        Func<DateOnly, DateOnly, int, OptionalQueryFilters, Task<(UsageReportFilter Filter, UsageReportAuditContext? Audit)>> buildFilter,
        CancellationToken ct)
    {
        if (!TryResolveOutputFormat(httpContext, format, out bool isCsv, out var fmtErr))
        {
            return fmtErr!;
        }

        if (!TryReadOptionalFilters(httpContext, out var queryFilters, out var filterErr))
        {
            return filterErr!;
        }

        try
        {
            var (fromUtc, toUtc) = reportService.ResolveAndValidateDateRange(from, to);
            var (filter, audit) = await buildFilter(fromUtc, toUtc, limit ?? DefaultReportLimit, queryFilters).ConfigureAwait(false);
            var report = await reportService.GetReportAsync(filter, audit, ct).ConfigureAwait(false);

            httpContext.Response.Headers.CacheControl = "no-store";
            if (isCsv)
            {
                httpContext.Response.Headers.ContentDisposition = UsageCsvWriter.BuildContentDisposition(reportSlug, report.From, report.To);
                return Results.Text(UsageCsvWriter.Write(report), "text/csv; charset=utf-8");
            }

            return Results.Ok(report);
        }
        catch (UsageReportValidationException ex)
        {
            return ApiProblems.BadRequest(ex.ErrorCode, ex.Message, httpContext);
        }
        catch (AuditUnavailableException)
        {
            return ApiProblems.ServiceUnavailable(ApiErrorCodes.UsageAuditUnavailable, "The audit store is temporarily unavailable. Retry later.", httpContext);
        }
    }

    private static bool IsUsageAdmin(ClaimsPrincipal principal) =>
        GatewayPolicies.HasAnyRole(principal, GatewayPolicies.UsageAdminRoles);

    private static bool TryGetAuthenticatedCaller(HttpContext httpContext, out SecurityPrincipalContext secContext, out IResult? error)
    {
        try
        {
            secContext = httpContext.GetSecurityContext();
        }
        catch (UnauthorizedAccessException)
        {
            secContext = null!;
            error = ApiProblems.Unauthorized(detail: "An authenticated caller identity is required.", httpContext: httpContext);
            return false;
        }

        if (!secContext.IsAuthenticated || string.IsNullOrWhiteSpace(secContext.UserSid.Value))
        {
            error = ApiProblems.Unauthorized(detail: "An authenticated caller identity is required.", httpContext: httpContext);
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryResolveTargetTenant(
        HttpContext httpContext,
        string? tenant,
        bool clusterAdminRequiresTenant,
        out TenantId targetTenant,
        out IResult? error)
    {
        var callerTenant = EndpointSecurity.GetRequestTenant(httpContext);
        bool isClusterAdmin = EndpointSecurity.IsCanonicalClusterAdmin(httpContext.User);

        if (clusterAdminRequiresTenant && isClusterAdmin && string.IsNullOrWhiteSpace(tenant))
        {
            targetTenant = default;
            error = ApiProblems.BadRequest(ApiErrorCodes.InvalidTenantId, "ClusterAdmin callers must specify the target tenant via the 'tenant' query parameter.", httpContext);
            return false;
        }

        if (!string.IsNullOrWhiteSpace(tenant))
        {
            string trimmed = tenant.Trim();
            if (!string.Equals(trimmed, callerTenant.Value, StringComparison.OrdinalIgnoreCase) && !isClusterAdmin)
            {
                targetTenant = default;
                error = ApiProblems.Forbidden(ApiErrorCodes.CrossTenantAccessForbidden, "Only a canonical ClusterAdmin may access another tenant's usage.", httpContext);
                return false;
            }

            if (!TenantId.TryParse(trimmed, out targetTenant))
            {
                error = ApiProblems.BadRequest(ApiErrorCodes.InvalidTenantId, "Invalid tenant identifier.", httpContext);
                return false;
            }

            error = null;
            return true;
        }

        targetTenant = callerTenant;
        error = null;
        return true;
    }

    private static bool TryResolveOutputFormat(HttpContext httpContext, string? format, out bool isCsv, out IResult? error)
    {
        if (!string.IsNullOrWhiteSpace(format))
        {
            string trimmed = format.Trim();
            if (string.Equals(trimmed, "csv", StringComparison.OrdinalIgnoreCase))
            {
                isCsv = true;
                error = null;
                return true;
            }

            if (string.Equals(trimmed, "json", StringComparison.OrdinalIgnoreCase))
            {
                isCsv = false;
                error = null;
                return true;
            }

            isCsv = false;
            error = ApiProblems.BadRequest(ApiErrorCodes.InvalidQuery, "Parameter 'format' must be 'json' or 'csv'.", httpContext);
            return false;
        }

        string accept = httpContext.Request.Headers.Accept.ToString();
        isCsv = accept.Contains("text/csv", StringComparison.OrdinalIgnoreCase)
            && !accept.Contains("application/json", StringComparison.OrdinalIgnoreCase);
        error = null;
        return true;
    }

    private static bool TryParseGroupBy(
        string? raw,
        UsageGroupBy defaultValue,
        IReadOnlyList<string> allowed,
        out UsageGroupBy groupBy)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            groupBy = defaultValue;
            return true;
        }

        string normalized = raw.Trim().ToLowerInvariant();
        bool found = false;
        for (int i = 0; i < allowed.Count; i++)
        {
            if (string.Equals(allowed[i], normalized, StringComparison.Ordinal))
            {
                found = true;
                break;
            }
        }

        if (!found)
        {
            groupBy = default;
            return false;
        }

        groupBy = normalized switch
        {
            "day" => UsageGroupBy.Day,
            "user" => UsageGroupBy.User,
            "group" => UsageGroupBy.Group,
            "billing_group" => UsageGroupBy.BillingGroup,
            "source" => UsageGroupBy.DataSource,
            "table" => UsageGroupBy.Table,
            "transport" => UsageGroupBy.Transport,
            "application" => UsageGroupBy.Application,
            "client" => UsageGroupBy.ClientId,
            "grant" => UsageGroupBy.Grant,
            _ => default
        };
        return true;
    }

    private static bool TryReadOptionalFilters(HttpContext httpContext, out OptionalQueryFilters filters, out IResult? error)
    {
        var q = httpContext.Request.Query;
        string? transportRaw = ReadQueryString(q, "transport");
        DataSourceTransportFlags? transport = null;
        if (transportRaw is not null)
        {
            if (Enum.TryParse<DataSourceTransportFlags>(transportRaw, true, out var parsed)
                && parsed != DataSourceTransportFlags.None
                && parsed != DataSourceTransportFlags.All
                && Enum.IsDefined(parsed))
            {
                transport = parsed;
            }
            else
            {
                filters = default;
                error = ApiProblems.BadRequest(ApiErrorCodes.InvalidQuery, "Invalid 'transport' filter value.", httpContext);
                return false;
            }
        }

        filters = new OptionalQueryFilters(
            DataSource: ReadQueryString(q, "dataSource"),
            TableId: ReadQueryString(q, "tableId"),
            Transport: transport,
            Application: ReadQueryString(q, "application"),
            ClientId: ReadQueryString(q, "clientId"),
            BillingGroup: ReadQueryString(q, "billingGroup"),
            GroupSid: ReadQueryString(q, "groupSid"),
            UserSid: ReadQueryString(q, "userSid"));
        error = null;
        return true;
    }

    private static string? ReadQueryString(IQueryCollection query, string key)
    {
        if (!query.TryGetValue(key, out var val))
        {
            return null;
        }

        string s = val.ToString().Trim();
        return s.Length == 0 ? null : s;
    }

    private readonly record struct OptionalQueryFilters(
        string? DataSource,
        string? TableId,
        DataSourceTransportFlags? Transport,
        string? Application,
        string? ClientId,
        string? BillingGroup,
        string? GroupSid,
        string? UserSid);
}
