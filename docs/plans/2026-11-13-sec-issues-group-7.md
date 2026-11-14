# Phase 3 security review: open-issue plans, priority group 7 (catalog scale)

- **Date:** 2026-11-14 · **Phase:** 3 (`csharp-security-expert`) · **Base verified:** `origin/main` @ `7a4df959`
- **Reviewed:** [`2026-11-13-plan-issues-catalog-scale.md`](2026-11-13-plan-issues-catalog-scale.md) sections #672-#678 (WP-OI-SCALE-1..7) and the invariants INV-1..INV-4. Owner defaults `Q-SCALE-1..6` are binding (2026-11-13), including Q-SCALE-3 (per-hit epoch read stays). #679 (SCALE-8) is out of this group (review only for PR 3). SCALE-0 needs no review.
- **Ids:** `SEC-OI-<issue>-<n>`. Every test below carries `[Trait("Category","Security")]`. A condition is binding for Phase 4 and Phase 5. Cluster-wide rule added here: **a restrictive change is never delayed, a permissive or additive one may be.**

| Issue | WP | Verdict | Developer-ready |
|---|---|---|:---:|
| #674 | WP-OI-SCALE-3 | APPROVED WITH CONDITIONS (false "unchanged" drops a restrictive change) | yes |
| #672 | WP-OI-SCALE-1 | CHANGES REQUIRED (deferral also delays hidden/deactivate/mask changes; lost bump on flush failure) | after amendment below |
| #673 | WP-OI-SCALE-2 | APPROVED WITH CONDITIONS | yes, after #674 and #672 |
| #675 | WP-OI-SCALE-4 | APPROVED WITH CONDITIONS (delta trusts every writer to bump the epoch) | yes |
| #676 | WP-OI-SCALE-5 | APPROVED WITH CONDITIONS | yes, after #675 |
| #677 | WP-OI-SCALE-6 | CHANGES REQUIRED (plan assumes tenant-scoped catalog reads that do not exist; paging after visibility) | after amendment below |
| #678 | WP-OI-SCALE-7 | APPROVED WITH CONDITIONS | yes (freeze-exception per Q-SCALE-1) |

Order change: none; SEC-OI-675-1 adds an architecture test that must exist before the delta path is switched on.

## #674 No-op detection

**Verified.** `GovernanceRepository.Catalog.cs:432-453` bumps unconditionally; cache TTL 30 s (`GovernanceRepository.cs:25,28`).

**Threats.** A false "unchanged" silently discards a restrictive change (new masking rule, classification, hidden flag, key or row-filter change). Causes: comparison against a cached (stale) copy, a persisted property missing from the projection, null vs empty vs whitespace equality, case-insensitive comparison of identifiers, an exception in the comparer treated as equal.

**Conditions.**
- SEC-OI-674-1: the stored state used for the comparison is read from the database inside the write transaction, never from `BoundedCache`; the call returns the freshly loaded metadata on `Unchanged`, not the writer's input.
- SEC-OI-674-2: the comparer is exact: ordinal, case-sensitive for identifiers, null differs from empty, no trimming; any exception, unknown property or missing stored row yields `Changed` (fail toward writing).
- SEC-OI-674-3: `CatalogTableProjectionCoverageTests` covers every persisted column of every table the upsert writes (tables, columns, masking rules, classifications, key definitions, row-filter strategy, visibility/hidden/`IsActive`, owners, relations) by reflection over the entity types and by a SQL-side check that every column of the written catalog tables is mapped; a new column without projection fails the build.
- SEC-OI-674-4: one generated test per projected property flips only that property and asserts `Changed` plus epoch and version bump; a masking-rule removal, a hidden-flag set, a re-activation and a classification change are named explicit cases.
- SEC-OI-674-5: documentation precedence and "governance fields preserved" merge happen **before** the comparison; a sync that omits a governance-set masking rule must not compare as `Changed` and must not delete it (parity with the single path).
- Tests: the plan names plus `CatalogNoOpSecurityTests.CachedStaleCopy_DoesNotCauseFalseUnchanged`, `.NullVersusEmpty_IsChanged`, `.CaseOnlyIdentifierChange_IsChanged`, `.ComparerException_FallsBackToWrite`, `.EveryProjectedProperty_FlipIsChanged`.

