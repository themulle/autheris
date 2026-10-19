namespace Autheris.Api.Endpoints;

using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Autheris.Application.DataCatalog.Interfaces;
using Autheris.Application.Governance;
using Autheris.Application.Governance.Interfaces;
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
            return Results.StatusCode(StatusCodes.Status403Forbidden);
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
            return Results.BadRequest(new { error = "Invalid JSON in consent manifest.", details = $"The manifest could not be parsed{position}." });
        }

        if (manifest == null)
        {
            return Results.BadRequest(new { error = "Manifest payload is null or empty." });
        }

        try
        {
            var result = await applyService.ApplyManifestAsync(context.User, manifest, dryRun, ct);
            return Results.Ok(result);
        }
        catch (Autheris.Application.Governance.Services.DeclarativeDatasetCatalogException ex)
        {
            return Results.Json(new { error = ex.ErrorCode }, statusCode: ex.StatusCode);
        }
        catch (Autheris.Application.Governance.Services.DeclarativeManifestValidationException ex)
        {
            // SEC-GOV158I1-03: fixed message naming only the policy index, never the rejected text.
            return Results.Json(new { error = "VALIDATION_ERROR", message = $"Manifest policy at index {ex.PolicyIndex} has an invalid description." }, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (Autheris.Application.Governance.Grants.GrantPolicyViolationException ex)
        {
            return Results.Json(
                new { error = ex.ErrorCode, message = ex.Decision.Reason, maxValidTo = ex.MaxValidTo },
                statusCode: StatusCodes.Status400BadRequest);
        }
    }
}
