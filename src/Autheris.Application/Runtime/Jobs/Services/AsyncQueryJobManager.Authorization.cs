namespace Autheris.Application.Runtime.Jobs.Services;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

public sealed partial class AsyncQueryJobManager
{
    public async Task<Stream?> GetJobResultStreamAsync(
        string jobId,
        TenantId tenantId,
        ClaimsPrincipal user,
        Autheris.Application.Policy.Pdp.IAccessDecisionPoint decisionPoint,
        ITableMetadataRepository metadataRepository,
        TrinoSqlEngine.ISqlEngine? sqlEngine = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(decisionPoint);
        ArgumentNullException.ThrowIfNull(metadataRepository);

        var userSid = AsyncJobOwner.Resolve(user) ?? string.Empty;
        var resolved = await ResolveAsync(jobId, tenantId, userSid, ct).ConfigureAwait(false);
        var job = resolved?.Descriptor;
        if (job == null || job.State != AsyncJobState.Completed)
        {
            return null;
        }

        await ReauthorizeJobFetchAsync(job, user, tenantId, userSid, decisionPoint, metadataRepository, sqlEngine ?? TrinoSqlEngine.FastSqlEngine.Default, ct)
            .ConfigureAwait(false);

        return await GetJobResultStreamAsync(jobId, tenantId, userSid, ct).ConfigureAwait(false);
    }

    private async Task ReauthorizeJobFetchAsync(
        AsyncJobDescriptor job,
        ClaimsPrincipal user,
        TenantId tenantId,
        string userSid,
        Autheris.Application.Policy.Pdp.IAccessDecisionPoint decisionPoint,
        ITableMetadataRepository metadataRepository,
        TrinoSqlEngine.ISqlEngine sqlEngine,
        CancellationToken ct)
    {
        if (_epochProvider is not null)
        {
            var currentEpoch = await _epochProvider.GetEpochAsync(tenantId, ct).ConfigureAwait(false);
            if (job.SubmittedGovernanceEpoch is null || currentEpoch != job.SubmittedGovernanceEpoch.Value)
                await FailForbiddenAsync(job, tenantId, userSid, "Async job result download denied: governance policy epoch changed since submission.", "Access to async job result is denied because governance policy changed since submission.", ct).ConfigureAwait(false);
        }

        IReadOnlyList<TableIdentifier> tables;
        try { tables = ResolveTablesForReauthorization(job, sqlEngine); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await FailForbiddenAsync(job, tenantId, userSid, "Async job referenced tables could not be validated at fetch.", "Access to async job result is denied.", ct).ConfigureAwait(false);
            return;
        }

        if (tables.Count == 0)
            await FailForbiddenAsync(job, tenantId, userSid, "Async job has no governed target tables for fetch re-authorization.", "Access to async job result is denied.", ct).ConfigureAwait(false);

        var secCtx = SecurityPrincipalContext.FromPrincipal(user, tenantId);
        var now = _time.GetUtcNow();
        if (GroupResolutionClaims.IsMarkedDegraded(user) ||
            GroupResolutionClaims.IsExpired(user, now) ||
            (job.SubmittedGroupResolutionComplete && (secCtx.GroupResolutionStatus != GroupResolutionStatus.Complete || (secCtx.GroupsExpireAt is { } exp && exp <= now))))
            await FailForbiddenAsync(job, tenantId, userSid, "Async job result download denied: caller group resolution degraded or expired.", "Access to async job result is denied because group resolution is degraded or expired.", ct).ConfigureAwait(false);

        if (!AreSubmittedPrincipalAttributesCompatible(job, user, secCtx))
            await FailForbiddenAsync(job, tenantId, userSid, "Async job result download denied: caller roles, groups, or scopes changed since submission.", "Access to async job result is denied because caller privileges changed since submission.", ct).ConfigureAwait(false);

        foreach (var tableId in tables)
        {
            var (meta, decision) = await TryDecideTableReadAsync(secCtx, tableId, decisionPoint, metadataRepository, ct).ConfigureAwait(false);
            if (meta is null || decision is null)
                await FailForbiddenAsync(job, tenantId, userSid, $"Async job result download denied: referenced table '{tableId}' is unavailable.", "Access to async job result is denied.", ct).ConfigureAwait(false);

            if (!decision!.IsAllowed)
            {
                var denyReason = decision.Reasons.Count > 0 ? decision.Reasons[0].Code : decision.DeniedReasons.FirstOrDefault() ?? "POLICY_DENIED";
                await FailForbiddenAsync(job, tenantId, userSid, $"Async job result download denied by governance policy ({denyReason}).", "Access to async job result is denied by governance policy.", ct).ConfigureAwait(false);
            }

            if (job.SubmittedTableSnapshots is { Count: > 0 } snapshots &&
                (!snapshots.TryGetValue(tableId.ToString(), out var snapshot) || !IsPolicySnapshotCompatible(snapshot, decision, meta!)))
                await FailForbiddenAsync(job, tenantId, userSid, $"Async job result download denied: column masking or row-level policy changed for '{tableId}' since execution.", "Access to async job result is denied because column or row protections changed since execution.", ct).ConfigureAwait(false);
        }
    }

