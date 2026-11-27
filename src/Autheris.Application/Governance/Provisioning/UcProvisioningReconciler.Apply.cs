namespace Autheris.Application.Governance.Provisioning;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>Where one wanted privilege stands after a cycle.</summary>
public enum UcKeyStatus
{
    /// <summary>Present in Unity Catalog, confirmed by a read in this or an earlier cycle.</summary>
    Satisfied = 0,

    /// <summary>Not yet confirmed; retried with backoff.</summary>
    Pending = 1,

    /// <summary>The add failed the maximum number of times.</summary>
    Failed = 2,

    /// <summary>Planned only (dry run).</summary>
    Planned = 3
}

/// <summary>The result of applying the desired state: the status of every wanted privilege and the number of overdue revokes.</summary>
public sealed record UcApplyOutcome(IReadOnlyDictionary<UcPrivilegeKey, (UcKeyStatus Status, string? ErrorCode)> Keys, long OverdueRevokes);

public sealed partial class UcProvisioningReconciler
{
    /// <summary>Dry run: nothing is changed; every wanted privilege is reported as planned.</summary>
    private static UcApplyOutcome DryRun(UcDesiredState desired, IReadOnlyList<UcLedgerEntry> ledger)
    {
        var planned = desired.Privileges.Keys.ToDictionary(k => k, _ => (UcKeyStatus.Planned, (string?)null));
        var revokes = ledger.Count(row => !desired.Privileges.ContainsKey(row.Key));
        UcProvisioningMetrics.Count("grant", "dry_run", planned.Count);
        UcProvisioningMetrics.Count("revoke", "dry_run", revokes);
        return new UcApplyOutcome(planned, 0);
    }

    /// <summary>Revokes what is no longer wanted, then grants what is missing; every change is confirmed by a read.</summary>
    private async Task<UcApplyOutcome> ApplyAsync(
        string dataSource, TenantId tenant, UcDesiredState desired, IReadOnlyList<UcLedgerEntry> ledger, bool deferRevokes, DateTimeOffset now, CancellationToken ct)
    {
        var byKey = ledger.ToDictionary(row => row.Key);
        var overdue = deferRevokes
            ? ledger.Count(row => row.State == UcLedgerState.RevokePending && row.Attempts >= OverdueRevokeAttempts)
            : await RevokeAsync(dataSource, tenant, desired, ledger, now, ct).ConfigureAwait(false);
        var keys = await GrantAsync(dataSource, tenant, desired, byKey, now, ct).ConfigureAwait(false);
        return new UcApplyOutcome(keys, overdue);
    }

    // ---- revokes ----