## #672 Catalog write scope

**Verified.** Per-table writes bump and publish inside the writer (`GovernanceRepository.Catalog.cs:448-453`); catalog reads and writes carry no tenant key (no tenant column or filter in the catalog repository files), so the plan's "scope captures the tenant" has nothing to bind to.

**Threats.**
- Deferral delays the catalog version for up to a chunk (500 tables). The snapshot, search index, GraphQL schema and MCP listings are driven by that version, so a **hidden flag, deactivation, visibility reduction or added masking** stays visible there until the flush (the per-table epoch protects only decision and plan caches).
- `AsyncLocal` is copied into `Task.Run` children: a write after the scope was disposed, or from an unrelated flow, may defer into a dead scope and never flush.
- A failed flush (version transaction or publish) after committed tables leaves a stale version; the 30 s poll compares versions only, so it never repairs a missing bump.
- `CancellationToken.None` in `finally` can hang shutdown.

**Conditions.**
- SEC-OI-672-1 (replaces the tenant capture): the scope records writer name and an unique scope id; the repository defers only while the scope is active **and** not disposed; a write into a disposed or foreign scope bumps and publishes immediately. The scope type has no public mutable state and is not captured by background tasks of the writer.
- SEC-OI-672-2: only additive or permissive writes are deferred (new table, new column, description or tag change, relation add). A write that hides, deactivates, removes, reduces visibility, adds a masking rule, raises a classification, changes keys or row-filter strategy, or whose class is unknown flushes the pending delta immediately and bumps the version in its own transaction before the writer continues. The classification uses the SCALE-3 projection diff; `Unchanged` writes defer nothing.
- SEC-OI-672-3: the flush bumps the version with the summed deltas; on failure the scope retries up to 3 times, then sets a process-wide "catalog dirty" flag that the snapshot service checks every poll and that forces a bump and event; a persistent failure logs Critical (counts only) and degrades health. Publish failure never rolls back or hides a committed bump.
- SEC-OI-672-4: the `finally` flush uses a 30 s timeout token (not `None`); on timeout the dirty flag path of SEC-OI-672-3 applies. An exception in the flush never replaces the writer's original exception (aggregate or log).
- SEC-OI-672-5: nested scopes join the outer one; the outermost scope's disposal is the only deferred flush.
- Tests: the plan names plus `CatalogWriteScopeSecurityTests.HiddenFlagSet_InScope_FlushesImmediately`, `.MaskingRuleAdded_InScope_FlushesImmediately`, `.WriteAfterScopeDisposed_PublishesImmediately`, `.WriteInTaskRunChild_AfterDispose_IsNotDeferred`, `.FlushFailure_SetsDirtyFlag_PollRepairs`, `.FlushTimeout_DoesNotHangDispose`.

## #673 Batch upsert

**Threats.** Set-based SQL with identifier or id lists (injection, parameter-limit overrun); reporting `Created/Updated` for a rolled-back chunk; epoch bumped before commit or cache invalidated before commit; update statements overwriting governance-set fields; skipped per-field validation in the batch path; deadlocks between concurrent syncs.

