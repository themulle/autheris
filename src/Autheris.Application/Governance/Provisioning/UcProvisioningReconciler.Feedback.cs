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
        var catalogAccess = new Dictionary<(string Catalog, string Principal), bool>();
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

    private static bool IsTransient(string? reason) =>
        reason is not null && (reason == PlanningFailedReason || (reason.StartsWith("UC_", StringComparison.Ordinal) && UcErrorCodes.IsTransient(reason)));

    private async Task<(UcGrantState State, string? Reason)> EnforcedStateAsync(
        string dataSource,
        UcConsentPlan plan,
        UcApplyOutcome outcome,
        UcGrantFeedback? before,
        Dictionary<(string Catalog, string Principal), bool> catalogAccess,
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
        if (!catalogAccess.TryGetValue(access, out var hasAccess))
        {
            try
            {
                hasAccess = await client.HasEffectivePrivilegeAsync(dataSource, UcSecurableType.Catalog, plan.Catalog!, plan.Principal!, CatalogAccessPrivileges, ct)
                    .ConfigureAwait(false);
            }
            catch (UcProvisioningException ex)
            {
                return (UcGrantState.Pending, ex.Code);
            }

            catalogAccess[access] = hasAccess;
        }

        return hasAccess ? (UcGrantState.Provisioned, null) : (UcGrantState.Failed, CatalogAccessMissingReason);
    }
}
