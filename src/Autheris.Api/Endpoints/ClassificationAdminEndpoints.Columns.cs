namespace Autheris.Api.Endpoints;

using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Errors;
using Autheris.Api.Extensions;
using Autheris.Api.Security;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Policy.Classification;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options.DataSources;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

public static partial class ClassificationAdminEndpoints
{
    private static IEndpointRouteBuilder MapClassificationColumnEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPut(ColumnRoute + "/classifications/{code}", async (
            string columnId,
            string code,
            string? scope,
            HttpContext http,
            [FromServices] IClassificationAdminService service,
            [FromServices] IGovernanceCapabilities capabilities,
            [FromServices] IGovernanceDomainRepository domainRepo,
            [FromServices] IClassificationRepository classificationRepo,
            [FromServices] ITableMetadataRepository tableMetadataRepo,
            CancellationToken ct) =>
        {
            if (!TryParseScope(scope, out var parsedScope))
            {
                return InvalidScope(http);
            }

            if (await AuthorizeColumnWriteAsync(http, columnId, capabilities, domainRepo, classificationRepo, tableMetadataRepo, ct).ConfigureAwait(false) is { } denied)
            {
                return denied;
            }

            var result = await service.AssignColumnClassificationAsync(BuildColumnCaller(http), columnId, code, parsedScope, ct).ConfigureAwait(false);
            return ToResult(result, http, static a => new { scope = a.Scope, columnId = a.TableColumnId, code = a.Code, source = a.Source });
        })
        .RequireAuthorization(GatewayPolicies.AuthenticatedCaller)
        .WithAudit(AuditLevel.Full, AuditEventTypes.ColumnClassificationChanged)
        .WithIngressTransport(DataSourceTransportFlags.Rest)
        .WithTags("Governance");

        app.MapDelete(ColumnRoute + "/classifications/{code}", async (
            string columnId,
            string code,
            string? scope,
            HttpContext http,
            [FromServices] IClassificationAdminService service,
            [FromServices] IGovernanceCapabilities capabilities,
            [FromServices] IGovernanceDomainRepository domainRepo,
            [FromServices] IClassificationRepository classificationRepo,
            [FromServices] ITableMetadataRepository tableMetadataRepo,
            CancellationToken ct) =>
        {
            if (!TryParseScope(scope, out var parsedScope))
            {
                return InvalidScope(http);
            }

            if (await AuthorizeColumnWriteAsync(http, columnId, capabilities, domainRepo, classificationRepo, tableMetadataRepo, ct).ConfigureAwait(false) is { } denied)
            {
                return denied;
            }

            var result = await service.RemoveColumnClassificationAsync(BuildColumnCaller(http), columnId, code, parsedScope, ct).ConfigureAwait(false);
            return result.IsOk ? Results.NoContent() : ToResult(result, http, static _ => new { });
        })
        .RequireAuthorization(GatewayPolicies.AuthenticatedCaller)
        .WithAudit(AuditLevel.Full, AuditEventTypes.ColumnClassificationChanged)
        .WithIngressTransport(DataSourceTransportFlags.Rest)
        .WithTags("Governance");