**Conditions.**
- SEC-OI-673-1: bound parameters only, no concatenated values; id and name lists go through parameters in chunks within `DatabaseParameterBudgetProvider` (SQL Server 2,100); every new statement is in `GovernanceSqlAllowlist` and the golden dialect files; input lists above 500 are chunked by the repository, never passed through.
- SEC-OI-673-2: the batch applies the same validation, length limits and normalisation as the single path to every row before the first write; one invalid row fails the chunk with the table identifier named, no value echoed.
- SEC-OI-673-3: result honesty: a failed chunk reports every table of the chunk as `Failed`, none as `Created/Updated`, and no epoch or cache invalidation happened; epoch increments are inside the chunk transaction and `InvalidateTableEpochAsync` runs only after commit, for every changed table.
- SEC-OI-673-4: the `UPDATE` column lists equal the single path's (governance fields such as hidden flag, owners, manual masking rules and classifications are not written by sync batches); the single path is a one-element batch call (as planned) so there is no second code path.
- SEC-OI-673-5: rows are processed in a deterministic order (by id) inside the chunk to keep lock order stable; a deadlock or serialization failure fails the chunk and the writer retries the chunk at most 3 times with backoff.
- Tests: the plan names plus `GovernanceRepositoryBatchUpsertContractTests.SyncBatch_DoesNotOverwriteGovernanceMaskingRule`, `.InvalidRow_FailsWholeChunkWithoutEpochBump`, `.ChunkFailure_ReportsAllTablesFailed`, `.NamesWithQuotesAndPercent_AreBoundNotConcatenated`, `.InvalidationOnlyAfterCommit` (both engines).

## #675 Snapshot coalescing and deltas

**Verified.** `CatalogSnapshotProvider.cs:39` (event to refresh), 30 s poll, `InProcessChannelEventBus.cs:146-152`; no `change_version` exists yet.

**Threats.** The delta is trustworthy only if **every** statement that changes catalog authorization data also bumps the table's epoch and `change_version`; one writer that does not (admin hidden flag, WP-SIMPL-41 hidden objects, row-filter strategy, physical delete) makes the snapshot serve a stale table indefinitely. A watermark read after the rows reproduces the #501 race. A hard delete leaves no epoch row to report as removed. Coalescing could drop the final event.

**Conditions.**
- SEC-OI-675-1: architecture test `CatalogMutatingWritersBumpChangeVersionTests` enumerates every repository method and SQL core that writes a catalog table and fails unless it increments the epoch and sets `change_version` in the same transaction; a new writer without it fails the build. Until it is green the delta path stays off (full reload).
- SEC-OI-675-2: `GetTablesChangedSinceAsync` reads the high watermark first, then rows with `change_version > since AND <= watermark` in one transaction; the snapshot stores the watermark as its version. A hard delete writes a tombstone (inactive epoch row or a delete marker) so the delta reports removal; otherwise deletion forces a full reload.
- SEC-OI-675-3: backstop: a full reload runs at least every 10 minutes regardless of deltas and on any gap, read error, unknown table in a relation, version regression or consumer error (INV-4); the delta and the full reload produce equal snapshots (test).
- SEC-OI-675-4: a removed, hidden or inactive table leaves the snapshot in the same published update that carries the change; the update publishes one immutable snapshot by reference swap; entries are immutable (no exposed mutable collections), asserted by a test over the five outside consumers.
- SEC-OI-675-5: the coalescing loop cannot lose the last signal (capacity-1 `DropOldest` plus read-version-after-signal); a failing refresh retries within the poll interval and reports `Degraded` after 3 consecutive failures; the old snapshot keeps serving only until a refresh succeeds, documented latency for other nodes stays the 30 s poll.
- SEC-OI-675-6: migration step forward-only for both engines, backfills `change_version` with the current version for existing rows (never 0 or NULL), index added, `GovernanceMigrationStepParityTests` entry; the column is never exposed by an endpoint.
- Tests: the plan names plus `CatalogSnapshotDeltaSecurityTests.HardDelete_RemovesTableFromSnapshot`, `.WriterWithoutChangeVersion_FailsArchitectureTest`, `.WatermarkReadBeforeRows_NoLostChange`, `.DeltaAndFullReload_ProduceEqualSnapshots`, `.Entries_AreImmutable`, `.LastEventOfBurst_IsNotLost`.

## #676 Incremental search index

