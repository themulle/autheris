namespace Autheris.Application.Governance.Provisioning;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Options;

public sealed partial class UcProvisioningReconciler
{
    public const string ProvisioningOffReason = "PROVISIONING_OFF";
    public const string DryRunReason = "DRY_RUN";
    public const string CatalogAccessMissingReason = "CATALOG_ACCESS_MISSING";
    public const string CatalogAccessUnverifiedReason = "CATALOG_ACCESS_UNVERIFIED";

    /// <summary>The workspace group every account user and service principal belongs to.</summary>
    public const string AccountUsersGroup = "account users";
    public const string PlanningFailedReason = "PLANNING_FAILED";
    public const string RevokePendingReason = "REVOKE_PENDING";

    private static readonly string[] CatalogAccessPrivileges = [UcPrivileges.UseCatalog, "ALL_PRIVILEGES"];

    /// <summary>
    /// Writes the feedback of every active consent and marks consents that are no longer active as revoked. A consent is reported
    /// provisioned only when all its privileges were confirmed by a read and its principal effectively holds <c>USE_CATALOG</c>.
    /// Only changed rows are written.
    /// </summary>
    private async Task UpdateFeedbackAsync(
        string dataSource,
        DatabricksProvisioningMode mode,
        TenantId tenant,
        UcDesiredState desired,
        UcApplyOutcome outcome,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var existing = (await store.ListFeedbackAsync(tenant, dataSource, ct).ConfigureAwait(false)).ToDictionary(f => f.ConsentId);
        var catalogAccess = new Dictionary<(string Catalog, string Principal), string?>();
        var changed = new List<UcGrantFeedback>();
        var events = new List<UcFeedbackEvent>();
        var active = new HashSet<Guid>();

        foreach (var plan in desired.Consents)
        {
            active.Add(plan.Consent.Id);
            existing.TryGetValue(plan.Consent.Id, out var before);
            var (state, reason) = mode switch
            {
                DatabricksProvisioningMode.Off => (UcGrantState.Unsupported, (string?)ProvisioningOffReason),
                _ when plan.PendingReason is { } pending => (UcGrantState.Pending, pending),
                _ when !plan.IsProvisionable => (UcGrantState.Unsupported, plan.UnsupportedReason),
                DatabricksProvisioningMode.DryRun => (UcGrantState.Pending, DryRunReason),
                _ => await EnforcedStateAsync(dataSource, plan, outcome, before, catalogAccess, ct).ConfigureAwait(false)
            };

            if (before is not null && before.State == state && string.Equals(before.ReasonCode, reason, StringComparison.Ordinal))
            {
                continue;
            }

            // A transient failure (a lookup or read that failed this cycle) does not change what consumers were told: the reported
            // state stays until a cycle decides it, so a recovery never announces the same transition twice.
            if (state == UcGrantState.Pending && IsTransient(reason) && before is { State: UcGrantState.Provisioned or UcGrantState.Failed })
            {
                continue;
            }

            changed.Add(new UcGrantFeedback(tenant, plan.Consent.Id, dataSource, state, reason, now, state == UcGrantState.Provisioned ? now : null));

            // Consumers learn the transitions that matter to them: effective in Unity Catalog, or not achievable.
            if (state is UcGrantState.Provisioned or UcGrantState.Failed && before?.State != state)
            {
                events.Add(new UcFeedbackEvent(plan.Consent.Id, plan.Consent.TableIdentifier, state == UcGrantState.Provisioned, now));
            }
        }

        foreach (var row in existing.Values)
        {
            if (active.Contains(row.ConsentId))
            {
                continue;
            }

            // An ended consent is reported revoked only once nothing unwanted is left in Unity Catalog.
            var (state, reason) = outcome.RevokesOutstanding ? (UcGrantState.Pending, (string?)RevokePendingReason) : (UcGrantState.Revoked, null);
            if (row.State != state || !string.Equals(row.ReasonCode, reason, StringComparison.Ordinal))
            {
                changed.Add(row with { State = state, ReasonCode = reason, UpdatedAt = now, VerifiedAt = null });
            }
        }

        // Chunks keep each transaction (and the tenant event lock it ends with) short on a large first run.
        foreach (var chunk in changed.Chunk(FeedbackChunk))
        {
            var ids = chunk.Select(r => r.ConsentId).ToHashSet();
            await store.SaveFeedbackAsync(tenant, dataSource, _fencingToken, chunk, [], events.Where(e => ids.Contains(e.ConsentId)).ToList(), ct)
                .ConfigureAwait(false);
        }
    }

    private const int FeedbackChunk = 100;

