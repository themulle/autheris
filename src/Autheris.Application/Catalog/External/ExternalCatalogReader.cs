namespace Autheris.Application.Catalog.External;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.ConsentRequests;
using Autheris.Application.Governance;
using Autheris.Application.Governance.Services;
using Autheris.Application.Governance.Paging;
using Autheris.Application.Security;
using Autheris.Application.Tenancy;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.Extensions.Options;

/// <summary>
/// Read-only external catalog service supporting user tokens (via <see cref="IDiscoveryService"/>) and
/// registered app-only service principals with strict hidden-object visibility rules and caller-specific hashing.
/// </summary>
public sealed class ExternalCatalogReader(
    ITableMetadataRepository metadataRepository,
    IDiscoveryService discoveryService,
    IConsentRepository consentRepository,
    IDataOwnershipRepository ownershipRepository,
    IConsentWorkflowConfigRepository workflowConfigRepository,
    ICatalogChangeStore changeStore,
    IOptions<GatewayOptions> gatewayOptions,
    IDiscoveryArtifactStore? artifactStore = null,
    TimeProvider? timeProvider = null,
    IOptions<GovernanceTextOptions>? textOptions = null)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;
    public const int MaxChangesLimit = 500;
    private const string CatalogReadAdminPermission = "Autheris.Catalog.Read.Admin";

    /// <summary>Issue #798: fixed text of an unknown <c>accessStatus</c> value (the value is never echoed).</summary>
    internal const string InvalidAccessStatusMessage =
        "Invalid accessStatus filter. Allowed values: granted, requested, expired, none (or GRANTED, PENDING, EXPIRED, REQUEST_ACCESS).";

    private readonly ITableMetadataRepository _metadataRepository = metadataRepository ?? throw new ArgumentNullException(nameof(metadataRepository));
    private readonly IDiscoveryService _discoveryService = discoveryService ?? throw new ArgumentNullException(nameof(discoveryService));
    private readonly IConsentRepository _consentRepository = consentRepository ?? throw new ArgumentNullException(nameof(consentRepository));
    private readonly IDataOwnershipRepository _ownershipRepository = ownershipRepository ?? throw new ArgumentNullException(nameof(ownershipRepository));
    private readonly IConsentWorkflowConfigRepository _workflowConfigRepository = workflowConfigRepository ?? throw new ArgumentNullException(nameof(workflowConfigRepository));
    private readonly ICatalogChangeStore _changeStore = changeStore ?? throw new ArgumentNullException(nameof(changeStore));
    private readonly GatewayOptions _gatewayOptions = gatewayOptions?.Value ?? throw new ArgumentNullException(nameof(gatewayOptions));
    private readonly IDiscoveryArtifactStore? _artifactStore = artifactStore;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly int _descriptionMaxLength = (textOptions?.Value ?? new GovernanceTextOptions()).ConsentDescriptionMaxLength;

    public async Task<ExternalCatalogResult<ExternalCatalogPageDto>> ListObjectsAsync(
        ClaimsPrincipal user,
        TenantId tenantId,
        ExternalCatalogListQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(query);

        if (!TryParseKindFilter(query.Kind, out var kindFilter))
        {
            return ExternalCatalogResult.Fail<ExternalCatalogPageDto>(
                400,
                "INVALID_QUERY",
                "Invalid kind filter. Allowed values: table, view, procedure.");
        }

        int afterOffset = 0;
        if (query.Cursor is not null)
        {
            if (!PageCursor.TryDecode(query.Cursor, out var rawCursor)
                || !int.TryParse(rawCursor, out afterOffset)
                || afterOffset < 0)
            {
                return ExternalCatalogResult.Fail<ExternalCatalogPageDto>(400, "INVALID_CURSOR", "The cursor is malformed.");
            }
        }

        if (!CatalogAccessStatus.TryParseFilter(query.AccessStatus, out var accessFilter))
        {
            return ExternalCatalogResult.Fail<ExternalCatalogPageDto>(400, "INVALID_QUERY", InvalidAccessStatusMessage);
        }

        int pageSize = Math.Clamp(query.PageSize <= 0 ? DefaultPageSize : query.PageSize, 1, MaxPageSize);
        var (allTables, versions, visibility) = await LoadContextAsync(user, tenantId, ct).ConfigureAwait(false);

        // Issue #798: an app-only caller has no caller access state (callerState is null), so there is nothing to filter on.
        if (accessFilter is not null && visibility.IsServicePrincipal)
        {
            return ExternalCatalogResult.Fail<ExternalCatalogPageDto>(400, "INVALID_QUERY", "The accessStatus filter applies to user callers only.");
        }

        // The access filter runs in the discovery service, before the total and the cursor are computed below.
        List<ExternalCatalogObjectDto> allDtos = visibility.IsServicePrincipal
            ? await BuildAppCatalogObjectsAsync(allTables, tenantId, versions, visibility, ct).ConfigureAwait(false)
            : await BuildUserCatalogObjectsAsync(user, tenantId, allTables, versions, visibility, accessFilter, ct).ConfigureAwait(false);

        var filtered = ApplyFilters(allDtos, query, kindFilter)
            .OrderBy(static o => o.ObjectId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var pageItems = filtered.Skip(afterOffset).Take(pageSize).ToList();
        if (!visibility.IsServicePrincipal)
        {
            await AttachRowScopeDescriptionsAsync(pageItems, allTables, user, tenantId, ct).ConfigureAwait(false);
        }

        var nextOffset = afterOffset + pageItems.Count;
        var nextCursor = nextOffset < filtered.Count ? PageCursor.Encode(nextOffset.ToString()) : null;

        return ExternalCatalogResult.Ok(
            new ExternalCatalogPageDto(pageItems, nextCursor, filtered.Count));
    }

    public async Task<ExternalCatalogResult<ExternalCatalogObjectDto>> GetObjectAsync(
        ClaimsPrincipal user,
        TenantId tenantId,
        string rawId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (string.IsNullOrWhiteSpace(rawId))
        {
            return ExternalCatalogResult.Fail<ExternalCatalogObjectDto>(404, "OBJECT_NOT_FOUND", "Catalog object was not found.");
        }

        var (allTables, versions, visibility) = await LoadContextAsync(user, tenantId, ct).ConfigureAwait(false);
        var matchedTable = FindMatchingTable(allTables, rawId);
        if (matchedTable is null)
        {
            return ExternalCatalogResult.Fail<ExternalCatalogObjectDto>(404, "OBJECT_NOT_FOUND", "Catalog object was not found.");
        }

        if (!await IsTableInTenantScopeAsync(matchedTable, tenantId, ct).ConfigureAwait(false))
        {
            return ExternalCatalogResult.Fail<ExternalCatalogObjectDto>(404, "OBJECT_NOT_FOUND", "Catalog object was not found.");
        }

        if (matchedTable.Table.DiscoveryHidden && !CanViewHiddenTable(matchedTable, visibility))
        {
            return ExternalCatalogResult.Fail<ExternalCatalogObjectDto>(404, "OBJECT_NOT_FOUND", "Catalog object was not found.");
        }

        if (visibility.IsServicePrincipal)
        {
            var dto = await MapSingleAppObjectAsync(matchedTable, versions, CanViewHiddenColumns(matchedTable, visibility), ct)
                .ConfigureAwait(false);
            return ExternalCatalogResult.Ok(dto);
        }

        var caller = new CatalogCaller(user, tenantId, CatalogChannel.Rest);
        var discovered = await _discoveryService.GetAsync(matchedTable.Identifier, caller, ct).ConfigureAwait(false);
        if (discovered is null)
        {
            return ExternalCatalogResult.Fail<ExternalCatalogObjectDto>(404, "OBJECT_NOT_FOUND", "Catalog object was not found.");
        }

        versions.TryGetValue(matchedTable.Identifier.ToString(), out var ver);
        var mapped = MapUserObject(discovered, matchedTable, ver?.Seq, CanViewHiddenColumns(matchedTable, visibility));
        var single = new List<ExternalCatalogObjectDto> { mapped };
        await AttachRowScopeDescriptionsAsync(single, [matchedTable], user, tenantId, ct).ConfigureAwait(false);
        return ExternalCatalogResult.Ok(single[0]);
    }

    public async Task<ExternalCatalogResult<ExternalCatalogChangesResponseDto>> GetChangesAsync(
        ClaimsPrincipal user,
        TenantId tenantId,
        long sinceSeq,
        int limit,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        var clampedLimit = Math.Clamp(limit <= 0 ? 100 : limit, 1, MaxChangesLimit);
        var normalizedSince = Math.Max(0L, sinceSeq);
        var feed = await _changeStore.ReadCatalogChangesAsync(tenantId, normalizedSince, clampedLimit, ct).ConfigureAwait(false);

        if (feed.Changes.Count == 0)
        {
            return ExternalCatalogResult.Ok(
                new ExternalCatalogChangesResponseDto([], feed.NextSince));
        }

        var (allTables, _, visibility) = await LoadContextAsync(user, tenantId, ct).ConfigureAwait(false);
        var byObjectId = allTables.ToDictionary(static t => t.Identifier.ToString(), StringComparer.OrdinalIgnoreCase);

        var items = new List<ExternalCatalogChangeDto>(feed.Changes.Count);
        foreach (var ch in feed.Changes)
        {
            var effectiveKind = NormalizeChangeKind(ch.Kind);
            if (!string.Equals(effectiveKind, "removed", StringComparison.Ordinal)
                && byObjectId.TryGetValue(ch.ObjectId, out var table)
                && table.Table.DiscoveryHidden
                && !CanViewHiddenTable(table, visibility))
            {
                effectiveKind = "removed";
            }

            items.Add(new ExternalCatalogChangeDto(ch.ObjectId, effectiveKind, ch.Seq, ch.ChangedAt));
        }

        return ExternalCatalogResult.Ok(
            new ExternalCatalogChangesResponseDto(items, feed.NextSince));
    }

    public static string ComputeAdminRepresentationHash(
        TableMetadata table,
        IReadOnlyList<DataOwner> owners,
        DiscoveryArtifacts? artifacts)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(owners);

        var payload = new
        {
            objectId = table.Identifier.ToString(),
            kind = KindName(table.Table.ObjectKind),
            domain = table.Identifier.Domain,
            schema = table.Identifier.Schema,
            name = table.Identifier.TableName,
            displayName = table.Table.DisplayName,
            description = table.Table.Description,
            sensitivity = table.Table.Sensitivity,
            isActive = table.Table.IsActive,
            discoveryHidden = table.Table.DiscoveryHidden,
            samplesDisabled = table.Table.SamplesDisabled,
            documentationSource = table.Table.DocumentationSource,
            tags = table.CatalogTags.Concat(table.Table.Tags).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray(),
            owners = owners.Where(static o => o.IsActive).Select(static o => $"{o.DisplayName}|{o.Email}").Order(StringComparer.Ordinal).ToArray(),
            columns = table.Columns
                .OrderBy(static c => c.ColumnName, StringComparer.OrdinalIgnoreCase)
                .Select(c => new
                {
                    c.ColumnName,
                    c.DisplayName,
                    c.DataType,
                    c.Description,
                    Classified = ClassificationMetadata.IsColumnSensitive(table, c),
                    c.DiscoveryHidden,
                    c.WriteProtected,
                    masking = DefaultMaskingState(table, c)
                })
                .ToArray(),
            artifactsHash = artifacts?.ContentSha256
        };

        var json = JsonSerializer.Serialize(payload);
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private async Task<(IReadOnlyList<TableMetadata> AllTables, IReadOnlyDictionary<string, CatalogObjectVersionRow> Versions, CallerVisibility Visibility)>
        LoadContextAsync(ClaimsPrincipal user, TenantId tenantId, CancellationToken ct)
    {
        var allTables = await CatalogTablePageReader.ReadAllAsync(
            _metadataRepository,
            new CatalogTableQuery(IncludeInactive: false),
            static t => t.Table.IsActive,
            ct: ct).ConfigureAwait(false);
        var versions = await _changeStore.GetObjectVersionsAsync(tenantId, ct).ConfigureAwait(false);
        var visibility = await ResolveCallerVisibilityAsync(user, tenantId, allTables, ct).ConfigureAwait(false);
        return (allTables, versions, visibility);
    }

    private async Task<CallerVisibility> ResolveCallerVisibilityAsync(
        ClaimsPrincipal user,
        TenantId tenantId,
        IReadOnlyList<TableMetadata> allTables,
        CancellationToken ct)
    {
        bool isServicePrincipal = IsAppOnlyPrincipal(user);
        if (isServicePrincipal)
        {
            if (!HasAppRole(user, CatalogReadAdminPermission))
            {
                return new CallerVisibility(true, false, new HashSet<TableIdentifier>());
            }

            var clientId = GetClientId(user);
            if (string.IsNullOrWhiteSpace(clientId))
            {
                return new CallerVisibility(true, false, new HashSet<TableIdentifier>());
            }

            var system = await _workflowConfigRepository.GetExternalWorkflowSystemByClientIdAsync(tenantId, clientId, ct)
                .ConfigureAwait(false);
            bool allowed = system is { Enabled: true } && system.TenantId == tenantId;
            return new CallerVisibility(true, allowed, new HashSet<TableIdentifier>());
        }

        // User token (interactive or delegated): delegated scopes never unlock hidden objects on their own;
        // only tenant GovernanceAdmin / ClusterAdmin (validated via CallerScopeResolver) or data owner / delegate applies.
        var catalogOwner = TenantResolver.EffectiveDefaultOf(_gatewayOptions.Tenancy);
        var callerScope = CallerScopeResolver.Resolve(user, catalogOwner);
        bool allHiddenAllowed = callerScope is CallerScope.Global or CallerScope.Tenant
            || (callerScope == CallerScope.CatalogOwner && CallerScopeResolver.IsOwner(tenantId, catalogOwner));

        var ownedHidden = new HashSet<TableIdentifier>();
        var sid = user.GetUserSid();
        if (!allHiddenAllowed && sid.HasValue)
        {
            var hiddenIds = allTables
                .Where(static t => t.Table.IsActive && t.Table.DiscoveryHidden)
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

    private async Task<List<ExternalCatalogObjectDto>> BuildAppCatalogObjectsAsync(
        IReadOnlyList<TableMetadata> allTables,
        TenantId tenantId,
        IReadOnlyDictionary<string, CatalogObjectVersionRow> versions,
        CallerVisibility visibility,
        CancellationToken ct)
    {
        var inScope = await FilterInScopeTablesAsync(allTables, tenantId, ct).ConfigureAwait(false);
        var visibleTables = inScope
            .Where(t => !t.Table.DiscoveryHidden || CanViewHiddenTable(t, visibility))
            .ToList();

        if (visibleTables.Count == 0)
        {
            return [];
        }

        var ownersByTable = await _ownershipRepository.GetDataOwnersForTablesAsync(
            visibleTables.Select(static t => t.Identifier).ToList(), ct).ConfigureAwait(false);

        var result = new List<ExternalCatalogObjectDto>(visibleTables.Count);
        foreach (var table in visibleTables)
        {
            var owners = ownersByTable.TryGetValue(table.Identifier, out var list) ? list : Array.Empty<DataOwner>();
            var artifacts = _artifactStore is null
                ? null
                : await _artifactStore.GetAsync(table.Table.Id, ct).ConfigureAwait(false);
            versions.TryGetValue(table.Identifier.ToString(), out var ver);
            result.Add(MapAppObject(table, owners, artifacts, ver?.Seq, CanViewHiddenColumns(table, visibility)));
        }

        return result;
    }

    private async Task<List<ExternalCatalogObjectDto>> BuildUserCatalogObjectsAsync(
        ClaimsPrincipal user,
        TenantId tenantId,
        IReadOnlyList<TableMetadata> allTables,
        IReadOnlyDictionary<string, CatalogObjectVersionRow> versions,
        CallerVisibility visibility,
        DiscoveryAccessState? accessFilter,
        CancellationToken ct)
    {
        var byId = allTables.ToDictionary(static t => t.Identifier);
        var caller = new CatalogCaller(user, tenantId, CatalogChannel.Rest);
        var result = new List<ExternalCatalogObjectDto>();
        int after = 0;

        while (true)
        {
            var page = await _discoveryService.ListAsync(
                new DiscoveryQuery(First: DiscoveryService.MaxPageSize, After: after, Surface: DiscoverySurface.Rest, Access: accessFilter),
                caller,
                ct).ConfigureAwait(false);

            foreach (var obj in page.Items)
            {
                var id = new TableIdentifier(obj.Domain, obj.Schema, obj.Name);
                if (!byId.TryGetValue(id, out var table))
                {
                    continue;
                }

                if (obj.IsHidden && !CanViewHiddenTable(table, visibility))
                {
                    continue;
                }

                versions.TryGetValue(id.ToString(), out var ver);
                result.Add(MapUserObject(obj, table, ver?.Seq, CanViewHiddenColumns(table, visibility)));
            }

            if (!page.HasNextPage || page.Items.Count == 0)
            {
                break;
            }

            after += page.Items.Count;
        }

        return result;
    }

    /// <summary>
    /// Fills <c>callerState.rowScopeDescription</c> for the granted objects of one response page: the caller's own applicable allow consents
    /// (the predicate of the access explanation), tenant bound, one consent read and one description load per page. Any failure leaves the
    /// field <c>null</c> and never hides the object. The text is never logged.
    /// </summary>
    private async Task AttachRowScopeDescriptionsAsync(
        List<ExternalCatalogObjectDto> items,
        IReadOnlyList<TableMetadata> tables,
        ClaimsPrincipal user,
        TenantId tenantId,
        CancellationToken ct)
    {
        var granted = items.Where(static i => i.CallerState is { Access: "granted" }).ToList();
        var userSid = user.GetUserSid();
        if (granted.Count == 0 || userSid is null)
        {
            return;
        }

        Dictionary<string, string?> byObject;
        try
        {
            var groupSids = user.GetGroupSids();
            var roles = user.GetUserRoles();
            var allUserSids = user.GetAllUserSids();
            var subjects = (allUserSids.Count > 0 ? allUserSids : [userSid.Value]).Concat(groupSids).Distinct().ToList();
            var now = _timeProvider.GetUtcNow();
            var all = (await _consentRepository.GetAllActiveConsentsForSubjectsAsync(subjects, roles, now, tenantId, ct).ConfigureAwait(false))
                .Where(c => c.TenantId == tenantId)
                .ToList();

            var tableById = tables.ToDictionary(static t => t.Identifier.ToString(), static t => t.Identifier, StringComparer.OrdinalIgnoreCase);
            var applicableByObject = new Dictionary<string, List<Consent>>(StringComparer.OrdinalIgnoreCase);
            var distinct = new Dictionary<Guid, Consent>();
            foreach (var item in granted)
            {
                if (!tableById.TryGetValue(item.ObjectId, out var id))
                {
                    continue;
                }

                var applicable = EffectiveAccessExplanationService.MatchingAllowConsents(all, id, now, userSid.Value, groupSids, roles, allUserSids);
                applicableByObject[item.ObjectId] = applicable;
                foreach (var c in applicable)
                {
                    distinct[c.Id] = c;
                }
            }

            await ConsentDescriptionLoader.LoadAsync(_consentRepository, distinct.Values.ToList(), ct).ConfigureAwait(false);
            byObject = applicableByObject.ToDictionary(
                static kv => kv.Key,
                kv => ConsentDescriptionSelector.SelectOne(kv.Value, _descriptionMaxLength),
                StringComparer.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return;
        }

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.CallerState is { Access: "granted" } state
                && byObject.TryGetValue(item.ObjectId, out var text)
                && text is not null)
            {
                items[i] = item with { CallerState = state with { RowScopeDescription = text } };
            }
        }
    }

    private async Task<ExternalCatalogObjectDto> MapSingleAppObjectAsync(
        TableMetadata table,
        IReadOnlyDictionary<string, CatalogObjectVersionRow> versions,
        bool canSeeHidden,
        CancellationToken ct)
    {
        var ownersByTable = await _ownershipRepository.GetDataOwnersForTablesAsync([table.Identifier], ct).ConfigureAwait(false);
        var owners = ownersByTable.TryGetValue(table.Identifier, out var list) ? list : Array.Empty<DataOwner>();
        var artifacts = _artifactStore is null
            ? null
            : await _artifactStore.GetAsync(table.Table.Id, ct).ConfigureAwait(false);
        versions.TryGetValue(table.Identifier.ToString(), out var ver);
        return MapAppObject(table, owners, artifacts, ver?.Seq, canSeeHidden);
    }

    private static ExternalCatalogObjectDto MapAppObject(
        TableMetadata table,
        IReadOnlyList<DataOwner> activeOwners,
        DiscoveryArtifacts? artifacts,
        long? changeSeq,
        bool includeHiddenColumns)
    {
        var baseObj = DiscoveryAccessMapper.Map(
            table,
            new TableAccessDecision(
                table.Identifier,
                IsAllowed: false,
                ColumnAccess: new Dictionary<string, ColumnAccessLevel>(),
                CombinedRowFilterSql: null,
                DeniedReasons: []),
            activeOwners,
            isPendingRequest: false,
            isRecentlyExpired: false,
            artifacts,
            language: null,
            isHidden: table.Table.DiscoveryHidden);

        var cols = BuildExternalColumns(table, baseObj.Columns, includeHiddenColumns, includeAccessState: false);
        var write = baseObj.Write ?? DiscoveryWriteState.None;
        return new ExternalCatalogObjectDto(
            ObjectId: table.Identifier.ToString(),
            Kind: KindName(baseObj.Kind),
            Domain: baseObj.Domain,
            Schema: baseObj.Schema,
            Name: baseObj.Name,
            DisplayName: baseObj.DisplayName,
            Description: baseObj.Description,
            Tags: MergeTags(table, baseObj.Tags),
            Sensitivity: baseObj.Sensitivity,
            Owners: baseObj.Owners.Select(static o => new ExternalCatalogOwnerDto(o.DisplayName, o.Email)).ToArray(),
            LinkedDocuments: BuildLinkedDocuments(table),
            Write: write,
            StatisticsAvailable: baseObj.Statistics is not null,
            Statistics: baseObj.Statistics,
            SamplesAvailable: baseObj.SamplesAvailable,
            Hidden: baseObj.IsHidden,
            CallerState: null,
            Columns: cols,
            ChangeSeq: changeSeq);
    }

    private static ExternalCatalogObjectDto MapUserObject(
        DiscoveryObject obj,
        TableMetadata table,
        long? changeSeq,
        bool includeHiddenColumns)
    {
        var cols = BuildExternalColumns(table, obj.Columns, includeHiddenColumns, includeAccessState: true);
        var callerState = new ExternalCatalogCallerStateDto(
            Access: obj.Access.ToString().ToLowerInvariant(),
            RowScope: obj.RowScope?.ToString(),
            Write: obj.Write);

        return new ExternalCatalogObjectDto(
            ObjectId: table.Identifier.ToString(),
            Kind: KindName(obj.Kind),
            Domain: obj.Domain,
            Schema: obj.Schema,
            Name: obj.Name,
            DisplayName: obj.DisplayName,
            Description: obj.Description,
            Tags: MergeTags(table, obj.Tags),
            Sensitivity: obj.Sensitivity,
            Owners: obj.Owners.Select(static o => new ExternalCatalogOwnerDto(o.DisplayName, o.Email)).ToArray(),
            LinkedDocuments: BuildLinkedDocuments(table),
            Write: obj.Write ?? DiscoveryWriteState.None,
            StatisticsAvailable: obj.Statistics is not null,
            Statistics: obj.Statistics,
            SamplesAvailable: obj.SamplesAvailable,
            Hidden: obj.IsHidden,
            CallerState: callerState,
            Columns: cols,
            ChangeSeq: changeSeq);
    }

    private static IReadOnlyList<ExternalCatalogColumnDto> BuildExternalColumns(
        TableMetadata table,
        IReadOnlyList<DiscoveryColumn> visibleCols,
        bool includeHiddenColumns,
        bool includeAccessState)
    {
        var byName = table.Columns.ToDictionary(static c => c.ColumnName, StringComparer.OrdinalIgnoreCase);
        var list = new List<ExternalCatalogColumnDto>(visibleCols.Count + 4);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var vc in visibleCols)
        {
            seen.Add(vc.Name);
            byName.TryGetValue(vc.Name, out var metaCol);
            bool colHidden = metaCol?.DiscoveryHidden ?? false;
            if (colHidden && !includeHiddenColumns)
            {
                continue;
            }

            var columnSecurity = metaCol is not null && ClassificationMetadata.IsColumnSensitive(table, metaCol);
            var exposeClassifications = !colHidden && !table.Table.DiscoveryHidden;
            list.Add(new ExternalCatalogColumnDto(
                Name: vc.Name,
                DisplayName: vc.DisplayName,
                DataType: vc.DataType,
                Description: vc.Description,
                Tags: Array.Empty<string>(),
                Sensitivity: vc.Pii ? "SENSITIVE" : "NORMAL",
                Pii: vc.Pii,
                DefaultMaskingState: metaCol is not null ? DefaultMaskingState(table, metaCol) : "clear",
                ColumnSecurity: columnSecurity,
                Hidden: colHidden,
                WriteProtected: vc.WriteProtected,
                AccessState: includeAccessState ? vc.State.ToString().ToLowerInvariant() : null,
                ClearInSomeRows: vc.ClearInSomeRows,
                Statistics: vc.Statistics,
                Classifications: exposeClassifications ? vc.Classifications : [],
                ClearRequestable: exposeClassifications ? vc.ClearRequestable : []));
        }

        if (includeHiddenColumns)
        {
            foreach (var col in table.Columns)
            {
                if (!col.DiscoveryHidden || !seen.Add(col.ColumnName))
                {
                    continue;
                }

                var classified = ClassificationMetadata.IsColumnSensitive(table, col);
                bool pii = classified || CatalogPiiDetector.IsPii(col.ColumnName, classified);
                list.Add(new ExternalCatalogColumnDto(
                    Name: col.ColumnName,
                    DisplayName: col.DisplayName,
                    DataType: col.DataType,
                    Description: col.Description,
                    Tags: Array.Empty<string>(),
                    Sensitivity: pii ? "SENSITIVE" : "NORMAL",
                    Pii: pii,
                    DefaultMaskingState: DefaultMaskingState(table, col),
                    ColumnSecurity: classified,
                    Hidden: true,
                    WriteProtected: col.WriteProtected,
                    AccessState: includeAccessState ? DiscoveryColumnState.NoAccess.ToString().ToLowerInvariant() : null,
                    ClearInSomeRows: false,
                    Statistics: null,
                    Classifications: [],
                    ClearRequestable: []));
            }
        }

        return list;
    }

    private async Task<IReadOnlyList<TableMetadata>> FilterInScopeTablesAsync(
        IReadOnlyList<TableMetadata> allTables,
        TenantId tenantId,
        CancellationToken ct)
    {
        var virtualFiltered = DiscoveryTenantScope.DropForeignVirtualTables(allTables, tenantId);
        var consentTables = await _consentRepository.GetTablesWithConsentsForTenantAsync(tenantId, ct).ConfigureAwait(false);
        var consentSet = consentTables as IReadOnlySet<TableIdentifier> ?? consentTables.ToHashSet();
        var catalogOwner = TenantResolver.EffectiveDefaultOf(_gatewayOptions.Tenancy);
        var sharedTables = DiscoveryTenantScope.ParseSharedTables(_gatewayOptions.Catalog.SharedTables);

        return virtualFiltered
            .Where(t => t.Table.IsActive && DiscoveryTenantScope.IsInScope(t, tenantId, catalogOwner, sharedTables, consentSet))
            .ToList();
    }

    private async Task<bool> IsTableInTenantScopeAsync(TableMetadata table, TenantId tenantId, CancellationToken ct)
    {
        if (!table.Table.IsActive)
        {
            return false;
        }

        var virtualFiltered = DiscoveryTenantScope.DropForeignVirtualTables([table], tenantId);
        if (virtualFiltered.Count == 0)
        {
            return false;
        }

        var consentTables = await _consentRepository.GetTablesWithConsentsForTenantAsync(tenantId, ct).ConfigureAwait(false);
        var consentSet = consentTables as IReadOnlySet<TableIdentifier> ?? consentTables.ToHashSet();
        var catalogOwner = TenantResolver.EffectiveDefaultOf(_gatewayOptions.Tenancy);
        var sharedTables = DiscoveryTenantScope.ParseSharedTables(_gatewayOptions.Catalog.SharedTables);
        return DiscoveryTenantScope.IsInScope(table, tenantId, catalogOwner, sharedTables, consentSet);
    }

    private static TableMetadata? FindMatchingTable(IReadOnlyList<TableMetadata> allTables, string rawId)
    {
        var trimmed = rawId.Trim();
        var parts = trimmed.Split('.', 3, StringSplitOptions.TrimEntries);
        if (parts.Length == 3 && TableIdentifier.TryCreate(parts[0], parts[1], parts[2], out var parsedId))
        {
            return allTables.FirstOrDefault(t => t.Identifier.Equals(parsedId));
        }

        if (Guid.TryParse(trimmed, out var guidId))
        {
            return allTables.FirstOrDefault(t => t.Table.Id == guidId);
        }

        return null;
    }

    private static bool CanViewHiddenTable(TableMetadata table, CallerVisibility visibility) =>
        !table.Table.DiscoveryHidden
        || CanViewHiddenColumns(table, visibility);

    private static bool CanViewHiddenColumns(TableMetadata table, CallerVisibility visibility) =>
        visibility.AllHiddenAllowed
        || visibility.OwnedHiddenTables.Contains(table.Identifier);

    private static IEnumerable<ExternalCatalogObjectDto> ApplyFilters(
        IEnumerable<ExternalCatalogObjectDto> items,
        ExternalCatalogListQuery query,
        string? kindFilter)
    {
        if (!string.IsNullOrEmpty(kindFilter))
        {
            items = items.Where(o => string.Equals(o.Kind, kindFilter, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(query.Domain))
        {
            var d = query.Domain.Trim();
            items = items.Where(o => string.Equals(o.Domain, d, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(query.Schema))
        {
            var s = query.Schema.Trim();
            items = items.Where(o => string.Equals(o.Schema, s, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(query.Owner))
        {
            var owner = query.Owner.Trim();
            items = items.Where(o => o.Owners.Any(ow =>
                string.Equals(ow.DisplayName, owner, StringComparison.OrdinalIgnoreCase)
                || string.Equals(ow.Email, owner, StringComparison.OrdinalIgnoreCase)));
        }

        if (!string.IsNullOrWhiteSpace(query.Tag))
        {
            var tag = query.Tag.Trim();
            items = items.Where(o => o.Tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)));
        }

        if (!string.IsNullOrWhiteSpace(query.Sensitivity))
        {
            var sens = query.Sensitivity.Trim();
            items = items.Where(o => string.Equals(o.Sensitivity, sens, StringComparison.OrdinalIgnoreCase));
        }

        if (query.Pii.HasValue)
        {
            items = query.Pii.Value
                ? items.Where(static o => o.Columns.Any(static c => c.Pii))
                : items.Where(static o => o.Columns.All(static c => !c.Pii));
        }

        if (query.ChangedSince.HasValue)
        {
            var minSeq = query.ChangedSince.Value;
            items = items.Where(o => (o.ChangeSeq ?? 0L) > minSeq);
        }

        return items;
    }

    private static bool TryParseKindFilter(string? rawKind, out string? normalized)
    {
        if (string.IsNullOrWhiteSpace(rawKind))
        {
            normalized = null;
            return true;
        }

        var trimmed = rawKind.Trim().ToLowerInvariant();
        if (trimmed is "table" or "view" or "procedure")
        {
            normalized = trimmed;
            return true;
        }

        normalized = null;
        return false;
    }

    private static string DefaultMaskingState(TableMetadata table, TableColumn column)
    {
        if (table.ColumnMaskingRules.TryGetValue(column.ColumnName, out var rule))
        {
            return rule.RuleType?.ToUpperInvariant() switch
            {
                "HMAC" or "TOKENIZE" => "pseudonymised",
                _ => "masked"
            };
        }

        return ClassificationMetadata.IsColumnSensitive(table, column) ? "masked" : "clear";
    }

    private static IReadOnlyList<string> MergeTags(TableMetadata table, IReadOnlyList<string> primary) =>
        primary.Count > 0 ? primary : table.Table.Tags;

    private static IReadOnlyList<string> BuildLinkedDocuments(TableMetadata table) =>
        string.IsNullOrWhiteSpace(table.Table.DocumentationSource)
            ? Array.Empty<string>()
            : [table.Table.DocumentationSource!];

    private static string KindName(DiscoveryObjectKind kind) => kind switch
    {
        DiscoveryObjectKind.View => "view",
        DiscoveryObjectKind.Procedure => "procedure",
        _ => "table"
    };

    private static string NormalizeChangeKind(string kind) =>
        string.Equals(kind, "REMOVED", StringComparison.OrdinalIgnoreCase) ? "removed" : "upsert";

    private static bool IsAppOnlyPrincipal(ClaimsPrincipal user)
    {
        if (string.Equals(user.FindFirst("idtyp")?.Value, "app", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var gty = user.FindFirst("gty")?.Value;
        if (string.Equals(gty, "client-credentials", StringComparison.OrdinalIgnoreCase)
            || string.Equals(gty, "client_credentials", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(GetClientId(user))
            && TokenAccessScope.GetScopeSet(user.Claims).Count == 0
            && user.GetUserSid() is null;
    }

    private static string? GetClientId(ClaimsPrincipal user) =>
        user.FindFirst("azp")?.Value
        ?? user.FindFirst("appid")?.Value
        ?? user.FindFirst("client_id")?.Value;

    private static bool HasAppRole(ClaimsPrincipal user, string role)
    {
        foreach (var claim in user.Claims)
        {
            if (claim.Type is "roles" or "role" or ClaimTypes.Role)
            {
                foreach (var token in claim.Value.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (string.Equals(token, role, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }

        return user.IsInRole(role);
    }

    private sealed record CallerVisibility(
        bool IsServicePrincipal,
        bool AllHiddenAllowed,
        IReadOnlySet<TableIdentifier> OwnedHiddenTables);
}
