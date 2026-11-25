using Autheris.Domain.Audit;
using Autheris.Domain.Security;

namespace Autheris.Application.Common.Interfaces;

public interface ITableMetadataRepository : Autheris.Application.Catalog.ICatalogVersionFlusher
{
    Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default);
    Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default);

    /// <summary>Returns active SQL tables whose stored <c>SourceType</c> is not a supported SQL dialect, avoiding full metadata hydration when healthy.</summary>
    async Task<IReadOnlyList<(TableIdentifier Identifier, string SourceName, string CurrentSourceType)>> GetUnparseableSqlSourceTypesAsync(CancellationToken ct = default)
    {
        var page = await Autheris.Application.Catalog.CatalogTablePageReader.ReadAllAsync(
            this,
            new Autheris.Application.Catalog.CatalogTableQuery(IncludeInactive: false),
            static t => t.Table.DataSourceType == DataSourceType.Sql &&
                !Autheris.Domain.Common.DatabaseDialectExtensions.TryParseDialect(t.Table.SourceType, out _),
            ct: ct).ConfigureAwait(false);
        return page.Select(static t => (t.Identifier, t.Table.SourceName, t.Table.SourceType)).ToList();
    }

    /// <summary>
    /// Issue #1043: the table, its columns, masking rules and global classifications (the epoch-validated structural metadata) without
    /// the dataset catalog overlay (tags, discovery visibility, group size, non-personal flag). For readers that need none of the overlay,
    /// such as the table sensitivity lookup. Default: <see cref="GetTableMetadataAsync"/>.
    /// </summary>
    Task<TableMetadata?> GetStructuralTableMetadataAsync(TableIdentifier table, CancellationToken ct = default) =>
        GetTableMetadataAsync(table, ct);

    /// <summary>
    /// Resolves several tables for one request (one entry per requested identifier in request order, null = unknown or inactive).
    /// Same per-table semantics as <see cref="GetTableMetadataAsync"/> (an unavailable table still throws); implementations
    /// validate all cached entries with one batched epoch read. Default: sequential single lookups.
    /// </summary>
    async Task<IReadOnlyList<TableMetadata?>> GetTableMetadataBatchAsync(IReadOnlyList<TableIdentifier> tables, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tables);
        var result = new TableMetadata?[tables.Count];
        for (var i = 0; i < tables.Count; i++)
        {
            result[i] = await GetTableMetadataAsync(tables[i], ct).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// <see cref="GetTableMetadataBatchAsync"/> without the dataset catalog overlay (see <see cref="GetStructuralTableMetadataAsync"/>), so
    /// structural batch readers such as container ownership do not read the overlay per table. Default: <see cref="GetTableMetadataBatchAsync"/>.
    /// </summary>
    Task<IReadOnlyList<TableMetadata?>> GetStructuralTableMetadataBatchAsync(IReadOnlyList<TableIdentifier> tables, CancellationToken ct = default) =>
        GetTableMetadataBatchAsync(tables, ct);

    /// <summary>
    /// Returns identifiers of tables whose persisted <c>POLICY_EPOCHS.change_version</c> is strictly greater than <paramref name="sinceVersion"/>.
    /// </summary>
    Task<IReadOnlyList<TableIdentifier>> GetTablesChangedSinceAsync(long sinceVersion, CancellationToken ct = default)
        => throw new NotSupportedException($"{GetType().Name} does not implement delta table queries.");

    /// <summary>
    /// Unfiltered raw internal batch read for active table metadata matching <paramref name="identifiers"/>.
    /// Intended strictly for internal Application and Infrastructure services; callers MUST NOT map results directly to
    /// endpoint DTOs and MUST enforce tenant, visibility, masking, and consent filtering before returning data.
    /// Unknown and hidden identifiers are omitted identically without raising existence errors. Internal callers may pass
    /// arbitrarily sized batches (chunked by DB parameter budget); external request-derived lists must call
    /// <see cref="GetTablesForRequestAsync"/> so the 500-item cap applies.
    /// </summary>
    async Task<IReadOnlyList<TableMetadata>> GetTablesAsync(IReadOnlyCollection<TableIdentifier> identifiers, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identifiers);
        if (identifiers.Count == 0)
        {
            return Array.Empty<TableMetadata>();
        }

        var wanted = identifiers as IReadOnlySet<TableIdentifier> ?? new HashSet<TableIdentifier>(identifiers);
        var all = await GetAllTablesAsync(ct).ConfigureAwait(false);
        return all.Where(t => wanted.Contains(t.Identifier)).ToList();
    }

    /// <summary>
    /// Request-capped wrapper over <see cref="GetTablesAsync"/> for identifier lists derived from external request input
    /// (enforces <see cref="Autheris.Application.Catalog.CatalogTableQuery.MaxRequestIdentifiers"/> = 500).
    /// Unfiltered raw internal read; callers MUST apply tenant, visibility, masking, and consent filtering before returning DTOs.
    /// </summary>
    Task<IReadOnlyList<TableMetadata>> GetTablesForRequestAsync(IReadOnlyCollection<TableIdentifier> identifiers, CancellationToken ct = default)
    {
        Autheris.Application.Catalog.CatalogTableQuery.ValidateRequestIdentifiers(identifiers);
        return GetTablesAsync(identifiers, ct);
    }

    /// <summary>
    /// Unfiltered raw internal keyset-paged catalog table query for Application and Infrastructure services only.
    /// Never map returned <see cref="TableMetadata"/> directly to endpoint DTOs; callers MUST apply tenant, visibility,
    /// masking, and consent filtering before determining page boundaries, counts, or visible continuation cursors.
    /// The repository has no tenant concept and clamps <see cref="Autheris.Application.Catalog.CatalogTableQuery.Limit"/> to 1..500.
    /// </summary>
    async Task<Autheris.Application.Catalog.CatalogTablePage> QueryTablesAsync(
        Autheris.Application.Catalog.CatalogTableQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.EnsureValid(out var domain, out var schema, out var namePrefix, out var after);
        var limit = query.EffectiveLimit;

        var all = await GetAllTablesAsync(ct).ConfigureAwait(false);
        var matched = all
            .Where(t => query.IncludeInactive || t.Table.IsActive)
            .Where(t => domain is null || string.Equals(t.Identifier.Domain, domain, StringComparison.OrdinalIgnoreCase))
            .Where(t => schema is null || string.Equals(t.Identifier.Schema, schema, StringComparison.OrdinalIgnoreCase))
            .Where(t => namePrefix is null || t.Identifier.TableName.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase))
            .Where(t => after is null || Autheris.Application.Catalog.CatalogTableCursor.Compare(t.Identifier, after.Value) > 0)
            .OrderBy(t => t.Identifier.Domain, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Identifier.Schema, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Identifier.TableName, StringComparer.OrdinalIgnoreCase)
            .Take(limit + 1)
            .ToList();

        var hasMore = matched.Count > limit;
        var items = hasMore ? matched.Take(limit).ToList() : matched;
        var nextAfter = hasMore ? Autheris.Application.Catalog.CatalogTableCursor.Encode(items[^1].Identifier) : null;
        return new Autheris.Application.Catalog.CatalogTablePage(items, nextAfter);
    }

    Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default);

    /// <summary>
    /// Like <see cref="UpsertTableMetadataAsync"/>, and also reports whether the write created, changed or left the stored table
    /// unchanged and how the change is classified for the version-bump decision. An implementation that cannot tell reports an update classified
    /// <see cref="Autheris.Application.Catalog.CatalogWriteClassification.Restrictive"/> (fail closed).
    /// </summary>
    async Task<Autheris.Application.Catalog.CatalogTableUpsertResult> UpsertTableMetadataWithResultAsync(TableMetadata metadata, CancellationToken ct = default)
    {
        var stored = await UpsertTableMetadataAsync(metadata, ct).ConfigureAwait(false);
        return new Autheris.Application.Catalog.CatalogTableUpsertResult(
            stored,
            Autheris.Application.Catalog.CatalogUpsertKind.Updated,
            Autheris.Application.Catalog.CatalogWriteClassification.Restrictive);
    }

    /// <summary>Inserts <paramref name="metadata"/> only when absent; returns the existing entry unchanged otherwise.</summary>
    async Task<Autheris.Application.Catalog.CatalogTableUpsertResult> InsertTableMetadataIfAbsentAsync(TableMetadata metadata, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var existing = await GetTableMetadataAsync(metadata.Identifier, ct).ConfigureAwait(false);
        if (existing is not null)
            return new(existing, Autheris.Application.Catalog.CatalogUpsertKind.Unchanged, Autheris.Application.Catalog.CatalogWriteClassification.Unchanged);
        var stored = await UpsertTableMetadataAsync(metadata, ct).ConfigureAwait(false);
        return new(stored, Autheris.Application.Catalog.CatalogUpsertKind.Created, Autheris.Application.Catalog.CatalogWriteClassification.Additive);
    }

    /// <summary>
    /// Validates and upserts a batch of table metadata entries using parameter-budget chunking, deterministic lock ordering,
    /// per-chunk transactions, persisted-state change classification, and transient conflict retries.
    /// </summary>
    async Task<IReadOnlyList<Autheris.Application.Catalog.CatalogTableUpsertResult>> UpsertTableMetadataBatchAsync(
        IReadOnlyList<TableMetadata> batch,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Count == 0)
        {
            return Array.Empty<Autheris.Application.Catalog.CatalogTableUpsertResult>();
        }

        var results = new Autheris.Application.Catalog.CatalogTableUpsertResult[batch.Count];
        for (var i = 0; i < batch.Count; i++)
        {
            results[i] = await UpsertTableMetadataWithResultAsync(batch[i], ct).ConfigureAwait(false);
        }

        return results;
    }
    Task<Autheris.Application.Catalog.CatalogVersion> GetCatalogVersionAsync(CancellationToken ct = default)
        => Task.FromResult(new Autheris.Application.Catalog.CatalogVersion(1, 1));

    /// <summary>
    /// Issue #111: the persisted catalog tags and discovery settings of an active table, or null when the table is
    /// unknown or inactive. <paramref name="strict"/> (admin read mode) throws on any store error instead of degrading to the secure value.
    /// </summary>
    Task<DatasetCatalogSettings?> GetDatasetCatalogSettingsAsync(TableIdentifier table, bool strict = true, CancellationToken ct = default)
        => throw new NotSupportedException($"{GetType().Name} does not implement dataset catalog settings.");

    /// <summary>
    /// Issue #111: replaces the tags of one <paramref name="source"/> (admin replaces only admin rows, dbt only dbt rows; a tag present as admin
    /// stays admin) in one transaction with the before read. No epoch bump (tags are not policy inputs). Null when the table is unknown or inactive.
    /// </summary>
    Task<DatasetCatalogChange?> ReplaceDatasetTagsAsync(
        TableIdentifier table, IReadOnlyList<string> tags, DatasetTagSource source, Sid actor, CancellationToken ct = default)
        => throw new NotSupportedException($"{GetType().Name} does not implement dataset catalog settings.");

    /// <summary>Issue #111: adds and removes admin-source tags (a dbt row of a removed tag is kept) in one transaction. Null when the table is unknown or inactive.</summary>
    Task<DatasetCatalogChange?> ChangeDatasetTagsAsync(
        TableIdentifier table, IReadOnlyList<string> add, IReadOnlyList<string> remove, Sid actor, CancellationToken ct = default)
        => throw new NotSupportedException($"{GetType().Name} does not implement dataset catalog settings.");

    /// <summary>Issue #448: sets the minimum group size and non-personal table overlay in one transaction. Null when the table is unknown or inactive.</summary>
    Task<DatasetCatalogChange?> SetDatasetPrivacyAsync(
        TableIdentifier table, int? minGroupSize, bool? nonPersonal, Sid actor, CancellationToken ct = default)
        => throw new NotSupportedException($"{GetType().Name} does not implement dataset catalog settings.");

    /// <summary>Sets discovery visibility flags and hidden columns in one transaction with the before read.</summary>
    Task<DatasetCatalogChange?> SetDatasetDiscoveryAsync(
        TableIdentifier table, bool? discoveryHidden, bool? samplesDisabled, IReadOnlyList<string>? hiddenColumns, Sid actor, CancellationToken ct = default)
        => throw new NotSupportedException($"{GetType().Name} does not implement dataset catalog settings.");

    /// <summary>Gets discovery artifacts for a table.</summary>
    Task<DiscoveryArtifacts?> GetDiscoveryArtifactsAsync(Guid tableId, CancellationToken ct = default)
        => throw new NotSupportedException($"{GetType().Name} does not implement discovery artifacts.");

    /// <summary>Gets discovery artifacts for a table and provenance.</summary>
    Task<DiscoveryArtifacts?> GetDiscoveryArtifactsAsync(Guid tableId, ArtifactProvenance provenance, CancellationToken ct = default)
        => throw new NotSupportedException($"{GetType().Name} does not implement discovery artifacts.");

    /// <summary>Gets all discovery artifact rows (across provenances) for a table.</summary>
    Task<IReadOnlyList<DiscoveryArtifacts>> GetAllDiscoveryArtifactsAsync(Guid tableId, CancellationToken ct = default)
        => throw new NotSupportedException($"{GetType().Name} does not implement discovery artifacts.");

    /// <summary>Gets all discovery artifact rows (across provenances) for a set of tables, keyed by table id.</summary>
    async Task<IReadOnlyDictionary<Guid, IReadOnlyList<DiscoveryArtifacts>>> GetAllDiscoveryArtifactsBatchAsync(IReadOnlyCollection<Guid> tableIds, CancellationToken ct = default)
    {
        var map = new Dictionary<Guid, IReadOnlyList<DiscoveryArtifacts>>(tableIds?.Count ?? throw new ArgumentNullException(nameof(tableIds)));
        foreach (var id in tableIds) if (!map.ContainsKey(id)) map[id] = await GetAllDiscoveryArtifactsAsync(id, ct).ConfigureAwait(false);
        return map;
    }

    /// <summary>Upserts discovery artifacts for a table.</summary>
    Task<bool> UpsertDiscoveryArtifactsAsync(DiscoveryArtifacts artifacts, Sid actor, CancellationToken ct = default)
        => throw new NotSupportedException($"{GetType().Name} does not implement discovery artifacts.");

    /// <summary>Deletes discovery artifacts for a table.</summary>
    Task<bool> DeleteDiscoveryArtifactsAsync(Guid tableId, Sid actor, CancellationToken ct = default)
        => throw new NotSupportedException($"{GetType().Name} does not implement discovery artifacts.");

    /// <summary>Deletes discovery artifacts for a specific table and provenance.</summary>
    Task<bool> DeleteDiscoveryArtifactsAsync(Guid tableId, ArtifactProvenance provenance, Sid actor, CancellationToken ct = default)
        => throw new NotSupportedException($"{GetType().Name} does not implement discovery artifacts.");

    /// <summary>
    /// Issue #94 (SD-5): replaces ONLY the declared primary key and indexes of a table. In one transaction the stored columns are read,
    /// the definition is validated against them and replaced, and the epoch is bumped once; masking rules and column metadata other than
    /// the key markers are untouched. Returns null when the table does not exist; throws <c>TableDefinitionException</c> when invalid.
    /// </summary>
    Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(
        TableIdentifier table,
        IReadOnlyList<string> primaryKey,
        IReadOnlyList<TableIndexDefinition> indexes,
        IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null,
        CancellationToken ct = default);

    /// <summary>
    /// Resolves an active table by identifier from its catalog row only: the stored key definition is NOT loaded or validated, so a
    /// table whose definition is invalid still resolves (an administrator must be able to replace it). Null when unknown or inactive.
    /// Default: via <see cref="GetTableMetadataAsync"/>, where an unavailable table resolves to null.
    /// </summary>
    async Task<TableIdentifier?> GetActiveTableIdentifierAsync(TableIdentifier table, CancellationToken ct = default)
    {
        try
        {
            var existing = await GetTableMetadataAsync(table, ct).ConfigureAwait(false);
            return existing is { Table.IsActive: true } ? existing.Identifier : null;
        }
        catch (Autheris.Domain.Exceptions.TableMetadataUnavailableException)
        {
            return null;
        }
    }

    /// <summary>Resolves an active table by its id without loading every table (default: scan of <see cref="GetAllTablesAsync"/>).</summary>
    async Task<TableIdentifier?> GetActiveTableIdentifierByIdAsync(Guid tableId, CancellationToken ct = default)
    {
        var all = await GetAllTablesAsync(ct).ConfigureAwait(false);
        foreach (var t in all)
        {
            if (t.Table.Id == tableId)
            {
                return t.Identifier;
            }
        }

        return null;
    }
}

