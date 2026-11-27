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

/// <summary>
/// The result of one cycle for a data source: the status of every wanted privilege, the overdue revokes and whether privileges that
/// nobody wants any more are still in place.
/// </summary>
public sealed record UcApplyOutcome(
    IReadOnlyDictionary<UcPrivilegeKey, (UcKeyStatus Status, string? ErrorCode)> Keys,
    long OverdueRevokes,
    bool RevokesOutstanding);

public sealed partial class UcProvisioningReconciler
{
    /// <summary>Error codes of Unity Catalog that mean the securable itself no longer exists (nothing can be granted on it).</summary>
    private static readonly HashSet<string> SecurableGoneCodes = new(StringComparer.Ordinal)
    {
        "TABLE_DOES_NOT_EXIST", "SCHEMA_DOES_NOT_EXIST", "CATALOG_DOES_NOT_EXIST"
    };

    /// <summary>A ledger row nobody wants: not desired, and its principal is not held back by an unfinished plan.</summary>
    private static bool IsUnwanted(UcLedgerEntry row, UcDesiredState desired) =>
        !desired.Privileges.ContainsKey(row.Key) && !desired.ProtectedExternalIds.Contains(row.PrincipalExternalId);

    // ---- revokes ----

    /// <summary>
    /// Removes every unwanted ledger privilege under the principal's current name and deletes the row only after a read confirms the
    /// removal (or after Unity Catalog reports the securable itself gone). Failures keep the row as revoke-pending with backoff.
    /// </summary>
    private async Task<long> RevokeAsync(
        string dataSource, TenantId tenant, UcDesiredState desired, IReadOnlyList<UcLedgerEntry> ledger, DateTimeOffset now, CancellationToken ct)
    {
        long overdue = 0;
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var securable in ledger.Where(row => IsUnwanted(row, desired)).GroupBy(row => (row.SecurableType, row.FullName)))
        {
            var rows = securable.ToList();
            var due = rows.Where(row => row.State != UcLedgerState.RevokePending || row.NextAttemptAt is not { } next || next <= now).ToList();
            overdue += rows.Count(row => row.State == UcLedgerState.RevokePending && row.Attempts >= OverdueRevokeAttempts && !due.Contains(row));
            if (due.Count == 0)
            {
                continue;
            }

            var current = new Dictionary<UcLedgerEntry, string>();
            foreach (var row in due)
            {
                current[row] = await CurrentNameAsync(dataSource, row, names, ct).ConfigureAwait(false);
            }

            var (gone, errors, after) = await RemoveAsync(dataSource, securable.Key.SecurableType, securable.Key.FullName, due, current, ct).ConfigureAwait(false);
            var removed = new List<UcLedgerEntry>();
            var pending = new List<UcLedgerEntry>();
            var audit = new List<AuditLogEntry>();
            foreach (var row in due)
            {
                var error = errors.TryGetValue(current[row], out var code) ? code : null;
                if (gone || (error is null && after is not null && !Holds(after, current[row], row.Privilege)))
                {
                    removed.Add(row);
                    audit.Add(Audit(tenant, AuditEventTypes.GrantDeprovisioned, gone ? "SECURABLE_GONE" : "REVOKED", dataSource, row.Key, []));
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

    /// <summary>
    /// One update for all principals; when it fails, one update per principal so a single bad principal blocks only its own rows.
    /// Returns whether the securable is gone, the error code per principal and the confirming read (null when it failed).
    /// </summary>
    private async Task<(bool Gone, Dictionary<string, string> Errors, IReadOnlyList<UcPrivilegeAssignment>? After)> RemoveAsync(
        string dataSource, UcSecurableType type, string fullName, IReadOnlyList<UcLedgerEntry> due, IReadOnlyDictionary<UcLedgerEntry, string> current, CancellationToken ct)
    {
        var changes = due.GroupBy(row => current[row], StringComparer.Ordinal)
            .Select(g => new UcPermissionChange(g.Key, [], g.Select(row => row.Privilege).Distinct(StringComparer.Ordinal).ToList()))
            .ToList();
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var chunk in changes.Chunk(100))
            {
                await client.UpdatePermissionsAsync(dataSource, type, fullName, chunk, ct).ConfigureAwait(false);
            }
        }
        catch (UcProvisioningException ex) when (ex.Code == UcErrorCodes.NotFound && ex.RemoteErrorCode is { } remote && SecurableGoneCodes.Contains(remote))
        {
            return (true, errors, null);
        }
        catch (UcProvisioningException)
        {
            foreach (var change in changes)
            {
                try
                {
                    await client.UpdatePermissionsAsync(dataSource, type, fullName, [change], ct).ConfigureAwait(false);
                }
                catch (UcProvisioningException single)
                {
                    errors[change.Principal] = single.Code;
                }
            }
        }

        try
        {
            return (false, errors, await client.GetPermissionsAsync(dataSource, type, fullName, ct).ConfigureAwait(false));
        }
        catch (UcProvisioningException ex)
        {
            foreach (var change in changes)
            {
                errors.TryAdd(change.Principal, ex.Code);
            }

            return (false, errors, null);
        }
    }

    /// <summary>
    /// The name Unity Catalog uses for the row's principal now: a renamed group or user is resolved again by its external id, so the
    /// removal and its confirming read target the current name. Service principals are named by their application id.
    /// </summary>
    private async Task<string> CurrentNameAsync(string dataSource, UcLedgerEntry row, Dictionary<string, string> cache, CancellationToken ct)
    {
        if (string.Equals(row.Principal, row.PrincipalExternalId, StringComparison.OrdinalIgnoreCase))
        {
            return row.Principal;
        }

        if (cache.TryGetValue(row.PrincipalExternalId, out var known))
        {
            return known;
        }

        var name = row.Principal;
        try
        {
            var resolved = await client.ResolvePrincipalAsync(dataSource, GranteeType.Group, row.PrincipalExternalId, ct).ConfigureAwait(false);
            if (resolved.Status == UcPrincipalResolutionStatus.NotFound)
            {
                resolved = await client.ResolvePrincipalAsync(dataSource, GranteeType.User, row.PrincipalExternalId, ct).ConfigureAwait(false);
            }

            name = resolved.Principal?.Name ?? row.Principal;
        }
        catch (UcProvisioningException)
        {
            // Keep the recorded name; a failing removal stays revoke-pending.
        }

        cache[row.PrincipalExternalId] = name;
        return name;
    }

    // ---- grants ----

    private async Task<Dictionary<UcPrivilegeKey, (UcKeyStatus Status, string? ErrorCode)>> GrantAsync(
        string dataSource, TenantId tenant, UcDesiredState desired, IReadOnlyDictionary<UcPrivilegeKey, UcLedgerEntry> ledger, DateTimeOffset now, CancellationToken ct)
    {
        var result = new Dictionary<UcPrivilegeKey, (UcKeyStatus, string?)>();
        var renamed = new List<(UcDesiredPrivilege Wanted, UcLedgerEntry Row)>();
        var todo = new List<UcDesiredPrivilege>();
        foreach (var wanted in desired.Privileges.Values)
        {
            ledger.TryGetValue(wanted.Key, out var row);
            if (row is { State: UcLedgerState.Applied })
            {
                if (string.Equals(row.Principal, wanted.Principal, StringComparison.Ordinal))
                {
                    result[wanted.Key] = (UcKeyStatus.Satisfied, null);
                }
                else
                {
                    renamed.Add((wanted, row));
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

        await ConfirmRenamesAsync(dataSource, tenant, renamed, result, now, ct).ConfigureAwait(false);

        // Schemas first: USE_SCHEMA before SELECT on the tables below.
        foreach (var securable in todo.GroupBy(w => (w.Key.SecurableType, w.Key.FullName)).OrderBy(g => g.Key.SecurableType == UcSecurableType.Table ? 1 : 0))
        {
            await GrantSecurableAsync(dataSource, tenant, securable.Key.SecurableType, securable.Key.FullName, securable.ToList(), ledger, result, now, ct).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// A principal renamed since its privilege was applied: the ledger follows the new name only after a read shows the privilege
    /// under it (recorded as drift, never a silent re-grant). Until then the privilege counts as not confirmed.
    /// </summary>
    private async Task ConfirmRenamesAsync(
        string dataSource,
        TenantId tenant,
        IReadOnlyList<(UcDesiredPrivilege Wanted, UcLedgerEntry Row)> renamed,
        Dictionary<UcPrivilegeKey, (UcKeyStatus, string?)> result,
        DateTimeOffset now,
        CancellationToken ct)
    {
        foreach (var securable in renamed.GroupBy(r => (r.Row.SecurableType, r.Row.FullName)))
        {
            IReadOnlyList<UcPrivilegeAssignment>? actual = null;
            try
            {
                actual = await client.GetPermissionsAsync(dataSource, securable.Key.SecurableType, securable.Key.FullName, ct).ConfigureAwait(false);
            }
            catch (UcProvisioningException ex)
            {
                foreach (var (wanted, _) in securable)
                {
                    result[wanted.Key] = (UcKeyStatus.Pending, ex.Code);
                }

                continue;
            }

            var rows = new List<UcLedgerEntry>();
            var audit = new List<AuditLogEntry>();
            foreach (var (wanted, row) in securable)
            {
                UcProvisioningMetrics.DriftFindings.Add(1, new KeyValuePair<string, object?>("kind", "principal_renamed"));
                if (Holds(actual, wanted.Principal, wanted.Key.Privilege))
                {
                    rows.Add(row with { Principal = wanted.Principal, VerifiedAt = now, UpdatedAt = now });
                    audit.Add(Audit(tenant, AuditEventTypes.GrantProvisioningDrift, "PRINCIPAL_RENAMED", dataSource, wanted.Key, wanted.ConsentIds));
                    result[wanted.Key] = (UcKeyStatus.Satisfied, null);
                }
                else
                {
                    result[wanted.Key] = (UcKeyStatus.Pending, "PRINCIPAL_RENAMED");
                }
            }

            await store.SaveLedgerAsync(dataSource, _fencingToken, rows, audit, ct).ConfigureAwait(false);
        }
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
        var intents = needed.Select(w => Row(dataSource, w, ledger, now)).ToList();
        await store.SaveLedgerAsync(dataSource, _fencingToken, intents, [], ct).ConfigureAwait(false);
        var tracked = intents.ToDictionary(r => r.Key);

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
            await RecordGrantFailuresAsync(dataSource, tenant, needed, tracked, result, ex.Code, now, ct).ConfigureAwait(false);
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

            applied.Add(tracked[w.Key] with
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
            await RecordGrantFailuresAsync(dataSource, tenant, missing, tracked, result, UcErrorCodes.Rejected, now, ct).ConfigureAwait(false);
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
                NextAttemptAt = now + (failed ? GrantBackoffCap : UcProvisioningPlanner.Backoff(attempts, GrantBackoffCap)),
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

    /// <summary>
    /// The intent row of a wanted privilege. A row that was being revoked starts a fresh grant (its revoke attempts do not count);
    /// a failed row keeps its state until a confirming read.
    /// </summary>
    private static UcLedgerEntry Row(string dataSource, UcDesiredPrivilege wanted, IReadOnlyDictionary<UcPrivilegeKey, UcLedgerEntry> ledger, DateTimeOffset now)
    {
        if (!ledger.TryGetValue(wanted.Key, out var existing))
        {
            return new UcLedgerEntry
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
        }

        return existing.State switch
        {
            UcLedgerState.RevokePending => existing with
            {
                Principal = wanted.Principal, State = UcLedgerState.Pending, Attempts = 0, NextAttemptAt = null, LastErrorCode = null, UpdatedAt = now
            },
            UcLedgerState.Failed => existing with { Principal = wanted.Principal, UpdatedAt = now },
            _ => existing with { Principal = wanted.Principal, State = UcLedgerState.Pending, UpdatedAt = now }
        };
    }

    private static bool Holds(IReadOnlyList<UcPrivilegeAssignment> assignments, string principal, string privilege) =>
        assignments.Any(a => string.Equals(a.Principal, principal, StringComparison.Ordinal) &&
                             a.Privileges.Contains(privilege, StringComparer.Ordinal));
}
