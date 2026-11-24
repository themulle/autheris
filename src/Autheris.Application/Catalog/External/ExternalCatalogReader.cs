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
using Autheris.Application.Security;
using Autheris.Application.Tenancy;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

/// <summary>
/// Read-only external catalog service supporting user tokens (via <see cref="IDiscoveryService"/>) and
/// registered app-only service principals with strict hidden-object visibility rules and caller-specific hashing.
/// </summary>
public sealed partial class ExternalCatalogReader(
    ITableMetadataRepository metadataRepository,
    IDiscoveryService discoveryService,
    IConsentRepository consentRepository,
    IDataOwnershipRepository ownershipRepository,
    IConsentWorkflowConfigRepository workflowConfigRepository,
    ICatalogChangeStore changeStore,
    IOptions<GatewayOptions> gatewayOptions,
    IDiscoveryArtifactStore? artifactStore = null,
    TimeProvider? timeProvider = null,
    IOptions<GovernanceTextOptions>? textOptions = null,
    IKeyVaultSecretProvider? secretProvider = null,
    IConfiguration? configuration = null,
    IProtectionSnapshotProvider? protectionProvider = null,
    ICatalogSnapshotProvider? snapshotProvider = null,
    ICatalogVisibilityEvaluator? visibilityEvaluator = null)
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
    private readonly byte[]? _cursorKey = ExternalCatalogCursor.ResolvePersistentKey(
        (gatewayOptions ?? throw new ArgumentNullException(nameof(gatewayOptions))).Value,
        secretProvider,
        configuration);
    private readonly int _descriptionMaxLength = (textOptions?.Value ?? new GovernanceTextOptions()).ConsentDescriptionMaxLength;
    private readonly IProtectionSnapshotProvider? _protectionProvider = protectionProvider;
    private readonly ICatalogVisibilityEvaluator _visibilityEvaluator =
        visibilityEvaluator ?? new CatalogVisibilityEvaluator(
            gatewayOptions ?? throw new ArgumentNullException(nameof(gatewayOptions)),
            timeProvider: timeProvider);

    private const string InvalidCursorMessage = "The cursor is invalid.";

    /// <summary>Upper bound of the <c>q</c> filter (the same bound the catalog search engine applies to its query text).</summary>
    public const int MaxSearchTextLength = Autheris.Application.Catalog.Search.CatalogSearchEngine.DefaultMaxQueryLength;

    public async Task<ExternalCatalogResult<ExternalCatalogPageDto>> ListObjectsAsync(
        ClaimsPrincipal user,
        TenantId tenantId,
        ExternalCatalogListQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(query);

        if (!TryParseKindFilter(query.Kind, out var kindFilter, out var kindEnum))
        {
            return ExternalCatalogResult.Fail<ExternalCatalogPageDto>(
                400,
                "INVALID_QUERY",
                "Invalid kind filter. Allowed values: table, view, procedure.");
        }

        if (!CatalogAccessStatus.TryParseFilter(query.AccessStatus, out var accessFilter))
        {
            return ExternalCatalogResult.Fail<ExternalCatalogPageDto>(400, "INVALID_QUERY", InvalidAccessStatusMessage);
        }

        var searchText = string.IsNullOrWhiteSpace(query.Q) ? null : query.Q.Trim();
        if (searchText is not null && searchText.Length > MaxSearchTextLength)
        {
            return ExternalCatalogResult.Fail<ExternalCatalogPageDto>(
                400,
                "INVALID_QUERY",
                $"The q filter exceeds the maximum length of {MaxSearchTextLength} characters.");
        }

        if (!TryParseSearchMode(query.Mode, out var normalizedMode))
        {
            return ExternalCatalogResult.Fail<ExternalCatalogPageDto>(
                400,
                "INVALID_QUERY",
                "Invalid search mode. Allowed values: hybrid, keyword, semantic.");
        }

        if (!TryParseSortOption(query.Sort, out var orderByRelevance))
        {
            return ExternalCatalogResult.Fail<ExternalCatalogPageDto>(
                400,
                "INVALID_QUERY",
                "Invalid sort option. Allowed values: id, relevance.");
        }

        var cursorBinding = ExternalCatalogCursor.ComputeBinding(query, kindFilter, accessFilter, searchText, tenantId, user);
        string? afterKey = null;
        int? cursorCount = null;
        if (query.Cursor is not null)
        {
            if (!ExternalCatalogCursor.TryDecode(query.Cursor, cursorBinding, _cursorKey, out var decodedKey, out var decodedCount))
            {
                return ExternalCatalogResult.Fail<ExternalCatalogPageDto>(400, "INVALID_CURSOR", InvalidCursorMessage);
            }

            afterKey = decodedKey;
            cursorCount = decodedCount;
        }

        bool isServicePrincipal = IsAppOnlyPrincipal(user);
        if (isServicePrincipal)
        {
            if (accessFilter is not null)
            {
                return ExternalCatalogResult.Fail<ExternalCatalogPageDto>(400, "INVALID_QUERY", "The accessStatus filter applies to user callers only.");
            }

            if (searchText is not null)
            {
                return ExternalCatalogResult.Fail<ExternalCatalogPageDto>(400, "INVALID_QUERY", "The q filter applies to user callers only.");
            }
        }

        int pageSize = Math.Clamp(query.PageSize <= 0 ? DefaultPageSize : query.PageSize, 1, MaxPageSize);

        return isServicePrincipal
            ? await ListAppObjectsKeysetAsync(user, tenantId, query, kindEnum, afterKey, cursorCount, pageSize, cursorBinding, ct).ConfigureAwait(false)
            : await ListUserObjectsKeysetAsync(user, tenantId, query, kindEnum, accessFilter, searchText, normalizedMode, orderByRelevance, afterKey, cursorCount, pageSize, cursorBinding, ct).ConfigureAwait(false);
    }

    internal static bool TryParseSearchMode(string? mode, out string? normalizedMode)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            normalizedMode = null;
            return true;
        }

        switch (mode.Trim().ToLowerInvariant())
        {
            case "hybrid":
                normalizedMode = "hybrid";
                return true;
            case "keyword":
            case "keywordbm25":
                normalizedMode = "keyword";
                return true;
            case "semantic":
            case "semanticvector":
                normalizedMode = "semantic";
                return true;
            default:
                normalizedMode = null;
                return false;
        }
    }

    private static bool TryParseSortOption(string? sort, out bool orderByRelevance)
    {
        if (string.IsNullOrWhiteSpace(sort) || string.Equals(sort.Trim(), "id", StringComparison.OrdinalIgnoreCase))
        {
            orderByRelevance = false;
            return true;
        }

        if (string.Equals(sort.Trim(), "relevance", StringComparison.OrdinalIgnoreCase))
        {
            orderByRelevance = true;
            return true;
        }

        orderByRelevance = false;
        return false;
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

        var (matchedTable, versions, visibility) = await LoadSingleContextAsync(user, tenantId, rawId, ct).ConfigureAwait(false);
        if (matchedTable is null)
        {
            return ExternalCatalogResult.Fail<ExternalCatalogObjectDto>(404, "OBJECT_NOT_FOUND", "Catalog object was not found.");
        }

        // A user caller is scoped by the discovery read below (tenant scope plus the container visibility rule), an app caller here.
        if (visibility.IsServicePrincipal && !await IsTableInTenantScopeAsync(matchedTable, tenantId, ct).ConfigureAwait(false))
        {
            return ExternalCatalogResult.Fail<ExternalCatalogObjectDto>(404, "OBJECT_NOT_FOUND", "Catalog object was not found.");
        }

        if (matchedTable.Table.DiscoveryHidden && !CanViewHiddenTable(matchedTable, visibility))
        {
            return ExternalCatalogResult.Fail<ExternalCatalogObjectDto>(404, "OBJECT_NOT_FOUND", "Catalog object was not found.");
        }

        if (visibility.IsServicePrincipal)
        {
            var dto = await MapSingleAppObjectAsync(tenantId, matchedTable, versions, CanViewHiddenColumns(matchedTable, visibility), ct)
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
        var protection = _protectionProvider is null ? null : await _protectionProvider.GetAsync(tenantId, matchedTable.Identifier, ct).ConfigureAwait(false);
        var mapped = MapUserObject(discovered, matchedTable, ver?.Seq, CanViewHiddenColumns(matchedTable, visibility), protection);
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

        var allTables = await LoadActiveTablesAsync(ct).ConfigureAwait(false);
        var byObjectId = allTables.ToDictionary(static t => t.Identifier.ToString(), StringComparer.OrdinalIgnoreCase);
        var referencedTables = new List<TableMetadata>(feed.Changes.Count);
        foreach (var ch in feed.Changes)
        {
            if (byObjectId.TryGetValue(ch.ObjectId, out var referenced))
            {
                referencedTables.Add(referenced);
            }
        }

        var visibility = await ResolveCallerVisibilityAsync(user, tenantId, referencedTables, ct).ConfigureAwait(false);

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
        DiscoveryArtifacts? artifacts,
        EffectiveProtection? protection = null)
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
            protectionClass = (protection?.TableClass ?? ProtectionClass.Confidential).ToCanonicalName(),
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

    private Task<IReadOnlyList<TableMetadata>> LoadActiveTablesAsync(CancellationToken ct) =>
        _visibilityEvaluator.LoadActiveTablesAsync(_metadataRepository, snapshotProvider, ct: ct);

    /// <summary>
    /// Issue #1050: the context of one object. With the catalog snapshot the id is resolved in O(1) (by identifier or table id, active
    /// tables only, as the full read) and the caller's hidden-object visibility is resolved for that table only; an unknown id returns
    /// before any further read. Without a snapshot it falls back to the explicit metadata fallback.
    /// </summary>
    private async Task<(TableMetadata? Table, IReadOnlyDictionary<string, CatalogObjectVersionRow> Versions, CallerVisibility Visibility)>
        LoadSingleContextAsync(ClaimsPrincipal user, TenantId tenantId, string rawId, CancellationToken ct)
    {
        if (snapshotProvider is null)
        {
            var allTables = await LoadActiveTablesAsync(ct).ConfigureAwait(false);
            var matched = FindMatchingTable(allTables, rawId);
            if (matched is null)
            {
                return (null, new Dictionary<string, CatalogObjectVersionRow>(), new CallerVisibility(false, false, new HashSet<TableIdentifier>()));
            }

            var allVersions = await _changeStore.GetObjectVersionsAsync(tenantId, ct).ConfigureAwait(false);
            var singleVisibility = await ResolveCallerVisibilityAsync(user, tenantId, [matched], ct).ConfigureAwait(false);
            return (matched, allVersions, singleVisibility);
        }

        var snapshot = await snapshotProvider.GetSnapshotAsync(ct).ConfigureAwait(false);
        var table = FindInSnapshot(snapshot, rawId);
        if (table is null)
        {
            return (null, new Dictionary<string, CatalogObjectVersionRow>(), new CallerVisibility(false, false, new HashSet<TableIdentifier>()));
        }

        var versions = await _changeStore.GetObjectVersionsAsync(tenantId, ct).ConfigureAwait(false);
        var visibility = await ResolveCallerVisibilityAsync(user, tenantId, [table], ct).ConfigureAwait(false);
        return (table, versions, visibility);
    }

    private static TableMetadata? FindInSnapshot(CatalogSnapshot snapshot, string rawId)
    {
        var trimmed = rawId.Trim();
        var parts = trimmed.Split('.', 3, StringSplitOptions.TrimEntries);
        int ordinal;
        var found = parts.Length == 3 && TableIdentifier.TryCreate(parts[0], parts[1], parts[2], out var parsedId)
            ? snapshot.OrdinalByIdentifier.TryGetValue(parsedId, out ordinal)
            : Guid.TryParse(trimmed, out var guidId) & snapshot.OrdinalById.TryGetValue(guidId, out ordinal);
        return found && snapshot.Tables[ordinal] is { Table.IsActive: true } table ? table : null;
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

        // User token (interactive or delegated): delegate hidden-object rules to the shared catalog visibility evaluator.
        var userVisibility = await _visibilityEvaluator.EvaluateUserHiddenVisibilityAsync(
            user, tenantId, allTables, includeHiddenColumns: false, _ownershipRepository, ct).ConfigureAwait(false);
        return new CallerVisibility(false, userVisibility.AllHiddenAllowed, userVisibility.OwnedHiddenTables);
    }

    private static bool HasAppEffectiveTag(TableMetadata table, string tag)
    {
        var sanitizedTags = DiscoveryTextSanitizer.SanitizeTags(table.CatalogTags);
        var effectiveTags = sanitizedTags.Count > 0 ? sanitizedTags : table.Table.Tags;
        return effectiveTags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));
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
            var tableById = tables.ToDictionary(static t => t.Identifier.ToString(), static t => t.Identifier, StringComparer.OrdinalIgnoreCase);

            // Issue #1050: the consents of the listed granted tables only (the per-table row filter, patterns included), not every consent
            // of the caller with all its rules.
            var listed = granted.Where(i => tableById.ContainsKey(i.ObjectId)).Select(i => tableById[i.ObjectId]).Distinct().ToList();
            var byTable = await _consentRepository.GetActiveConsentsForSubjectsBatchAsync(subjects, listed, now, tenantId, ct).ConfigureAwait(false);
            var applicableByObject = new Dictionary<string, List<Consent>>(StringComparer.OrdinalIgnoreCase);
            var distinct = new Dictionary<Guid, Consent>();
            foreach (var item in granted)
            {
                if (!tableById.TryGetValue(item.ObjectId, out var id))
                {
                    continue;
                }

                var forTable = byTable.TryGetValue(id, out var rows) ? rows.Where(c => c.TenantId == tenantId).ToList() : [];
                var applicable = EffectiveAccessExplanationService.MatchingAllowConsents(forTable, id, now, userSid.Value, groupSids, roles, allUserSids);
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
        TenantId tenantId,
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
        var protection = _protectionProvider is null
            ? null
            : await _protectionProvider.GetAsync(tenantId, table.Identifier, ct).ConfigureAwait(false);
        versions.TryGetValue(table.Identifier.ToString(), out var ver);
        return MapAppObject(table, owners, artifacts, ver?.Seq, canSeeHidden, protection);
    }

    private static ExternalCatalogObjectDto MapAppObject(
        TableMetadata table,
        IReadOnlyList<DataOwner> activeOwners,
        DiscoveryArtifacts? artifacts,
        long? changeSeq,
        bool includeHiddenColumns,
        EffectiveProtection? protection = null)
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
            isHidden: table.Table.DiscoveryHidden,
            effectiveProtection: protection);

        var cols = BuildExternalColumns(table, baseObj.Columns, includeHiddenColumns, includeAccessState: false, personView: false, protection);
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
            ProtectionClass: ResolveTableProtectionClass(baseObj.Sensitivity, protection),
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
        bool includeHiddenColumns,
        EffectiveProtection? protection = null)
    {
        var cols = BuildExternalColumns(table, DiscoveryColumnVisibility.VisibleColumns(obj), includeHiddenColumns, includeAccessState: true, personView: true, protection);
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
            ProtectionClass: ResolveTableProtectionClass(obj.Sensitivity, protection),
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

    private static string ResolveTableProtectionClass(string? fallbackSensitivity, EffectiveProtection? protection)
    {
        if (protection is not null)
        {
            return protection.TableClass.ToCanonicalName();
        }

        return ProtectionClassExtensions.TryParse(fallbackSensitivity, out var parsed)
            ? parsed.ToCanonicalName()
            : ProtectionClass.Confidential.ToCanonicalName();
    }

    private static string ResolveColumnProtectionClass(string columnName, bool isPii, EffectiveProtection? protection)
    {
        if (protection is not null && protection.Columns.TryGetValue(columnName, out var cp))
        {
            return cp.EffectiveClass.ToCanonicalName();
        }

        return isPii
            ? ProtectionClass.Confidential.ToCanonicalName()
            : (protection?.TableClass ?? ProtectionClass.Confidential).ToCanonicalName();
    }

    private static string PersonAccessState(DiscoveryColumnState state) => state switch
    {
        DiscoveryColumnState.Clear => "clear",
        DiscoveryColumnState.Masked => "masked",
        _ => throw new InvalidOperationException("A column that is not visible must never be projected.")
    };

    private static IReadOnlyList<ExternalCatalogColumnDto> BuildExternalColumns(
        TableMetadata table,
        IReadOnlyList<DiscoveryColumn> visibleCols,
        bool includeHiddenColumns,
        bool includeAccessState,
        bool personView,
        EffectiveProtection? protection = null)
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
                ProtectionClass: ResolveColumnProtectionClass(vc.Name, vc.Pii, protection),
                Pii: vc.Pii,
                DefaultMaskingState: metaCol is not null ? DefaultMaskingState(table, metaCol) : "clear",
                ColumnSecurity: columnSecurity,
                Hidden: colHidden,
                WriteProtected: vc.WriteProtected,
                AccessState: includeAccessState ? PersonAccessState(vc.State) : null,
                ClearInSomeRows: vc.ClearInSomeRows,
                Statistics: vc.Statistics,
                Classifications: exposeClassifications ? vc.Classifications : [],
                ClearRequestable: exposeClassifications ? vc.ClearRequestable : [],
                Role: vc.Role?.ToWireName()));
        }

        // A person never receives the appended hidden columns: they carry no caller state, so they stay hidden.
        if (includeHiddenColumns && !personView)
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
                    ProtectionClass: ResolveColumnProtectionClass(col.ColumnName, pii, protection),
                    Pii: pii,
                    DefaultMaskingState: DefaultMaskingState(table, col),
                    ColumnSecurity: classified,
                    Hidden: true,
                    WriteProtected: col.WriteProtected,
                    AccessState: null,
                    ClearInSomeRows: false,
                    Statistics: null,
                    Classifications: [],
                    ClearRequestable: []));
            }
        }

        return list;
    }

    private Task<IReadOnlyList<TableMetadata>> FilterInScopeTablesAsync(
        IReadOnlyList<TableMetadata> allTables,
        TenantId tenantId,
        CancellationToken ct) =>
        _visibilityEvaluator.FilterInScopeAsync(allTables, tenantId, _consentRepository, ct).AsTask();

    private Task<bool> IsTableInTenantScopeAsync(TableMetadata table, TenantId tenantId, CancellationToken ct) =>
        _visibilityEvaluator.IsInScopeAsync(table, tenantId, _consentRepository, ct).AsTask();

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

    private static bool TryParseKindFilter(string? rawKind, out string? normalized, out DiscoveryObjectKind? kindEnum)
    {
        if (string.IsNullOrWhiteSpace(rawKind))
        {
            normalized = null;
            kindEnum = null;
            return true;
        }

        switch (rawKind.Trim().ToLowerInvariant())
        {
            case "table":
                normalized = "table";
                kindEnum = DiscoveryObjectKind.Table;
                return true;
            case "view":
                normalized = "view";
                kindEnum = DiscoveryObjectKind.View;
                return true;
            case "procedure":
                normalized = "procedure";
                kindEnum = DiscoveryObjectKind.Procedure;
                return true;
            default:
                normalized = null;
                kindEnum = null;
                return false;
        }
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
        DiscoveryObjectKind.Table => "table",
        DiscoveryObjectKind.View => "view",
        DiscoveryObjectKind.Procedure => "procedure",
        DiscoveryObjectKind.MetricView => "metric_view",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown DiscoveryObjectKind.")
    };

    private static string NormalizeChangeKind(string kind) =>
        string.Equals(kind, "REMOVED", StringComparison.OrdinalIgnoreCase) ? "removed" : "upsert";

    internal static bool IsAppOnlyPrincipal(ClaimsPrincipal user) => AppOnlyPrincipal.Is(user);

    private static string? GetClientId(ClaimsPrincipal user) => AppOnlyPrincipal.GetClientId(user);

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
