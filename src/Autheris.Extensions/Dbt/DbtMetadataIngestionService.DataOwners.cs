namespace Autheris.Extensions.Dbt;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Dbt.Interfaces;
using Autheris.Application.Governance;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

/// <summary>
/// Data owners from the dbt governance import (issue #393 part b; I393B-SC-01..11). Owners are written only by the governance import, never by the
/// manifest sync. Everything here fails closed: an owner payload is rejected as a whole, before any metadata or owner write, unless the opt-in
/// option is on, the caller is a global governance admin and the write and audit repositories exist. No identifier value ever reaches an error
/// message, a warning, a log line or an audit detail; audit and messages carry owner ids, tables and governed codes only.
/// </summary>
public sealed partial class DbtMetadataIngestionService
{
    /// <summary>
    /// Composition with the owner import (issue #393 part b). The audit repository is mandatory here (L-5); <paramref name="ownerAssignments"/> may be
    /// absent, which makes an owner payload fail closed.
    /// </summary>
    public DbtMetadataIngestionService(
        IDbtProposalRepository proposalRepository,
        ITableMetadataRepository metadataRepository,
        ILineageGraphStore lineageGraphStore,
        IPolicyEpochRepository? epochRepository,
        Microsoft.Extensions.Options.IOptions<Autheris.Domain.Options.GatewayOptions>? gatewayOptions,
        Autheris.Application.SqlEndpoints.Services.SqlEndpointLoader? sqlEndpointLoader,
        ITableRelationRepository? relationRepository,
        VirtualFilterAdministrationService? virtualFilterAdmin,
        IRebacStore? rebacStore,
        Autheris.Application.Governance.Interfaces.IDatasetTagImportService? tagImport,
        Autheris.Application.Catalog.Profiling.ISyntheticSampleRowGenerator? syntheticRowGenerator,
        IDbtTestTargetMap? testTargetMap,
        ILogger<DbtMetadataIngestionService> logger,
        IAuditLogRepository auditLog,
        IDataOwnerAssignmentRepository? ownerAssignments = null,
        IDataOwnerIdentityVerifier? ownerIdentityVerifier = null)
        : this(proposalRepository, metadataRepository, lineageGraphStore, epochRepository, gatewayOptions, sqlEndpointLoader, relationRepository,
            virtualFilterAdmin, rebacStore, tagImport, syntheticRowGenerator, testTargetMap, logger)
    {
        _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
        _ownerAssignments = ownerAssignments;
        _ownerIdentityVerifier = ownerIdentityVerifier;
    }

    private const int MaxOwnerAssignmentsPerImport = 5000;
    private const int MaxNewOwnersPerImport = 1000;
    private const string OwnerImportSource = "dbt-governance-import";

    private sealed record OwnerImportError(string Code, string Table, string Field);

    private sealed class OwnerImportCollector
    {
        public List<TableOwnerAssignmentSpec> Specs { get; } = [];
        public List<OwnerImportError> Errors { get; } = [];
        public int Assignments { get; set; }
    }

    private sealed record OwnerImportPlan(IReadOnlyList<DataOwnerChange> Changes, IReadOnlyDictionary<string, OwnerIdentityStatus> Identity);

