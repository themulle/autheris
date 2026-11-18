namespace Autheris.Api.Endpoints;

using System.Threading;
using Autheris.Api.Errors;
using Autheris.Api.Extensions;
using Autheris.Api.Security;
using Autheris.Application.Policy.Classification;
using Autheris.Domain.Audit;
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
            string columnId, string code, string? scope, HttpContext http, [FromServices] IClassificationAdminService service, CancellationToken ct) =>
        {
            if (!TryParseScope(scope, out var parsedScope))
            {
                return InvalidScope(http);
            }

            var result = await service.AssignColumnClassificationAsync(BuildCaller(http), columnId, code, parsedScope, ct).ConfigureAwait(false);
            return ToResult(result, http, static a => new { scope = a.Scope, columnId = a.TableColumnId, code = a.Code, source = a.Source });
        })
        .RequireAuthorization(GatewayPolicies.Approver)
        .WithAudit(AuditLevel.Full, AuditEventTypes.ColumnClassificationChanged)
        .WithIngressTransport(DataSourceTransportFlags.Rest)
        .WithTags("Governance");

        app.MapDelete(ColumnRoute + "/classifications/{code}", async (
            string columnId, string code, string? scope, HttpContext http, [FromServices] IClassificationAdminService service, CancellationToken ct) =>
        {
            if (!TryParseScope(scope, out var parsedScope))
            {
                return InvalidScope(http);
            }

            var result = await service.RemoveColumnClassificationAsync(BuildCaller(http), columnId, code, parsedScope, ct).ConfigureAwait(false);
            return result.IsOk ? Results.NoContent() : ToResult(result, http, static _ => new { });
        })
        .RequireAuthorization(GatewayPolicies.Approver)
        .WithAudit(AuditLevel.Full, AuditEventTypes.ColumnClassificationChanged)
        .WithIngressTransport(DataSourceTransportFlags.Rest)
        .WithTags("Governance");

        app.MapPut(ColumnRoute + "/mask-overrides/{code}", async (
            string columnId, string code, MaskRuleRequest? body, string? scope, HttpContext http,
            [FromServices] IClassificationAdminService service, CancellationToken ct) =>
        {
            if (!TryParseScope(scope, out var parsedScope))
            {
                return InvalidScope(http);
            }

            if (body is null || !TryParseMaskRule(body, out var input))
            {
                return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "The mask override is not valid.", http);
            }

            var result = await service.UpsertMaskOverrideAsync(
                BuildCaller(http), columnId, ClassificationAdminService.ParseOverrideCode(code), input, parsedScope, ct).ConfigureAwait(false);
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
        .RequireAuthorization(GatewayPolicies.Approver)
        .WithAudit(AuditLevel.Full, AuditEventTypes.MaskOverrideChanged)
        .WithIngressTransport(DataSourceTransportFlags.Rest)
        .WithTags("Governance");

        app.MapDelete(ColumnRoute + "/mask-overrides/{code}", async (
            string columnId, string code, string? scope, HttpContext http, [FromServices] IClassificationAdminService service, CancellationToken ct) =>
        {
            if (!TryParseScope(scope, out var parsedScope))
            {
                return InvalidScope(http);
            }

            var result = await service.DeleteMaskOverrideAsync(
                BuildCaller(http), columnId, ClassificationAdminService.ParseOverrideCode(code), parsedScope, ct).ConfigureAwait(false);
            return result.IsOk ? Results.NoContent() : ToResult(result, http, static _ => new { });
        })
        .RequireAuthorization(GatewayPolicies.Approver)
        .WithAudit(AuditLevel.Full, AuditEventTypes.MaskOverrideChanged)
        .WithIngressTransport(DataSourceTransportFlags.Rest)
        .WithTags("Governance");

        return app;
    }
}
