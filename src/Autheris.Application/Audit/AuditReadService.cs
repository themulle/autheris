namespace Autheris.Application.Audit;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Paging;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.Extensions.Options;

public enum AuditReadOutcome
{
    Ok = 0,
    Forbidden = 1,
    InvalidCursor = 2,
    InvalidView = 3
}

/// <param name="Caller">The resolved principal; owner membership is evaluated for it only.</param>
/// <param name="Tenant">Tenant derived server-side (never taken from the request body).</param>
/// <param name="TenantWide">True for Data Governance, Security Auditor and canonical ClusterAdmin; false for an owner-group member.</param>
public sealed record AuditReadRequest(
    SecurityPrincipalContext Caller,
    TenantId Tenant,
    bool TenantWide,
    string? TargetTable,
    string? Actor,
    DateTimeOffset? Since,
    string? View,
    string? Cursor,
    int? PageSize);

public sealed record AuditReadResult(
    AuditReadOutcome Outcome,
    IReadOnlyList<AuditLogEntry> Items,
    string? NextCursor,
    int PageSize)
{
    public static AuditReadResult Refused(AuditReadOutcome outcome) => new(outcome, [], null, 0);
}

/// <summary>
/// Scoped audit read: tenant-wide callers see their tenant, owner-group members only entries of objects in the containers whose owner group
/// they belong to. A view or cursor can only narrow the scope; the page size is bounded by the configured maximum.
/// </summary>
public sealed class AuditReadService(
    IAuditLogRepository auditRepository,
    ITableMetadataRepository? metadataRepository = null,
    IProtectionSnapshotProvider? protectionSnapshots = null,
    IGovernanceCapabilities? capabilities = null,
    IOptions<AccessPolicyOptions>? accessPolicy = null)
{
    /// <summary>Upper bound of objects one owner-scoped read covers; a larger ownership is truncated (narrower, never wider).</summary>
    public const int MaxScopedObjects = AuditLogPageQuery.MaxFilterValues;

    private const int MaxScanBatches = 5;
    private const int MaxScannedTables = 20_000;

    private readonly int _maxPageSize = Math.Clamp(
        accessPolicy?.Value.MaxPageSize ?? AuditLogPageQuery.MaxPageSize, 1, AuditLogPageQuery.MaxPageSize);

    public async Task<AuditReadResult> ReadAsync(AuditReadRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!AuditReadViews.TryParse(request.View, out var view))
        {
            return AuditReadResult.Refused(AuditReadOutcome.InvalidView);
        }

        var pageSize = Math.Clamp(request.PageSize ?? AuditLogPageQuery.DefaultPageSize, 1, _maxPageSize);

        string? singleTable = string.IsNullOrWhiteSpace(request.TargetTable) ? null : request.TargetTable;
        IReadOnlyCollection<string>? scopedTables = null;
        if (!request.TenantWide)
        {
            var owned = await ResolveOwnedObjectsAsync(request.Caller, request.Tenant, ct).ConfigureAwait(false);
            if (owned.Count == 0 || (singleTable is not null && !owned.Contains(singleTable)))
            {
                return AuditReadResult.Refused(AuditReadOutcome.Forbidden);
            }

            scopedTables = singleTable is null ? owned.Order(StringComparer.Ordinal).ToArray() : [singleTable];
            singleTable = null;
        }

        var scopeTag = ScopeTag(request, view, scopedTables, singleTable);
        long? before = null;
        if (request.Cursor is not null)
        {
            if (!TryDecodeCursor(request.Cursor, scopeTag, out var decoded))
            {
                return AuditReadResult.Refused(AuditReadOutcome.InvalidCursor);
            }

            before = decoded;
        }

        var items = new List<AuditLogEntry>(pageSize);
        long? next = null;
        var filterActorKind = AuditReadViews.NeedsActorKindFilter(view);
        for (var batch = 0; batch < MaxScanBatches; batch++)
        {
            var page = await auditRepository.QueryAuditLogPageAsync(
                new AuditLogPageQuery(
                    request.Tenant,
                    singleTable,
                    string.IsNullOrWhiteSpace(request.Actor) ? (Sid?)null : new Sid(request.Actor),
                    request.Since,
                    before,
                    pageSize,
                    AuditReadViews.EventTypes(view),
                    scopedTables),
                ct).ConfigureAwait(false);

            var filled = false;
            foreach (var entry in page.Items)
            {
                if (filterActorKind && !AuditReadViews.Matches(view, entry))
                {
                    continue;
                }

                items.Add(entry);
                if (items.Count >= pageSize)
                {
                    next = entry.ChainSequence;
                    filled = true;
                    break;
                }
            }

            if (filled)
            {
                break;
            }

            next = page.NextBeforeSequence;
            before = page.NextBeforeSequence;
            if (!filterActorKind || before is null)
            {
                break;
            }
        }

        return new AuditReadResult(
            AuditReadOutcome.Ok,
            items,
            next is { } position ? EncodeCursor(position, scopeTag) : null,
            pageSize);
    }

    private async Task<HashSet<string>> ResolveOwnedObjectsAsync(SecurityPrincipalContext caller, TenantId tenant, CancellationToken ct)
    {
        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (metadataRepository is null || protectionSnapshots is null || capabilities is null)
        {
            // without the ownership collaborators no owner scope can be established (fail closed)
            return owned;
        }

        var memberships = new Dictionary<ContainerRef, bool>();
        string? after = null;
        var scanned = 0;
        do
        {
            CatalogTablePage page;
            try
            {
                page = await metadataRepository.QueryTablesAsync(
                    new CatalogTableQuery(After: after, Limit: CatalogTableQuery.MaxLimit), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return owned;
            }

            foreach (var table in page.Items)
            {
                if (owned.Count >= MaxScopedObjects)
                {
                    return owned;
                }

                try
                {
                    var snapshot = await protectionSnapshots.GetAsync(tenant, table.Identifier, ct).ConfigureAwait(false);
                    if (snapshot.OwnerGroupSid is null)
                    {
                        continue;
                    }

                    if (!memberships.TryGetValue(snapshot.Container, out var member))
                    {
                        member = await capabilities.IsOwnerMemberAsync(caller, snapshot.Container, ct).ConfigureAwait(false);
                        memberships[snapshot.Container] = member;
                    }

                    if (member)
                    {
                        owned.Add(table.Identifier.ToString());
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // an object whose ownership cannot be determined is not part of the scope (fail closed)
                }
            }

            after = page.NextAfter;
            scanned += page.Items.Count;
        }
        while (after is not null && scanned < MaxScannedTables);

        return owned;
    }

    private static string ScopeTag(AuditReadRequest request, AuditReadView view, IReadOnlyCollection<string>? scopedTables, string? singleTable)
    {
        var sb = new StringBuilder()
            .Append(request.Tenant.Value).Append('|')
            .Append(request.TenantWide ? "wide" : "owner").Append('|')
            .Append(view).Append('|')
            .Append(singleTable?.ToLowerInvariant()).Append('|')
            .Append(request.Actor).Append('|')
            .Append(request.Since?.UtcTicks.ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(request.Caller.UserSid.Value).Append('|');
        if (scopedTables is not null)
        {
            foreach (var table in scopedTables.Order(StringComparer.Ordinal))
            {
                sb.Append(table.ToLowerInvariant()).Append(';');
            }
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }

    private static string EncodeCursor(long position, string scopeTag) =>
        PageCursor.Encode(position.ToString(CultureInfo.InvariantCulture) + "." + scopeTag);

    private static bool TryDecodeCursor(string cursor, string expectedTag, out long position)
    {
        position = 0;
        if (!PageCursor.TryDecode(cursor, out var raw))
        {
            return false;
        }

        var dot = raw.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0
            || !long.TryParse(raw.AsSpan(0, dot), NumberStyles.None, CultureInfo.InvariantCulture, out position)
            || position <= 0)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(raw[(dot + 1)..]),
            Encoding.UTF8.GetBytes(expectedTag));
    }
}