    private static bool AnyOwnerKeys(JsonElement modelsArray)
    {
        if (modelsArray.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var item in modelsArray.EnumerateArray())
        {
            if (DbtOwnerSpecValidator.HasOwnerKeys(item))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The gate of the owner path (I393B-SC-01, SC-02): null when the owner payload may be processed.</summary>
    private string? OwnerImportGate(DbtGovernanceImportCaller caller)
    {
        if (_gatewayOptions?.Value?.Dbt?.ImportDataOwners != true)
        {
            return "dbt_owner_import_disabled";
        }

        if (!caller.IsGovernanceAdmin)
        {
            return "dbt_owner_admin_required";
        }

        return _ownerAssignments is null || _auditLog is null ? "dbt_owner_import_unavailable" : null;
    }

    private static DbtGovernanceSyncResult OwnerFailure(string code, List<string> warnings, string? table = null, string? field = null) =>
        new(
            Success: false,
            UpdatedTablesCount: 0,
            UpdatedColumnsCount: 0,
            MaskingRulesCount: 0,
            VirtualFiltersCount: 0,
            AccessProfilesCount: 0,
            Warnings: warnings,
            ErrorMessage: $"Governance import rejected: owner import failed ({code})" +
                          (table is null ? string.Empty : $" for table '{table}'") + (field is null ? string.Empty : $" field '{field}'") +
                          ". Nothing was applied.");

    private void CollectOwners(JsonElement item, TableIdentifier table, DbtGovernanceImportCaller caller, OwnerImportCollector collector)
    {
        var tableName = table.ToString();
        var parsed = DbtOwnerSpecValidator.Parse(item);
        var error = parsed.Error ?? DbtOwnerSpecValidator.Validate(parsed.Owners, CallerIdentifiers(caller));
        if (error is not null)
        {
            collector.Errors.Add(new OwnerImportError(error.Code, tableName, error.Field));
            return;
        }

        collector.Assignments += parsed.Owners.Count;
        collector.Specs.Add(new TableOwnerAssignmentSpec(table, parsed.Owners));
    }

    private static IReadOnlyCollection<string> CallerIdentifiers(DbtGovernanceImportCaller caller)
    {
        var ids = new List<string>();
        if (!string.IsNullOrWhiteSpace(caller.Actor.Sid.Value))
        {
            ids.Add(caller.Actor.Sid.Value);
        }

        if (caller.Actor.Identifiers is not null)
        {
            ids.AddRange(caller.Actor.Identifiers.Where(i => !string.IsNullOrWhiteSpace(i)));
        }

        return ids;
    }

    /// <summary>
    /// Everything that can reject an owner payload before the first write: collected errors, caps, the read-only repository pre-check, the identity
    /// verification and (real runs only) the awaited audit intent (I393B-SC-03, SC-04, SC-06).
    /// </summary>
    private async Task<(DbtGovernanceSyncResult? Failure, OwnerImportPlan? Plan)> PrepareOwnerImportAsync(
        OwnerImportCollector collector, DbtGovernanceImportCaller caller, bool replace, bool dryRun, List<string> warnings, CancellationToken ct)
    {
        if (collector.Errors.Count > 0)
        {
            var first = collector.Errors[0];
            return (OwnerFailure(first.Code, warnings, first.Table, first.Field), null);
        }

        if (collector.Assignments > MaxOwnerAssignmentsPerImport)
        {
            return (OwnerFailure("dbt_owner_cap_exceeded", warnings), null);
        }

        var request = new DataOwnerAssignmentRequest(collector.Specs, replace, DryRun: true, MaxNewOwners: MaxNewOwnersPerImport);
        DataOwnerAssignmentResult preCheck;
        try
        {
            preCheck = await _ownerAssignments!.ApplyTableOwnerAssignmentsAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Dbt governance owner pre-check failed.");
            return (OwnerFailure("dbt_owner_apply_failed", warnings), null);
        }

        if (!preCheck.Success)
        {
            return (OwnerFailure(preCheck.ErrorCode ?? "dbt_owner_apply_failed", warnings, preCheck.ErrorTable), null);
        }

        var identity = new Dictionary<string, OwnerIdentityStatus>(StringComparer.OrdinalIgnoreCase);
        foreach (var sid in preCheck.Changes
                     .Where(c => c.Kind is DataOwnerChangeKind.OwnerCreated or DataOwnerChangeKind.OwnerAssigned)
                     .Select(c => c.OwnerSid)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var status = OwnerIdentityStatus.NotConfigured;
            if (_ownerIdentityVerifier is not null)
            {
                try
                {
                    status = await _ownerIdentityVerifier.VerifyAsync(caller.Tenant, sid, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    status = OwnerIdentityStatus.Unavailable;
                }
            }

            if (status == OwnerIdentityStatus.NotFoundOrInactive)
            {
                return (OwnerFailure("dbt_owner_identity_unverified", warnings), null);
            }

            if (status == OwnerIdentityStatus.Unavailable)
            {
                return (OwnerFailure("dbt_owner_identity_unavailable", warnings), null);
            }

            identity[sid] = status;
        }

        var plan = new OwnerImportPlan(preCheck.Changes, identity);
        if (!dryRun)
        {
            try
            {
                await _auditLog!.RecordAuditEventsAsync(BuildIntentEntries(plan, caller, replace), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Dbt governance owner audit intent could not be recorded; the import is aborted.");
                return (OwnerFailure("dbt_owner_audit_failed", warnings), null);
            }
        }

        return (null, plan);
    }

    private async Task<(DbtGovernanceSyncResult? Failure, IReadOnlyList<DataOwnerChange> Changes)> ApplyOwnersAsync(
        OwnerImportCollector collector, OwnerImportPlan plan, DbtGovernanceImportCaller caller, bool replace, List<string> warnings, CancellationToken ct)
    {
        string? failureCode = null;
        IReadOnlyList<DataOwnerChange> changes = [];
        try
        {
            var result = await _ownerAssignments!
                .ApplyTableOwnerAssignmentsAsync(new DataOwnerAssignmentRequest(collector.Specs, replace, DryRun: false, MaxNewOwners: MaxNewOwnersPerImport), ct)
                .ConfigureAwait(false);
            if (result.Success)
            {
                changes = result.Changes;
            }
            else
            {
                failureCode = result.ErrorCode ?? "dbt_owner_apply_failed";
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Dbt governance owner apply failed.");
            failureCode = "dbt_owner_apply_failed";
        }

        await RecordOutcomeAsync(failureCode, changes, plan, caller, replace, warnings, ct).ConfigureAwait(false);
        if (failureCode is not null)
        {
            warnings.Add("Table metadata, filters and profiles of this import were already applied; no owner row was written.");
            return (OwnerFailure(failureCode, warnings), changes);
        }

        // The write set was the planned one unless the data changed between the pre-check and the apply; report what was written.
        return (null, changes);
    }

    private async Task RecordOutcomeAsync(
        string? failureCode, IReadOnlyList<DataOwnerChange> changes, OwnerImportPlan plan, DbtGovernanceImportCaller caller, bool replace,
        List<string> warnings, CancellationToken ct)
    {
        var allVerified = plan.Identity.Count > 0 && plan.Identity.Values.All(s => s == OwnerIdentityStatus.Verified);
        var details = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["source"] = OwnerImportSource,
            ["phase"] = "outcome",
            ["outcome"] = failureCode ?? "applied",
            ["replace"] = replace,
            ["created"] = changes.Count(c => c.Kind == DataOwnerChangeKind.OwnerCreated),
            ["assigned"] = changes.Count(c => c.Kind == DataOwnerChangeKind.OwnerAssigned),
            ["removed"] = changes.Count(c => c.Kind == DataOwnerChangeKind.OwnerRemoved),
            ["identity_verified"] = allVerified
        });
        try
        {
            await _auditLog!.RecordAuditEventsAsync(
            [
                new AuditLogEntry
                {
                    TenantId = caller.Tenant,
                    EventType = "DBT_OWNER_IMPORT_OUTCOME",
                    ActorSid = caller.Actor.Sid,
                    TargetTable = "dbt-governance-import",
                    Decision = failureCode is null ? "ALLOW" : "DENY",
                    DetailsJson = details
                }
            ], ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Dbt governance owner outcome audit could not be recorded.");
            warnings.Add("The outcome audit event of the owner import could not be recorded.");
        }
    }

    private static List<AuditLogEntry> BuildIntentEntries(OwnerImportPlan plan, DbtGovernanceImportCaller caller, bool replace)
    {
        var entries = new List<AuditLogEntry>();
        foreach (var change in plan.Changes)
        {
            plan.Identity.TryGetValue(change.OwnerSid, out var status);
            var details = new Dictionary<string, object?>
            {
                ["table"] = change.Table.ToString(),
                ["data_owner_id"] = change.OwnerId,
                ["role"] = change.Role,
                ["source"] = OwnerImportSource,
                ["phase"] = "intent",
                ["replace"] = replace,
                ["identity_verified"] = status == OwnerIdentityStatus.Verified
            };
            if (change.Kind == DataOwnerChangeKind.OwnerRemoved)
            {
                details["removed_all"] = change.RemovedAll;
            }

            entries.Add(new AuditLogEntry
            {
                TenantId = caller.Tenant,
                EventType = change.Kind switch
                {
                    DataOwnerChangeKind.OwnerCreated => "DATA_OWNER_CREATED",
                    DataOwnerChangeKind.OwnerAssigned => "TABLE_OWNER_ASSIGNED",
                    _ => "TABLE_OWNER_REMOVED"
                },
                ActorSid = caller.Actor.Sid,
                TargetTable = change.Kind == DataOwnerChangeKind.OwnerCreated ? $"data_owner:{change.OwnerId}" : change.Table.ToString(),
                Decision = "ALLOW",
                DetailsJson = JsonSerializer.Serialize(details)
            });
        }

        return entries;
    }
}
