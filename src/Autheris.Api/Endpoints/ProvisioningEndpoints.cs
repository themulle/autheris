namespace Autheris.Api.Endpoints;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Errors;
using Autheris.Api.Extensions;
using Autheris.Api.Security;
using Autheris.Application.Governance.Provisioning;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Options.DataSources;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

/// <summary>The Unity Catalog provisioning state of one grant, as consumers read it with the grant.</summary>
/// <param name="Target">The provisioning target, <c>databricks:{dataSource}</c>.</param>
/// <param name="State"><c>PENDING</c>, <c>PROVISIONED</c>, <c>UNSUPPORTED</c>, <c>FAILED</c> or <c>REVOKED</c>.</param>
/// <param name="ReasonCode">A stable reason code for unsupported, failed or pending grants; never error text.</param>
public sealed record GrantProvisioningDto(string Target, string State, string? ReasonCode, DateTimeOffset? VerifiedAt);

/// <summary>Provisioning status of one Databricks data source for its administrators: counts only, no principals.</summary>
public sealed record DatabricksProvisioningStatusDto(
    string DataSource,
    string Mode,
    string DriftMode,
    IReadOnlyDictionary<string, int> Privileges,
    long OverdueRevokes,
    IReadOnlyDictionary<string, int> Grants,
    IReadOnlyDictionary<string, int> ErrorCodes);

/// <summary>
/// The provisioning field of grant responses and the administrators' status endpoint
/// <c>GET /api/v1/admin/provisioning/databricks/{dataSource}</c>.
/// </summary>
public static class ProvisioningEndpoints
{
    public static IEndpointRouteBuilder MapProvisioningEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(
                "/api/v1/admin/provisioning/databricks/{dataSource}",
                (string dataSource, HttpContext httpContext, IOptionsMonitor<GatewayOptions> options, IUcProvisioningStore store, CancellationToken ct) =>
                    GetStatusAsync(dataSource, httpContext, options, store, ct))
            .RequireAuthorization(GatewayPolicies.GovernanceAdmin)
            .WithIngressTransport(DataSourceTransportFlags.Rest)
            .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        return app;
    }

    internal static async Task<IResult> GetStatusAsync(
        string dataSource, HttpContext httpContext, IOptionsMonitor<GatewayOptions> options, IUcProvisioningStore store, CancellationToken ct)
    {
        if (!GatewayPolicies.HasRequestRole(httpContext, GatewayRole.GovernanceAdmin))
        {
            return ApiProblems.Problem(StatusCodes.Status403Forbidden, ApiErrorCodes.AccessDenied, "GovernanceAdmin role is required.");
        }

        // Only administrators of the provisioning tenant see the status; for anyone else the data source is not provisioned.
        var source = Find(options.CurrentValue, dataSource);
        if (source is not { } found ||
            UcProvisioningPlanner.ProvisioningTenant(found.Provisioning) is not { } tenant ||
            EndpointSecurity.GetRequestTenant(httpContext) != tenant)
        {
            return ApiProblems.Problem(StatusCodes.Status404NotFound, ApiErrorCodes.ResourceNotFound, "The data source is not provisioning-managed.");
        }

        var ledger = await store.ListLedgerAsync(found.Name, ct).ConfigureAwait(false);
        var feedback = await store.ListFeedbackAsync(tenant, found.Name, ct).ConfigureAwait(false);
        var status = new DatabricksProvisioningStatusDto(
            found.Name,
            found.Provisioning.Mode.ToString(),
            found.Provisioning.DriftMode.ToString(),
            Count(ledger.Select(row => row.State.ToStored())),
            ledger.Count(row => row.State == UcLedgerState.RevokePending && row.Attempts >= UcProvisioningReconciler.OverdueRevokeAttempts),
            Count(feedback.Select(row => row.State.ToStored())),
            Count(ledger.Where(row => row.LastErrorCode is not null && row.State != UcLedgerState.Applied).Select(row => row.LastErrorCode!)));
        return Results.Ok(status);
    }

    /// <summary>
    /// The provisioning state of each grant on a provisioning-managed data source, keyed by grant id. Grants on other sources are
    /// absent (their field stays null); grants of another tenant than the provisioning tenant are unsupported.
    /// </summary>
    internal static async Task<IReadOnlyDictionary<Guid, GrantProvisioningDto>> LookupAsync(
        IReadOnlyCollection<Consent> grants, GatewayOptions options, IUcProvisioningStore? store, CancellationToken ct)
    {
        var result = new Dictionary<Guid, GrantProvisioningDto>();
        if (store is null || grants.Count == 0)
        {
            return result;
        }

        foreach (var bySource in grants.GroupBy(g => g.IsPatternGrant ? g.DataSource ?? string.Empty : g.TableIdentifier.Domain, StringComparer.OrdinalIgnoreCase))
        {
            if (Find(options, bySource.Key) is not { } found)
            {
                continue;
            }

            var target = "databricks:" + found.Name;
            var tenant = UcProvisioningPlanner.ProvisioningTenant(found.Provisioning);
            var own = bySource.Where(g => tenant is { } t && g.TenantId == t).ToList();
            var rows = own.Count == 0 || tenant is null
                ? new Dictionary<Guid, UcGrantFeedback>()
                : await store.GetFeedbackAsync(tenant.Value, own.Select(g => g.Id).ToList(), ct).ConfigureAwait(false);
            foreach (var grant in bySource)
            {
                result[grant.Id] = grant.IsPatternGrant
                    ? new GrantProvisioningDto(target, UcGrantState.Unsupported.ToStored(), UcReasonCodes.PatternGrant, null)
                    : tenant is null || grant.TenantId != tenant
                        ? new GrantProvisioningDto(target, UcGrantState.Unsupported.ToStored(), UcReasonCodes.TenantNotProvisioned, null)
                        : rows.TryGetValue(grant.Id, out var row)
                            ? new GrantProvisioningDto(target, row.State.ToStored(), row.ReasonCode, row.VerifiedAt)
                            : new GrantProvisioningDto(target, UcGrantState.Pending.ToStored(), null, null);
            }
        }

        return result;
    }

    private static (string Name, DatabricksProvisioningOptions Provisioning)? Find(GatewayOptions options, string? dataSource)
    {
        if (string.IsNullOrWhiteSpace(dataSource))
        {
            return null;
        }

        foreach (var (name, _, provisioning) in UcProvisioningReconciler.ProvisionedSources(options))
        {
            if (string.Equals(name, dataSource, StringComparison.OrdinalIgnoreCase))
            {
                return (name, provisioning);
            }
        }

        return null;
    }

    private static Dictionary<string, int> Count(IEnumerable<string> values) =>
        values.GroupBy(v => v, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
}
