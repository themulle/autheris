namespace Autheris.Application.Catalog.External;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Tenancy;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

public sealed partial class ExternalCatalogReader
{
    private async Task<ExternalCatalogResult<ExternalCatalogPageDto>> ListAppObjectsKeysetAsync(
        ClaimsPrincipal user,
        TenantId tenantId,
        ExternalCatalogListQuery query,
        DiscoveryObjectKind? kindFilter,
        string? afterKey,
        int? cursorCount,
        int pageSize,
        string cursorBinding,
        CancellationToken ct)
    {
        var allTables = await CatalogTablePageReader.ReadAllAsync(
            _metadataRepository,
            new CatalogTableQuery(IncludeInactive: false),
            static t => t.Table.IsActive,
            ct: ct).ConfigureAwait(false);
        var visibility = await ResolveCallerVisibilityAsync(user, tenantId, allTables, ct).ConfigureAwait(false);
        var inScope = await FilterInScopeTablesAsync(allTables, tenantId, ct).ConfigureAwait(false);

        IReadOnlyDictionary<string, CatalogObjectVersionRow>? versions = null;
        if (query.ChangedSince.HasValue)
        {
            versions = await _changeStore.GetObjectVersionsAsync(tenantId, ct).ConfigureAwait(false);
        }

        var candidates = FilterAppCandidates(inScope, visibility, query, kindFilter, versions);
        candidates.Sort(static (a, b) => DiscoveryService.CompareTableKeys(a.Identifier.ToString(), b.Identifier.ToString()));

        var startIndex = string.IsNullOrEmpty(afterKey) ? 0 : DiscoveryService.FindFirstIndexAfter(candidates, afterKey);
        var ownerFilter = string.IsNullOrWhiteSpace(query.Owner) ? null : query.Owner.Trim();

        List<TableMetadata> pageTables;
        var ownersByTable = new Dictionary<TableIdentifier, IReadOnlyList<DataOwner>>();
        int totalCount;
        bool hasMore;

        if (ownerFilter is null)
        {
            totalCount = candidates.Count;
            var takeCount = Math.Min(pageSize, Math.Max(0, candidates.Count - startIndex));
            hasMore = startIndex + takeCount < candidates.Count;
            pageTables = candidates.GetRange(startIndex, takeCount);
            if (pageTables.Count > 0)
            {
                var fetched = await _ownershipRepository.GetDataOwnersForTablesAsync(
                    pageTables.Select(static t => t.Identifier).ToList(), ct).ConfigureAwait(false);
                foreach (var kv in fetched)
                {
                    ownersByTable[kv.Key] = kv.Value;
                }
            }
        }
        else
        {
            pageTables = new List<TableMetadata>(pageSize);
            hasMore = false;
            var countFromCursor = cursorCount;
            var windowSize = countFromCursor.HasValue ? Math.Min(pageSize + 1, MaxPageSize) : MaxPageSize;
            var scanStart = countFromCursor.HasValue ? startIndex : 0;
            var matchedTotal = 0;

            for (var offset = scanStart; offset < candidates.Count; offset += windowSize)
            {
                if (countFromCursor.HasValue && hasMore)
                {
                    break;
                }

                var chunkLen = Math.Min(windowSize, candidates.Count - offset);
                var window = candidates.GetRange(offset, chunkLen);
                var fetched = await _ownershipRepository.GetDataOwnersForTablesAsync(
                    window.Select(static t => t.Identifier).ToList(), ct).ConfigureAwait(false);

                for (var i = 0; i < window.Count; i++)
                {
                    var table = window[i];
                    var owners = fetched.TryGetValue(table.Identifier, out var list) ? list : Array.Empty<DataOwner>();
                    if (!owners.Any(ow => ow.IsActive && (
                        string.Equals(ow.DisplayName, ownerFilter, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(ow.Email, ownerFilter, StringComparison.OrdinalIgnoreCase))))
                    {
                        continue;
                    }

                    matchedTotal++;
                    if (offset + i < startIndex)
                    {
                        continue;
                    }

                    if (pageTables.Count < pageSize)
                    {
                        pageTables.Add(table);
                        ownersByTable[table.Identifier] = owners;
                    }
                    else
                    {
                        hasMore = true;
                        if (countFromCursor.HasValue)
                        {
                            break;
                        }
                    }
                }
            }

            totalCount = countFromCursor ?? matchedTotal;
        }

        var dtos = new List<ExternalCatalogObjectDto>(pageTables.Count);
        if (pageTables.Count > 0)
        {
            versions ??= await _changeStore.GetObjectVersionsAsync(tenantId, ct).ConfigureAwait(false);
            foreach (var table in pageTables)
            {
                var owners = ownersByTable.TryGetValue(table.Identifier, out var list) ? list : Array.Empty<DataOwner>();
                var artifacts = _artifactStore is null
                    ? null
                    : await _artifactStore.GetAsync(table.Table.Id, ct).ConfigureAwait(false);
                versions.TryGetValue(table.Identifier.ToString(), out var ver);
                dtos.Add(MapAppObject(table, owners, artifacts, ver?.Seq, CanViewHiddenColumns(table, visibility)));
            }
        }

        var nextCursor = hasMore && pageTables.Count > 0
            ? ExternalCatalogCursor.Encode(pageTables[^1].Identifier.ToString(), totalCount, cursorBinding, _cursorKey)
            : null;
        return ExternalCatalogResult.Ok(new ExternalCatalogPageDto(dtos, nextCursor, totalCount));
    }

    private async Task<ExternalCatalogResult<ExternalCatalogPageDto>> ListUserObjectsKeysetAsync(
        ClaimsPrincipal user,
        TenantId tenantId,
        ExternalCatalogListQuery query,
        DiscoveryObjectKind? kindFilter,
        DiscoveryAccessState? accessFilter,
        string? searchText,
        string? searchMode,
        bool orderByRelevance,
        string? afterKey,
        int? cursorCount,
        int pageSize,
        string cursorBinding,
        CancellationToken ct)
    {
        IReadOnlyDictionary<string, CatalogObjectVersionRow>? versions = null;
        Func<TableMetadata, bool>? metadataPredicate = null;
        if (query.ChangedSince is { } minSeq)
        {
            versions = await _changeStore.GetObjectVersionsAsync(tenantId, ct).ConfigureAwait(false);
            metadataPredicate = t => (versions.TryGetValue(t.Identifier.ToString(), out var v) ? v.Seq : 0L) > minSeq;
        }

        var ownerFilter = string.IsNullOrWhiteSpace(query.Owner) ? null : query.Owner.Trim();
        var piiFilter = query.Pii;
        Func<DiscoveryObject, bool>? projectedPredicate = null;
        if (ownerFilter is not null || piiFilter.HasValue)
        {
            projectedPredicate = obj =>
            {
                if (ownerFilter is not null && !obj.Owners.Any(ow =>
                    string.Equals(ow.DisplayName, ownerFilter, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(ow.Email, ownerFilter, StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }

                if (piiFilter is { } wantPii)
                {
                    var hasPii = DiscoveryColumnVisibility.VisibleColumns(obj).Any(static c => c.Pii);
                    if (hasPii != wantPii)
                    {
                        return false;
                    }
                }

                return true;
            };
        }

        var caller = new CatalogCaller(user, tenantId, CatalogChannel.Rest);
        var keysetQuery = new DiscoveryKeysetQuery(
            First: pageSize,
            AfterKey: afterKey,
            Search: searchText,
            Language: string.IsNullOrWhiteSpace(query.Language) ? null : query.Language.Trim(),
            Surface: DiscoverySurface.Rest,
            Access: accessFilter,
            Domain: string.IsNullOrWhiteSpace(query.Domain) ? null : query.Domain.Trim(),
            Schema: string.IsNullOrWhiteSpace(query.Schema) ? null : query.Schema.Trim(),
            Kind: kindFilter,
            Tag: string.IsNullOrWhiteSpace(query.Tag) ? null : query.Tag.Trim(),
            Sensitivity: string.IsNullOrWhiteSpace(query.Sensitivity) ? null : query.Sensitivity.Trim(),
            MetadataPredicate: metadataPredicate,
            ProjectedPredicate: projectedPredicate,
            CountTotal: !cursorCount.HasValue,
            Mode: searchMode,
            OrderByRelevance: orderByRelevance);

        DiscoveryKeysetPage keysetPage;
        try
        {
            keysetPage = await _discoveryService.ListKeysetAsync(keysetQuery, caller, ct).ConfigureAwait(false);
        }
        catch (DiscoveryStatusException ex)
        {
            return MapDiscoveryFailure<ExternalCatalogPageDto>(ex);
        }

        var dtos = new List<ExternalCatalogObjectDto>(keysetPage.Items.Count);
        var pageTables = new List<TableMetadata>(keysetPage.Items.Count);
        if (keysetPage.Items.Count > 0)
        {
            versions ??= await _changeStore.GetObjectVersionsAsync(tenantId, ct).ConfigureAwait(false);
            var pageVisibility = await ResolveUserPageVisibilityAsync(
                user,
                tenantId,
                keysetPage.Items.Select(static i => i.Table).ToList(),
                ct).ConfigureAwait(false);

            foreach (var item in keysetPage.Items)
            {
                pageTables.Add(item.Table);
                versions.TryGetValue(item.Table.Identifier.ToString(), out var ver);
                dtos.Add(MapUserObject(item.Object, item.Table, ver?.Seq, CanViewHiddenColumns(item.Table, pageVisibility)));
            }

            await AttachRowScopeDescriptionsAsync(dtos, pageTables, user, tenantId, ct).ConfigureAwait(false);
        }

        var totalCount = keysetPage.TotalCount ?? cursorCount ?? dtos.Count;
        var nextCursor = keysetPage.NextKey is not null
            ? ExternalCatalogCursor.Encode(keysetPage.NextKey, totalCount, cursorBinding, _cursorKey)
            : null;
        return ExternalCatalogResult.Ok(new ExternalCatalogPageDto(dtos, nextCursor, totalCount));
    }

    private async Task<CallerVisibility> ResolveUserPageVisibilityAsync(
        ClaimsPrincipal user,
        TenantId tenantId,
        IReadOnlyList<TableMetadata> pageTables,
        CancellationToken ct)
    {
        var catalogOwner = TenantResolver.EffectiveDefaultOf(_gatewayOptions.Tenancy);
        var callerScope = CallerScopeResolver.Resolve(user, catalogOwner);
        bool allHiddenAllowed = callerScope is CallerScope.Global or CallerScope.Tenant
            || (callerScope == CallerScope.CatalogOwner && CallerScopeResolver.IsOwner(tenantId, catalogOwner));

        var ownedHidden = new HashSet<TableIdentifier>();
        var sid = user.GetUserSid();
        if (!allHiddenAllowed && sid.HasValue)
        {
            var hiddenIds = pageTables
                .Where(static t => t.Table.DiscoveryHidden || t.Columns.Any(static c => c.DiscoveryHidden))
                .Select(static t => t.Identifier)
                .ToList();
            if (hiddenIds.Count > 0)
            {
                var owned = await _ownershipRepository.GetOwnedOrDelegatedTablesAsync(
                    sid.Value, hiddenIds, _timeProvider.GetUtcNow(), ct).ConfigureAwait(false);
                foreach (var id in owned)
                {
                    ownedHidden.Add(id);
                }
            }
        }

        return new CallerVisibility(false, allHiddenAllowed, ownedHidden);
    }

    private static List<TableMetadata> FilterAppCandidates(
        IReadOnlyList<TableMetadata> inScope,
        CallerVisibility visibility,
        ExternalCatalogListQuery query,
        DiscoveryObjectKind? kindFilter,
        IReadOnlyDictionary<string, CatalogObjectVersionRow>? versions)
    {
        var d = string.IsNullOrWhiteSpace(query.Domain) ? null : query.Domain.Trim();
        var s = string.IsNullOrWhiteSpace(query.Schema) ? null : query.Schema.Trim();
        var tag = string.IsNullOrWhiteSpace(query.Tag) ? null : query.Tag.Trim();
        var sens = string.IsNullOrWhiteSpace(query.Sensitivity) ? null : query.Sensitivity.Trim();
        var minSeq = query.ChangedSince;

        var matched = new List<TableMetadata>(inScope.Count);
        foreach (var table in inScope)
        {
            if (table.Table.DiscoveryHidden && !CanViewHiddenTable(table, visibility))
            {
                continue;
            }

            if (kindFilter is { } k && table.Table.ObjectKind != k)
            {
                continue;
            }

            if (d is not null && !string.Equals(table.Identifier.Domain, d, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (s is not null && !string.Equals(table.Identifier.Schema, s, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (tag is not null && !HasAppEffectiveTag(table, tag))
            {
                continue;
            }

            if (sens is not null && !string.Equals(table.Table.Sensitivity, sens, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (minSeq is { } seqBound)
            {
                var seq = versions is not null && versions.TryGetValue(table.Identifier.ToString(), out var ver) ? ver.Seq : 0L;
                if (seq <= seqBound)
                {
                    continue;
                }
            }

            if (query.Pii is { } wantPii && AppTableHasPii(table, visibility) != wantPii)
            {
                continue;
            }

            matched.Add(table);
        }

        return matched;
    }

    private static bool AppTableHasPii(TableMetadata table, CallerVisibility visibility)
    {
        var includeHidden = CanViewHiddenColumns(table, visibility);
        foreach (var col in table.Columns)
        {
            if (col.DiscoveryHidden && !includeHidden)
            {
                continue;
            }

            var classified = ClassificationMetadata.IsColumnSensitive(table, col);
            if (classified || CatalogPiiDetector.IsPii(col.ColumnName, classified))
            {
                return true;
            }
        }

        return false;
    }

    private static ExternalCatalogResult<T> MapDiscoveryFailure<T>(DiscoveryStatusException ex)
    {
        var statusCode = ex.Kind switch
        {
            DiscoveryFailureKind.Unauthenticated => 401,
            DiscoveryFailureKind.Forbidden => 403,
            DiscoveryFailureKind.RateLimited => 429,
            _ => 400
        };

        return ExternalCatalogResult.Fail<T>(statusCode, ex.Code, ex.Message, ex.RetryAfterSeconds);
    }
}
