namespace Autheris.Api.Endpoints;

using System;
using System.Threading.Tasks;
using Autheris.Api.Endpoints.Support;
using Autheris.Api.Errors;
using Autheris.Api.Middleware;
using Autheris.Api.Security;
using Autheris.Application.Governance.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Http;

public static partial class GovernanceEndpoints
{
    // Issue #340: an empty or malformed body binds to null/default members; validate after authorization and
    // fail closed with a sanitized 400 instead of letting a downstream guard surface as 500.
    private static IResult InvalidRequest(HttpContext context) =>
        SafeErrorResults.BadRequest(ApiErrorCodes.BadRequest, "The request body is missing or contains invalid or missing required fields.", context);

    internal static async Task<IResult> RegisterSunsettingRuleAsync(
        FieldSunsettingRule? rule,
        ISchemaSunsettingService sunsettingService,
        HttpContext context)
    {
        // SEC M-11: Sunsetting rules are global (not tenant-scoped) -> canonical ClusterAdmin only (OD-9).
        if (!EndpointSecurity.IsGlobalGovernanceAdmin(context.User))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        if (rule is null
            || rule.Id == Guid.Empty
            || string.IsNullOrWhiteSpace(rule.TargetTable)
            || string.IsNullOrWhiteSpace(rule.FieldName)
            || rule.DeprecatedAt == default
            || rule.SunsetAt == default)
        {
            return InvalidRequest(context);
        }

        await sunsettingService.RegisterRuleAsync(rule, context.RequestAborted);
        return Results.Created($"/api/governance/sunsetting/rules/{rule.Id}", rule);
    }

    internal static async Task<IResult> EvaluateSunsettingAsync(
        EvaluateFieldSunsettingRequest? request,
        ISchemaSunsettingService sunsettingService,
        HttpContext context)
    {
        var isPrivileged = GatewayPolicies.HasAnyRole(context.User, [GatewayRole.GovernanceAdmin, GatewayRole.SchemaPublisher, GatewayRole.DataOwner, GatewayRole.Consumer]);
        if (!isPrivileged)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        if (request is null
            || string.IsNullOrWhiteSpace(request.TargetTable)
            || string.IsNullOrWhiteSpace(request.FieldName))
        {
            return InvalidRequest(context);
        }

        var evaluation = await sunsettingService.EvaluateFieldAsync(
            request.TargetTable,
            request.FieldName,
            request.EvaluationDate,
            context.RequestAborted);

        if (evaluation == null)
        {
            return Results.Ok(new { isDeprecated = false });
        }

        if (evaluation.IsHardSunsetBlocked)
        {
            context.Response.Headers["Sunset"] = evaluation.HttpSunsetHeader;
            return ApiProblems.Problem(
                StatusCodes.Status410Gone,
                ApiErrorCodes.ApiVersionSunset,
                ApiProblems.SanitizeOperatorText(evaluation.DeprecationNotice, "The requested field has been sunset."),
                context,
                title: "Gone",
                extraExtensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["phase"] = evaluation.Phase.ToString(),
                    ["sunsetDate"] = evaluation.SunsetDate,
                    ["replacement"] = evaluation.Rule.ReplacementField
                });
        }

        if (evaluation.ShouldInjectSyntheticLatency && evaluation.SyntheticLatencyMs > 0)
        {
            await Task.Delay(evaluation.SyntheticLatencyMs, context.RequestAborted);
        }

        if (evaluation.ShouldRejectWith426)
        {
            context.Response.Headers["Sunset"] = evaluation.HttpSunsetHeader;
            return ApiProblems.Problem(
                StatusCodes.Status426UpgradeRequired,
                ApiErrorCodes.ApiVersionUpgradeRequired,
                ApiProblems.SanitizeOperatorText("Chaos Testing: Upgrade Required. " + evaluation.DeprecationNotice, "Upgrade required."),
                context,
                title: "Upgrade Required",
                extraExtensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["phase"] = evaluation.Phase.ToString(),
                    ["sunsetDate"] = evaluation.SunsetDate,
                    ["replacement"] = evaluation.Rule.ReplacementField
                });
        }

        context.Response.Headers["Sunset"] = evaluation.HttpSunsetHeader;
        return Results.Ok(evaluation);
    }
}
