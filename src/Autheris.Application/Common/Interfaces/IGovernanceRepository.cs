using Autheris.Domain.Audit;
using Autheris.Domain.Security;

namespace Autheris.Application.Common.Interfaces;

public interface ITableMetadataRepository : Autheris.Application.Catalog.ICatalogVersionFlusher
{
    Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default);
    Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default);

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
    /// Issue #107: upserts the object-level row filter evaluation settings (null deletes the row) in TABLE_ROW_FILTER_STRATEGIES and bumps
    /// the table epoch, in one transaction. Returns null when the table is unknown.
    /// </summary>
    Task<RowFilterEvaluationChange?> SetRowFilterEvaluationAsync(
        TableIdentifier table, RowFilterEvaluationSettings? settings, Sid actor, CancellationToken ct = default)
        => throw new NotSupportedException($"{GetType().Name} does not implement row filter evaluation settings.");

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

    /// <summary>Upserts discovery artifacts for a table.</summary>
    Task<bool> UpsertDiscoveryArtifactsAsync(DiscoveryArtifacts artifacts, Sid actor, CancellationToken ct = default)
        => throw new NotSupportedException($"{GetType().Name} does not implement discovery artifacts.");

    /// <summary>Deletes discovery artifacts for a table.</summary>
    Task<bool> DeleteDiscoveryArtifactsAsync(Guid tableId, Sid actor, CancellationToken ct = default)
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
    Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, CancellationToken ct = default);
    Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, TenantId? tenantId, CancellationToken ct = default);
    Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, IEnumerable<string>? roles = null, DateTimeOffset? atTime = null, CancellationToken ct = default);
    Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, IEnumerable<string>? roles, DateTimeOffset? atTime, TenantId? tenantId, CancellationToken ct = default);
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

    /// <summary>Returns all active non-revoked Internal-workflow Allow pattern grants across tenants at <paramref name="atTime"/>.</summary>
    Task<IReadOnlyList<Consent>> GetActiveInternalPatternConsentsAsync(
        DateTimeOffset atTime, CancellationToken ct = default)
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
    Task<IReadOnlyList<ConsentRequest>> GetRequestsByRequesterSidAsync(Sid requesterSid, string? status = null, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ConsentRequest>>(Array.Empty<ConsentRequest>());
    Task<IReadOnlyList<ConsentRequest>> GetPendingRequestsForApproverAsync(Sid approverSid, CancellationToken ct = default);
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
    /// <summary>Issue #107: one row filter with its consent effect, tenant and target table; null when it does not exist.</summary>
    Task<(ConsentRowFilter Filter, ConsentEffect Effect, TenantId Tenant, TableIdentifier Table)?> GetRowFilterAsync(
        Guid rowFilterId, CancellationToken ct = default)
        => throw new NotSupportedException($"{GetType().Name} does not implement row filter lookup.");

    /// <summary>
    /// Issue #107: in one transaction reads the before settings, updates the three key-set columns and bumps POLICY_EPOCHS of the consent's
    /// target table; then <c>InvalidateEpochAsync</c>. Null when the filter does not exist or its consent belongs to another tenant.
    /// </summary>
    Task<RowFilterEvaluationChange?> UpdateRowFilterEvaluationAsync(
        Guid rowFilterId, TenantId tenant, RowFilterEvaluationSettings settings, CancellationToken ct = default)
        => throw new NotSupportedException($"{GetType().Name} does not implement row filter evaluation updates.");

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

    Task<Consent> ExecuteExternalAtomicApprovalWithConsentAsync(
        Guid requestId,
        ExternalDecisionAttribution attribution,
        ApprovalStep step,
        Consent consent,
        IReadOnlyList<ConsentRowFilter> rowFilters,
        IReadOnlyList<ConsentColumnRule> columnRules,
        CancellationToken ct = default)
        => throw new NotSupportedException($"{GetType().Name} does not implement external atomic approval.");

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
    string? RequesterSid = null);

public sealed record ConsentRequestPage(
    IReadOnlyList<ConsentRequest> Items,
    string? NextCursor);

public interface IDataOwnershipRepository
{
    Task<DataOwnerDelegation> DelegateDataOwnershipAsync(DataOwnerDelegation delegation, CancellationToken ct = default);
    Task<IReadOnlyList<DataOwner>> GetDataOwnersForTableAsync(TableIdentifier table, CancellationToken ct = default);
    Task<bool> IsAuthorizedApproverForTableAsync(TableIdentifier table, Sid approverSid, CancellationToken ct = default);