    private async Task<long> RevokeAsync(
        string dataSource, TenantId tenant, UcDesiredState desired, IReadOnlyList<UcLedgerEntry> ledger, DateTimeOffset now, CancellationToken ct)
    {
        long overdue = 0;
        var unwanted = ledger.Where(row => !desired.Privileges.ContainsKey(row.Key)).ToList();
        foreach (var securable in unwanted.GroupBy(row => (row.SecurableType, row.FullName)))
        {
            var rows = securable.ToList();
            var due = rows.Where(row => row.State != UcLedgerState.RevokePending || row.NextAttemptAt is not { } next || next <= now).ToList();
            overdue += rows.Count(row => row.State == UcLedgerState.RevokePending && row.Attempts >= OverdueRevokeAttempts && !due.Contains(row));
            if (due.Count == 0)
            {
                continue;
            }

            string? error = null;
            IReadOnlyList<UcPrivilegeAssignment> after = [];
            try
            {
                var changes = due.GroupBy(row => row.Principal, StringComparer.Ordinal)
                    .Select(g => new UcPermissionChange(g.Key, [], g.Select(row => row.Privilege).Distinct(StringComparer.Ordinal).ToList()))
                    .ToList();
                foreach (var chunk in changes.Chunk(100))
                {
                    await client.UpdatePermissionsAsync(dataSource, securable.Key.SecurableType, securable.Key.FullName, chunk, ct).ConfigureAwait(false);
                }

                after = await client.GetPermissionsAsync(dataSource, securable.Key.SecurableType, securable.Key.FullName, ct).ConfigureAwait(false);
            }
            catch (UcProvisioningException ex) when (ex.Code != UcErrorCodes.NotFound)
            {
                error = ex.Code;
            }
            catch (UcProvisioningException)
            {
                // The securable no longer exists: nothing can still be granted on it.
            }

            var removed = new List<UcLedgerEntry>();
            var pending = new List<UcLedgerEntry>();
            var audit = new List<AuditLogEntry>();
            foreach (var row in due)
            {
                if (error is null && !Holds(after, row.Principal, row.Privilege))
                {
                    removed.Add(row);
                    audit.Add(Audit(tenant, AuditEventTypes.GrantDeprovisioned, "REVOKED", dataSource, row.Key, []));
                    continue;
                }

                var attempts = row.State == UcLedgerState.RevokePending ? row.Attempts + 1 : 1;
                pending.Add(row with
                {
                    State = UcLedgerState.RevokePending,
                    Attempts = attempts,
                    NextAttemptAt = now + UcProvisioningPlanner.Backoff(attempts, RevokeBackoffCap),
                    LastErrorCode = error ?? UcErrorCodes.Rejected,
                    UpdatedAt = now
                });
                if (attempts == OverdueRevokeAttempts)
                {
                    audit.Add(Audit(tenant, AuditEventTypes.GrantProvisioningFailed, "REVOKE_OVERDUE", dataSource, row.Key, [], error ?? UcErrorCodes.Rejected, attempts));
                }

                if (attempts >= OverdueRevokeAttempts)
                {
                    overdue++;
                }
            }

            UcProvisioningMetrics.Count("revoke", "ok", removed.Count);
            UcProvisioningMetrics.Count("revoke", "failed", pending.Count);
            await store.SaveLedgerAsync(dataSource, _fencingToken, pending, removed.Count == 0 ? audit : [], ct).ConfigureAwait(false);
            if (removed.Count > 0)
            {
                await store.DeleteLedgerAsync(dataSource, _fencingToken, removed, audit, ct).ConfigureAwait(false);
            }
        }

        return overdue;
    }

    // ---- grants ----

    private async Task<Dictionary<UcPrivilegeKey, (UcKeyStatus Status, string? ErrorCode)>> GrantAsync(
        string dataSource, TenantId tenant, UcDesiredState desired, IReadOnlyDictionary<UcPrivilegeKey, UcLedgerEntry> ledger, DateTimeOffset now, CancellationToken ct)
    {
        var result = new Dictionary<UcPrivilegeKey, (UcKeyStatus, string?)>();
        var renamed = new List<UcLedgerEntry>();
        var renameAudit = new List<AuditLogEntry>();
        var todo = new List<UcDesiredPrivilege>();
        foreach (var wanted in desired.Privileges.Values)
        {
            ledger.TryGetValue(wanted.Key, out var row);
            if (row is { State: UcLedgerState.Applied })
            {
                result[wanted.Key] = (UcKeyStatus.Satisfied, null);
                if (!string.Equals(row.Principal, wanted.Principal, StringComparison.Ordinal))
                {
                    // Same external id, new name: recorded as drift, never a silent re-grant.
                    renamed.Add(row with { Principal = wanted.Principal, UpdatedAt = now });
                    renameAudit.Add(Audit(tenant, AuditEventTypes.GrantProvisioningDrift, "PRINCIPAL_RENAMED", dataSource, wanted.Key, wanted.ConsentIds));
                    UcProvisioningMetrics.DriftFindings.Add(1, new KeyValuePair<string, object?>("kind", "principal_renamed"));
                }

                continue;
            }

            if (row is { NextAttemptAt: { } next } && next > now && row.State != UcLedgerState.RevokePending)
            {
                result[wanted.Key] = (row.State == UcLedgerState.Failed ? UcKeyStatus.Failed : UcKeyStatus.Pending, row.LastErrorCode);
                continue;
            }

            todo.Add(wanted);
        }

        if (renamed.Count > 0)
        {
            await store.SaveLedgerAsync(dataSource, _fencingToken, renamed, renameAudit, ct).ConfigureAwait(false);
        }

        // Schemas first: USE_SCHEMA before SELECT on the tables below.
        foreach (var securable in todo.GroupBy(w => (w.Key.SecurableType, w.Key.FullName)).OrderBy(g => g.Key.SecurableType == UcSecurableType.Table ? 1 : 0))
        {
            await GrantSecurableAsync(dataSource, tenant, securable.Key.SecurableType, securable.Key.FullName, securable.ToList(), ledger, result, now, ct).ConfigureAwait(false);
        }

        return result;
    }

