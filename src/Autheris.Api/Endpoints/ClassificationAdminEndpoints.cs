namespace Autheris.Api.Endpoints;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Errors;
using Autheris.Api.Extensions;
using Autheris.Api.Security;
using Autheris.Application.Policy.Classification;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options.DataSources;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

/// <summary>
/// Administration of the data classification vocabulary, classification mask rules, column classification
/// assignments and per-column mask overrides. Authority and scope isolation are enforced by
/// <see cref="IClassificationAdminService"/>; the caller identity always comes from the authenticated principal.
/// </summary>
public static partial class ClassificationAdminEndpoints
{
    private const string ClassificationRoute = "/api/v1/governance/classifications/{code}";
    private const string ColumnRoute = "/api/v1/catalog/columns/{columnId}";

    public sealed record ClassificationDefinitionRequest(
        string DisplayName,
        string? Description = null,
        string? ParentCode = null,
        int RestrictionRank = 0,
        bool ClearGrantable = true,
        bool SeparateApproval = true,
        string? ApproverMode = null,
        string? StewardGroupSid = null,
        int? MaxClearDays = null,
        bool IsActive = true);

    public sealed record MaskRuleRequest(
        string MaskFunction,
        string? ParamsJson = null,
        string? KeyRef = null,
        string? CatalogText = null);

