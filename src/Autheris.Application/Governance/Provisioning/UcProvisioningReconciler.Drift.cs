namespace Autheris.Application.Governance.Provisioning;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;

/// <summary>
/// Process-wide state of the reconciler that outlives one cycle: when each data source was last checked for drift and which foreign
/// privileges are currently reported (so a standing finding is audited once, and again after it disappeared and came back).
/// </summary>
public sealed class UcProvisioningState
{
    public ConcurrentDictionary<string, DateTimeOffset> LastDriftCheck { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Reported foreign findings per data source and securable.</summary>
    public ConcurrentDictionary<string, HashSet<string>> ReportedForeign { get; } = new(StringComparer.Ordinal);

    /// <summary>Ledger privileges of data sources without a provisioning section that were already audited.</summary>
    public ConcurrentDictionary<string, byte> ReportedOrphans { get; } = new(StringComparer.Ordinal);
}

public sealed partial class UcProvisioningReconciler
{
    /// <summary>How often the managed securables of one data source are read back for drift.</summary>
    public static readonly TimeSpan DriftCheckInterval = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Reads every table and schema with an applied privilege back from Unity Catalog. A missing Autheris privilege that is still
    /// wanted under the same principal name is re-applied through the normal add-and-read path (audited as drift, then as applied).
    /// A direct privilege that neither Autheris (on any data source), a wanted consent, a provisioning principal nor a query principal
    /// accounts for is foreign: it is reported once while it stands and never removed. Securables whose principals are being renamed or
    /// could not be planned are skipped. Returns the status of the re-applied privileges.
    /// </summary>
    private async Task<Dictionary<UcPrivilegeKey, (UcKeyStatus Status, string? ErrorCode)>> CheckDriftAsync(
        string dataSource, TenantId tenant, UcDesiredState desired, DateTimeOffset now, CancellationToken ct)
    {
        var repaired = new Dictionary<UcPrivilegeKey, (UcKeyStatus, string?)>();
        if (_state.LastDriftCheck.TryGetValue(dataSource, out var last) && now - last < DriftCheckInterval)
        {
            return repaired;
        }

        _state.LastDriftCheck[dataSource] = now;
        var ledger = await store.ListLedgerAsync(dataSource, ct).ConfigureAwait(false);
        var accounted = await AccountedPrincipalsAsync(desired, ct).ConfigureAwait(false);
        foreach (var securable in ledger.Where(row => row.State == UcLedgerState.Applied).GroupBy(row => (row.SecurableType, row.FullName)))
        {
            var rows = ledger.Where(row => row.SecurableType == securable.Key.SecurableType && row.FullName == securable.Key.FullName).ToList();
            var unsettled = rows.Any(row => desired.ProtectedExternalIds.Contains(row.PrincipalExternalId) ||
                                            (desired.Privileges.TryGetValue(row.Key, out var w) && !string.Equals(w.Principal, row.Principal, StringComparison.Ordinal)));
            IReadOnlyList<UcPrivilegeAssignment> actual;
            try
            {
                actual = await client.GetPermissionsAsync(dataSource, securable.Key.SecurableType, securable.Key.FullName, ct).ConfigureAwait(false);
            }
            catch (UcProvisioningException ex)
            {
                UcProvisioningMetrics.Count("drift", "failed");
                logger.LogWarning("Drift check of a securable on {DataSource} failed ({Code}).", dataSource, ex.Code);
                continue;
            }

            await RepairMissingAsync(dataSource, tenant, desired, securable.ToList(), actual, rows, repaired, now, ct).ConfigureAwait(false);
            if (!unsettled)
            {
                await ReportForeignAsync(dataSource, tenant, securable.Key.SecurableType, securable.Key.FullName, actual, accounted, ct).ConfigureAwait(false);
            }
        }

        return repaired;
    }

    /// <summary>
    /// Principal/privilege pairs that are not foreign on a securable: Autheris rows of every data source, the wanted privileges of this
    /// cycle, and every provisioning or query principal of a Databricks data source (by name, any privilege).
    /// </summary>
    private async Task<(HashSet<string> Pairs, HashSet<string> Principals)> AccountedPrincipalsAsync(UcDesiredState desired, CancellationToken ct)
    {
        var pairs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in await store.ListLedgerDataSourcesAsync(ct).ConfigureAwait(false))
        {
            foreach (var row in await store.ListLedgerAsync(source, ct).ConfigureAwait(false))
            {
                pairs.Add(Pair(row.SecurableType, row.FullName, row.Principal, row.Privilege));
            }
        }

        foreach (var wanted in desired.Privileges.Values)
        {
            pairs.Add(Pair(wanted.Key.SecurableType, wanted.Key.FullName, wanted.Principal, wanted.Key.Privilege));
        }

