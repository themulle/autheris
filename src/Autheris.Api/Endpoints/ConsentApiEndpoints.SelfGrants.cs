namespace Autheris.Api.Endpoints;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Errors;
using Autheris.Api.Extensions;
using Autheris.Api.Security;
using Autheris.Application.ConsentRequests;
using Autheris.Application.Governance.Requests;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

public static partial class ConsentApiEndpoints
{
    private static void MapSelfGrantEndpoints(RouteGroupBuilder group)
    {
        group.MapPost("/self-grants", CreateSelfGrantAsync)
            .RequireAuthorization(GatewayPolicies.ItAdmin)
            .WithAudit(AuditLevel.Full, AuditEventTypes.SelfGrant);

        group.MapPost("/grants/{id:guid}/renew", RenewGrantAsync)
            .RequireAuthorization(GatewayPolicies.AuthenticatedCaller)
            .WithAudit(AuditLevel.Full, AuditEventTypes.GrantRenewed);
    }

    internal static async Task<IResult> RenewGrantAsync(
        Guid id,
        RenewGrantRequestBody? body,
        HttpContext context,
        [FromServices] PermissionRequestEngine engine,
        CancellationToken ct = default)
    {
        if (body is null)
        {
            return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "Request body is required.", context);
        }

        var renewer = context.GetSecurityContext();
        if (!renewer.IsAuthenticated || string.IsNullOrWhiteSpace(renewer.UserSid.Value))
        {
            return ApiProblems.Forbidden(ConsentDecisionErrorCodes.DecisionNotPermitted, "Authenticated user SID is required.", context);
        }

        try
        {
            var result = await engine.RenewGrantAsync(new GrantRenewalCommand(id, renewer, body.ValidTo, body.Justification), ct).ConfigureAwait(false);
            return Results.Ok(new
            {
                grantId = result.Renewed.Id,
                previousGrantId = result.PreviousConsentId,
                validTo = result.Renewed.ValidTo,
                renewalCount = result.Renewed.RenewalCount
            });
        }
        catch (ConsentDecisionException ex)
        {
            var detail = ex.StatusCode switch
            {
                StatusCodes.Status403Forbidden => "Grant renewal is not permitted.",
                StatusCodes.Status404NotFound => "Grant was not found.",
                StatusCodes.Status409Conflict => "Grant cannot be renewed in its current state.",
                _ => "Grant renewal request is invalid."
            };
            return ApiProblems.Problem(ex.StatusCode, ex.ErrorCode, detail, context);
        }
    }

    internal static async Task<IResult> CreateSelfGrantAsync(
        SelfGrantRequestBody? body,
        HttpContext context,
        [FromServices] SelfGrantService selfGrantService,
        CancellationToken ct = default)
    {
        if (body is null)
        {
            return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "Request body is required.", context);
        }

        var actor = context.GetSecurityContext();
        if (!actor.IsAuthenticated || string.IsNullOrWhiteSpace(actor.UserSid.Value))
        {
            return ApiProblems.Forbidden(ConsentDecisionErrorCodes.DecisionNotPermitted, "Authenticated user SID is required.", context);
        }

        if (!TryParseSelfGrantKind(body.Kind, out var kind))
        {
            return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "Unsupported kind; expected 'data' or 'unmask'.", context);
        }

        var granteeType = GranteeType.User;
        if (!string.IsNullOrWhiteSpace(body.GranteeType) &&
            !Enum.TryParse(body.GranteeType, ignoreCase: true, out granteeType))
        {
            return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "Unsupported granteeType.", context);
        }

        if (!body.ValidTo.HasValue)
        {
            return ApiProblems.Problem(StatusCodes.Status422UnprocessableEntity, Autheris.Application.Governance.Grants.GrantErrorCodes.EndDateRequired, "A finite validTo is required.", context);
        }

        var (domain, schema, tableName) = ResolveTableParts(
            new CreateConsentRequestBody(Domain: body.Domain, Schema: body.Schema, TableName: body.TableName, Table: body.Table));
        if (string.IsNullOrWhiteSpace(tableName))
        {
            return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "A table target is required.", context);
        }

        var command = new SelfGrantCommand(
            actor,
            kind,
            PermissionTarget.ForTable(new TableIdentifier(domain, schema, tableName), body.Columns),
            granteeType,
            string.IsNullOrWhiteSpace(body.Grantee) ? actor.UserSid.Value : body.Grantee.Trim(),
            body.ValidTo.Value,
            body.Justification ?? string.Empty);

        try
        {
            var result = await selfGrantService.SelfGrantAsync(command, ct).ConfigureAwait(false);
            return Results.Ok(new
            {
                requestId = result.Request.Id,
                status = result.Request.Status,
                grantId = result.PrimaryGrant?.GrantId
            });
        }
        catch (ConsentDecisionException ex)
        {
            var detail = ex.StatusCode switch
            {
                StatusCodes.Status403Forbidden => "Self-grant is not permitted.",
                StatusCodes.Status404NotFound => "Target was not found.",
                StatusCodes.Status409Conflict => "Self-grant could not be completed.",
                _ => "Self-grant request is invalid."
            };
            return ApiProblems.Problem(ex.StatusCode, ex.ErrorCode, detail, context);
        }
    }

    private static bool TryParseSelfGrantKind(string? value, out PermissionKind kind)
    {
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "data", StringComparison.OrdinalIgnoreCase))
        {
            kind = PermissionKind.Data;
            return true;
        }

        if (string.Equals(value, "unmask", StringComparison.OrdinalIgnoreCase))
        {
            kind = PermissionKind.Unmask;
            return true;
        }

        kind = PermissionKind.Data;
        return false;
    }
}

/// <summary>
/// Self-grant request body. Deliberately has no self-grant flag; the service decides from identity and ownership facts.
/// </summary>
public sealed record SelfGrantRequestBody(
    string? Domain = null,
    string? Schema = null,
    string? TableName = null,
    string? Table = null,
    IReadOnlyList<string>? Columns = null,
    string? Kind = null,
    string? Grantee = null,
    string? GranteeType = null,
    DateTimeOffset? ValidTo = null,
    string? Justification = null);

/// <summary>Grant renewal body: a new end date and a fresh justification.</summary>
public sealed record RenewGrantRequestBody(DateTimeOffset? ValidTo = null, string? Justification = null);