    private async Task GrantSecurableAsync(
        string dataSource,
        TenantId tenant,
        UcSecurableType type,
        string fullName,
        IReadOnlyList<UcDesiredPrivilege> wanted,
        IReadOnlyDictionary<UcPrivilegeKey, UcLedgerEntry> ledger,
        Dictionary<UcPrivilegeKey, (UcKeyStatus, string?)> result,
        DateTimeOffset now,
        CancellationToken ct)
    {
        IReadOnlyList<UcPrivilegeAssignment> before;
        try
        {
            before = await client.GetPermissionsAsync(dataSource, type, fullName, ct).ConfigureAwait(false);
        }
        catch (UcProvisioningException ex)
        {
            await RecordGrantFailuresAsync(dataSource, tenant, wanted, ledger, result, ex.Code, now, ct).ConfigureAwait(false);
            return;
        }

        // A privilege the principal already holds without a ledger row is not Autheris's: it satisfies the consent and is never revoked.
        var needed = new List<UcDesiredPrivilege>();
        foreach (var w in wanted)
        {
            if (!ledger.ContainsKey(w.Key) && Holds(before, w.Principal, w.Key.Privilege))
            {
                result[w.Key] = (UcKeyStatus.Satisfied, null);
                continue;
            }

            needed.Add(w);
        }

        if (needed.Count == 0)
        {
            return;
        }

        // Intent before effect: the ledger lists the privilege before it can exist, so a crash never leaves it untracked.
        var intents = needed.Select(w => Row(dataSource, w, ledger, now) with { State = ledger.TryGetValue(w.Key, out var r) && r.State == UcLedgerState.Failed ? UcLedgerState.Failed : UcLedgerState.Pending }).ToList();
        await store.SaveLedgerAsync(dataSource, _fencingToken, intents, [], ct).ConfigureAwait(false);

        IReadOnlyList<UcPrivilegeAssignment> after;
        try
        {
            var changes = needed.GroupBy(w => w.Principal, StringComparer.Ordinal)
                .Select(g => new UcPermissionChange(g.Key, g.Select(w => w.Key.Privilege).Distinct(StringComparer.Ordinal).ToList(), []))
                .ToList();
            foreach (var chunk in changes.Chunk(100))
            {
                await client.UpdatePermissionsAsync(dataSource, type, fullName, chunk, ct).ConfigureAwait(false);
            }

            after = await client.GetPermissionsAsync(dataSource, type, fullName, ct).ConfigureAwait(false);
        }
        catch (UcProvisioningException ex)
        {
            var current = intents.ToDictionary(r => r.Key);
            await RecordGrantFailuresAsync(dataSource, tenant, needed, current, result, ex.Code, now, ct).ConfigureAwait(false);
            return;
        }

        var applied = new List<UcLedgerEntry>();
        var audit = new List<AuditLogEntry>();
        var missing = new List<UcDesiredPrivilege>();
        foreach (var w in needed)
        {
            if (!Holds(after, w.Principal, w.Key.Privilege))
            {
                missing.Add(w);
                continue;
            }

            applied.Add(Row(dataSource, w, ledger, now) with
            {
                State = UcLedgerState.Applied, Attempts = 0, NextAttemptAt = null, LastErrorCode = null, AppliedAt = now, VerifiedAt = now
            });
            audit.Add(Audit(tenant, AuditEventTypes.GrantProvisioned, "APPLIED", dataSource, w.Key, w.ConsentIds));
            result[w.Key] = (UcKeyStatus.Satisfied, null);
        }

        UcProvisioningMetrics.Count("grant", "ok", applied.Count);
        await store.SaveLedgerAsync(dataSource, _fencingToken, applied, audit, ct).ConfigureAwait(false);
        if (missing.Count > 0)
        {
            await RecordGrantFailuresAsync(dataSource, tenant, missing, intents.ToDictionary(r => r.Key), result, UcErrorCodes.Rejected, now, ct).ConfigureAwait(false);
        }
    }