    /// <summary>
    /// Feedback of tenants that are no longer the data source's provisioning tenant (the tenant was changed): their privileges are
    /// revoked like any unwanted privilege, so their feedback reads <c>REVOKE_PENDING</c> until nothing unwanted is left, then
    /// <c>REVOKED</c>.
    /// </summary>
    private async Task RetireFormerTenantsAsync(string dataSource, TenantId tenant, UcApplyOutcome outcome, DateTimeOffset now, CancellationToken ct)
    {
        var (state, reason) = outcome.RevokesOutstanding ? (UcGrantState.Pending, (string?)RevokePendingReason) : (UcGrantState.Revoked, null);
        foreach (var former in await store.ListFeedbackTenantsAsync(dataSource, ct).ConfigureAwait(false))
        {
            if (former == tenant)
            {
                continue;
            }

            var rows = (await store.ListFeedbackAsync(former, dataSource, ct).ConfigureAwait(false))
                .Where(row => row.State != UcGrantState.Revoked && (row.State != state || !string.Equals(row.ReasonCode, reason, StringComparison.Ordinal)))
                .Select(row => row with { State = state, ReasonCode = reason, UpdatedAt = now, VerifiedAt = null })
                .ToList();
            foreach (var chunk in rows.Chunk(FeedbackChunk))
            {
                await store.SaveFeedbackAsync(former, dataSource, _fencingToken, chunk, [], [], ct).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransient(string? reason) =>
        reason is not null && (reason == PlanningFailedReason || (reason.StartsWith("UC_", StringComparison.Ordinal) && UcErrorCodes.IsTransient(reason)));

    private async Task<(UcGrantState State, string? Reason)> EnforcedStateAsync(
        string dataSource,
        UcConsentPlan plan,
        UcApplyOutcome outcome,
        UcGrantFeedback? before,
        Dictionary<(string Catalog, string Principal), string?> catalogAccess,
        CancellationToken ct)
    {
        string? error = null;
        foreach (var key in plan.Keys)
        {
            if (!outcome.Keys.TryGetValue(key, out var status))
            {
                return (UcGrantState.Pending, null);
            }

            if (status.Status == UcKeyStatus.Failed)
            {
                return (UcGrantState.Failed, status.ErrorCode ?? UcErrorCodes.Rejected);
            }

            if (status.Status != UcKeyStatus.Satisfied)
            {
                error ??= status.ErrorCode;
            }
        }

        if (error is not null || plan.Keys.Any(k => outcome.Keys[k].Status != UcKeyStatus.Satisfied))
        {
            return (UcGrantState.Pending, error);
        }

        if (before?.State == UcGrantState.Provisioned)
        {
            return (UcGrantState.Provisioned, null);
        }

        // The provisioning principal never grants USE_CATALOG; without it the SELECT is not usable, so the consent is not effective.
        var access = (plan.Catalog!, plan.Principal!);
        if (!catalogAccess.TryGetValue(access, out var verdict))
        {
            try
            {
                verdict = await CatalogAccessAsync(dataSource, plan.Catalog!, plan.Principal!, ct).ConfigureAwait(false);
            }
            catch (UcProvisioningException ex)
            {
                return (UcGrantState.Pending, ex.Code);
            }

            catalogAccess[access] = verdict;
        }

        return verdict is null ? (UcGrantState.Provisioned, null) : (UcGrantState.Failed, verdict);
    }

    /// <summary>
    /// Null when the principal can use the catalog, otherwise the reason. The catalog's direct grants are readable by every catalog
    /// user: a grant to the principal itself or to <see cref="AccountUsersGroup"/> settles it. Otherwise the effective permissions are
    /// asked; reading them for another principal needs more than the provisioning principal holds, so a refusal is reported as
    /// unverified, never as provisioned.
    /// </summary>
    private async Task<string?> CatalogAccessAsync(string dataSource, string catalog, string principal, CancellationToken ct)
    {
        var direct = await client.GetPermissionsAsync(dataSource, UcSecurableType.Catalog, catalog, ct).ConfigureAwait(false);
        if (direct.Any(a => (string.Equals(a.Principal, principal, StringComparison.Ordinal) ||
                             string.Equals(a.Principal, AccountUsersGroup, StringComparison.OrdinalIgnoreCase)) &&
                            a.Privileges.Any(p => CatalogAccessPrivileges.Contains(p, StringComparer.Ordinal))))
        {
            return null;
        }

        try
        {
            return await client.HasEffectivePrivilegeAsync(dataSource, UcSecurableType.Catalog, catalog, principal, CatalogAccessPrivileges, ct).ConfigureAwait(false)
                ? null
                : CatalogAccessMissingReason;
        }
        catch (UcProvisioningException ex) when (ex.Code is UcErrorCodes.Forbidden or UcErrorCodes.Rejected)
        {
            return CatalogAccessUnverifiedReason;
        }
    }
}