        app.MapPut(ColumnRoute + "/mask-overrides/{code}", async (
            string columnId,
            string code,
            MaskRuleRequest? body,
            string? scope,
            HttpContext http,
            [FromServices] IClassificationAdminService service,
            [FromServices] IGovernanceCapabilities capabilities,
            [FromServices] IGovernanceDomainRepository domainRepo,
            [FromServices] IClassificationRepository classificationRepo,
            [FromServices] ITableMetadataRepository tableMetadataRepo,
            CancellationToken ct) =>
        {
            if (!TryParseScope(scope, out var parsedScope))
            {
                return InvalidScope(http);
            }

            if (body is null || !TryParseMaskRule(body, out var input))
            {
                return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "The mask override is not valid.", http);
            }

            if (await AuthorizeColumnWriteAsync(http, columnId, capabilities, domainRepo, classificationRepo, tableMetadataRepo, ct).ConfigureAwait(false) is { } denied)
            {
                return denied;
            }

            var result = await service.UpsertMaskOverrideAsync(
                BuildColumnCaller(http), columnId, ClassificationAdminService.ParseOverrideCode(code), input, parsedScope, ct).ConfigureAwait(false);
            return ToResult(result, http, static o => new
            {
                scope = o.Scope,
                columnId = o.TableColumnId,
                code = o.Code,
                maskFunction = ClassificationMaskFunctions.ToCode(o.MaskFunction),
                keyRef = o.KeyRef,
                catalogText = ClassificationCatalogTextVisibilities.ToCode(o.CatalogText)
            });
        })
        .RequireAuthorization(GatewayPolicies.AuthenticatedCaller)
        .WithAudit(AuditLevel.Full, AuditEventTypes.MaskOverrideChanged)
        .WithIngressTransport(DataSourceTransportFlags.Rest)
        .WithTags("Governance");

        app.MapDelete(ColumnRoute + "/mask-overrides/{code}", async (
            string columnId,
            string code,
            string? scope,
            HttpContext http,
            [FromServices] IClassificationAdminService service,
            [FromServices] IGovernanceCapabilities capabilities,
            [FromServices] IGovernanceDomainRepository domainRepo,
            [FromServices] IClassificationRepository classificationRepo,
            [FromServices] ITableMetadataRepository tableMetadataRepo,
            CancellationToken ct) =>
        {
            if (!TryParseScope(scope, out var parsedScope))
            {
                return InvalidScope(http);
            }

            if (await AuthorizeColumnWriteAsync(http, columnId, capabilities, domainRepo, classificationRepo, tableMetadataRepo, ct).ConfigureAwait(false) is { } denied)
            {
                return denied;
            }

            var result = await service.DeleteMaskOverrideAsync(
                BuildColumnCaller(http), columnId, ClassificationAdminService.ParseOverrideCode(code), parsedScope, ct).ConfigureAwait(false);
            return result.IsOk ? Results.NoContent() : ToResult(result, http, static _ => new { });
        })
        .RequireAuthorization(GatewayPolicies.AuthenticatedCaller)
        .WithAudit(AuditLevel.Full, AuditEventTypes.MaskOverrideChanged)
        .WithIngressTransport(DataSourceTransportFlags.Rest)
        .WithTags("Governance");

        return app;
    }

    private static ClassificationAdminCaller BuildColumnCaller(HttpContext http) =>
        BuildCaller(http) with { IsContainerOwnerAuthorized = true };

    private static async Task<IResult?> AuthorizeColumnWriteAsync(
        HttpContext http,
        string columnId,
        IGovernanceCapabilities capabilities,
        IGovernanceDomainRepository domainRepo,
        IClassificationRepository classificationRepo,
        ITableMetadataRepository tableMetadataRepo,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(columnId))
        {
            return ApiProblems.BadRequest(ClassificationAdminErrorCodes.Invalid, "The column id is required.", http);
        }

        var table = await classificationRepo.GetTableIdentifierByColumnIdAsync(columnId, ct).ConfigureAwait(false);
        if (table is null)
        {
            return ApiProblems.NotFound(ClassificationAdminErrorCodes.ColumnNotFound, "The column was not found.", http);
        }

        var meta = await tableMetadataRepo.GetTableMetadataAsync(table.Value, ct).ConfigureAwait(false);
        if (meta is null)
        {
            return ApiProblems.NotFound(ClassificationAdminErrorCodes.ColumnNotFound, "The column was not found.", http);
        }

        var security = EndpointSecurity.GetSecurityContext(http);
        TenantId objectTenant;
        if (!string.IsNullOrWhiteSpace(meta.OwnerTenantId) && !ClassificationScopes.IsGlobal(meta.OwnerTenantId))
        {
            if (!TenantId.TryParse(meta.OwnerTenantId, out objectTenant))
            {
                return ApiProblems.NotFound(ClassificationAdminErrorCodes.ColumnNotFound, "The column was not found.", http);
            }
        }
        else if (security.ResolvedTenant is { } resolvedTenant && resolvedTenant.Value is not null)
        {
            objectTenant = resolvedTenant;
        }
        else
        {
            return ApiProblems.Forbidden(ClassificationAdminErrorCodes.AuthorityRequired, "The caller is not permitted to perform this classification change.", http);
        }

        var containerDefaults = await domainRepo.GetContainerForTableAsync(objectTenant, meta.Table.Id, ct).ConfigureAwait(false);
        var containerRef = containerDefaults.IsValid ? containerDefaults.Container : ContainerDefaults.Unassigned.Container;

        if (!await capabilities.CanClassifyAsync(security, containerRef, objectTenant, ct).ConfigureAwait(false))
        {
            if (security.ResolvedTenant is { } callerTenant && objectTenant != callerTenant)
            {
                return ApiProblems.NotFound(ClassificationAdminErrorCodes.ColumnNotFound, "The column was not found.", http);
            }

            return ApiProblems.Forbidden(ClassificationAdminErrorCodes.AuthorityRequired, "The caller is not permitted to perform this classification change.", http);
        }

        return null;
    }
}
