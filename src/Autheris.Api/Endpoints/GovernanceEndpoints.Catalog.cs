namespace Autheris.Api.Endpoints;

using Autheris.Api.Errors;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Governance;
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
    /// </summary>
    internal static async Task<IResult> ApplyConsentManifestAsync(
        HttpContext context,
        IDeclarativeConsentApplyService applyService,
        CancellationToken ct)
    {
        var isPrivileged = GatewayPolicies.HasAnyRole(context.User, [GatewayRole.GovernanceAdmin, GatewayRole.ClusterAdmin]);
        if (!isPrivileged)
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
            var result = await applyService.ApplyManifestAsync(context.User, manifest, dryRun, callerContext, pruneMode, ct);
            return Results.Ok(result);
        }
        catch (ConsentRevocationForbiddenException)
        {
            return ApiProblems.Forbidden(ApiErrorCodes.AccessDenied, ConsentRevocationForbiddenException.FixedMessage, context);
        }
        catch (Autheris.Application.Governance.Services.ConsentApplyForbiddenException)
        {
            return ApiProblems.Forbidden(ApiErrorCodes.AccessDenied, "You do not have permission to apply declarative consent manifests.", context);
        }
        catch (Autheris.Application.Governance.Services.DeclarativePruneCapExceededException)
        {
            return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "The number of consents to prune exceeds the maximum allowed per run.", context);
        }
        catch (Autheris.Application.Governance.Services.DeclarativePruneThresholdExceededException)
        {
            return ApiProblems.Conflict(ApiErrorCodes.Conflict, "Prune exceeds 50% of active declarative consents for manifest grantees; retry with prune=force.", context);
        }
        catch (Autheris.Application.Governance.Services.DeclarativeDatasetCatalogException ex)
        {
            return ApiProblems.Problem(ex.StatusCode, ex.ErrorCode, "The dataset catalog declaration was rejected.", context);
        }
        catch (Autheris.Application.Governance.Services.DeclarativeManifestValidationException ex)
        {
            // fixed message naming only the policy index and the violated rule, never the rejected text.
            return ApiProblems.BadRequest(
                ApiErrorCodes.BadRequest,
                Autheris.Application.Governance.Services.DeclarativeManifestValidationException.Describe(ex.PolicyIndex, ex.Violation),
                context);
        }
        catch (ArgumentException ex) when (ex.ParamName == "manifest")
        {
            // an unsupported grantee (a role) or an invalid effect: a client error with a fixed message, never the rejected text.
            return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "The manifest contains a policy that is not supported.", context);
        }
        catch (Autheris.Application.Governance.Grants.GrantPolicyViolationException ex)
        {
            return ApiProblems.BadRequest(
                ex.ErrorCode,
                "The grant violates the grant rules.",
                context,
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["maxValidTo"] = ex.MaxValidTo });
        }
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