**Threats.** A tombstone set that is not applied atomically (old document searchable, or both versions); a merge that loses updates made during the merge; an embedding cache that returns a vector computed for another model or for text that included hidden material; unbounded cache.

**Conditions.**
- SEC-OI-676-1: base, delta and tombstones are published as one immutable snapshot by reference swap; a hidden, inactive or removed table is tombstoned in both BM25 and vector structures in the same update; query-time visibility and scrub (SEC-OI-501-5) stay authoritative and do not rely on the index for hiding.
- SEC-OI-676-2: the background merge builds from a captured snapshot and replays every update that arrived after the capture before the swap (`Update_During_Merge_IsNotLost`); a failed merge keeps base plus delta serving; tombstoned documents are never returned even if the merge fails.
- SEC-OI-676-3: embedding cache key = provider, model id, dimension, SHA-256 of the **scrubbed** text (HIDDEN text removed before hashing); stores vectors only, bounded (LRU, entry and byte cap), cleared when the model identity changes.
- SEC-OI-676-4: BM25 counters (document count, average length, document frequencies) are updated under the same publication as the segments; the property test compares ranking and scores with a full rebuild for random add/replace/remove/hide sequences, including hide and deactivate.
- SEC-OI-676-5: a failure of the delta path falls back to a full rebuild (INV-4) and sets the #501 status; both failing degrades health and keeps the previous index.
- Tests: the plan names plus `CatalogSearchDeltaSecurityTests.TombstoneAndNewDocument_PublishedAtomically`, `.Update_During_Merge_IsNotLost`, `.EmbeddingCache_ModelChange_NotReused`, `.EmbeddingCache_KeyUsesScrubbedText`, `.MergeFailure_TombstonedStaysHidden`.

## #677 Stale-only load and paged reads

**Verified.** The catalog repository has no tenant concept: `GovernanceRepository.Catalog.cs` and the catalog stores contain no tenant key; tenant and visibility filtering happens in callers (`CatalogVisibilityService`, `TenantCatalogLookup`, the snapshot). The plan's "tenant-scoped like every catalog read" and the test `QueryTables_IsTenantScoped` therefore describe a control that does not exist at that layer.

**Threats.** Paged reads that apply visibility after the limit leak existence of hidden tables (short pages, counts, cursor jumps); keyset `After` cursor equal to a hidden identifier; LIKE wildcards in the name prefix (`%`, `_`, `[`); a stale-only load that stamps data with a newer epoch than the data (served as fresh, fail-open); identifier lists from request input without a cap; different responses for hidden vs nonexistent.

**Conditions.**
- SEC-OI-677-1: `QueryTablesAsync`/`GetTablesAsync` are documented and tested as **unfiltered raw reads**; they are Application-layer only, never mapped to an endpoint DTO directly; every migrated caller applies the identical visibility, masking and consent filter it applies today. The tenant claim in the plan test names is dropped or replaced by "caller filter parity".
- SEC-OI-677-2: listing paths filter first, then sort, then cut the page; `Limit` is clamped to 500 server-side, `After` is parsed strictly and equals the last **returned visible** item, never a filtered-out one; the number of hidden rows is never observable (no total count, no short page).
- SEC-OI-677-3: name prefix is a bound parameter with LIKE metacharacters escaped (`ESCAPE` clause on both engines) and the existing identifier case semantics; test with `%`, `_`, `[` and a quote.
- SEC-OI-677-4: stale-only load reads the table epochs **before** the data and stamps each cache entry with that earlier epoch; a concurrent write therefore makes the entry stale, never fresh. Chunks respect the parameter budget.
- SEC-OI-677-5: identifier lists derived from request input are capped (500 per request) and unknown, hidden and forbidden identifiers return the identical response (no existence oracle); the existing behaviour for hidden-by-id is pinned per caller in the parity test.
- SEC-OI-677-6: `GetAllTablesCallerRatchetTests` baseline may only shrink; a caller moved to the snapshot (SCALE-4) must handle "snapshot not ready" as unavailable, not as an empty catalog.
- Tests: the plan names (minus tenant wording) plus `CatalogPagedReadSecurityTests.Page_AppliesVisibilityBeforeLimit`, `.Cursor_NeverEqualsHiddenIdentifier`, `.NamePrefix_LikeMetacharactersEscaped`, `.EpochReadBeforeData_ConcurrentWriteMakesEntryStale`, `.HiddenAndMissingIdentifier_SameResponse`, `.SnapshotNotReady_IsUnavailableNotEmpty`.

