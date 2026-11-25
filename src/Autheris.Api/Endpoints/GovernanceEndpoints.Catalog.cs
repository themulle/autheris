namespace Autheris.Api.Endpoints;

using Autheris.Api.Errors;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Governance;
using Autheris.Application.Governance.Declarative;
using Autheris.Application.Governance.Interfaces;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Http;

public static partial class GovernanceEndpoints
{
    internal static async Task<IResult> IngestOpenApiAsync(HttpContext context, IOpenApiIngestionUseCase ingestionUseCase)
    {
        // Role first: an unprivileged caller gets 403 before any tenant resolution (a malformed tenant claim throws) or body read.
        if (!ingestionUseCase.HasPublishRole(context.User))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        var callerTenant = EndpointSecurity.GetRequestTenant(context);
        var isClusterAdmin = EndpointSecurity.IsCanonicalClusterAdmin(context.User);

        string? domain = context.Request.Query.TryGetValue("domain", out var dVal) && !string.IsNullOrWhiteSpace(dVal)
            ? dVal.ToString()
            : null;

        var baseUrl = context.Request.Query.TryGetValue("baseUrl", out var bVal) && !string.IsNullOrWhiteSpace(bVal)
            ? bVal.ToString().Trim()
            : null;

        // Reject before the body is read; IngestAsync re-checks the same authorization.
        if (!ingestionUseCase.IsAuthorized(context.User, callerTenant, isClusterAdmin, domain))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        // SEC M-07: Bounded read instead of a bypassable Content-Length check.
        var (json, tooLarge) = await EndpointSecurity.TryReadBodyAsync(
            context.Request,
            20 * 1024 * 1024,
            "OpenAPI specification exceeds maximum allowed size (20 MB).",
            context.RequestAborted);
        if (json == null)
        {
            return tooLarge!;
        }

        var outcome = await ingestionUseCase.IngestAsync(
            context.User, callerTenant, isClusterAdmin, domain, baseUrl, json, context.TraceIdentifier, context.RequestAborted);
        return outcome.Kind switch
        {
            OpenApiIngestionOutcomeKind.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
            OpenApiIngestionOutcomeKind.Rejected => Results.BadRequest(outcome.Result),
            _ => Results.Ok(outcome.Result)
        };
    }

    /// <summary>
    /// Issue #40: GitOps / declarative consent and row filter import. An unparsable manifest answers a curated 400: the text of the
    /// <see cref="JsonException"/> (an offending value, a type name, a path) never reaches the response, only the position.
    /// Amendment 6: Data Governance applies directly (or proposes with <c>mode=propose</c>); IT administration always submits a proposal, which has
    /// no effect until one Data Governance confirmation. An explicit <c>mode=apply</c> by a caller without Data Governance is refused.
    /// </summary>
    internal static Task<IResult> ApplyConsentManifestAsync(
        HttpContext context,
        IDeclarativeConsentApplyService applyService,
        CancellationToken ct) =>
        ApplyOrProposeConsentManifestAsync(context, applyService, proposals: null, ct);

    /// <inheritdoc cref="ApplyConsentManifestAsync"/>
    /// <param name="proposals">The proposal service; without it a caller that would submit a proposal is refused (fail closed).</param>
    internal static async Task<IResult> ApplyOrProposeConsentManifestAsync(
        HttpContext context,
        IDeclarativeConsentApplyService applyService,
        [Microsoft.AspNetCore.Mvc.FromServices] ManifestProposalService? proposals,
        CancellationToken ct)
    {
        var isGovernance = GatewayPolicies.HasRequestRole(context, GatewayRole.GovernanceAdmin);
        var isIt = GatewayPolicies.HasRequestRole(context, GatewayRole.TenantAdmin);
        if (!isGovernance && !isIt)
        {
            return ApiProblems.Forbidden(ApiErrorCodes.AccessDenied, "You do not have permission to apply declarative consent manifests.", context);
        }

        if (!TryParseManifestMode(context.Request.Query, out var propose))
        {
            return ApiProblems.BadRequest(ApiErrorCodes.InvalidQuery, "Invalid 'mode' query parameter. Allowed values are 'apply' and 'propose'.", context);
        }

        if (propose == false && !isGovernance)
        {
            return ApiProblems.Forbidden(ApiErrorCodes.AccessDenied, "Only Data Governance applies declarative consent manifests directly; submit a proposal instead.", context);
        }

        var asProposal = propose ?? !isGovernance;
        if (asProposal && proposals is null)
        {
            return ApiProblems.Forbidden(ApiErrorCodes.AccessDenied, "You do not have permission to apply declarative consent manifests.", context);
        }

        if (!TryParsePruneMode(context.Request.Query, out var pruneMode))
        {
            return ApiProblems.BadRequest(ApiErrorCodes.InvalidQuery, "Invalid 'prune' query parameter. Allowed values are 'false', 'true', and 'force'.", context);
        }

        var dryRun = context.Request.Query.TryGetValue("dryRun", out var dryRunVal) &&
                     bool.TryParse(dryRunVal, out var parsedDryRun) && parsedDryRun;

        var (json, tooLarge) = await EndpointSecurity.TryReadBodyAsync(
            context.Request,
            10 * 1024 * 1024,
            "Declarative consent manifest exceeds maximum allowed size (10 MB).",
            ct);

        if (json == null)
        {
            return tooLarge!;
        }

        DeclarativeConsentManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<DeclarativeConsentManifest>(json, GitOpsJsonOptions);
        }
        catch (JsonException ex)
        {
            var position = ex.LineNumber is { } line && ex.BytePositionInLine is { } column
                ? $" (line {line + 1}, position {column + 1})"
                : string.Empty;
            return ApiProblems.BadRequest(ApiErrorCodes.InvalidJson, $"The manifest could not be parsed{position}.", context);
        }

