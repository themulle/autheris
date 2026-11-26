using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Catalog;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Policy.WriteGrants;
using Autheris.Application.Governance.Caching;
using Autheris.Application.Policy;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

namespace Autheris.Application.Governance.Writes;

public enum TableWritePolicyStatus
{
    Ok = 0,
    Forbidden = 1,
    NotFound = 2,
    Invalid = 3
}

public sealed record TableWritePolicyResult<T>(
    TableWritePolicyStatus Status,
    T? Value = default,
    string? ErrorCode = null,
    string? ErrorMessage = null);

public sealed record UpsertTableWritePolicyInput(
    string DatasetId,
    TableWriteMode Mode,
    IReadOnlyList<WriteOperation> AllowedOperations,
    IReadOnlyList<string>? WritableColumns = null,
    bool RequirePrimaryKeyPredicate = false,
    bool RequireRowFilter = false,
    long? MaxRowsPerStatement = null,
    IReadOnlyDictionary<string, string>? ServerStampedColumns = null);

/// <summary>
/// Governance application service for explicit table-level write policies (issue #1154).
/// Enforces GovernanceAdmin authorization, tenant ownership, policy-dependency exclusion,
/// column metadata validation, fail-closed audit, and cache invalidation.
/// </summary>
public sealed class TableWritePolicyService(
    ITableWritePolicyRepository policies,
    ITableMetadataRepository tables,
    IAuditWriter auditWriter,
    IGovernanceCapabilities capabilities,
    IRowPolicyDependencyIndex? policyDependencyIndex = null,
    IGovernanceWriteInvalidator? writeInvalidator = null,
    TimeProvider? timeProvider = null)
{
    private readonly ITableWritePolicyRepository _policies = policies ?? throw new ArgumentNullException(nameof(policies));
    private readonly ITableMetadataRepository _tables = tables ?? throw new ArgumentNullException(nameof(tables));
    private readonly IAuditWriter _auditWriter = auditWriter ?? throw new ArgumentNullException(nameof(auditWriter));
    private readonly IGovernanceCapabilities _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
    private readonly IRowPolicyDependencyIndex? _policyDependencyIndex = policyDependencyIndex;
    private readonly IGovernanceWriteInvalidator _writeInvalidator = writeInvalidator ?? new GovernanceWriteInvalidator(timeProvider: timeProvider);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<TableWritePolicyResult<TableWritePolicy?>> GetAsync(
        SecurityPrincipalContext caller,
        TenantId tenant,
        string datasetId,
        CancellationToken ct = default)
    {
        if (!_capabilities.IsGovernance(caller.Principal, tenant))
        {
            return new(TableWritePolicyStatus.Forbidden, ErrorCode: "ACCESS_DENIED", ErrorMessage: "Only Data Governance administrators can read table write policies.");
        }

        var meta = await ResolveTenantTableAsync(tenant, datasetId, ct).ConfigureAwait(false);
        if (meta is null)
        {
            return new(TableWritePolicyStatus.NotFound, ErrorCode: "RESOURCE_NOT_FOUND", ErrorMessage: "Dataset was not found.");
        }

        var found = await _policies.GetAsync(tenant, meta.Table.Id, ct).ConfigureAwait(false);
        return new(TableWritePolicyStatus.Ok, found);
    }

    public async Task<TableWritePolicyResult<TableWritePolicy>> UpsertAsync(
        SecurityPrincipalContext caller,
        TenantId tenant,
        UpsertTableWritePolicyInput input,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!_capabilities.IsGovernance(caller.Principal, tenant))
        {
            return new(TableWritePolicyStatus.Forbidden, ErrorCode: "ACCESS_DENIED", ErrorMessage: "Only Data Governance administrators can manage table write policies.");
        }

        var meta = await ResolveTenantTableAsync(tenant, input.DatasetId, ct).ConfigureAwait(false);
        if (meta is null)
        {
            return new(TableWritePolicyStatus.NotFound, ErrorCode: "RESOURCE_NOT_FOUND", ErrorMessage: "Dataset was not found.");
        }

        if (input.Mode != TableWriteMode.ReadOnly && _policyDependencyIndex?.IsPolicyDependency(tenant, meta.Identifier) == true)
        {
            return new(TableWritePolicyStatus.Invalid, ErrorCode: "WRITE_POLICY_DEPENDENCY_TABLE", ErrorMessage: "Tables referenced by row-level policies cannot be opted into writes.");
        }

        if (input.Mode == TableWriteMode.BoundedSingleTable
            && (input.AllowedOperations.Contains(WriteOperation.Update) || input.AllowedOperations.Contains(WriteOperation.Delete))
            && meta.PrimaryKeyColumns.Count == 0)
        {
            return new(TableWritePolicyStatus.Invalid, ErrorCode: "PRIMARY_KEY_REQUIRED", ErrorMessage: "BoundedSingleTable UPDATE/DELETE requires declared primary key columns on the table.");
        }

        var catalogCols = meta.Columns.ToDictionary(static c => c.ColumnName, StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<string>? normalizedWritable = null;
        if (input.WritableColumns is { Count: > 0 })
        {
            var resolved = new List<string>(input.WritableColumns.Count);
            foreach (var col in input.WritableColumns)
            {
                if (string.IsNullOrWhiteSpace(col) || !catalogCols.TryGetValue(col.Trim(), out var catalogCol))
                {
                    return new(TableWritePolicyStatus.Invalid, ErrorCode: "INVALID_COLUMNS", ErrorMessage: $"Unknown column '{col}' on target table.");
                }

                if (catalogCol.WriteProtected)
                {
                    return new(TableWritePolicyStatus.Invalid, ErrorCode: "WRITE_PROTECTED_COLUMN", ErrorMessage: $"Write-protected column '{catalogCol.ColumnName}' cannot be listed as writable.");
                }

                resolved.Add(catalogCol.ColumnName);
            }

            normalizedWritable = resolved.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        Dictionary<string, string>? normalizedStamped = null;
        if (input.ServerStampedColumns is { Count: > 0 })
        {
            normalizedStamped = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (col, stamp) in input.ServerStampedColumns)
            {
                if (string.IsNullOrWhiteSpace(col) || !catalogCols.TryGetValue(col.Trim(), out var catalogCol))
                {
                    return new(TableWritePolicyStatus.Invalid, ErrorCode: "INVALID_COLUMNS", ErrorMessage: $"Unknown server-stamped column '{col}' on target table.");
                }

                if (!TableWriteServerStampKinds.IsSupported(stamp))
                {
                    return new(TableWritePolicyStatus.Invalid, ErrorCode: "INVALID_WRITE_POLICY", ErrorMessage: $"Unsupported server stamp token '{stamp}'.");
                }

                normalizedStamped[catalogCol.ColumnName] = TableWriteServerStampKinds.Normalize(stamp);
            }
        }

        var now = _time.GetUtcNow().ToUniversalTime();
        var policy = new TableWritePolicy(
            Tenant: tenant,
            TableId: meta.Table.Id,
            Mode: input.Mode,
            AllowedOperations: input.AllowedOperations.Distinct().ToArray(),
            WritableColumns: normalizedWritable,
            RequirePrimaryKeyPredicate: input.Mode == TableWriteMode.BoundedSingleTable || input.RequirePrimaryKeyPredicate,
            RequireRowFilter: input.RequireRowFilter,
            MaxRowsPerStatement: input.MaxRowsPerStatement,
            ServerStampedColumns: normalizedStamped,
            UpdatedBy: caller.UserSid.Value,
            UpdatedAt: now);

        try
        {
            policy.Validate();
        }
        catch (InvalidOperationException ex)
        {
            return new(TableWritePolicyStatus.Invalid, ErrorCode: "INVALID_WRITE_POLICY", ErrorMessage: ex.Message);
        }

        var saved = await _policies.UpsertAsync(policy, ct).ConfigureAwait(false);
        await _auditWriter.RecordRequiredAsync(new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            TenantId = tenant,
            OccurredAt = now,
            EventType = AuditEventTypes.GovernanceDomainChanged,
            ActorSid = caller.UserSid,
            TargetTable = meta.Identifier.ToString(),
            Decision = "ALLOW",
            TraceId = Activity.Current?.TraceId.ToString() ?? string.Empty,
            DetailsJson = JsonSerializer.Serialize(new
            {
                action = "table_write_policy_updated",
                tableId = meta.Table.Id,
                mode = saved.Mode.ToString(),
                allowedOperations = saved.AllowedOperations.Select(static o => o.ToString()).ToArray(),
                requirePrimaryKeyPredicate = saved.EffectiveRequirePrimaryKeyPredicate,
                requireRowFilter = saved.RequireRowFilter,
                maxRowsPerStatement = saved.MaxRowsPerStatement
            })
        }, ct).ConfigureAwait(false);

        await _writeInvalidator.InvalidateTablesAsync(tenant, [meta.Identifier], ct: ct).ConfigureAwait(false);
        return new(TableWritePolicyStatus.Ok, saved);
    }

    private async Task<TableMetadata?> ResolveTenantTableAsync(TenantId tenant, string datasetId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(datasetId) || !TableIdentifier.TryParse(datasetId.Trim(), out var id))
        {
            return null;
        }

        var meta = await _tables.GetTableMetadataAsync(id, ct).ConfigureAwait(false);
        if (meta is null || !meta.Table.IsActive || !CatalogTenantOwnership.IsEligibleTargetInTenant(meta, tenant))
        {
            return null;
        }

        return meta;
    }
}