    public static IEndpointRouteBuilder MapClassificationAdminEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(ClassificationRoute, async (
            string code, string? scope, string? tenant, HttpContext http, [FromServices] IClassificationAdminService service, CancellationToken ct) =>
        {
            if (!TryParseScope(scope, out var parsedScope))
            {
                return InvalidScope(http);
            }

            var result = await service.GetClassificationAsync(BuildCaller(http), code, parsedScope, tenant, ct).ConfigureAwait(false);
            return ToResult(result, http, ToResponse);
        })
        .RequireAuthorization(GatewayPolicies.GovernanceAdmin)
        .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead)
        .WithIngressTransport(DataSourceTransportFlags.Rest)
        .WithTags("Governance");

        app.MapPut(ClassificationRoute, async (
            string code, ClassificationDefinitionRequest? body, string? scope, string? tenant, HttpContext http,
            [FromServices] IClassificationAdminService service, CancellationToken ct) =>
        {
            if (!TryParseScope(scope, out var parsedScope))
            {
                return InvalidScope(http);
            }

            if (body is null || !TryParseDefinition(body, out var input))
            {
                return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "The classification definition is not valid.", http);
            }

            var result = await service.UpsertClassificationAsync(BuildCaller(http), code, input, parsedScope, tenant, ct).ConfigureAwait(false);
            return ToResult(result, http, ToResponse);
        })
        .RequireAuthorization(GatewayPolicies.GovernanceAdmin)
        .WithAudit(AuditLevel.Full, AuditEventTypes.ClassificationChanged)
        .WithIngressTransport(DataSourceTransportFlags.Rest)
        .WithTags("Governance");

        app.MapGet(ClassificationRoute + "/mask-rule", async (
            string code, string? scope, string? tenant, HttpContext http, [FromServices] IClassificationAdminService service, CancellationToken ct) =>
        {
            if (!TryParseScope(scope, out var parsedScope))
            {
                return InvalidScope(http);
            }

            var result = await service.GetMaskRuleAsync(BuildCaller(http), code, parsedScope, tenant, ct).ConfigureAwait(false);
            return ToResult(result, http, ToResponse);
        })
        .RequireAuthorization(GatewayPolicies.GovernanceAdmin)
        .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead)
        .WithIngressTransport(DataSourceTransportFlags.Rest)
        .WithTags("Governance");

        app.MapPut(ClassificationRoute + "/mask-rule", async (
            string code, MaskRuleRequest? body, string? scope, string? tenant, HttpContext http,
            [FromServices] IClassificationAdminService service, CancellationToken ct) =>
        {
            if (!TryParseScope(scope, out var parsedScope))
            {
                return InvalidScope(http);
            }

            if (body is null || !TryParseMaskRule(body, out var input))
            {
                return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "The mask rule is not valid.", http);
            }

            var result = await service.UpsertMaskRuleAsync(BuildCaller(http), code, input, parsedScope, tenant, ct).ConfigureAwait(false);
            return ToResult(result, http, ToResponse);
        })
        .RequireAuthorization(GatewayPolicies.GovernanceAdmin)
        .WithAudit(AuditLevel.Full, AuditEventTypes.MaskRuleChanged)
        .WithIngressTransport(DataSourceTransportFlags.Rest)
        .WithTags("Governance");

        return app.MapClassificationColumnEndpoints();
    }

    internal static ClassificationAdminCaller BuildCaller(HttpContext http)
    {
        var user = http.User;
        var sid = user.GetUserSid()?.Value ?? EndpointSecurity.GetCallerIdentity(http) ?? string.Empty;
        return new ClassificationAdminCaller(
            EndpointSecurity.GetRequestTenant(http),
            sid,
            EndpointSecurity.IsCanonicalClusterAdmin(user),
            GatewayPolicies.HasRole(user, GatewayRole.GovernanceAdmin));
    }

    internal static bool TryParseScope(string? scope, out ClassificationAdminScope parsed)
    {
        switch (scope)
        {
            case null or "" or "tenant":
                parsed = ClassificationAdminScope.Tenant;
                return true;
            case "global":
                parsed = ClassificationAdminScope.Global;
                return true;
            default:
                parsed = default;
                return false;
        }
    }

    internal static IResult InvalidScope(HttpContext http) =>
        ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "The scope must be 'tenant' or 'global'.", http);

    internal static IResult ToResult<T>(ClassificationAdminResult<T> result, HttpContext http, Func<T, object> map) =>
        result.Status switch
        {
            ClassificationAdminStatus.Ok => Results.Ok(map(result.Value!)),
            ClassificationAdminStatus.NotFound => ApiProblems.NotFound(result.ErrorCode ?? ApiErrorCodes.ResourceNotFound, "The requested resource was not found.", http),
            ClassificationAdminStatus.Forbidden => ApiProblems.Forbidden(result.ErrorCode ?? ApiErrorCodes.AccessDenied, "The caller is not permitted to perform this classification change.", http),
            _ => ApiProblems.BadRequest(result.ErrorCode ?? ApiErrorCodes.BadRequest, "The classification request is not valid.", http)
        };

    private static bool TryParseDefinition(ClassificationDefinitionRequest body, out ClassificationDefinitionInput input)
    {
        input = default!;
        var mode = Autheris.Domain.Model.ApproverMode.DataOwner;
        if (body.ApproverMode is not null && !ClassificationApproverModes.TryParse(body.ApproverMode, out mode))
        {
            return false;
        }

        input = new ClassificationDefinitionInput(
            body.DisplayName,
            body.Description,
            body.ParentCode,
            body.RestrictionRank,
            body.ClearGrantable,
            body.SeparateApproval,
            mode,
            body.StewardGroupSid,
            body.MaxClearDays,
            body.IsActive);
        return true;
    }

    internal static bool TryParseMaskRule(MaskRuleRequest body, out MaskRuleInput input)
    {
        input = default!;
        if (!ClassificationMaskFunctions.TryParse(body.MaskFunction, out var function))
        {
            return false;
        }

        var text = CatalogTextVisibility.Visible;
        if (body.CatalogText is not null && !ClassificationCatalogTextVisibilities.TryParse(body.CatalogText, out text))
        {
            return false;
        }

        input = new MaskRuleInput(function, body.ParamsJson, body.KeyRef, text);
        return true;
    }

    private static object ToResponse(DataClassification c) => new
    {
        scope = c.Scope,
        code = c.Code,
        displayName = c.DisplayName,
        description = c.Description,
        parentCode = c.ParentCode,
        restrictionRank = c.RestrictionRank,
        clearGrantable = c.ClearGrantable,
        separateApproval = c.SeparateApproval,
        approverMode = ClassificationApproverModes.ToCode(c.ApproverMode),
        stewardGroupSid = c.StewardGroupSid,
        maxClearDays = c.MaxClearDays,
        isActive = c.IsActive,
        version = c.Version
    };

    internal static object ToResponse(ClassificationMaskRule r) => new
    {
        scope = r.Scope,
        code = r.Code,
        maskFunction = ClassificationMaskFunctions.ToCode(r.MaskFunction),
        keyRef = r.KeyRef,
        catalogText = ClassificationCatalogTextVisibilities.ToCode(r.CatalogText),
        version = r.Version
    };
}