    private async Task FailForbiddenAsync(AsyncJobDescriptor job, TenantId tenantId, string userSid, string auditMessage, string clientMessage, CancellationToken ct)
    {
        await DenyResultAsync(job, tenantId, userSid, auditMessage, ct).ConfigureAwait(false);
        throw new GatewayForbiddenException(clientMessage, "JOB_RESULT_FORBIDDEN");
    }

    private static async Task<(TableMetadata? Meta, TableAccessDecision? Decision)> TryDecideTableReadAsync(
        SecurityPrincipalContext secCtx,
        TableIdentifier tableId,
        IAccessDecisionPoint decisionPoint,
        ITableMetadataRepository metadataRepository,
        CancellationToken ct)
    {
        TableMetadata? meta;
        try { meta = await metadataRepository.GetTableMetadataAsync(tableId, ct).ConfigureAwait(false); }
        catch (TableMetadataUnavailableException) { meta = null; }
        if (meta is null || !meta.Table.IsActive) return (null, null);

        var decision = await decisionPoint.DecideAsync(new AccessRequest(secCtx, meta, AccessOperation.Read, AccessTransport.WebSql), ct).ConfigureAwait(false);
        return (meta, decision);
    }

    internal async Task<IReadOnlyDictionary<string, AsyncJobTablePolicySnapshot>?> CaptureExecutionSnapshotsAsync(
        AsyncJobDescriptor job, ClaimsPrincipal user, TenantId tenantId, IAccessDecisionPoint decisionPoint, ITableMetadataRepository metadataRepository, TrinoSqlEngine.ISqlEngine? sqlEngine, CancellationToken ct)
    {
        IReadOnlyList<TableIdentifier> tables;
        try { tables = ResolveTablesForReauthorization(job, sqlEngine ?? TrinoSqlEngine.FastSqlEngine.Default); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
        if (tables.Count == 0) return null;

        var secCtx = SecurityPrincipalContext.FromPrincipal(user, tenantId);
        var snapshots = new Dictionary<string, AsyncJobTablePolicySnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var tableId in tables)
        {
            var (meta, decision) = await TryDecideTableReadAsync(secCtx, tableId, decisionPoint, metadataRepository, ct).ConfigureAwait(false);
            if (meta is null || decision is null) return null;

            var cols = new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase);
            foreach (var col in meta.Columns) cols[col.ColumnName] = decision.GetEffectiveColumnAccess(col.ColumnName, meta);
            foreach (var colKey in decision.ColumnAccess.Keys) cols.TryAdd(colKey, decision.GetEffectiveColumnAccess(colKey, meta));
            snapshots[tableId.ToString()] = new AsyncJobTablePolicySnapshot(
                decision.IsAllowed,
                cols,
                decision.ColumnMasks?.Fingerprint,
                decision.CombinedRowFilterSql,
                decision.RowPolicy?.Fingerprint,
                decision.MandatoryRowPolicy?.Fingerprint,
                decision.AppliedVirtualFilters);
        }

