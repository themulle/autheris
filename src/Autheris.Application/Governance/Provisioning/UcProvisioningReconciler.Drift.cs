namespace Autheris.Application.Governance.Provisioning;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;

/// <summary>
/// Process-wide state of the reconciler that outlives one cycle: when each data source was last checked for drift and which foreign
/// privileges were already reported (so a finding is audited once, not on every check).
/// </summary>
public sealed class UcProvisioningState
{
    public ConcurrentDictionary<string, DateTimeOffset> LastDriftCheck { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ConcurrentDictionary<string, byte> ReportedForeign { get; } = new(StringComparer.Ordinal);
}

public sealed partial class UcProvisioningReconciler
{
    /// <summary>How often the managed securables of one data source are read back for drift.</summary>
    public static readonly TimeSpan DriftCheckInterval = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Reads every table with an applied privilege back from Unity Catalog. A missing Autheris privilege is re-applied (audited as a
    /// repair). A direct privilege nobody recorded is foreign: it is reported once, and removed only with <c>DriftMode = Revert</c>.
    /// Schemas and catalogs are never reverted; the provisioning principal's own privileges are never foreign.
    /// </summary>
    private async Task CheckDriftAsync(
        string dataSource, TenantId tenant, DatabricksProvisioningOptions provisioning, UcDesiredState desired, DateTimeOffset now, CancellationToken ct)
    {
        if (_state.LastDriftCheck.TryGetValue(dataSource, out var last) && now - last < DriftCheckInterval)
        {
            return;
        }

        _state.LastDriftCheck[dataSource] = now;
        var ledger = await store.ListLedgerAsync(dataSource, ct).ConfigureAwait(false);
        var applied = ledger.Where(row => row.State == UcLedgerState.Applied).ToList();
        var provisioningPrincipal = provisioning.Auth?.ClientId?.Trim();
        foreach (var securable in applied.GroupBy(row => (row.SecurableType, row.FullName)))
        {
            IReadOnlyList<UcPrivilegeAssignment> actual;
            try
            {
                actual = await client.GetPermissionsAsync(dataSource, securable.Key.SecurableType, securable.Key.FullName, ct).ConfigureAwait(false);
            }
            catch (UcProvisioningException ex)
            {
                logger.LogWarning("Drift check of a securable on {DataSource} failed ({Code}).", dataSource, ex.Code);
                continue;
            }

            await RepairMissingAsync(dataSource, tenant, desired, securable.ToList(), actual, ledger, now, ct).ConfigureAwait(false);
            if (securable.Key.SecurableType == UcSecurableType.Table)
            {
                var managedRows = ledger.Where(row => row.SecurableType == UcSecurableType.Table && row.FullName == securable.Key.FullName).ToList();
                await HandleForeignAsync(dataSource, tenant, provisioning.DriftMode, securable.Key.FullName, actual, managedRows, provisioningPrincipal, ct)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task RepairMissingAsync(
        string dataSource,
        TenantId tenant,
        UcDesiredState desired,
        IReadOnlyList<UcLedgerEntry> rows,
        IReadOnlyList<UcPrivilegeAssignment> actual,
        IReadOnlyList<UcLedgerEntry> ledger,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var missing = rows.Where(row => !Holds(actual, row.Principal, row.Privilege) && desired.Privileges.ContainsKey(row.Key)).ToList();
        if (missing.Count == 0)
        {
            return;
        }

        var audit = missing.Select(row => Audit(tenant, AuditEventTypes.GrantProvisioningDrift, "REPAIRED", dataSource, row.Key, desired.Privileges[row.Key].ConsentIds)).ToList();
        UcProvisioningMetrics.DriftFindings.Add(missing.Count, new KeyValuePair<string, object?>("kind", "missing"));

        // The rows go back to pending with the repair audit; the add below confirms them again by a read.
        await store.SaveLedgerAsync(dataSource, _fencingToken, missing.Select(row => row with { State = UcLedgerState.Pending, UpdatedAt = now }).ToList(), audit, ct)
            .ConfigureAwait(false);
        var byKey = ledger.ToDictionary(row => row.Key);
        foreach (var row in missing)
        {
            byKey[row.Key] = row with { State = UcLedgerState.Pending };
        }

        var result = new Dictionary<UcPrivilegeKey, (UcKeyStatus, string?)>();
        var first = missing[0];
        await GrantSecurableAsync(
            dataSource, tenant, first.SecurableType, first.FullName, missing.Select(row => desired.Privileges[row.Key]).ToList(), byKey, result, now, ct)
            .ConfigureAwait(false);
    }

    private async Task HandleForeignAsync(
        string dataSource,
        TenantId tenant,
        DatabricksDriftMode mode,
        string table,
        IReadOnlyList<UcPrivilegeAssignment> actual,
        IReadOnlyList<UcLedgerEntry> managed,
        string? provisioningPrincipal,
        CancellationToken ct)
    {
        var foreign = new List<(string Principal, string Privilege)>();
        foreach (var assignment in actual)
        {
            if (provisioningPrincipal is not null && string.Equals(assignment.Principal, provisioningPrincipal, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var privilege in assignment.Privileges)
            {
                if (!managed.Any(row => string.Equals(row.Principal, assignment.Principal, StringComparison.Ordinal) &&
                                        string.Equals(row.Privilege, privilege, StringComparison.Ordinal)))
                {
                    foreign.Add((assignment.Principal, privilege));
                }
            }
        }

        if (foreign.Count == 0)
        {
            return;
        }

        UcProvisioningMetrics.DriftFindings.Add(foreign.Count, new KeyValuePair<string, object?>("kind", "foreign"));
        var audit = new List<AuditLogEntry>();
        foreach (var (principal, privilege) in foreign)
        {
            // The finding names the securable and the privilege; the foreign principal's name stays out of the audit details.
            var findingKey = dataSource + "|" + table + "|" + principal + "|" + privilege;
            if (mode == DatabricksDriftMode.Report && !_state.ReportedForeign.TryAdd(findingKey, 0))
            {
                continue;
            }

            var key = new UcPrivilegeKey(UcSecurableType.Table, table, "foreign", privilege);
            audit.Add(Audit(tenant, AuditEventTypes.GrantProvisioningDrift, mode == DatabricksDriftMode.Revert ? "REVERTED" : "FOREIGN", dataSource, key, []));
        }

        if (mode == DatabricksDriftMode.Revert)
        {
            try
            {
                var changes = foreign.GroupBy(f => f.Principal, StringComparer.Ordinal)
                    .Select(g => new UcPermissionChange(g.Key, [], g.Select(f => f.Privilege).Distinct(StringComparer.Ordinal).ToList()))
                    .ToList();
                foreach (var chunk in changes.Chunk(100))
                {
                    await client.UpdatePermissionsAsync(dataSource, UcSecurableType.Table, table, chunk, ct).ConfigureAwait(false);
                }
            }
            catch (UcProvisioningException ex)
            {
                logger.LogWarning("Reverting foreign privileges on {DataSource} failed ({Code}).", dataSource, ex.Code);
                return;
            }
        }

        if (audit.Count > 0)
        {
            await store.SaveLedgerAsync(dataSource, _fencingToken, [], audit, ct).ConfigureAwait(false);
        }
    }
}