public interface IConsentRepository
{
    Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, TenantId? tenantId, CancellationToken ct = default);

    /// <summary>
    /// Issue #1043: <see cref="GetActiveConsentsForSubjectsAsync"/> for several tables in a few set-based statements. Each requested table
    /// maps to exactly the consents (direct and pattern, including not-yet-valid denies) that the per-table read returns for it at
    /// <paramref name="atTime"/>. Default: one per-table read per table.
    /// </summary>
    async Task<IReadOnlyDictionary<TableIdentifier, IReadOnlyList<Consent>>> GetActiveConsentsForSubjectsBatchAsync(
        IEnumerable<Sid> subjects, IReadOnlyCollection<TableIdentifier> tables, DateTimeOffset atTime, TenantId? tenantId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subjects);
        ArgumentNullException.ThrowIfNull(tables);
        var subjectList = subjects.ToList();
        var result = new Dictionary<TableIdentifier, IReadOnlyList<Consent>>();
        foreach (var table in tables)
        {
            if (!result.ContainsKey(table))
            {
                result[table] = await GetActiveConsentsForSubjectsAsync(subjectList, table, atTime, tenantId, ct).ConfigureAwait(false);
            }
        }

        return result;
    }

    Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, IEnumerable<string>? roles, DateTimeOffset? atTime, TenantId? tenantId, CancellationToken ct = default);

    /// <summary>
    /// Reads active data-consent headers (without hydrating child rule tables) for grant candidate pre-screening.
    /// </summary>
    Task<IReadOnlyList<Consent>> GetAllActiveConsentHeadersForSubjectsAsync(IEnumerable<Sid> subjects, DateTimeOffset? atTime, TenantId tenantId, CancellationToken ct = default) =>
        GetAllActiveConsentsForSubjectsAsync(subjects, null, atTime, tenantId, ct);

    /// <summary>
    /// Earliest <c>ValidFrom</c> strictly after <paramref name="atTime"/> among non-revoked data Allow consents for
    /// <paramref name="subjects"/> in <paramref name="tenantId"/> (#1063 §4d), or null when none are scheduled.
    /// </summary>
    Task<DateTimeOffset?> GetNextScheduledAllowAsync(IEnumerable<Sid> subjects, DateTimeOffset atTime, TenantId tenantId, CancellationToken ct = default)
        => Task.FromResult<DateTimeOffset?>(null);
    Task<Consent> CreateConsentAsync(Consent consent, CancellationToken ct = default);
    Task<Consent?> GetConsentByIdAsync(Guid consentId, CancellationToken ct = default);
    Task RevokeConsentAsync(Guid consentId, Sid revokedBySid, string reason, ConsentRevocationAuthority authority, CancellationToken ct = default);
    /// <summary>
    /// Active (not revoked, not yet expired) consents that expire at or before <paramref name="threshold"/>, soonest first, capped at
    /// <paramref name="maxResults"/> (PERS-04: already expired consents are never returned, on every provider).
    /// </summary>
    Task<IReadOnlyList<Consent>> GetExpiringConsentsAsync(DateTimeOffset threshold, int maxResults = 1000, CancellationToken ct = default);

    /// <summary>
    /// SEC E-05 / EX-02: All non-revoked, not yet expired consents carrying <paramref name="consentRequestId"/>
    /// (e.g. the OpenMetadata sync marker) – without any subject, role, table or tenant filter (used for reconcile).
    /// </summary>
    Task<IReadOnlyList<Consent>> GetActiveConsentsByConsentRequestIdAsync(Guid consentRequestId, DateTimeOffset atTime, CancellationToken ct = default);

    /// <summary>
    /// SEC E-05 / EX-02: System revocation of a consent owned by an automated sync. Only revokes when the consent carries
    /// <paramref name="consentRequestId"/> (the sync marker); there is no data-owner check because the system actor is not
    /// a table owner and a revocation can only tighten access. Returns false when nothing was revoked.
    /// </summary>
    Task<bool> RevokeSystemConsentAsync(Guid consentId, Guid consentRequestId, Sid revokedBySid, string reason, CancellationToken ct = default);

    /// <summary>
    /// System revocation of one consent by id, scoped to <paramref name="tenantId"/>: a consent of another tenant is never touched.
    /// Used by deprovisioning and grant renewal, which revoke grants regardless of kind or request linkage. Returns false when
    /// nothing was revoked. Implementations must override; the default fails closed.
    /// </summary>
    Task<bool> RevokeSystemConsentByIdAsync(TenantId tenantId, Guid consentId, Sid revokedBySid, string reason, CancellationToken ct = default)
        => throw new NotSupportedException("RevokeSystemConsentByIdAsync is not implemented by this repository.");

    /// <summary>
    /// All active grants of any kind (data, unmask, catalog-scoped table grants and pattern grants) held by <paramref name="subjects"/> in
    /// <paramref name="tenantId"/> at <paramref name="atTime"/>, capped at <paramref name="maxResults"/>. Unlike
    /// <see cref="GetAllActiveConsentsForSubjectsAsync"/> it is not restricted to data grants and is meant for revocation sweeps only.
    /// Implementations must override; the default fails closed.
    /// </summary>
    Task<IReadOnlyList<Consent>> GetActiveGrantsForSubjectsAsync(TenantId tenantId, IReadOnlyCollection<Sid> subjects, DateTimeOffset atTime, int maxResults, CancellationToken ct = default)
        => throw new NotSupportedException("GetActiveGrantsForSubjectsAsync is not implemented by this repository.");

    /// <summary>
    /// Keyset-paginated counterpart to <see cref="GetActiveGrantsForSubjectsAsync"/> returning rows ordered after
    /// <paramref name="afterId"/> so a failing or large batch never starves later active grants.
    /// </summary>
    Task<IReadOnlyList<Consent>> GetActiveGrantsForSubjectsAfterAsync(
        TenantId tenantId, IReadOnlyCollection<Sid> subjects, DateTimeOffset atTime, Guid? afterId, int maxResults, CancellationToken ct = default)
        => afterId is null
            ? GetActiveGrantsForSubjectsAsync(tenantId, subjects, atTime, maxResults, ct)
            : Task.FromResult<IReadOnlyList<Consent>>(Array.Empty<Consent>());

    /// <summary>
    /// SR-L-21: Tables on which <paramref name="tenantId"/> holds at least one non-revoked consent record (Allow or Deny).
    /// Consent-derived tenant ownership signal for catalog isolation (decision G-11: <c>TableMetadata</c> has no owner tenant).
    /// The default returns nothing (fail-closed: no consent-derived visibility).
    /// </summary>
    Task<IReadOnlyList<TableIdentifier>> GetTablesWithConsentsForTenantAsync(TenantId tenantId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<TableIdentifier>>(Array.Empty<TableIdentifier>());

    /// <summary>
    /// one keyset page of the consents of a tenant (ordered by id), including group and user grants, optionally filtered by
    /// table and grantee. The tenant predicate is mandatory. Every persistence provider implements it with an indexed query; there is no
    /// default, so a consent listing can never load every consent into memory.
    /// </summary>
    Task<Autheris.Application.Governance.Paging.ConsentPage> GetConsentPageAsync(
        Autheris.Application.Governance.Paging.ConsentPageQuery query, CancellationToken ct = default);

    /// <summary>
    /// atomically revokes<paramref name="revokeConsentIds"/> and creates <paramref name="create"/> in ONE transaction
    /// with ONE epoch bump per affected table. Either every change is applied or none. There is no non-atomic fallback and no default: every provider
    /// must implement it transactionally, so a partial manifest can never be applied. <paramref name="auditEntries"/> are written in the SAME transaction (chain-linked, Tier-A anchored), so a committed change always has its audit record.
    /// </summary>
    Task ReplaceConsentsAsync(
        IReadOnlyList<Guid> revokeConsentIds,
        IReadOnlyList<Consent> create,
        Sid actor,
        string reason,
        ConsentRevocationAuthority authority,
        IReadOnlyList<AuditLogEntry>? auditEntries = null,
        CancellationToken ct = default);

    Task ReplaceConsentsAsync(
        IReadOnlyList<Guid> revokeConsentIds,
        IReadOnlyList<Consent> create,
        Sid actor,
        string reason,
        ConsentRevocationAuthority authority,
        IReadOnlyList<AuditLogEntry>? auditEntries,
        IReadOnlySet<Guid>? prunedConsentIds,
        string? pruneReason = null,
        CancellationToken ct = default) =>
        ReplaceConsentsAsync(revokeConsentIds, create, actor, reason, authority, auditEntries, ct);

    /// <summary>
    /// Returns the tables in <paramref name="tenantId"/> on which <paramref name="userSid"/> held a non-revoked
    /// Allow consent whose validity window expired on or after <paramref name="since"/>. Used by discovery to mark
    /// <c>Expired</c> vs <c>None</c>; never grants access.
    /// </summary>
    Task<IReadOnlySet<TableIdentifier>> GetRecentlyExpiredTableIdsAsync(
        Sid userSid, TenantId tenantId, DateTimeOffset since, CancellationToken ct = default)
        => Task.FromResult<IReadOnlySet<TableIdentifier>>(new HashSet<TableIdentifier>());

    /// <summary>Returns all active non-revoked pattern grants in <paramref name="tenantId"/> at <paramref name="atTime"/>.</summary>
    Task<IReadOnlyList<Consent>> GetActivePatternConsentsAsync(
        TenantId tenantId, DateTimeOffset atTime, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Consent>>([]);

    /// <summary>
    /// Issue #1043: <see cref="GetActivePatternConsentsAsync"/> plus the not-yet-valid Deny pattern grants, for a batch prefetch that
    /// re-applies the activity rule at each decision's time (a Deny that starts during the batch must not be lost).
    /// </summary>
    Task<IReadOnlyList<Consent>> GetPatternConsentsWithPendingDeniesAsync(
        TenantId tenantId, DateTimeOffset atTime, CancellationToken ct = default)
        => GetActivePatternConsentsAsync(tenantId, atTime, ct);

    /// <summary>Returns all active non-revoked Internal-workflow Allow pattern grants across tenants at <paramref name="atTime"/>.</summary>
    Task<IReadOnlyList<Consent>> GetActiveInternalPatternConsentsAsync(
        DateTimeOffset atTime, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Consent>>([]);

    /// <summary>
    /// Returns active non-revoked <see cref="PermissionGrantKind.Unmask"/> grants covering <paramref name="subjects"/> on <paramref name="table"/>.
    /// </summary>
    Task<IReadOnlyList<Consent>> GetActiveUnmaskGrantsForSubjectsAsync(
        IEnumerable<Sid> subjects,
        TableIdentifier table,
        DateTimeOffset atTime,
        TenantId tenantId,
        CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Consent>>([]);
}


public interface IAuditLogRepository
{
    Task RecordAuditEventAsync(AuditLogEntry entry, CancellationToken ct = default);
    Task RecordAuditEventAsync(AuditLogEntry entry, System.Data.Common.DbTransaction existingTx, CancellationToken ct = default);
    async Task RecordAuditEventsAsync(IReadOnlyList<AuditLogEntry> entries, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        for (int i = 0; i < entries.Count; i++)
        {
            await RecordAuditEventAsync(entries[i], ct).ConfigureAwait(false);
        }
    }
    Task<IReadOnlyList<AuditLogEntry>> GetAuditLogEntriesAsync(int limit = 100, TenantId? tenantId = null, CancellationToken ct = default);
    Task<IReadOnlyList<AuditLogEntry>> QueryAuditLogsAsync(
        string? targetTable = null,
        Sid? actorSid = null,
        DateTimeOffset? since = null,
        int limit = 1000,
        TenantId? tenantId = null,
        CancellationToken ct = default);
    Task<bool> VerifyAuditHashChainAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the newest persisted purge checkpoint anchor, or null if none exists.
    /// </summary>
    Task<AuditPurgeCheckpoint?> GetLatestPurgeCheckpointAsync(CancellationToken ct = default)
        => Task.FromResult<AuditPurgeCheckpoint?>(null);

    /// <summary>
    /// Persists a signed purge checkpoint anchor to AUDIT_PURGE_CHECKPOINTS table.
    /// Enforces monotonic progression over existing checkpoints.
    /// </summary>
    Task SavePurgeCheckpointAsync(AuditPurgeCheckpoint checkpoint, CancellationToken ct = default)
        => Task.CompletedTask;

    /// <summary>
    /// Plans a contiguous prefix purge bounded by the first entry whose retention horizon has not yet elapsed.
    /// Returns (MaxEligibleSequence, FirstSurvivingSequence, SurvivingPrevHash, PurgeCount) or null if no eligible prefix exists.
    /// </summary>
    Task<(long MaxEligibleSequence, long FirstSurvivingSequence, string SurvivingPrevHash, long PurgeCount)?> PlanPurgePrefixAsync(DateTimeOffset thresholdUtc, CancellationToken ct = default)
        => Task.FromResult<(long MaxEligibleSequence, long FirstSurvivingSequence, string SurvivingPrevHash, long PurgeCount)?>(null);

    /// <summary>
    /// Deletes up to<paramref name="batchSize"/> entries with sequence numbers &lt;= <paramref name="maxSequenceInclusive"/>.
    /// Uses transaction-scoped session context to bypass append-only triggers.
    /// </summary>
    Task<int> ExecutePurgeBatchAsync(long maxSequenceInclusive, int batchSize, CancellationToken ct = default)
        => Task.FromResult(0);

    /// <summary>
    /// one keyset page over the audit chain (newest first, by chain sequence). Rows without a sequence number are never
    /// listed. The default fails closed; every governance repository overrides it.
    /// </summary>
    Task<Autheris.Application.Governance.Paging.AuditLogPage> QueryAuditLogPageAsync(
        Autheris.Application.Governance.Paging.AuditLogPageQuery query, CancellationToken ct = default)
        => throw new NotSupportedException(
            $"{GetType().Name} does not implement the audit cursor query; refusing an unbounded listing.");
}

public interface IPolicyEpochRepository
{
    Task<long> GetTableEpochAsync(TableIdentifier table, CancellationToken ct = default);
    Task<long> IncrementTableEpochAsync(TableIdentifier table, CancellationToken ct = default);
}

public interface IConsentApprovalRepository
{
    Task<ConsentRequest> CreateConsentRequestAsync(ConsentRequest request, CancellationToken ct = default);
    Task<ConsentRequest?> GetConsentRequestAsync(Guid requestId, CancellationToken ct = default);
    Task<IReadOnlyList<ConsentRequest>> GetRequestsByRequesterSidAsync(Sid requesterSid, TenantId tenantId, string? status = null, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ConsentRequest>>(Array.Empty<ConsentRequest>());

    /// <summary>
    /// Pending approval requests in <paramref name="tenantId"/> where <paramref name="subject"/> is either the requester
    /// or the target grantee, across all withdrawable pending workflow states.
    /// </summary>
    Task<IReadOnlyList<ConsentRequest>> GetPendingRequestsForSubjectAsync(Sid subject, TenantId tenantId, CancellationToken ct = default)
        => GetRequestsByRequesterSidAsync(subject, tenantId, status: null, ct);
    Task<IReadOnlyList<ConsentRequest>> GetPendingRequestsForApproverAsync(Sid approverSid, TenantId tenantId, CancellationToken ct = default);
    Task<ConsentRequest> ApproveConsentRequestStepAsync(Guid requestId, Sid approverSid, CancellationToken ct = default);

    /// <summary>
    /// Records an approval step like <see cref="ApproveConsentRequestStepAsync(Guid, Sid, CancellationToken)"/>; when
    /// <paramref name="requireSecondApprover"/> is set the request needs a second distinct approver regardless of the table's
    /// four-eyes flag (per-classification Clear approved by a steward after the data owner).
    /// </summary>
    Task<ConsentRequest> ApproveConsentRequestStepAsync(Guid requestId, Sid approverSid, bool requireSecondApprover, CancellationToken ct = default)
        => requireSecondApprover
            ? throw new NotSupportedException($"{GetType().Name} does not implement mandatory second approvers.")
            : ApproveConsentRequestStepAsync(requestId, approverSid, ct);
    Task<ConsentRequest> RejectConsentRequestAsync(Guid requestId, Sid approverSid, string reason, CancellationToken ct = default);

    /// <summary>
    /// Activation for the insecure getting-started auto-approve path (request still pending).
    /// Regular activation (<see cref="ActivateConsentAsync(Guid, Sid?, CancellationToken)"/>) only accepts 'APPROVED'.
    /// </summary>
    Task ActivateConsentForAutoApproveAsync(Guid requestId, CancellationToken ct = default)
        => ActivateConsentAsync(requestId, ct);

    Task ActivateConsentAsync(Guid requestId, CancellationToken ct = default);
    Task DeleteConsentRequestAsync(Guid requestId, CancellationToken ct = default);

    /// <summary>
    /// Tenant-bound external reference lookup (<c>WHERE external_system_id = @sysId AND external_reference = @ref AND tenant_id = @tenant</c>).
    /// Fails closed when not overridden.
    /// </summary>
    Task<ConsentRequest?> GetConsentRequestByExternalReferenceAsync(
        Guid externalSystemId,
        string externalReference,
        TenantId tenantId,
        CancellationToken ct = default)
        => throw new NotSupportedException(
            $"{GetType().Name} does not implement tenant-scoped external reference lookup.");

    Task<ConsentRequest> ApproveExternalConsentRequestStepAsync(
        Guid requestId,
        ExternalDecisionAttribution attribution,
        Sid effectiveApproverSid,
        CancellationToken ct = default)
        => throw new NotSupportedException($"{GetType().Name} does not implement external approval steps.");

    Task<ConsentRequest> ApproveExternalConsentRequestStepAsync(
        Guid requestId,
        ExternalDecisionAttribution attribution,
        Sid effectiveApproverSid,
        bool requireSecondApprover,
        CancellationToken ct = default)
        => requireSecondApprover
            ? throw new NotSupportedException($"{GetType().Name} does not support enforcing a second approver on external approval steps.")
            : ApproveExternalConsentRequestStepAsync(requestId, attribution, effectiveApproverSid, ct);

    /// <summary>
    /// Counts pending permission requests submitted by <paramref name="requesterSid"/> in <paramref name="tenantId"/>.
    /// </summary>
    Task<int> CountPendingRequestsForRequesterAsync(
        TenantId tenantId,
        Sid requesterSid,
        CancellationToken ct = default) =>
        Task.FromResult(0);

    /// <summary>
    /// Atomically locks the request row, evaluates state inside the transaction, executes <paramref name="decideInTransaction"/>,
    /// performs the versioned CAS update (<c>WHERE id = @id AND status = 'PENDING' AND version = @expected</c>), inserts the single
    /// <c>APPROVAL_STEPS</c> row, and invokes <paramref name="applyEffect"/> (on approval) within the same database transaction.
    /// </summary>
    Task<Autheris.Application.Governance.Requests.DecisionCasResult> TryDecideAsync(
        Guid requestId,
        int expectedVersion,
        Func<Autheris.Application.Governance.Requests.IGovernanceTransaction, ConsentRequest, Task<Autheris.Application.Governance.Requests.PermissionDecisionRecord>> decideInTransaction,
        Func<Autheris.Application.Governance.Requests.IGovernanceTransaction, Autheris.Application.Governance.Requests.PermissionDecisionRecord, ConsentRequest, Task<IReadOnlyList<Autheris.Application.Governance.Requests.GrantRef>>> applyEffect,
        CancellationToken ct = default) =>
        throw new NotSupportedException($"{GetType().Name} does not implement atomic single-transaction request decisions.");

    Task<ConsentRequest> RejectExternalConsentRequestAsync(
        Guid requestId,
        ExternalDecisionAttribution attribution,
        Sid effectiveApproverSid,
        string reason,
        CancellationToken ct = default)
        => throw new NotSupportedException($"{GetType().Name} does not implement external rejection.");

    /// <summary>
    /// Activates a pending consent request and records <paramref name="approvedBy"/> as audit actor.
    /// </summary>
    Task ActivateConsentAsync(Guid requestId, Sid? approvedBy, CancellationToken ct = default)
        => ActivateConsentAsync(requestId, ct);

    /// <summary>
    /// Atomically withdraws a pending consent request owned by<paramref name="requesterSid"/>.
    /// Returns true if updated to WITHDRAWN, false if not found or no longer pending.
    /// </summary>
    Task<bool> WithdrawConsentRequestAsync(Guid requestId, Sid requesterSid, CancellationToken ct = default)
        => Task.FromResult(false);

    /// <summary>
    /// Atomically records the approval step, updates the request status to APPROVED, creates the active consent
    /// and its column rules and row filters, and bumps the table policy epoch in a single database transaction.
    /// Security review (four-eyes on the atomic path): implementations must apply the same checks as
    /// <see cref="ApproveConsentRequestStepAsync(Guid, Sid, CancellationToken)"/> under the row lock (approver is not the requester, has not
    /// already approved this request) and only complete the request when this step satisfies the four-eyes requirement (table flag or
    /// HIGH/RESTRICTED/SECRET sensitivity, ADR-008). Otherwise nothing is written and <see cref="InvalidOperationException"/> is thrown
    /// (<c>ConsentRequestStatusConflictException</c> when the request is no longer pending). The step number is assigned by the
    /// repository; <see cref="ApprovalStep.StepNumber"/> is ignored.
    /// </summary>
    Task<Consent> ExecuteAtomicApprovalWithConsentAsync(
        Guid requestId,
        ApprovalStep step,
        Consent consent,
        IReadOnlyList<ConsentRowFilter> rowFilters,
        IReadOnlyList<ConsentColumnRule> columnRules,
        CancellationToken ct = default)
        => throw new NotSupportedException($"{GetType().Name} does not implement atomic approval.");

    /// <summary>
    /// Atomic approval like <see cref="ExecuteAtomicApprovalWithConsentAsync(Guid, ApprovalStep, Consent, IReadOnlyList{ConsentRowFilter}, IReadOnlyList{ConsentColumnRule}, CancellationToken)"/>;
    /// when <paramref name="requireSecondApprover"/> is set the step only completes the request as the second distinct approval.
    /// </summary>
    Task<Consent> ExecuteAtomicApprovalWithConsentAsync(
        Guid requestId,
        ApprovalStep step,
        Consent consent,
        IReadOnlyList<ConsentRowFilter> rowFilters,
        IReadOnlyList<ConsentColumnRule> columnRules,
        bool requireSecondApprover,
        CancellationToken ct = default)
        => requireSecondApprover
            ? throw new NotSupportedException($"{GetType().Name} does not implement mandatory second approvers.")
            : ExecuteAtomicApprovalWithConsentAsync(requestId, step, consent, rowFilters, columnRules, ct);

    Task<ConsentRequestPage> QueryTenantConsentRequestsAsync(
        ConsentRequestPageQuery query,
        CancellationToken ct = default)
        => Task.FromResult(new ConsentRequestPage(Array.Empty<ConsentRequest>(), null));
}

/// <param name="AdditionalStatus">A second status matched besides <paramref name="Status"/> (the decision inbox lists PENDING and PENDING_SECOND_APPROVAL).</param>
public sealed record ConsentRequestPageQuery(
    TenantId TenantId,
    string? Status = null,
    Guid? ExternalSystemId = null,
    DateTimeOffset? UpdatedSince = null,
    string? Cursor = null,
    int Limit = 50,
    string? Domain = null,
    string? Schema = null,
    string? TableName = null,
    string? RequesterSid = null,
    string? AdditionalStatus = null);

public sealed record ConsentRequestPage(
    IReadOnlyList<ConsentRequest> Items,
    string? NextCursor);

public interface ITableRelationRepository
{
    Task<IReadOnlyList<TableRelation>> GetRelationsForTableAsync(TableIdentifier parentTable, CancellationToken ct = default);
    Task<IReadOnlyList<TableRelation>> GetAllRelationsAsync(CancellationToken ct = default);

    /// <summary>
    /// Issue #786: true when <see cref="GetAllRelationsAsync"/> is complete, so an empty result means "no relations" and
    /// callers must not fall back to one <see cref="GetRelationsForTableAsync"/> read per table. Test doubles keep the default.
    /// </summary>
    bool BulkRelationsAuthoritative => false;

    /// <summary>
    /// Creates or updates a relation by its natural key (parent table, child table, relation name) together with its ordered key pairs in one
    /// transaction (issue #151). Throws <see cref="Autheris.Domain.Exceptions.InvalidRelationException"/> for invalid keys, unknown, inactive or ambiguous
    /// tables and unknown key columns; nothing is written then.
    /// </summary>
    Task<RelationWriteResult> CreateRelationAsync(TableRelation relation, CancellationToken ct = default);

    /// <summary>
    /// Issue #223: Admin/owner approval path to opt-in or revoke cross-data-source navigation for a relation.
    /// </summary>
    Task<bool> SetRelationCrossDataSourceOptInAsync(Guid relationId, bool allowCrossDataSource, CancellationToken ct = default);
}

/// <summary>Outcome of <see cref="ITableRelationRepository.CreateRelationAsync"/>the stored relation id, whether the row was inserted, and whether the ordered key pairs of an existing relation changed.</summary>
public sealed record RelationWriteResult(Guid StoredId, bool Created, bool KeysChanged);

public interface IGovernanceRepository :
    ITableMetadataRepository,
    IConsentRepository,
    IAuditLogRepository,
    IPolicyEpochRepository,
    IConsentApprovalRepository,
    ITableRelationRepository,
    IConsumerAggregationRepository
{
}

/// <summary>
/// INC1 (06): the single write seam of<c>setConsentDescription</c>. Implemented by every governance provider; deliberately not part
/// of <see cref="IConsentRepository"/> so the attribute-validating decorator and consent fakes are untouched.
/// </summary>
public interface IConsentDescriptionRepository
{
    /// <summary>
    /// Sets the description of one non-revoked, unexpired consent of <paramref name="tenant"/> and writes <paramref name="audit"/> in the SAME transaction
    /// (an audit failure rolls the update back). Touches only <c>description</c>: no epoch bump, no validity or rule change. Returns false, changes nothing and
    /// writes no audit row when no such consent exists.
    /// </summary>
    Task<bool> UpdateConsentDescriptionWithAuditAsync(Guid consentId, TenantId tenant, string? description, AuditLogEntry audit, CancellationToken ct = default);

    /// <summary>
    /// Opt-in load of <c>CONSENTS.description</c> into <paramref name="consents"/> (matched by id). The read methods of <see cref="IConsentRepository"/>
    /// never load it, so the access-decision hot path carries no description cost; only explain, consent listings, myActiveConsents and the
    /// manifest compare call this. One extra round trip per call; a consent without a stored description is left untouched.
    /// </summary>
    Task LoadDescriptionsAsync(IReadOnlyCollection<Consent> consents, CancellationToken ct = default);

    /// <summary>
    /// Same contract as <see cref="IConsentRepository.ReplaceConsentsAsync(IReadOnlyList{Guid}, IReadOnlyList{Consent}, Sid, string, ConsentRevocationAuthority, IReadOnlyList{AuditLogEntry}?, CancellationToken)"/>
    /// plus in-place description updates of non-revoked consents in the SAME transaction. A description update changes no other column and bumps no epoch;
    /// a revoked, expired, unknown or foreign-tenant target changes nothing (no error, no resurrection). The attribute-validating decorator implements this
    /// interface too, so the create list is validated on this path as well.
    /// </summary>
    Task ReplaceConsentsAsync(
        IReadOnlyList<Guid> revokeConsentIds,
        IReadOnlyList<Consent> create,
        Sid actor,
        string reason,
        ConsentRevocationAuthority authority,
        IReadOnlyList<AuditLogEntry>? auditEntries,
        IReadOnlyList<ConsentDescriptionUpdate>? descriptionUpdates,
        CancellationToken ct);

    Task ReplaceConsentsAsync(
        IReadOnlyList<Guid> revokeConsentIds,
        IReadOnlyList<Consent> create,
        Sid actor,
        string reason,
        ConsentRevocationAuthority authority,
        IReadOnlyList<AuditLogEntry>? auditEntries,
        IReadOnlyList<ConsentDescriptionUpdate>? descriptionUpdates,
        IReadOnlySet<Guid>? prunedConsentIds,
        string? pruneReason = null,
        CancellationToken ct = default) =>
        ReplaceConsentsAsync(revokeConsentIds, create, actor, reason, authority, auditEntries, descriptionUpdates, ct);
}

public static partial class TableMetadataRepositoryExtensions
{
    public static async Task<IReadOnlyList<Autheris.Application.Catalog.CatalogTableUpsertResult>> UpsertBatchWithFallbackAsync(
        this ITableMetadataRepository repository, IReadOnlyList<TableMetadata> batch, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Count == 0) return Array.Empty<Autheris.Application.Catalog.CatalogTableUpsertResult>();
        var results = await repository.UpsertTableMetadataBatchAsync(batch, ct).ConfigureAwait(false);
        if (results is { Count: var count } && count == batch.Count) return results;
        var fallback = new Autheris.Application.Catalog.CatalogTableUpsertResult[batch.Count];
        for (var i = 0; i < batch.Count; i++)
            fallback[i] = new(await repository.UpsertTableMetadataAsync(batch[i], ct).ConfigureAwait(false), Autheris.Application.Catalog.CatalogUpsertKind.Updated, Autheris.Application.Catalog.CatalogWriteClassification.Restrictive);
        return fallback;
    }
}