    /// <summary>Batch-loads active data owners keyed by table identifier.</summary>
    async Task<IReadOnlyDictionary<TableIdentifier, IReadOnlyList<DataOwner>>> GetDataOwnersForTablesAsync(
        IReadOnlyCollection<TableIdentifier> tables, CancellationToken ct = default)
    {
        var result = new Dictionary<TableIdentifier, IReadOnlyList<DataOwner>>();
        foreach (var table in tables)
        {
            var owners = await GetDataOwnersForTableAsync(table, ct).ConfigureAwait(false);
            result[table] = owners.Where(static o => o.IsActive).ToList();
        }
        return result;
    }

    /// <summary>
    /// Returns the subset of <paramref name="tables"/> on which <paramref name="actorSid"/> is an active owner
    /// or active delegate at <paramref name="atTime"/>.
    /// </summary>
    async Task<IReadOnlySet<TableIdentifier>> GetOwnedOrDelegatedTablesAsync(
        Sid actorSid, IReadOnlyCollection<TableIdentifier> tables, DateTimeOffset atTime, CancellationToken ct = default)
    {
        var owned = new HashSet<TableIdentifier>();
        foreach (var table in tables)
        {
            if (await IsAuthorizedApproverForTableAsync(table, actorSid, ct).ConfigureAwait(false))
            {
                owned.Add(table);
            }
        }
        return owned;
    }

    /// <summary>Returns active direct owner and active delegate SIDs for <paramref name="table"/> at <paramref name="atTime"/>.</summary>
    async Task<IReadOnlySet<string>> GetAuthorizedApproverSidsForTableAsync(
        TableIdentifier table, DateTimeOffset atTime, CancellationToken ct = default)
    {
        var owners = await GetDataOwnersForTableAsync(table, ct).ConfigureAwait(false);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var owner in owners)
        {
            if (owner.IsActive && !string.IsNullOrWhiteSpace(owner.AdSid.Value))
            {
                set.Add(owner.AdSid.Value);
            }
        }
        return set;
    }

    /// <summary>Returns true if any SID in <paramref name="ownerSids"/> is currently an active owner or active delegate for <paramref name="table"/>.</summary>
    async Task<bool> IsAnyApproverAuthorizedForTableAsync(
        TableIdentifier table, IReadOnlyCollection<string> ownerSids, DateTimeOffset atTime, CancellationToken ct = default)
    {
        foreach (var sid in ownerSids)
        {
            if (!string.IsNullOrWhiteSpace(sid) &&
                await IsAuthorizedApproverForTableAsync(table, new Sid(sid), ct).ConfigureAwait(false))
            {
                return true;
            }
        }
        return false;
    }
}

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
    IDataOwnershipRepository,
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

/// <summary>
/// Batch upsert helpers on <see cref="ITableMetadataRepository"/>.
/// </summary>
public static class TableMetadataRepositoryExtensions
{
    /// <summary>
    /// Calls <see cref="ITableMetadataRepository.UpsertTableMetadataBatchAsync"/> and falls back to per-item
    /// <see cref="ITableMetadataRepository.UpsertTableMetadataAsync"/> only when an unconfigured mock returns fewer
    /// results than the batch count.
    /// </summary>
    public static async Task<IReadOnlyList<Autheris.Application.Catalog.CatalogTableUpsertResult>> UpsertBatchWithFallbackAsync(
        this ITableMetadataRepository repository,
        IReadOnlyList<TableMetadata> batch,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Count == 0)
        {
            return Array.Empty<Autheris.Application.Catalog.CatalogTableUpsertResult>();
        }

        var results = await repository.UpsertTableMetadataBatchAsync(batch, ct).ConfigureAwait(false);
        if (results is { Count: var count } && count == batch.Count)
        {
            return results;
        }

        var fallback = new Autheris.Application.Catalog.CatalogTableUpsertResult[batch.Count];
        for (var i = 0; i < batch.Count; i++)
        {
            var stored = await repository.UpsertTableMetadataAsync(batch[i], ct).ConfigureAwait(false);
            fallback[i] = new Autheris.Application.Catalog.CatalogTableUpsertResult(
                stored,
                Autheris.Application.Catalog.CatalogUpsertKind.Updated,
                Autheris.Application.Catalog.CatalogWriteClassification.Restrictive);
        }

        return fallback;
    }
}