        return snapshots;
    }

    private static bool AreSubmittedPrincipalAttributesCompatible(AsyncJobDescriptor job, ClaimsPrincipal user, SecurityPrincipalContext secCtx) =>
        (job.SubmittedByGroupSids is not { } subGroups || SetEquals(subGroups, secCtx.GroupSids.Select(static g => g.Value), StringComparer.Ordinal)) &&
        (job.SubmittedByRoles is not { } subRoles || SetEquals(subRoles, user.GetUserRoles(), StringComparer.OrdinalIgnoreCase)) &&
        (job.SubmittedByScopes is not { } subScopes || SetEquals(subScopes, TokenAccessScope.GetScopeSet(user.Claims), StringComparer.Ordinal));

    private static bool SetEquals(IReadOnlyList<string> expected, IEnumerable<string> actual, StringComparer comparer) =>
        new HashSet<string>(expected, comparer).SetEquals(new HashSet<string>(actual, comparer));

    private static bool IsPolicySnapshotCompatible(AsyncJobTablePolicySnapshot snapshot, TableAccessDecision current, TableMetadata meta)
    {
        if (!snapshot.IsAllowed || !current.IsAllowed ||
            !string.Equals(snapshot.CombinedRowFilterSql ?? string.Empty, current.CombinedRowFilterSql ?? string.Empty, StringComparison.Ordinal) ||
            !string.Equals(snapshot.RowPolicyFingerprint ?? string.Empty, current.RowPolicy?.Fingerprint ?? string.Empty, StringComparison.Ordinal) ||
            !string.Equals(snapshot.MandatoryRowPolicyFingerprint ?? string.Empty, current.MandatoryRowPolicy?.Fingerprint ?? string.Empty, StringComparison.Ordinal) ||
            !SequenceEqualsOrdinal(snapshot.AppliedVirtualFilters, current.AppliedVirtualFilters) ||
            !string.Equals(snapshot.ColumnMasksFingerprint ?? string.Empty, current.ColumnMasks?.Fingerprint ?? string.Empty, StringComparison.Ordinal))
        {
            return false;
        }

        var columns = new HashSet<string>(snapshot.ColumnAccess.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var col in meta.Columns) columns.Add(col.ColumnName);
        foreach (var col in current.ColumnAccess.Keys) columns.Add(col);
        foreach (var col in columns)
        {
            var prev = snapshot.ColumnAccess.TryGetValue(col, out var lvl) ? lvl : (current.HasUnconstrainedColumnAllow ? ColumnAccessLevel.Clear : ColumnAccessLevel.Mask);
            if (current.GetEffectiveColumnAccess(col, meta) != prev) return false;
        }

        return true;
    }

    private static bool SequenceEqualsOrdinal(IReadOnlyList<string>? left, IReadOnlyList<string>? right) =>
        (left ?? []).SequenceEqual(right ?? [], StringComparer.Ordinal);

    private IReadOnlyList<TableIdentifier> ResolveTablesForReauthorization(AsyncJobDescriptor job, TrinoSqlEngine.ISqlEngine sqlEngine)
    {
        if (job.TargetTables is { Count: > 0 })
        {
            var list = new List<TableIdentifier>(job.TargetTables.Count);
            foreach (var raw in job.TargetTables)
            {
                list.Add(ParseTargetTable(raw));
            }

            return list;
        }

        if (string.IsNullOrWhiteSpace(job.Query))
        {
            throw new InvalidOperationException("Async job metadata has no target tables for cross-node fetch re-authorization.");
        }

        var parsed = sqlEngine.Analyze(job.Query.AsMemory());
        if (parsed.ReferencedTables.Count == 0)
        {
            throw new InvalidOperationException("Async job query does not reference any governed table for fetch re-authorization.");
        }

        var result = new List<TableIdentifier>(parsed.ReferencedTables.Count);
        foreach (var t in parsed.ReferencedTables)
        {
            result.Add(ParseTargetTable(t.FullName));
        }

        return result;
    }

    private string[]? TryExtractQueryTables(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        try
        {
            var parsed = TrinoSqlEngine.FastSqlEngine.Default.Analyze(query.AsMemory());
            if (parsed.ReferencedTables.Count == 0)
            {
                return null;
            }

            return parsed.ReferencedTables
                .Select(t => ParseTargetTable(t.FullName).ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return null;
        }
    }

    private TableIdentifier ParseTargetTable(string rawName)
    {
        var defaultDs = _options?.Value?.WebSql?.DefaultDataSourceName;
        var defaultDomain = string.IsNullOrWhiteSpace(defaultDs) ? TableIdentifierNormalizer.DefaultDomain : defaultDs;
        var configuredSchema = _options?.Value is { } gw
            ? Autheris.Application.Sql.Services.SqlDataSourceResolver.ResolveConnectionOptions(gw, defaultDomain)?.DefaultSchema
            : null;
        var defaultSchema = string.IsNullOrWhiteSpace(configuredSchema)
            ? TableIdentifierNormalizer.DefaultSchema
            : configuredSchema;
        var parts = rawName.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length switch
        {
            3 => new TableIdentifier(UnquotePart(parts[0]), UnquotePart(parts[1]), UnquotePart(parts[2])),
            2 => new TableIdentifier(defaultDomain, UnquotePart(parts[0]), UnquotePart(parts[1])),
            1 => new TableIdentifier(defaultDomain, defaultSchema, UnquotePart(parts[0])),
            _ => throw new InvalidOperationException($"Unsupported table identifier '{rawName}'.")
        };
    }

    private static string UnquotePart(string part)
    {
        var trimmed = part.Trim();
        if (trimmed.Length >= 2 &&
            ((trimmed[0] == '"' && trimmed[^1] == '"') ||
             (trimmed[0] == '`' && trimmed[^1] == '`') ||
             (trimmed[0] == '[' && trimmed[^1] == ']')))
        {
            return trimmed[1..^1];
        }

        return trimmed;
    }
}