    private async Task RecordGrantFailuresAsync(
        string dataSource,
        TenantId tenant,
        IReadOnlyList<UcDesiredPrivilege> wanted,
        IReadOnlyDictionary<UcPrivilegeKey, UcLedgerEntry> ledger,
        Dictionary<UcPrivilegeKey, (UcKeyStatus, string?)> result,
        string code,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var rows = new List<UcLedgerEntry>();
        var audit = new List<AuditLogEntry>();
        foreach (var w in wanted)
        {
            ledger.TryGetValue(w.Key, out var existing);
            if (existing is null)
            {
                // Nothing was recorded, so nothing can have been added: retried next cycle without a ledger row.
                result[w.Key] = (UcKeyStatus.Pending, code);
                continue;
            }

            var attempts = existing.Attempts + 1;
            var failed = attempts >= MaxGrantAttempts;
            rows.Add(existing with
            {
                State = failed ? UcLedgerState.Failed : UcLedgerState.Pending,
                Attempts = attempts,
                NextAttemptAt = now + UcProvisioningPlanner.Backoff(attempts, GrantBackoffCap),
                LastErrorCode = code,
                UpdatedAt = now
            });
            if (failed && existing.State != UcLedgerState.Failed)
            {
                audit.Add(Audit(tenant, AuditEventTypes.GrantProvisioningFailed, "FAILED", dataSource, w.Key, w.ConsentIds, code, attempts));
            }

            result[w.Key] = (failed ? UcKeyStatus.Failed : UcKeyStatus.Pending, code);
        }

        UcProvisioningMetrics.Count("grant", "failed", wanted.Count);
        await store.SaveLedgerAsync(dataSource, _fencingToken, rows, audit, ct).ConfigureAwait(false);
    }

    private static UcLedgerEntry Row(string dataSource, UcDesiredPrivilege wanted, IReadOnlyDictionary<UcPrivilegeKey, UcLedgerEntry> ledger, DateTimeOffset now) =>
        ledger.TryGetValue(wanted.Key, out var existing)
            ? existing with { Principal = wanted.Principal, UpdatedAt = now }
            : new UcLedgerEntry
            {
                DataSource = dataSource,
                SecurableType = wanted.Key.SecurableType,
                FullName = wanted.Key.FullName,
                Principal = wanted.Principal,
                PrincipalExternalId = wanted.Key.PrincipalExternalId,
                Privilege = wanted.Key.Privilege,
                State = UcLedgerState.Pending,
                CreatedAt = now,
                UpdatedAt = now
            };

    private static bool Holds(IReadOnlyList<UcPrivilegeAssignment> assignments, string principal, string privilege) =>
        assignments.Any(a => string.Equals(a.Principal, principal, StringComparison.Ordinal) &&
                             a.Privileges.Contains(privilege, StringComparer.Ordinal));
}