        var principals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var connection in options.CurrentValue.DataSources.Connections.Values)
        {
            if (connection?.Databricks is not { } db)
            {
                continue;
            }

            foreach (var id in new[] { db.Auth.ClientId, db.Auth.Obo?.ClientId, db.Provisioning?.Auth?.ClientId }
                         .Concat(db.Auth.Tenants.Values.Select(t => t?.ClientId)))
            {
                if (!string.IsNullOrWhiteSpace(id))
                {
                    principals.Add(id.Trim());
                }
            }
        }

        return (pairs, principals);
    }

    private static string Pair(UcSecurableType type, string fullName, string principal, string privilege) =>
        type.ToStored() + "|" + fullName + "|" + principal + "|" + privilege;

    private async Task RepairMissingAsync(
        string dataSource,
        TenantId tenant,
        UcDesiredState desired,
        IReadOnlyList<UcLedgerEntry> applied,
        IReadOnlyList<UcPrivilegeAssignment> actual,
        IReadOnlyList<UcLedgerEntry> rows,
        Dictionary<UcPrivilegeKey, (UcKeyStatus, string?)> repaired,
        DateTimeOffset now,
        CancellationToken ct)
    {
        // Only privileges still wanted under the recorded name: a rename is confirmed by its own path, never re-granted here.
        var missing = applied.Where(row =>
                desired.Privileges.TryGetValue(row.Key, out var wanted) &&
                string.Equals(wanted.Principal, row.Principal, StringComparison.Ordinal) &&
                !Holds(actual, row.Principal, row.Privilege))
            .ToList();
        if (missing.Count == 0)
        {
            return;
        }

        var audit = missing.Select(row => Audit(tenant, AuditEventTypes.GrantProvisioningDrift, "MISSING", dataSource, row.Key, desired.Privileges[row.Key].ConsentIds)).ToList();
        UcProvisioningMetrics.DriftFindings.Add(missing.Count, new KeyValuePair<string, object?>("kind", "missing"));
        var pending = missing.Select(row => row with { State = UcLedgerState.Pending, UpdatedAt = now }).ToList();
        await store.SaveLedgerAsync(dataSource, _fencingToken, pending, audit, ct).ConfigureAwait(false);

        var byKey = rows.ToDictionary(row => row.Key);
        foreach (var row in pending)
        {
            byKey[row.Key] = row;
        }

        var first = missing[0];
        await GrantSecurableAsync(
            dataSource, tenant, first.SecurableType, first.FullName, missing.Select(row => desired.Privileges[row.Key]).ToList(), byKey, repaired, now, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Reports each foreign privilege once while it stands; never removes it. Foreign principal names stay out of the audit.</summary>
    private async Task ReportForeignAsync(
        string dataSource,
        TenantId tenant,
        UcSecurableType type,
        string fullName,
        IReadOnlyList<UcPrivilegeAssignment> actual,
        (HashSet<string> Pairs, HashSet<string> Principals) accounted,
        CancellationToken ct)
    {
        var current = new HashSet<string>(StringComparer.Ordinal);
        foreach (var assignment in actual)
        {
            if (accounted.Principals.Contains(assignment.Principal))
            {
                continue;
            }

            foreach (var privilege in assignment.Privileges)
            {
                if (!accounted.Pairs.Contains(Pair(type, fullName, assignment.Principal, privilege)))
                {
                    current.Add(assignment.Principal + "|" + privilege);
                }
            }
        }

        var slot = dataSource + "|" + type.ToStored() + "|" + fullName;
        var reported = _state.ReportedForeign.GetOrAdd(slot, _ => new HashSet<string>(StringComparer.Ordinal));
        List<string> fresh;
        lock (reported)
        {
            // A finding that disappeared is forgotten, so it is reported again if it comes back.
            reported.IntersectWith(current);
            fresh = current.Where(f => !reported.Contains(f)).ToList();
        }

        if (fresh.Count == 0)
        {
            return;
        }

        UcProvisioningMetrics.DriftFindings.Add(fresh.Count, new KeyValuePair<string, object?>("kind", "foreign"));
        var audit = fresh
            .Select(f => Audit(tenant, AuditEventTypes.GrantProvisioningDrift, "FOREIGN", dataSource, new UcPrivilegeKey(type, fullName, "foreign", f[(f.LastIndexOf('|', StringComparison.Ordinal) + 1)..]), []))
            .ToList();
        await store.SaveLedgerAsync(dataSource, _fencingToken, [], audit, ct).ConfigureAwait(false);
        lock (reported)
        {
            reported.UnionWith(fresh);
        }
    }
}