## #678 Bounded memory

**Threats.** Eviction followed by a recompute that skips the epoch check; option values that disable the bound or make the TTL very long; a single tenant evicting others (thrash, availability); readiness before the snapshot loads treated as an empty catalog; scale-test artifacts carrying real data.

**Conditions.**
- SEC-OI-678-1: every cache hit and every recompute is validated against the authoritative epoch; the batched epoch read returns an epoch for every requested table, a missing epoch row or a read failure is a miss or an error, never a valid hit; the per-hit epoch read stays (Q-SCALE-3).
- SEC-OI-678-2: options validated with lower **and** upper bounds: `MetadataMaxEntries` 1000..1,000,000, `ProviderMaxEntries` 100..1,000,000, `MetadataTtlSeconds` 1..300; invalid values fail startup naming the key; defaults unchanged; no value logged beyond the key name.
- SEC-OI-678-3: keys of `GovernedCatalogProvider` and `TenantCatalogLookup` include the tenant; eviction is global LRU with a per-tenant share cap (no tenant takes more than half of the entries) so one tenant cannot flush the others.
- SEC-OI-678-4: background loading of snapshot and search index: until loaded, endpoints that need them return an unavailable error (503 or a typed error), never an empty success; readiness stays tied to the GraphQL warm-up only as planned.
- SEC-OI-678-5: `Category=Scale` fixtures use synthetic data only; gcdump files are not uploaded as artifacts; metric and log output carries counts, never table names.
- Tests: the plan names plus `CatalogCacheOptionsValidatorTests.RejectsUpperBoundViolations`, `TableEpochBatchReadTests.MissingEpochRow_IsMissNotHit`, `.ReadFailure_FailsClosed`, `TenantCatalogLookupCacheTests.OneTenantCannotEvictAll`, `CatalogColdStartTests.BeforeSnapshotLoaded_ReturnsUnavailableNotEmpty`.

## Anything the plans missed (summary)

1. Deferral delays restrictive changes in every snapshot-driven surface (SEC-OI-672-2); no repair path when the flush fails (SEC-OI-672-3).
2. No-op comparison against a cached copy (SEC-OI-674-1); projection must cover every written column (SEC-OI-674-3).
3. The delta is only as complete as the least careful writer; no tombstone for hard deletes (SEC-OI-675-1/2).
4. The catalog repository is not tenant-scoped; paging after visibility leaks existence (SEC-OI-677-1/2); stale-only load stamping (SEC-OI-677-4).
5. Cache option bounds missing; one tenant can thrash the cache; cold start as empty catalog (SEC-OI-678-2/3/4).

## New owner questions

| Id | Question | Recommended default |
|---|---|---|
| Q-SEC7-1 | #672: restrictive catalog writes flush the version immediately even inside a write scope (a hidden/deactivate-heavy sync is slower)? | Yes; security over sync speed, additive writes keep the batching win |
| Q-SEC7-2 | #675: full snapshot reload at least every 10 minutes as a delta backstop? | Yes, constant, no option |
| Q-SEC7-3 | #677: keep the unfiltered raw read API internal only, with caller-level filter parity tests instead of a repository tenant filter? | Yes; a tenant dimension in the catalog repository is a separate design |
| Q-SEC7-4 | #678: per-tenant share cap of 50 % of cache entries? | Yes, constant |