        if (manifest == null)
        {
            return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "The manifest payload is null or empty.", context);
        }

        // SR-M-10: the resolved security context carries the caller's (plugin-enriched) groups and roles for the self-grant check.
        Autheris.Domain.Security.SecurityPrincipalContext callerContext;
        try
        {
            callerContext = EndpointSecurity.GetSecurityContext(context);
        }
        catch (UnauthorizedAccessException)
        {
            // Authenticated without a resolvable SID: refused (403) instead of surfacing as a 500.
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        try
        {
            if (asProposal)
            {
                if (dryRun)
                {
                    var preview = await proposals!.PreviewAsync(context.User, manifest, callerContext, pruneMode, ct);
                    return Results.Ok(new { preview.Preview.DryRun, preview.Diff });
                }

                var proposal = await proposals!.SubmitAsync(context.User, manifest, callerContext, pruneMode, ct);
                return Results.Accepted($"/api/v1/governance/consents/proposals/{proposal.Id:D}", proposal);
            }

            var result = await applyService.ApplyManifestAsync(context.User, manifest, dryRun, callerContext, pruneMode, ct);
            return Results.Ok(result);
        }
        catch (Exception ex) when (MapManifestException(ex, context) is { } mapped)
        {
            return mapped;
        }
    }

    /// <summary>Amendment 6: a manifest proposal with its diff per grant (IT administration or Data Governance of the tenant).</summary>
    internal static async Task<IResult> GetManifestProposalAsync(HttpContext context, Guid id, [Microsoft.AspNetCore.Mvc.FromServices] ManifestProposalService proposals, CancellationToken ct)
    {
        try
        {
            return Results.Ok(await proposals.GetAsync(context.User, id, ct));
        }
        catch (Exception ex) when (MapManifestException(ex, context) is { } mapped)
        {
            return mapped;
        }
    }

    /// <summary>Amendment 6: one Data Governance confirmation applies the proposal after re-validating it against the current state.</summary>
    internal static async Task<IResult> ConfirmManifestProposalAsync(HttpContext context, Guid id, [Microsoft.AspNetCore.Mvc.FromServices] ManifestProposalService proposals, CancellationToken ct)
    {
        Autheris.Domain.Security.SecurityPrincipalContext callerContext;
        try
        {
            callerContext = EndpointSecurity.GetSecurityContext(context);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        try
        {
            return Results.Ok(await proposals.ConfirmAsync(context.User, id, callerContext, ct));
        }
        catch (Exception ex) when (MapManifestException(ex, context) is { } mapped)
        {
            return mapped;
        }
    }

    /// <summary>Amendment 6: one Data Governance rejection closes the proposal; the reason is optional.</summary>
    internal static async Task<IResult> RejectManifestProposalAsync(HttpContext context, Guid id, [Microsoft.AspNetCore.Mvc.FromServices] ManifestProposalService proposals, CancellationToken ct)
    {
        string? reason = null;
        if (context.Request.ContentLength is > 0 || context.Request.Headers.TransferEncoding.Count > 0)
        {
            var (json, tooLarge) = await EndpointSecurity.TryReadBodyAsync(context.Request, 16 * 1024, "The rejection body exceeds the maximum allowed size (16 KB).", ct);
            if (json == null)
            {
                return tooLarge!;
            }

            if (!string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    reason = JsonSerializer.Deserialize<ManifestProposalRejectRequest>(json, GitOpsJsonOptions)?.Reason;
                }
                catch (JsonException)
                {
                    return ApiProblems.BadRequest(ApiErrorCodes.InvalidJson, "The rejection body could not be parsed.", context);
                }
            }
        }

        try
        {
            return Results.Ok(await proposals.RejectAsync(context.User, id, reason, ct));
        }
        catch (ArgumentException ex) when (ex.ParamName == "reason")
        {
            return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "The rejection reason is invalid.", context);
        }
        catch (Exception ex) when (MapManifestException(ex, context) is { } mapped)
        {
            return mapped;
        }
    }

    /// <summary>Maps the refusals of the apply and proposal paths to fixed client responses; null for anything else (rethrown).</summary>
    private static IResult? MapManifestException(Exception exception, HttpContext context) => exception switch
    {
        ConsentRevocationForbiddenException =>
            ApiProblems.Forbidden(ApiErrorCodes.AccessDenied, ConsentRevocationForbiddenException.FixedMessage, context),
        Autheris.Application.Governance.Services.ConsentApplyForbiddenException =>
            ApiProblems.Forbidden(ApiErrorCodes.AccessDenied, "You do not have permission to apply declarative consent manifests.", context),
        ManifestProposalSeparationOfDutiesException sod =>
            ApiProblems.Forbidden(ApiErrorCodes.AccessDenied, sod.Message, context),
        ManifestProposalNotFoundException =>
            ApiProblems.NotFound(ApiErrorCodes.ResourceNotFound, "The manifest proposal was not found.", context),
        // The recomputed diff is stored on the proposal (the governed problem body carries no free-form members): review it with GET and confirm again.
        ManifestProposalStaleException => ApiProblems.Conflict(
            ApiErrorCodes.ManifestProposalStale,
            "The grant state changed since the proposal was submitted; the recomputed diff is stored on the proposal. Review it and confirm again.",
            context),
        ManifestProposalClosedException closed =>
            ApiProblems.Conflict(ApiErrorCodes.ManifestProposalClosed, $"The manifest proposal is no longer pending ({closed.Status}).", context),
        DeclarativeManifestNameException =>
            ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "The manifest name is invalid.", context),
        Autheris.Application.Governance.Services.DeclarativePruneCapExceededException =>
            ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "The number of consents to prune exceeds the maximum allowed per run.", context),
        Autheris.Application.Governance.Services.DeclarativePruneThresholdExceededException =>
            ApiProblems.Conflict(ApiErrorCodes.Conflict, "Prune exceeds 50% of active declarative consents for manifest grantees; retry with prune=force.", context),
        Autheris.Application.Governance.Services.DeclarativeDatasetCatalogException ex =>
            ApiProblems.Problem(ex.StatusCode, ex.ErrorCode, "The dataset catalog declaration was rejected.", context),
        // fixed message naming only the policy index and the violated rule, never the rejected text.
        Autheris.Application.Governance.Services.DeclarativeManifestValidationException ex => ApiProblems.BadRequest(
            ApiErrorCodes.BadRequest,
            Autheris.Application.Governance.Services.DeclarativeManifestValidationException.Describe(ex.PolicyIndex, ex.Violation),
            context),
        // an unsupported grantee (a role) or an invalid effect: a client error with a fixed message, never the rejected text.
        ArgumentException { ParamName: "manifest" } =>
            ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "The manifest contains a policy that is not supported.", context),
        Autheris.Application.Governance.Grants.GrantPolicyViolationException ex => ApiProblems.BadRequest(
            ex.ErrorCode,
            "The grant violates the grant rules.",
            context,
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["maxValidTo"] = ex.MaxValidTo }),
        _ => null
    };

    /// <summary>Absent: the caller's default (Data Governance applies, IT proposes); <c>apply</c> or <c>propose</c>; anything else is invalid.</summary>
    private static bool TryParseManifestMode(IQueryCollection query, out bool? propose)
    {
        propose = null;
        if (!query.TryGetValue("mode", out var values))
        {
            return true;
        }

        if (values.Count != 1 || values[0] is not { } raw)
        {
            return false;
        }

        if (string.Equals(raw, "apply", StringComparison.OrdinalIgnoreCase))
        {
            propose = false;
            return true;
        }

        if (string.Equals(raw, "propose", StringComparison.OrdinalIgnoreCase))
        {
            propose = true;
            return true;
        }

        return false;
    }

    private static bool TryParsePruneMode(IQueryCollection query, out DeclarativePruneMode pruneMode)
    {
        if (!query.TryGetValue("prune", out var values))
        {
            pruneMode = DeclarativePruneMode.False;
            return true;
        }

        if (values.Count != 1 || values[0] is not { } raw)
        {
            pruneMode = DeclarativePruneMode.False;
            return false;
        }

        if (string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase))
        {
            pruneMode = DeclarativePruneMode.False;
            return true;
        }

        if (string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase))
        {
            pruneMode = DeclarativePruneMode.True;
            return true;
        }

        if (string.Equals(raw, "force", StringComparison.OrdinalIgnoreCase))
        {
            pruneMode = DeclarativePruneMode.Force;
            return true;
        }

        pruneMode = DeclarativePruneMode.False;
        return false;
    }
}

/// <summary>Body of a manifest proposal rejection; the reason is optional.</summary>
public sealed class ManifestProposalRejectRequest
{
    public string? Reason { get; set; }
}
