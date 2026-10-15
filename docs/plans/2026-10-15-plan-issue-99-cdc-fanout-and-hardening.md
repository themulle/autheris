# Implementation Plan: REQ-CDC-99 - Governed CDC Fan-Out (Durable Stream, Per-Receiver Authorization) and Change-Tracking Hardening

**Document ID:** `PLAN-CDC-99` (revision 1, 2026-10-15)
**Phase:** 2 (Solution Architect, `csharp-architect`)
**Input:** [PRD REQ-CDC-99](2026-10-15-req-issue-99-cdc-fanout-and-hardening.md) (decisions D-1..D-22, FR-*, SR-1..SR-13, AC-1..AC-14, increments I1..I3)
**Branch:** `feat/issue-99-cdc-fanout`
**Parent Plan:** [00-master-plan-overview.md](00-master-plan-overview.md)
**Status:** PLAN READY - next: Phase 4 increment I1 (after the Phase-3 review below)

**Binding project rules (restated):** no upgrade or repair migrations - the new tables and columns are added in place to the `0001` baseline DDL of SQLite, PostgreSQL and SQL Server (checksum constants unchanged) together with the schema-shape snapshots under `src/Autheris.Infrastructure/Persistence/Migrations/Shape/` and their tests; primary keys come only from the declared table definition (F-GOV-21), never from runtime discovery; least privilege on sources (`CONNECT`, `SELECT`, `VIEW CHANGE TRACKING`); fail closed; CI stays fast (shared container fixtures, per-test databases, no sleeps, `FakeTimeProvider`); Phase-4 TDD developers are Sonnet.

---

## 1. Verification of the PRD Against the Code (`feat/issue-99-cdc-fanout` = `main` `21dfd399` + PRD)

All twelve PRD verification rows (V1..V12) were re-checked. They hold, with the following corrections and additions that change the design:

| # | Finding in code | Consequence for this plan |
|---|---|---|
| P-1 | `MssqlChangeTrackingPoller.GetDataSourceOptions` uses `MssqlChangeTracking:ConnectionString` or **the first** SQL Server data source - the table's catalog `Table.SourceName` is ignored. | The source connection is resolved per tracked table: explicit `DataSource` option, else the catalog's `SourceName` connection; it must be a SQL Server provider, otherwise activation fails with `CDC_TABLE_UNAVAILABLE` (D-A3). |
| P-2 | `TableMetadata.TenantColumnName` is not a separately *declared* field; it is derived from the catalogued column names (`tenant_id` wins, then any spelling of `tenantid`). `TableMetadata.OwnerTenantId` exists. The read path treats a table without a tenant column as shared across tenants unless `DataSources:RequireTenantColumn` is set (`TableMetadata.RequireTenantColumnOrThrow`). | Tenant attribution reuses exactly these read-path rules (D-A6), including the "shared" case; the enforcer must learn a `Shared` tenant scope instead of dropping untenanted events. |
| P-3 | A delete event carries only the key; if the tenant column is not part of the declared PK, the tenant of a deleted row is only known from the last image. | Tenant of a delete = last-image tenant; unknown tenant on a tenanted table means the delete is delivered to nobody (fail closed, documented as part of R-8). The key fingerprint (PRD D-6) therefore cannot be keyed per tenant; it is keyed **per stream** (D-A9). |
| P-4 | `StreamRlsPolicyEnforcer.EvaluateAndMaskAsync` maps every internal exception to `Denied("Policy evaluation error")`; callers cannot distinguish "denied" from "decision input unavailable". | SR-7 (do not advance the offset when a decision input is unavailable) requires a tri-state outcome (`Allowed`/`Denied`/`Unavailable`). The enforcer is refactored into reusable parts (D-B1); the existing method keeps its observable behaviour for the non-stream channel path. |
| P-5 | The enforcer evaluates one payload per event (after-image, or before for deletes). | Move-in/move-out needs two visibility decisions per event (before and after) from **one** resolved access context (consent, Casbin row filter, ReBAC, virtual filters, masking) - D-B1/D-B2. |
| P-6 | `CdcEvent`-bearing `ICdcEventChannel` is also used by PostgreSQL logical replication (F-CDC-03), Debezium and `POST /api/v1/cdc/events` (D-22 keeps them). | The channel keeps carrying `CdcEvent` for those sources. AC-14 is met by **not publishing** MSSQL changes on the channel at all; the wake-up signal uses a separate, data-free contract (`ICdcStreamSignal`, D-A12). GraphQL and webhook consumers read MSSQL tables from the stream and other sources from the channel. |
| P-7 | `ClusterScheduledService<TJob>` supports exactly one fixed `LeaseKey`; leases come from `IDistributedClusterStateProvider.TryAcquireLeaseAsync` with strictly increasing tokens (in-memory provider seeded from wall-clock ms * 1000, Redis provider durable). | A new reusable `ClusterPartitionedService` runs one fenced lease loop per partition key (per tracked table in I1, per durable consumer in I2) with the same lease/fencing semantics (D-A2). |
| P-8 | The fenced watermark upsert of `autheris_cdc_watermarks` (three engines) is already correct and is read by F-DATA-09 (`InMemoryChangeVersionStore` via `IMssqlWatermarkStore.GetWatermarkAsync`). | The watermark table **stays**; its fenced upsert becomes the first statement of the stream append transaction (D-A4). `IMssqlWatermarkStore.TryAdvanceWatermarkAsync` is no longer called by the producer (kept for the interface only until I1 removes it; the read side stays). |
| P-9 | The webhook outbox (`WEBHOOK_OUTBOX`) and `CLOUDEVENT_SUBSCRIPTIONS` live in the governance database, in the same repository classes as the watermarks. | "Offset advance + outbox insert in one transaction" is a single repository method per engine (D-B6). |
| P-10 | `CLOUDEVENT_SUBSCRIPTIONS.filter_table` accepts `*` and plain table names (`CloudEventSubscriptionMatcher`). | Durable webhook consumers require one exact table (D-19 "Table (exact)"); `*` and unqualified ambiguous names are rejected at registration (breaking change, allowed). |
| P-11 | No live group/role resolution for an arbitrary SID exists (`IPrincipalResolverService` is a catalog search, `IdentitySubjectResolver` reads a `ClaimsPrincipal`). | D-17's "live resolution" has no provider today. The identity resolver (D-B4) is an extension point; the shipped implementation is the bounded snapshot (`CreatorSnapshotMaxAgeDays`) for webhooks and the operator-declared groups/roles for plugins. Consent revocation is live regardless (consent store + epoch-invalidated decision cache). See R-4 and the Phase-3 conditions. |
| P-12 | Plugins run in-process in a collectible `PluginAssemblyLoadContext` after SHA-256 verification (`PluginTrustList`); every `IPluginEventListener<T>` is bound to `IGatewayEventBus` by `PluginManager.BindListenerToEventBus`. | `GovernedCdcChange` implements `IGatewayEvent` (PRD D-18) and would therefore be bound to the bus automatically. The binding must skip it and it must never be published on the bus (architecture test, D-C2). AssemblyLoadContext is not a security boundary (Phase-3 condition). |
| P-13 | Masking: HMAC rules are tenant-scoped through `MaskingRule.CreateTenantScopedHmacRule`; `ColumnMaskingProvider` derives per-key-id HMAC keys from one master secret. | The delete-key rendering (D-7) reuses `ScopeRuleForTenant` + `IColumnMaskingProvider.MaskValue`, so a delete key equals what a read returns. |
| P-14 | `KeyVaultWebhookSecretProtector` (AES-256-GCM, `cdc-webhook-kek` via `IKeyVaultSecretProvider`, key = SHA-256(KEK), format `enc:v1:`) has no key id and no rotation. | The stream uses a new envelope scheme with KEK ids and DEK rotation (D-A8); the webhook protector is not changed in this REQ. |
| P-15 | The governance schema-shape comparer tolerates extra tables/columns/indexes; the `binary` type family is mapped for all three engines. | New tables with `VARBINARY(MAX)`/`BYTEA`/`BLOB` payload columns are shape-verifiable without comparer changes. |

No PRD decision is rejected. Deviations, all justified above: key fingerprint keyed per stream (not per tenant, P-3); `gap` is synthesised per consumer at delivery and never stored (it is consumer-specific, D-A5); event id is a deterministic encoding of (stream id, offset) rather than an HMAC (its input contains no key values, D-A10); exact-table webhook filter (P-10).

---

## 2. Architecture Decisions

Decisions are grouped by increment. `D-Ax` = I1, `D-Bx` = I2, `D-Cx` = I3.

### I1 - Producer hardening and durable stream

**D-A1 Stream identity.** One stream per (`source = "mssql_ct"`, domain, schema, table). `stream_id` is a random UUID (32 lowercase hex chars) created on first activation and stable afterwards; the unique index `(source, domain_name, schema_name, table_name)` prevents duplicates. Deactivating a table (removed from `TrackedTables`) sets status `Disabled`; its events and images are purged by the retention job after `RetentionHours` (images immediately, see D-A11).

**D-A2 Producer topology and fencing.** `CdcProducerService : ClusterPartitionedService` runs one lease loop per active table with key `cdc:mssql:poll:{domain}.{schema}.{table}` (lower-case), lease TTL `max(15 s, 3 x interval)`, per-table interval. The lease token is the fencing token for the append transaction. A lost lease cancels the poll (lease `LeaseLost` token). `ClusterPartitionedService` (Application/Hosting) generalises `ClusterScheduledService`: `GetPartitions()` returns `ClusterPartition(Key, ClusterSchedule)`; it starts/stops per-partition loops on change (options reload, catalog change), reuses the existing job metrics with a `partition`-free `job` label (no per-table label - cardinality), and never runs a partition unguarded.

**D-A3 Activation (FR-P2, D-12, D-14, D-16, P-1).** `CdcTableActivator.ActivateAsync` runs at startup, on `IOptionsMonitor` change and on catalog version change (`ICatalogVersionProvider`); each table independently. Checks, in order, each with a stable code; the first failure wins, is logged once per streak (code only), stored in `autheris_cdc_streams.activation_code` and counted in `autheris_cdc_stream_activation_failed{code}`:

| Order | Check | Code |
|---|---|---|
| 1 | Catalog metadata exists, `IsActive`, dialect SQL Server, source connection resolvable (P-1) | `CDC_TABLE_UNAVAILABLE` |
| 2 | Declared `PrimaryKeyColumns` non-empty; every PK and catalogued column passes `ValidateSqlIdentifier` | `CDC_NO_DECLARED_PK` |
| 3 | Tenant rule (D-A6): `RequireTenantColumn` and no tenant column and not exempt | `CDC_TENANT_COLUMN_MISSING` |
| 4 | `SELECT CHANGE_TRACKING_MIN_VALID_VERSION(OBJECT_ID(@t))` is not NULL | `CDC_TRACKING_DISABLED` |
| 5 | Key columns of `SELECT TOP (0) * FROM CHANGETABLE(CHANGES [s].[t], @minValid) AS CT` (all result columns except `SYS_CHANGE_*`), compared ordinal-ignore-case **as an ordered list** with the declared PK | `CDC_PK_MISMATCH` |
| 6 | Snapshot probe: `SET TRANSACTION ISOLATION LEVEL SNAPSHOT; BEGIN TRAN; SELECT TOP (0) 1 FROM [s].[t]; COMMIT;` - error 3952 means snapshot isolation is off (no `sys.databases` read needed). Skipped when `AllowNonSnapshotRead = true` | `CDC_SNAPSHOT_ISOLATION_OFF` |
| 7 | Stream keys available (D-A8): payload, image and fingerprint DEKs can be created/unwrapped | `CDC_KEY_UNAVAILABLE` |

Activation needs only `SELECT` + `VIEW CHANGE TRACKING`. The `sys.indexes` query and `_pkCache` are deleted; the `ArchitectureNoSourceKeyCatalogQueriesTests` allowlist entry for `Autheris.Extensions/Cdc/MssqlChangeTrackingPoller.cs` is removed (the `ExactAllowlist` fact then lists only `SqlProcedureRowScopeResolver.cs`), and a new positive-control fact asserts that `Autheris.Extensions/Cdc/**` is in the scanned set. Transient errors during activation (connection) are retried with the existing `CdcPollFailureTracker` backoff and are **not** activation codes.

**D-A4 Poll and append (FR-P4, FR-P5, D-4, D-14, D-15).** One poll of one table:

1. Source, one connection, one transaction at `SNAPSHOT` (or `READ COMMITTED` when `AllowNonSnapshotRead`): read `CHANGE_TRACKING_MIN_VALID_VERSION(OBJECT_ID(@t))` and `CHANGE_TRACKING_CURRENT_VERSION()` **inside** the transaction; compare with the stored watermark; read the batch with an **explicit, validated column list** (`CT.[pk..] AS [PK_..]`, `T.[col..]` for the catalogued columns only - never `T.*`), `TOP (@batchSize)`, `ORDER BY CT.SYS_CHANGE_VERSION`, command timeout = `QueryTimeoutSeconds`. Batch-boundary rule unchanged (only complete change versions; a one-version overflow re-reads that version completely). Commit the read-only transaction.
2. Build events (`CdcChangeEventBuilder`): fingerprint each key (D-A9), load last images for the batch's fingerprints in one call, set `before` = image or `beforeUnknown`, `after` = row (catalogued columns, null for delete), tenant (D-A6), serialize with `CdcPayloadCodec` (D-A7), encrypt (D-A8) with AAD bound to (stream id, offset, kind, tenant).
3. Governance DB, **one transaction** (`ICdcStreamStore.AppendAsync`): (a) the existing fenced watermark upsert of `autheris_cdc_watermarks` (0 rows = fenced out -> rollback, `FencingTokenRejectedException`); (b) `UPDATE autheris_cdc_streams SET head_offset = @newHead, producer_fencing_token = @token ... WHERE stream_id = @id AND head_offset = @expectedHead AND producer_fencing_token <= @token` (0 rows -> rollback, `CdcStreamConflictException`, re-read head next cycle); (c) insert the events with offsets `expectedHead + 1 .. newHead` (PK `(stream_id, event_offset)` makes a duplicate impossible); (d) upsert/delete row images. Offsets are assigned by the producer **before** encryption (they are part of the AAD) and verified by (b), so offsets are dense and immutable (FR-S1).
4. Signal `ICdcStreamSignal.NotifyAdvanced(streamId, newHead)` after commit (best effort, data-free).

A crash before 3 commits re-reads the same versions (nothing written); after commit nothing is produced twice (AC-6). An empty batch writes nothing (the watermark is not touched; `last_polled_ms` is updated at most every 60 s to bound write load).

**D-A5 Control events (D-10, D-11).** Stored kinds: `Change`, `WatermarkExpired`, `Resync`. `Gap` is **not stored**: it is synthesised per consumer when its next offset is below the stream tail (D-B3). Expired watermark (`stored < minValid`), policy `Restart` (default): one append transaction writes a `WatermarkExpired` event (payload: stored version, min valid version, current version - no row data) and the watermark `CHANGE_TRACKING_CURRENT_VERSION()` read in the same snapshot; metric `autheris_cdc_watermark_expired_total` kept. Policy `Halt`: status `Halted`, activation code `CDC_WATERMARK_EXPIRED`, no polling until `POST /api/v1/cdc/streams/{id}/resync` (admin, audited) which performs the `Restart` append with a `Resync` event. The `CdcWatermarkExpiredException` path and the manual-SQL runbook are removed.

**D-A6 Tenant attribution (D-16, P-2, P-3).** Read-path rules: tenant column = `TableMetadata.TenantColumnName`; if present, `TenantScope = Row` and the tenant is the row's value (after-image, else last image for deletes, else the PK value when the tenant column is part of the PK). A tenanted row whose tenant value is null/empty, or a delete whose tenant cannot be determined, gets `TenantScope = Unresolved` and is delivered to nobody (counted `autheris_cdc_stream_unresolved_tenant_total`). Without a tenant column: if `RequireTenantColumn` and not exempt -> activation fails (D-A3 #3); otherwise `TenantScope = Owner` with `OwnerTenantId` when set, else `TenantScope = Shared` (deliverable to any tenant's receiver that passes its own tenant-scoped consent - identical to the read path). The tenant id and scope are stored in clear (needed for cheap per-tenant skipping at delivery; tenant ids are not row data) and bound into the AAD.

**D-A7 Payload codec.** `CdcPayloadCodec` serialises `CdcChangePayload` (before, after, key, beforeUnknown) to UTF-8 JSON with type tags so that row-filter evaluation and masking see the same CLR types as a live read: `{"c":{"Amount":{"t":"dec","v":"12.30"},...}}` with tags `s, i64, i32, i16, u8, dec, f64, f32, bool, dt, dto, date, time, guid, bin (base64), null`. Unknown CLR types fail the production of that batch (`CDC_UNSUPPORTED_TYPE`, no partial write). Size limit per event `MaxEventPayloadBytes` (default 256 KiB, mirrors webhook bounds) - an oversize row produces a `Change` event with `payloadOversize = true` and no values (consumers dead-letter it with `bounds`, SR-13: never truncation).

**D-A8 Encryption at rest and key management (D-2, R-3).** Envelope encryption reusing `IKeyVaultSecretProvider`:

- **KEKs**: configured list `Gateway:CdcStream:Encryption:Keks` = `[{ "Id": "k1", "SecretRef": "cdc-stream-kek-k1" }]` plus `ActiveKekId`; each secret >= 32 bytes; key = HKDF-SHA256(secret, info `"autheris-cdc-kek-v1"`). Resolved by `KeyVaultCdcKekProvider` (Infrastructure), cached per process, never logged. Development without configuration generates an ephemeral random KEK per process and logs a warning once (the stream becomes unreadable after a restart and fails closed - documented). Outside Development a missing/short active KEK fails activation (`CDC_KEY_UNAVAILABLE`) and a startup check logs it (pattern of `CdcWebhookStartupCheck`).
- **DEKs** (`autheris_cdc_stream_keys`): random 256-bit keys per stream and purpose, wrapped with AES-256-GCM by a KEK (AAD = `"cdc-dek|{streamId}|{purpose}|{dekId}"`), stored with `kek_id`. Purposes: `payload` (rotated every `DekRotationHours`, default 24; events reference their `dek_id`), `image` (long-lived; rotated only by `POST /api/v1/cdc/streams/{id}/rotate-image-key`, which re-encrypts images in batches), `fingerprint` (HMAC key of D-A9; never rotated - rotation would orphan the images; a resync replaces it).
- **Payload**: AES-256-GCM, 96-bit random nonce, 128-bit tag, stored `nonce | tag | ciphertext` in the `payload` column; AAD = `"cdc-evt|v1|{streamId}|{offset}|{kind}|{tenantScope}|{tenantId}"` (images: `"cdc-img|v1|{streamId}|{fingerprintHex}"`). Moving a payload to another offset/stream/tenant fails authentication.
- **Crypto-shredding**: the retention job deletes a `payload` DEK once no retained event references it, so expired events are unrecoverable even from backups taken after the purge (R-3, PRD R-2).
- **KEK rotation**: add the new KEK to `Keks`, set `ActiveKekId`; the retention job re-wraps DEKs whose `kek_id` differs from the active one (small table); `GET /api/v1/cdc/streams` reports the per-KEK wrapped-key count so the operator knows when the old secret can be removed. No re-encryption of payloads is needed.
- **KEK loss**: unwrap fails -> producer activation `CDC_KEY_UNAVAILABLE`; consumers fail closed (offset not advanced, state `failed` after `MaxConsecutiveFailures`). Recovery: `POST /api/v1/cdc/streams/{id}/resync?discardUnreadable=true` (admin, audited) deletes the unreadable events, images and DEKs, writes a `Resync` event, restarts at the current version. Consumers then see a gap (D-B3).
- Unwrapped DEKs are cached in a bounded per-process cache (`MaxCachedDeks`, default 256, LRU) and zeroed on eviction.

**D-A9 Key fingerprint (D-6, P-3).** `HMAC-SHA256(fingerprintDek(stream), canonical(PK values))` where canonical = codec-tagged values joined with length prefixes (no ambiguity between `("a|b")` and `("a","b")`). 32 bytes stored as `BINARY(32)`/`BYTEA`/`BLOB`. Never leaves the producer/store; never in events, logs or metrics.

**D-A10 Event id (D-5, AC-1).** `CdcEventId.Format(streamId, offset)` = `"evt_" + streamId + "_" + offset` (lower-case hex UUID, decimal offset; max 58 chars, fits `WEBHOOK_OUTBOX.event_id NVARCHAR(128)`). Deterministic, identical on all receivers and redeliveries, contains no key value. `CdcWebhookEventId` (tenant-hashed native id) stays for the non-MSSQL channel path only; the native `{table}_{version}_{pk}` id is no longer produced.

**D-A11 Retention (D-9, FR-S2).** `CdcStreamRetentionService : ClusterScheduledService<CdcStreamRetentionJob>`, lease `cdc:stream:retention`, interval `RetentionSweepSeconds` (default 300). Per stream: `newTail = max(tail, first offset with produced_ms >= now - RetentionHours, head - RetentionMaxEvents + 1)`; delete events `< newTail` in bounded chunks (`RetentionDeleteBatch`, default 5,000, one short transaction each, fenced by the job token through `autheris_cdc_streams.retention_fencing_token`), then set `tail_offset`; shred unreferenced payload DEKs; re-wrap DEKs under the active KEK; purge images and DEKs of `Disabled` streams. Retention never waits for consumers. Readers never see a torn range: `ReadAsync` returns `TailOffset` read in the same statement/transaction as the rows, and a consumer whose requested offset is below the returned tail receives a gap (D-B3).

**D-A12 Wake-up signal (FR-P7, AC-14).** `ICdcStreamSignal` publishes `CdcStreamAdvanced(StreamId, HeadOffset)` through `IDistributedClusterStateProvider.PublishEventAsync("cdc:stream:advanced", ...)` (node-local with the in-memory provider, Redis Pub/Sub when cluster-wide). It carries no row data, no tenant and no table name. Tail readers also poll `autheris_cdc_streams.head_offset` every `TailPollIntervalMilliseconds` (default 1,000) so a lost signal only adds latency. The MSSQL poller no longer references `ICdcEventChannel`.

**D-A13 I1 compatibility feed.** Until I2 lands, `CdcStreamTailHub` (node-local singleton, one reader per stream with at least one local listener, starting at the head) decrypts events once and hands `CdcStreamDelivery` (decoded `CdcEvent` with opaque id, tenant scope, offset) to (a) `Subscription.SubscribeToTableEventsAsync` for MSSQL-tracked tables and (b) `CdcWebhookDeliveryService` - the latter only on the replica holding lease `cdc:webhook:compat` (outbox dedupe on `event_id` covers a takeover). Semantics stay at-most-once exactly as today (no regression), but with opaque ids and no Redis data path. I2 replaces (b) completely and upgrades (a).

**D-A14 Admin endpoints (FR-S3, FR-S4).** `GET /api/v1/cdc/streams` (status, activation code, head, tail, watermark, last poll, source lag = current version - watermark when known, per-KEK key counts; no payload, no fingerprints), `POST /api/v1/cdc/streams/{id}/resync[?discardUnreadable=true]`, `POST /api/v1/cdc/streams/{id}/rotate-image-key`. Authorization: global governance administrator (same policy as `cdc_all`); audited individually (`CDC_STREAM_RESYNC`, `CDC_STREAM_KEY_ROTATED`).

### I2 - Durable consumers, webhooks and GraphQL on the stream

**D-B1 Enforcer refactoring (P-4, P-5).** `StreamRlsPolicyEnforcer` is split without changing the decision chain:
- `StreamAccessContextResolver.ResolveAsync(table, tenantScope, tenantId, principal, ct)` -> `StreamAccessContextResult` = `Allowed(StreamAccessContext)` | `Denied(code)` | `Unavailable(code)`. It performs tenant match (Row/Owner: event tenant == principal tenant; Shared: principal tenant used for consent; Unresolved: Denied), metadata, consent (cache + epoch), virtual filters (applying filter = `Denied("virtual_filter")`, existing behaviour), Casbin gate + row filter, ReBAC. Any exception -> `Unavailable`. Resolved **once per (receiver, event)**.
- `StreamAccessContext.IsRowVisible(IReadOnlyDictionary<string, object?> row)` - `StreamingRowFilterAstEvaluator` on the combined row filter (three-valued logic, unknown = invisible).
- `StreamAccessContext.Project(row, projection)` - column access per column (Deny drops, Mask via tenant-scoped rule, Clear), intersected with the receiver projection.
- `EvaluateAndMaskAsync` is re-implemented on these parts and keeps its exact observable behaviour (existing tests stay green unchanged).

**D-B2 Transition matrix (D-3, D-7, SR-4).** `CdcTransitionEvaluator.Evaluate(op, beforeState, afterVisible, hasRowFilter)` - pure, mutation-tested. `beforeState` in {Visible, Invisible, Unknown}.

| Source op | before | after visible | Delivered as |
|---|---|---|---|
| Insert | any | yes / no | Insert / nothing |
| Update | Visible | yes / no | Update / Delete (key rendered) |
| Update | Invisible | yes / no | Insert / nothing |
| Update | Unknown | yes / no | Update with `beforeUnknown = true` / nothing |
| Delete | Visible | - | Delete (key rendered) |
| Delete | Invisible | - | nothing |
| Delete | Unknown | - | Delete only if the receiver has **no** row filter (table-level visibility already proven), else nothing |

Masked `before` is included only when before is Visible (an Invisible before is never sent). Delete key rendering (`CdcDeleteKeyRenderer`): per declared PK column, Clear -> value; Mask with an HMAC rule -> the tenant-scoped HMAC a read returns; any other mask or Deny -> omitted and `keyRedacted = true`. Deletes carry no other columns.

**D-B3 Consumer model (FR-C2, D-8, D-10, D-20).** A durable consumer = registry row + per-stream offset row (`committed_offset` = last fully handled offset). `CdcConsumerService : ClusterPartitionedService` runs one fenced loop per `active` consumer (lease `cdc:consumer:{consumerId}`). Each cycle: read offset; `ReadAsync(stream, from = committed + 1, max = MaxInFlight)`; if `from < tail`, deliver one synthesised `Gap` (`fromOffset = from`, `resumeOffset = tail`, reason `retention`), audit it, count `autheris_cdc_consumer_gaps_total`, and commit `tail - 1` with the gap; per event: tenant prefilter (skip without decrypt when the tenant can never match the consumer's identity tenant, except `Shared`), decrypt, authorize (D-B1/D-B2), filter (operations after transition, projection), hand to the receiver handler (`ICdcReceiverHandler`), then commit the offset (fenced, `committed_offset = max(...)`). Start position at registration: `Latest` (committed = head at registration), `Earliest` (committed = tail - 1), `FromOffset(n)` (committed = n - 1; below tail -> gap on first read).
- **Outcomes per event**: `Delivered`, `Skipped(reason)` (denied, filtered, not visible - offset advances, counted `denied_total`/audited in batch), `Unavailable(code)` (decision input, KEK, decryption, catalog - **offset does not advance**, backoff with jitter up to 5 min, `failures_total{reason}`; after `MaxConsecutiveFailures` (default 20) state `failed` with code; never skipped), `HandlerFailed` (retry with backoff; after `MaxAttempts` (default 5) dead letter (reference only: consumer, stream, offset, attempts, error code) and advance).
- **Backpressure**: at most `MaxInFlight` events per cycle; webhook consumers additionally pause while pending outbox rows of their subscription >= `MaxPendingOutbox` (default `MaxInFlight`).
- **Caps**: `MaxConsumersPerStream` (default 100) enforced at registration (409), existing `MaxSubscriptionsPerTenant` kept.

**D-B4 Receiver identity (D-17, SR-1, P-11).** `ICdcReceiverIdentityResolver.ResolveAsync(CdcReceiverIdentity, ct)` -> `ClaimsPrincipal?` (null = deny-all, SR-1). Shipped implementation: principal from the stored identity (SID, tenant from the consumer row - never from the snapshot, groups, roles) with `CreatorSnapshotMaxAgeDays` expiry for webhooks; operator-declared identities for plugins (no expiry, configuration is authoritative). Webhook registration takes an optional `identitySid`; default = caller; a different SID requires the caller to be a tenant governance administrator (`ITableAccessResolver`/role check as for subscriptions today) and captures that identity's groups/roles **only** from an existing principal snapshot API - since none exists, a foreign identity can only be a configured service account (`Gateway:CdcWebhooks:ServiceAccounts[]` with SID, groups, roles). Unknown -> 400 (same text as unknown table).

**D-B5 Registration checks (SR-2, D-19).** Exact table (P-10) resolved through the catalog; identity must have a table consent **now** (`StreamAccessContextResolver` with an empty probe row; Denied/Unavailable -> 400 "unknown table" text / 503); projection columns must be readable (`Clear` or `Mask`) for the identity, otherwise 400 with the same text as an unknown column (no existence oracle). Registration, rejection, deletion, pause/resume, offset reset and dead-letter replay are audited individually.

**D-B6 Webhook receivers (FR-C6, SR-3).** `CLOUDEVENT_SUBSCRIPTIONS` gains `identity_sid`, `start_position`, `start_offset`, `columns_json`; each subscription has a consumer row `consumer_id = "webhook:" + id`. `CdcWebhookReceiverHandler` builds the masked CloudEvent (`id` = D-A10 event id, `autherisoffset`/`autheriskind` extension attributes, data = transition result) and calls `ICdcConsumerStore.CommitWithOutboxAsync(consumerId, streamId, newOffset, fencingToken, messages)` - outbox inserts (dedupe on `(source, subscription_id, event_id)`) and offset advance in **one** transaction. `CdcWebhookDeliveryService` stops consuming MSSQL events (channel path for other sources stays). Send-time check (SR-3): `WebhookDeliveryHostedService`, for outbox rows of source CloudEvent-CDC, resolves the subscription and asks `ICdcDeliveryGate.IsTablePermittedAsync(subscription)` (identity + table-level consent, cached per batch for at most 5 s); denied -> row dead-lettered with `revoked` (not sent), Unavailable -> rescheduled (fail closed, not sent). HMAC signing, retries, circuit pause unchanged.

**D-B7 GraphQL (D-21, FR-C8).** `CdcStreamTailHub` (from D-A13) becomes the shared per-stream, per-node reader: one DB read per stream and poll/signal regardless of subscriber count; each local subscriber has a bounded channel (`SubscriberBufferSize`, default 1,000, `BoundedChannelFullMode.Wait` is **not** used - a full buffer completes the subscription with GraphQL error code `CDC_RESYNC_REQUIRED` carrying the last delivered offset). Optional argument `fromOffset: Long` starts a private catch-up reader for that subscriber up to the hub position, then joins the hub (no duplicate, no hole: the join point is the hub offset captured under the hub lock). Payload type `StreamCdcEvent` gains `offset`, `kind` (`CHANGE`, `GAP`, `WATERMARK_EXPIRED`, `RESYNC`), `keyRedacted`, `beforeUnknown`, `beforeJson`; `eventId` = D-A10. Authorization per subscriber per event through D-B1/D-B2 with the live session principal (existing revocation watch unchanged). The 50-event history replay and `before_subscription` filtering are removed for stream-backed tables. `cdc_all` (global admin) attaches to all active streams.

**D-B8 Audit (SR-10, AC-12).** `CdcDeliveryAuditBatcher` (generalises the existing per-subscription batch of `CdcWebhookDeliveryService`): per (tenant, consumer) one Tier-B event `CDC_DELIVERY_BATCH` per `AuditBatchSeconds` with delivered/denied/revoked/dead-lettered/gaps counts, identity SID, table, offset range - never payload, key or fingerprint. Individually audited: `CDC_CONSUMER_REGISTERED`, `CDC_CONSUMER_REJECTED`, `CDC_CONSUMER_DELETED`, `CDC_CONSUMER_PAUSED`, `CDC_CONSUMER_RESUMED`, `CDC_CONSUMER_OFFSET_RESET`, `CDC_DEAD_LETTER_REPLAYED`, `CDC_STREAM_RESYNC`, `CDC_STREAM_KEY_ROTATED`, `CDC_PLUGIN_RECEIVER_BOUND`, `CDC_PLUGIN_RECEIVER_REJECTED`.

**D-B9 Metrics (FR-C4).** Label `consumer` = consumer id (bounded by `MaxConsumersPerStream` x streams; ids are server-generated, never tenant/table names), `reason` from a fixed enum: `autheris_cdc_consumer_lag_events`, `autheris_cdc_consumer_oldest_unprocessed_age_seconds`, `autheris_cdc_consumer_failures_total{reason}`, `autheris_cdc_consumer_dead_letters_total`, `autheris_cdc_consumer_gaps_total`, `autheris_cdc_consumer_delivered_total`, `autheris_cdc_consumer_denied_total`. Stream metrics (I1, label `stream` = stream id): `autheris_cdc_stream_head_offset`, `autheris_cdc_stream_events_appended_total`, `autheris_cdc_stream_append_duration_seconds`, `autheris_cdc_stream_purged_total`, `autheris_cdc_stream_activation_failed{code}`, `autheris_cdc_stream_source_lag_versions`.

**D-B10 Admin endpoints (FR-C5).** `GET /api/v1/cdc/consumers` (tenant-scoped for tenant admins, all for global admins: id, kind, table, state, failure code, committed offset, lag), `POST /api/v1/cdc/consumers/{id}/pause|resume`, `POST /api/v1/cdc/consumers/{id}/offset` (`{ "position": "Latest|Earliest|FromOffset", "offset": n }`, fenced by acquiring the consumer lease), `GET /api/v1/cdc/consumers/{id}/dead-letters`, `POST /api/v1/cdc/consumers/{id}/dead-letters/{offset}/replay` (re-authorises as of now).

### I3 - C# DLL plugin receivers

**D-C1 Contract (D-18).** `GovernedCdcChange : IGatewayEvent` in `Autheris.Domain/Events` (plugin-facing): `EventId`, `EventType = "autheris.cdc.change"`, `Timestamp`, `TenantId`, `CorrelationId` (null), `Table` (qualified name string), `Operation` (`Insert|Update|Delete|None` for control kinds), `Kind`, `StreamOffset`, `Before`/`After` (`IReadOnlyDictionary<string, object?>?`, fresh immutable copies per call), `KeyRedacted`, `BeforeUnknown`, control fields `GapFromOffset`, `GapResumeOffset`. The internal `CdcEvent` is never passed to plugins.

**D-C2 Binding and isolation (D-17, SR-12, P-12).** `Gateway:Plugins:CdcReceivers[]` (operator configuration, validated by `IValidateOptions`): `Name` (consumer id `plugin:{Name}`), `PluginFile` (must be a `TrustedPluginHashes` key), `ListenerType` (full type name implementing `IPluginEventListener<GovernedCdcChange>` in that file), `TenantId`, `IdentitySid`, `IdentityGroups[]`, `IdentityRoles[]`, `Table` (exact), `Operations[]`, `Columns[]`, `StartPosition`, `StartOffset`, `MaxInFlight`, `HandlerTimeoutSeconds` (default 30), `MaxAttempts`. `PluginManager.BindListenerToEventBus` skips `IPluginEventListener<GovernedCdcChange>`; `IPluginManager.TryGetCdcListener(pluginFile, typeName, out listener)` returns only listeners loaded from that verified file. A listener without a configuration entry is never bound; an entry whose listener is missing, whose file is not trust-listed or whose identity fails D-B5 is rejected and audited (`CDC_PLUGIN_RECEIVER_REJECTED`). Plugin unload stops the consumer loop; offsets persist.

**D-C3 Dispatcher (FR-C7, AC-10).** `PluginCdcReceiverHandler : ICdcReceiverHandler` invokes the listener per event with a linked CTS (`HandlerTimeoutSeconds`) and `Task.WaitAsync(timeout)`; exceptions and timeouts are `HandlerFailed` (D-B3 retries, then dead letter). A non-cooperative handler that ignores cancellation keeps running in the background; at most `MaxAbandonedHandlers` (default 2) per receiver may be outstanding, beyond that the consumer goes to `failed` (`plugin_unresponsive`) - the gateway never blocks on it. Handlers run on the thread pool, never on the producer, and each receiver has its own loop, so one slow plugin cannot delay others. Exception messages and stack traces of plugin code are not logged above Debug (type name only).

---

## 3. Contracts (fixed up front)

### 3.1 Governance schema (baseline `0001`, in place, all three engines)

SQL Server shown; PostgreSQL uses `VARCHAR`/`BIGINT`/`INTEGER`/`BYTEA`, SQLite `TEXT`/`INTEGER`/`BLOB`, same names, keys and indexes. Created with the engine's existing idempotent guards (`IF OBJECT_ID(...) IS NULL` / `CREATE TABLE IF NOT EXISTS`). `0001` checksum constants are unchanged.

```sql
-- I1
CREATE TABLE dbo.autheris_cdc_streams (
    stream_id NVARCHAR(32) NOT NULL PRIMARY KEY,
    source NVARCHAR(32) NOT NULL,
    domain_name NVARCHAR(128) NOT NULL,
    schema_name NVARCHAR(128) NOT NULL,
    table_name NVARCHAR(128) NOT NULL,
    status INT NOT NULL,                     -- CdcStreamStatus
    activation_code NVARCHAR(64) NULL,
    head_offset BIGINT NOT NULL,             -- 0 = empty
    tail_offset BIGINT NOT NULL,             -- lowest retained offset; head + 1 when empty
    producer_fencing_token BIGINT NOT NULL,
    retention_fencing_token BIGINT NOT NULL,
    last_polled_ms BIGINT NULL,
    created_ms BIGINT NOT NULL,
    updated_ms BIGINT NOT NULL
);
CREATE UNIQUE INDEX ux_cdc_streams_table ON dbo.autheris_cdc_streams (source, domain_name, schema_name, table_name);

CREATE TABLE dbo.autheris_cdc_stream_events (
    stream_id NVARCHAR(32) NOT NULL,
    event_offset BIGINT NOT NULL,
    change_version BIGINT NOT NULL,
    kind INT NOT NULL,                       -- CdcStreamEventKind
    operation INT NULL,                      -- CdcOperation (Change only)
    tenant_scope INT NOT NULL,               -- CdcTenantScope
    tenant_id NVARCHAR(128) NULL,
    dek_id NVARCHAR(32) NOT NULL,
    payload VARBINARY(MAX) NOT NULL,         -- nonce | tag | ciphertext
    produced_ms BIGINT NOT NULL,
    CONSTRAINT PK_autheris_cdc_stream_events PRIMARY KEY (stream_id, event_offset)
);
CREATE INDEX ix_cdc_stream_events_produced ON dbo.autheris_cdc_stream_events (stream_id, produced_ms);
CREATE INDEX ix_cdc_stream_events_dek ON dbo.autheris_cdc_stream_events (stream_id, dek_id);

CREATE TABLE dbo.autheris_cdc_row_images (
    stream_id NVARCHAR(32) NOT NULL,
    key_fingerprint VARBINARY(32) NOT NULL,
    tenant_scope INT NOT NULL,
    tenant_id NVARCHAR(128) NULL,
    dek_id NVARCHAR(32) NOT NULL,
    image VARBINARY(MAX) NOT NULL,
    event_offset BIGINT NOT NULL,
    updated_ms BIGINT NOT NULL,
    CONSTRAINT PK_autheris_cdc_row_images PRIMARY KEY (stream_id, key_fingerprint)
);

CREATE TABLE dbo.autheris_cdc_stream_keys (
    stream_id NVARCHAR(32) NOT NULL,
    dek_id NVARCHAR(32) NOT NULL,
    purpose INT NOT NULL,                    -- CdcKeyPurpose
    kek_id NVARCHAR(64) NOT NULL,
    wrapped_key VARBINARY(256) NOT NULL,     -- nonce | tag | ciphertext
    created_ms BIGINT NOT NULL,
    retired_ms BIGINT NULL,
    CONSTRAINT PK_autheris_cdc_stream_keys PRIMARY KEY (stream_id, dek_id)
);
CREATE INDEX ix_cdc_stream_keys_kek ON dbo.autheris_cdc_stream_keys (kek_id);

-- I2
CREATE TABLE dbo.autheris_cdc_consumers (
    consumer_id NVARCHAR(160) NOT NULL PRIMARY KEY,   -- "webhook:{id}" | "plugin:{name}"
    kind INT NOT NULL,                                -- CdcConsumerKind
    tenant_id NVARCHAR(128) NOT NULL,
    stream_id NVARCHAR(32) NOT NULL,
    identity_sid NVARCHAR(256) NOT NULL,
    identity_groups_json NVARCHAR(MAX) NULL,
    identity_roles_json NVARCHAR(MAX) NULL,
    identity_captured_ms BIGINT NULL,
    filter_operations NVARCHAR(64) NULL,
    columns_json NVARCHAR(MAX) NULL,
    start_position INT NOT NULL,
    start_offset BIGINT NULL,
    state INT NOT NULL,                               -- CdcConsumerState
    failure_code NVARCHAR(64) NULL,
    max_in_flight INT NOT NULL,
    created_by_sid NVARCHAR(256) NULL,
    created_ms BIGINT NOT NULL,
    updated_ms BIGINT NOT NULL
);
CREATE INDEX ix_cdc_consumers_stream ON dbo.autheris_cdc_consumers (stream_id, state);
CREATE INDEX ix_cdc_consumers_tenant ON dbo.autheris_cdc_consumers (tenant_id);

CREATE TABLE dbo.autheris_cdc_consumer_offsets (
    consumer_id NVARCHAR(160) NOT NULL,
    stream_id NVARCHAR(32) NOT NULL,
    committed_offset BIGINT NOT NULL,
    fencing_token BIGINT NOT NULL,
    updated_ms BIGINT NOT NULL,
    CONSTRAINT PK_autheris_cdc_consumer_offsets PRIMARY KEY (consumer_id, stream_id)
);

CREATE TABLE dbo.autheris_cdc_dead_letters (
    consumer_id NVARCHAR(160) NOT NULL,
    stream_id NVARCHAR(32) NOT NULL,
    event_offset BIGINT NOT NULL,
    attempts INT NOT NULL,
    last_error_code NVARCHAR(64) NOT NULL,
    created_ms BIGINT NOT NULL,
    CONSTRAINT PK_autheris_cdc_dead_letters PRIMARY KEY (consumer_id, stream_id, event_offset)
);

-- I2: CLOUDEVENT_SUBSCRIPTIONS (columns added in place to the CREATE TABLE)
--   identity_sid NVARCHAR(256) NULL, start_position INT NULL, start_offset BIGINT NULL, columns_json NVARCHAR(MAX) NULL
```

Each increment updates the three `governance-shape.*.json` snapshots (regenerated with the existing snapshot generator/test), `GovernanceSchemaShapeSnapshotTests` and the PostgreSQL/SQL Server shape integration tests (fresh database passes, AC-13).

### 3.2 Domain (`src/Autheris.Domain/Model/CdcStreamModels.cs`, WP-I1-0)

```csharp
namespace Autheris.Domain.Model;

public enum CdcStreamStatus { Active = 1, ActivationFailed = 2, Halted = 3, Disabled = 4 }
public enum CdcStreamEventKind { Change = 1, WatermarkExpired = 2, Resync = 3, Gap = 4 /* synthesised, never stored */ }
public enum CdcTenantScope { Row = 1, Owner = 2, Shared = 3, Unresolved = 4 }
public enum CdcKeyPurpose { Payload = 1, Image = 2, Fingerprint = 3 }
public enum CdcOnWatermarkExpired { Restart = 1, Halt = 2 }
public enum CdcStartPosition { Latest = 1, Earliest = 2, FromOffset = 3 }

public static class CdcActivationCodes
{
    public const string TableUnavailable = "CDC_TABLE_UNAVAILABLE";
    public const string NoDeclaredPk = "CDC_NO_DECLARED_PK";
    public const string PkMismatch = "CDC_PK_MISMATCH";
    public const string TrackingDisabled = "CDC_TRACKING_DISABLED";
    public const string SnapshotIsolationOff = "CDC_SNAPSHOT_ISOLATION_OFF";
    public const string TenantColumnMissing = "CDC_TENANT_COLUMN_MISSING";
    public const string KeyUnavailable = "CDC_KEY_UNAVAILABLE";
    public const string WatermarkExpired = "CDC_WATERMARK_EXPIRED";
    public const string UnsupportedType = "CDC_UNSUPPORTED_TYPE";
}

public readonly record struct CdcStreamId(string Value);   // 32 lower-case hex chars, validated in the factory

public sealed record CdcStreamDescriptor(
    CdcStreamId Id, string Source, TableIdentifier Table, CdcStreamStatus Status, string? ActivationCode,
    long HeadOffset, long TailOffset, long? SourceWatermark, DateTimeOffset? LastPolledAt);

public sealed record CdcProtectedBlob(string DekId, byte[] Data);   // Data = nonce | tag | ciphertext

public sealed record CdcStreamEventRecord(
    CdcStreamId StreamId, long Offset, long ChangeVersion, CdcStreamEventKind Kind, CdcOperation? Operation,
    CdcTenantScope TenantScope, string? TenantId, CdcProtectedBlob Payload, DateTimeOffset ProducedAt);

public sealed record CdcRowImageWrite(byte[] KeyFingerprint, CdcTenantScope TenantScope, string? TenantId,
    CdcProtectedBlob? Image /* null = delete */, long Offset);

public sealed record CdcRowImageRecord(byte[] KeyFingerprint, CdcTenantScope TenantScope, string? TenantId, CdcProtectedBlob Image, long Offset);

public sealed record CdcAppendBatch(
    CdcStreamId StreamId, string Source, TableIdentifier Table, long FencingToken, long ExpectedHeadOffset,
    long NewWatermark, IReadOnlyList<CdcStreamEventRecord> Events, IReadOnlyList<CdcRowImageWrite> ImageWrites);

public sealed record CdcReadResult(long TailOffset, long HeadOffset, IReadOnlyList<CdcStreamEventRecord> Events);

/// Plaintext of a Change event (decrypted only in memory, per event).
public sealed record CdcChangePayload(
    IReadOnlyDictionary<string, object?> Key, IReadOnlyDictionary<string, object?>? Before,
    IReadOnlyDictionary<string, object?>? After, bool BeforeUnknown, bool PayloadOversize);

/// Plaintext of a control event: never row data.
public sealed record CdcControlPayload(string Reason, long? StoredVersion, long? MinValidVersion, long? CurrentVersion);

public sealed record CdcWrappedKey(CdcStreamId StreamId, string DekId, CdcKeyPurpose Purpose, string KekId, byte[] WrappedKey,
    DateTimeOffset CreatedAt, DateTimeOffset? RetiredAt);
```

`CdcStreamConflictException` and `CdcStreamKeyUnavailableException` (no message details beyond codes) go to `src/Autheris.Application/Streaming/CdcExceptions.cs`.

### 3.3 Application interfaces (`src/Autheris.Application/Streaming/Interfaces/`, WP-I1-0 / WP-I2-0)

```csharp
public interface ICdcStreamStore                     // I1, Infrastructure per engine + in-memory
{
    bool IsDurable { get; }
    Task<CdcStreamDescriptor> EnsureStreamAsync(string source, TableIdentifier table, CancellationToken ct = default);
    Task<CdcStreamDescriptor?> GetStreamAsync(CdcStreamId id, CancellationToken ct = default);
    Task<IReadOnlyList<CdcStreamDescriptor>> ListStreamsAsync(CancellationToken ct = default);
    Task SetStatusAsync(CdcStreamId id, CdcStreamStatus status, string? activationCode, CancellationToken ct = default);
    Task TouchPolledAsync(CdcStreamId id, DateTimeOffset at, CancellationToken ct = default);
    /// One transaction: fenced watermark upsert, fenced head CAS, event inserts, image upserts/deletes.
    /// Throws FencingTokenRejectedException or CdcStreamConflictException; nothing is written then.
    Task AppendAsync(CdcAppendBatch batch, CancellationToken ct = default);
    Task<IReadOnlyList<CdcRowImageRecord>> GetRowImagesAsync(CdcStreamId id, IReadOnlyList<byte[]> fingerprints, CancellationToken ct = default);
    /// TailOffset/HeadOffset are read consistently with the rows.
    Task<CdcReadResult> ReadAsync(CdcStreamId id, long fromOffsetInclusive, int maxEvents, CancellationToken ct = default);
    Task<long> PurgeAsync(CdcStreamId id, long newTailOffset, int maxRows, long fencingToken, CancellationToken ct = default);
    Task<int> PurgeImagesAsync(CdcStreamId id, int maxRows, CancellationToken ct = default);
    Task<int> DiscardUnreadableAsync(CdcStreamId id, IReadOnlyCollection<string> unreadableDekIds, CancellationToken ct = default);
}

public interface ICdcStreamKeyStore                  // I1
{
    Task<IReadOnlyList<CdcWrappedKey>> GetKeysAsync(CdcStreamId id, CancellationToken ct = default);
    Task<bool> TryAddKeyAsync(CdcWrappedKey key, CancellationToken ct = default);      // false = concurrent add, re-read
    Task RetireKeyAsync(CdcStreamId id, string dekId, DateTimeOffset at, CancellationToken ct = default);
    Task ReplaceWrappingAsync(CdcStreamId id, string dekId, string kekId, byte[] wrappedKey, CancellationToken ct = default);
    Task<int> DeleteUnreferencedPayloadKeysAsync(CdcStreamId id, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, int>> CountByKekAsync(CancellationToken ct = default);
}

public interface ICdcKeyEncryptionKeyProvider        // I1, Infrastructure (KeyVaultCdcKekProvider)
{
    string? ActiveKekId { get; }
    bool TryGetKek(string kekId, out ReadOnlyMemory<byte> key);   // false = unavailable (fail closed)
}

public interface ICdcStreamCrypto                    // I1, Application (CdcStreamCrypto over keyring)
{
    ValueTask<CdcProtectedBlob> ProtectEventAsync(CdcStreamId id, long offset, CdcStreamEventKind kind, CdcTenantScope scope, string? tenantId, ReadOnlyMemory<byte> plaintext, CancellationToken ct = default);
    ValueTask<byte[]> UnprotectEventAsync(CdcStreamEventRecord record, CancellationToken ct = default);   // CdcStreamKeyUnavailableException
    ValueTask<CdcProtectedBlob> ProtectImageAsync(CdcStreamId id, byte[] fingerprint, ReadOnlyMemory<byte> plaintext, CancellationToken ct = default);
    ValueTask<byte[]> UnprotectImageAsync(CdcStreamId id, CdcRowImageRecord image, CancellationToken ct = default);
    ValueTask<byte[]> FingerprintAsync(CdcStreamId id, IReadOnlyList<object?> keyValues, CancellationToken ct = default);
}

public interface ICdcStreamSignal                    // I1
{
    ValueTask NotifyAdvancedAsync(CdcStreamId id, long headOffset, CancellationToken ct = default);
    IAsyncDisposable Subscribe(Func<CdcStreamId, long, ValueTask> onAdvanced);
}

public interface ICdcStreamTailHub                   // I1 (compat) / I2 (GraphQL)
{
    IAsyncEnumerable<CdcStreamDelivery> AttachAsync(CdcStreamId id, long? fromOffset, CancellationToken ct);
}
public sealed record CdcStreamDelivery(CdcStreamEventRecord Record, CdcChangePayload? Change, CdcControlPayload? Control, TableIdentifier Table);
```

I2 (WP-I2-0):

```csharp
public enum CdcConsumerKind { Webhook = 1, Plugin = 2 }
public enum CdcConsumerState { Active = 1, Paused = 2, Failed = 3 }
public sealed record CdcReceiverIdentity(string TenantId, string Sid, IReadOnlyList<string> Groups, IReadOnlyList<string> Roles, DateTimeOffset? CapturedAt, bool Expires);
public sealed record CdcReceiverFilter(IReadOnlySet<CdcOperation> Operations, IReadOnlyList<string>? Columns);
public sealed record CdcConsumerRegistration(string ConsumerId, CdcConsumerKind Kind, CdcStreamId StreamId, CdcReceiverIdentity Identity,
    CdcReceiverFilter Filter, CdcStartPosition StartPosition, long? StartOffset, int MaxInFlight, string? CreatedBySid);
public sealed record CdcConsumerStatus(CdcConsumerRegistration Registration, CdcConsumerState State, string? FailureCode, long CommittedOffset);

public interface ICdcConsumerStore
{
    Task<bool> TryRegisterAsync(CdcConsumerRegistration registration, long initialCommittedOffset, int maxConsumersPerStream, CancellationToken ct = default);
    Task<CdcConsumerStatus?> GetAsync(string consumerId, CancellationToken ct = default);
    Task<IReadOnlyList<CdcConsumerStatus>> ListAsync(string? tenantId, CancellationToken ct = default);
    Task<bool> SetStateAsync(string consumerId, CdcConsumerState state, string? failureCode, CancellationToken ct = default);
    Task<bool> CommitAsync(string consumerId, CdcStreamId streamId, long committedOffset, long fencingToken, CancellationToken ct = default);
    Task<bool> CommitWithOutboxAsync(string consumerId, CdcStreamId streamId, long committedOffset, long fencingToken, IReadOnlyList<WebhookOutboxMessage> messages, CancellationToken ct = default);
    Task<bool> ResetOffsetAsync(string consumerId, CdcStreamId streamId, long committedOffset, long fencingToken, CancellationToken ct = default);
    Task DeadLetterAsync(string consumerId, CdcStreamId streamId, long offset, int attempts, string errorCode, CancellationToken ct = default);
    Task<IReadOnlyList<(long Offset, int Attempts, string ErrorCode, DateTimeOffset CreatedAt)>> ListDeadLettersAsync(string consumerId, int limit, CancellationToken ct = default);
    Task<bool> RemoveDeadLetterAsync(string consumerId, CdcStreamId streamId, long offset, CancellationToken ct = default);
    Task<bool> DeleteAsync(string consumerId, CancellationToken ct = default);
}

public enum CdcDeliveryOutcome { Deliver = 1, Skip = 2, Unavailable = 3 }
public sealed record CdcGovernedMessage(string EventId, long Offset, CdcStreamEventKind Kind, TableIdentifier Table, CdcOperation? Operation,
    string? TenantId, IReadOnlyDictionary<string, object?>? Before, IReadOnlyDictionary<string, object?>? After, bool KeyRedacted, bool BeforeUnknown,
    long? GapFromOffset, long? GapResumeOffset, CdcControlPayload? Control);
public sealed record CdcDeliveryDecision(CdcDeliveryOutcome Outcome, CdcGovernedMessage? Message, string ReasonCode);

public interface ICdcChangeAuthorizer
{
    ValueTask<CdcDeliveryDecision> AuthorizeAsync(CdcStreamDelivery delivery, ClaimsPrincipal identity, CdcReceiverFilter filter, CancellationToken ct = default);
}
public interface ICdcReceiverIdentityResolver { ValueTask<ClaimsPrincipal?> ResolveAsync(CdcReceiverIdentity identity, CancellationToken ct = default); }
public interface ICdcReceiverHandler              // one per consumer kind
{
    CdcConsumerKind Kind { get; }
    /// Returns the outcome per message; the runner commits only after every message is Delivered or dead-lettered.
    ValueTask<CdcHandlerResult> HandleAsync(CdcConsumerStatus consumer, IReadOnlyList<CdcGovernedMessage> messages, long fencingToken, CancellationToken ct);
}
public sealed record CdcHandlerResult(long LastHandledOffset, string? FailureCode, bool OffsetCommittedByHandler);
public interface ICdcDeliveryGate { ValueTask<CdcDeliveryOutcome> IsTablePermittedAsync(CloudEventWebhookSubscription subscription, CancellationToken ct = default); }
```

(`OffsetCommittedByHandler = true` for the webhook handler, which commits through `CommitWithOutboxAsync`.)

### 3.4 Options (`src/Autheris.Domain/Options/Protocols/StreamingCdcOptions.cs`, WP-I1-0; validator `CdcStreamOptionsValidator : IValidateOptions<GatewayOptions>`)

```jsonc
"Gateway": {
  "MssqlChangeTracking": {
    "Enabled": true,
    "PollingIntervalMilliseconds": 1000,          // min 250
    "BatchSize": 500,                             // 1..5000
    "QueryTimeoutSeconds": 30,                    // 1..300
    "AllowNonSnapshotRead": false,
    "OnWatermarkExpired": "Restart",              // Restart | Halt
    "TrackedTables": [ "finance.dbo.invoices" ],  // shorthand, defaults apply
    "Tables": [ { "Table": "finance.dbo.orders", "DataSource": "erp", "PollingIntervalMilliseconds": 250,
                  "BatchSize": 2000, "QueryTimeoutSeconds": 10, "RetentionHours": 24, "RetentionMaxEvents": 200000,
                  "OnWatermarkExpired": "Halt" } ]
  },
  "CdcStream": {
    "RetentionHours": 72, "RetentionMaxEvents": 1000000, "RetentionSweepSeconds": 300, "RetentionDeleteBatch": 5000,
    "MaxEventPayloadBytes": 262144, "TailPollIntervalMilliseconds": 1000, "SubscriberBufferSize": 1000,
    "Encryption": { "ActiveKekId": "k1", "Keks": [ { "Id": "k1", "SecretRef": "cdc-stream-kek-k1" } ],
                    "DekRotationHours": 24, "MaxCachedDeks": 256 },
    "Consumers": { "MaxConsumersPerStream": 100, "MaxInFlight": 100, "MaxAttempts": 5,
                   "MaxConsecutiveFailures": 20, "HandlerTimeoutSeconds": 30, "MaxAbandonedHandlers": 2 }
  },
  "Plugins": { "CdcReceivers": [ { "Name": "erp-sync", "PluginFile": "Acme.Sync.dll", "ListenerType": "Acme.Sync.OrderListener",
      "TenantId": "acme", "IdentitySid": "S-1-5-21-...-1234", "IdentityGroups": [], "IdentityRoles": ["svc-sync"],
      "Table": "finance.dbo.orders", "Operations": ["Insert","Update","Delete"], "Columns": [], "StartPosition": "Latest" } ] }
}
```

`TrackedTables` and `Tables` are merged (`Tables` wins per table; duplicates rejected by the validator). Validation fails start-up for out-of-range values, unknown enum names, duplicate tables, an `ActiveKekId` missing from `Keks`, and plugin receivers with missing name/file/type/identity.

---

## 4. Files per Layer

Legend: **N** new, **M** modified, **D** deleted. Owner = work package (section 5).

| Layer | File | Op | Owner |
|---|---|---|---|
| Domain | `Model/CdcStreamModels.cs` | N | I1-0 |
| Domain | `Options/Protocols/StreamingCdcOptions.cs` (+ `CdcStreamOptions`, `CdcTrackedTableOptions`) | M | I1-0 |
| Domain | `Audit/AuditEventTypes.cs` (CDC_* constants of D-B8, all at once) | M | I1-0 |
| Domain | `Events/GovernedCdcChange.cs` | N | I3-A |
| Domain | `Options/Integrations/ItsmOptions.cs` (`PluginsOptions.CdcReceivers`) + `CdcPluginReceiverOptions.cs` | M/N | I3-A |
| Application | `Streaming/Interfaces/ICdcStreamStore.cs`, `ICdcStreamKeyStore.cs`, `ICdcKeyEncryptionKeyProvider.cs`, `ICdcStreamCrypto.cs`, `ICdcStreamSignal.cs`, `ICdcStreamTailHub.cs` | N | I1-0 |
| Application | `Streaming/CdcExceptions.cs` | M | I1-0 |
| Application | `Streaming/Interfaces/IMssqlWatermarkStore.cs` (remove `TryAdvanceWatermarkAsync`) | M | I1-A |
| Application | `Hosting/ClusterPartitionedService.cs` | N | I1-B |
| Application | `Streaming/Crypto/CdcStreamKeyring.cs`, `CdcStreamCrypto.cs`, `CdcPayloadCodec.cs`, `CdcEventId.cs` | N | I1-C |
| Application | `Streaming/Services/CdcStreamRetentionService.cs` (+ job), `CdcStreamSignal.cs`, `CdcStreamTailHub.cs` | N | I1-C |
| Application | `Streaming/Services/CdcWebhookDeliveryService.cs` (I1 compat feed; I2 removes MSSQL path) | M | I1-C, then I2-C |
| Application | `Streaming/Services/StreamAccessContextResolver.cs`, `StreamAccessContext.cs`, `StreamRlsPolicyEnforcer.cs` (re-implemented) | N/M | I2-B |
| Application | `Streaming/Delivery/CdcTransitionEvaluator.cs`, `CdcDeleteKeyRenderer.cs`, `CdcChangeAuthorizer.cs`, `CdcReceiverIdentityResolver.cs`, `CdcDeliveryAuditBatcher.cs`, `CdcDeliveryGate.cs` | N | I2-B |
| Application | `Streaming/Interfaces/ICdcConsumerStore.cs`, `ICdcChangeAuthorizer.cs`, `ICdcReceiverHandler.cs`, `ICdcReceiverIdentityResolver.cs`, `ICdcDeliveryGate.cs`, `Streaming/Consumers/CdcConsumerModels.cs` | N | I2-0 |
| Application | `Streaming/Consumers/CdcConsumerService.cs`, `CdcConsumerRunner.cs`, `CdcConsumerMetrics.cs` | N | I2-A |
| Application | `Streaming/Consumers/CdcWebhookReceiverHandler.cs`; `Webhooks/WebhookDeliveryHostedService.cs` (send-time gate) | N/M | I2-C |
| Application | `Plugins/IPluginManager.cs` (`TryGetCdcListener`) | M | I3-A |
| Application | `Streaming/Consumers/PluginCdcReceiverHandler.cs`, `CdcPluginReceiverBinder.cs` | N | I3-B / I3-A |
| Extensions | `Cdc/MssqlChangeTrackingPoller.cs` (rewrite), `Cdc/CdcTableActivator.cs` N, `Cdc/CdcChangeEventBuilder.cs` N, `Cdc/CdcProducerService.cs` N, `Cdc/MssqlChangeTrackingHostedService.cs` D (tracker kept in `Cdc/CdcPollFailureTracker.cs`), `Cdc/InMemoryMssqlWatermarkStore.cs` D, `Cdc/CdcSourceServiceCollectionExtensions.cs` M | | I1-B |
| Infrastructure | `Persistence/Migrations/{Sqlite,PostgreSql,SqlServer}BaselineSchema.cs`, `Shape/governance-shape.*.json` | M | I1-A (I1 tables), I2-A (I2 tables/columns) |
| Infrastructure | `Persistence/{Sqlite,PostgreSql,SqlServer}GovernanceRepository.CdcStreams.cs`, `.CdcStreamKeys.cs` | N | I1-A |
| Infrastructure | `Persistence/{...}GovernanceRepository.CdcConsumers.cs`; `.CloudEventSubscriptions.cs` + `CloudEventSubscriptionMapping.cs` (new columns) | N/M | I2-A |
| Infrastructure | `Streaming/InMemoryCdcStreamStore.cs` (+ key store), `Streaming/InMemoryCdcConsumerStore.cs` | N | I1-A / I2-A |
| Infrastructure | `Streaming/GovernanceDbMssqlWatermarkStore.cs` (read side only) | M | I1-A |
| Infrastructure | `Security/KeyVaultCdcKekProvider.cs` | N | I1-C |
| Infrastructure | `Plugins/PluginManager.cs` (skip bus binding, `TryGetCdcListener`) | M | I3-A |
| GraphQL | `Subscriptions/Subscription.cs` (I1: stream-backed tables via hub; I2: `fromOffset`, new fields, overflow error), `Subscriptions/CdcSubscriptionGovernor.cs` (`cdc_all` over streams) | M | I1-C, then I2-C |
| Domain | `Model/CdcModels.cs` (`StreamCdcEvent` new fields: offset, kind, keyRedacted, beforeUnknown, beforeJson) | M | I2-C |
| Api | `Endpoints/CdcStreamAdminEndpoints.cs` N (I1-C); `Endpoints/CdcConsumerAdminEndpoints.cs` N (I2-A); `Endpoints/StreamingCdcEndpoints.cs` M (registration D-B4/D-B5; I2-C) | | |
| Api | `Hosting/CdcStreamStartupCheck.cs` N (I1-C); DI: `GatewayGovernanceServiceExtensions.cs` (stores, I1-A/I2-A), `CdcWebhooksFeature.cs` + `GatewayEventServiceExtensions.cs` (I1-C/I2-C), `CdcSourceServiceCollectionExtensions.cs` (I1-B) | M | |

Shared-file rule: a file listed for two packages of the same increment is edited only by its owner; the other package asks via the plan's handover note in its PR description. Across increments the later package owns it after the earlier merge.

---

## 5. Work Packages, Ownership, Parallelism and Sequencing

Three Sonnet TDD developers (Dev A, Dev B, Dev C) per increment. Increments are sequential (I1 -> I2 -> I3), each through Phases 4-6 and mergeable on its own. Within an increment, a short contracts package (`-0`) goes first (one commit, compiles, no behaviour); then the three packages run in parallel on disjoint files against the fixed contracts and the in-memory implementations.

### Increment I1 - Producer hardening and durable stream (AC-2, AC-4, AC-5, AC-6, AC-13, AC-14)

| WP | Dev | Scope | Depends on |
|---|---|---|---|
| **I1-0** Contracts | A | Section 3.2, 3.3 (I1 part), 3.4 options + validator skeleton, audit constants, exception types. Builds green, no logic. | - |
| **I1-A** Persistence | A | DDL (I1 tables) x3 engines + shape snapshots + shape tests; `ICdcStreamStore`/`ICdcStreamKeyStore` per engine + in-memory; `GovernanceDbMssqlWatermarkStore` read-only; DI; contract test base `CdcStreamStoreContractTests` run on SQLite (unit-speed), PostgreSQL and SQL Server (shared containers). | I1-0 |
| **I1-B** Producer | B | `ClusterPartitionedService`; `CdcTableActivator`; poller rewrite (snapshot tx, explicit columns, timeout, batch rule, control events); `CdcChangeEventBuilder` (images, before, tenant scope); `CdcProducerService`; options validator logic; architecture-test allowlist update + Cdc positive control; SQL Server change-tracking integration tests. Uses `InMemoryCdcStreamStore` and a test `ICdcStreamCrypto` (fixed key) until A/C merge. | I1-0 |
| **I1-C** Crypto, stream services, compat, admin | C | `KeyVaultCdcKekProvider`, `CdcStreamKeyring`, `CdcStreamCrypto`, `CdcPayloadCodec`, `CdcEventId`, retention service/job, signal, `CdcStreamTailHub`, GraphQL/webhook compat feed (D-A13), admin endpoints, startup check. | I1-0 |
| **I1-E** End-to-end | B | After A and C are merged: producer against real governance DB + real crypto (AC-6 failover, AC-2, AC-14), wiring review. | I1-A, I1-B, I1-C |

### Increment I2 - Durable consumers, webhooks, GraphQL (AC-1 sub+webhook, AC-3, AC-7, AC-8, AC-9, AC-11, AC-12)

| WP | Dev | Scope | Depends on |
|---|---|---|---|
| **I2-0** Contracts | A | I2 part of 3.3, consumer records, enums. | I1 merged |
| **I2-A** Consumer framework | A | I2 DDL + `CLOUDEVENT_SUBSCRIPTIONS` columns + shapes; `ICdcConsumerStore` x3 + in-memory incl. `CommitWithOutboxAsync`; `CdcConsumerService`/`CdcConsumerRunner` (lease, start positions, gap, backpressure, retries, dead letters, failed state); metrics; consumer admin endpoints. Tests with a fake `ICdcChangeAuthorizer` and fake handler. | I2-0 |
| **I2-B** Governed delivery | B | Enforcer split (D-B1, behaviour-preserving), `CdcTransitionEvaluator`, `CdcDeleteKeyRenderer`, `CdcChangeAuthorizer`, `CdcReceiverIdentityResolver`, `CdcDeliveryGate`, `CdcDeliveryAuditBatcher`; Stryker on transition matrix and outcome mapping. Pure/unit-heavy. | I2-0 |
| **I2-C** Receivers | C | Webhook registration (D-B4/D-B5), `CdcWebhookReceiverHandler`, send-time gate in `WebhookDeliveryHostedService`, removal of the MSSQL path from `CdcWebhookDeliveryService`, GraphQL `fromOffset`/fields/overflow, `cdc_all`. Unit tests with fakes first; E2E (AC-1 sub + webhook, AC-11 webhook paths) after A and B merge. | I2-0; E2E after I2-A, I2-B |

### Increment I3 - Plugin receivers (AC-1 all three, AC-10, AC-11/AC-12 for plugins)

| WP | Dev | Scope | Depends on |
|---|---|---|---|
| **I3-A** Contract & binding | A | `GovernedCdcChange`, receiver options + validator, `PluginManager` (bus exclusion, `TryGetCdcListener`), `CdcPluginReceiverBinder` (consumer registration with D-B5 checks, audit), architecture tests. | I2 merged |
| **I3-B** Dispatcher & fixture plugin | C | `PluginCdcReceiverHandler` (timeout, abandoned-handler cap, copies, retries via runner), test plugin project `tests/Autheris.Tests.CdcPlugin` (listeners: recording, throwing, hanging, mutating), trust-list fixture computing the hash at test time, AC-1 (three kinds), AC-10 integration. | I3-A contract (first commit of I3-A) |

Dev B is free during I3 for the Phase-5 fixes of I2 or to pair on I3-B.

---

## 6. Test Strategy (mapped to acceptance criteria)

**Speed rules.** Unit tests use `FakeTimeProvider`, fakes for `IDistributedClusterStateProvider` leases and the in-memory stores; no `Task.Delay`, no wall-clock waits - loops are driven by an explicit `RunOnceAsync` seam on `CdcProducerService`, `CdcConsumerRunner` and `CdcStreamRetentionJob`. Integration tests use `SharedDatabaseServers` (one SQL Server and one PostgreSQL container per run) and a fresh database per test class; change tracking is enabled per database (`ALTER DATABASE ... SET CHANGE_TRACKING = ON (CHANGE_RETENTION = 2 DAYS)`, `ALLOW_SNAPSHOT_ISOLATION ON`). A new `SqlServerChangeTrackingDatabase` helper (WP-I1-B) creates the database, the least-privilege login/user (`CONNECT`, `SELECT`, `VIEW CHANGE TRACKING` on the tracked tables only) and tables; statement capture is a counting `ISqlConnectionFactory` decorator (no XEvents).

| AC | Test (project / class) | Package |
|---|---|---|
| AC-2 | `Integration/Cdc/CdcProducerSqlServerTests.OneChangetableQueryPerPoll_With10Consumers` - 10 durable consumers registered (I2) / 10 hub listeners (I1); counting decorator sees exactly one `CHANGETABLE(CHANGES` statement per poll. | I1-E (re-run in I2) |
| AC-4 | `...WatermarkExpired_Restart_WritesControlEventAndContinues`, `...Halt_StopsUntilResync` - expiry produced deterministically: change table B, then enable CT on table A (its min valid version = current), store watermark 0 for A. | I1-B |
| AC-5 | `CdcTableActivatorTests` (unit, every code), `...SqlServerTests.Activation_RejectsWithStableCode` (no declared PK, PK mismatch, tracking off, snapshot off) with the least-privilege login; captured statements contain no `sys.` catalog view; `ArchitectureNoSourceKeyCatalogQueriesTests` (allowlist + Cdc positive control). | I1-B |
| AC-6 | `CdcProducerFailoverTests` - fault-injecting store decorator throws after the source read; second producer with a higher token appends; the stale producer's append is fenced; offsets dense 1..n, change versions continuous, no duplicate. Unit (in-memory) + SQL Server/PostgreSQL governance DB. | I1-B / I1-E |
| AC-13 | `GovernanceSchemaShapeSnapshotTests`, `PostgreSqlGovernanceSchemaShapeTests`, `SqlServerGovernanceSchemaShapeTests` (fresh DB verifies), checksum-constant test unchanged. | I1-A, I2-A |
| AC-14 | `CdcProducerTests.NeverPublishesOnCdcEventChannel` (strict mock), `CdcStreamSignalTests.PayloadHasNoRowDataTenantOrTable` (serialised signal shape). | I1-B, I1-C |
| AC-1 | I2: `CdcFanOutEndToEndTests.SubscriptionAndWebhookGetSameIdAndOffset`; I3: `...AllThreeReceiverKinds`. | I2-C, I3-B |
| AC-3 / AC-8 | `CdcConsumerRunnerTests.SlowConsumerDoesNotDelayOthers` (consumer A's handler blocked by a gate; B delivers within one runner step), `...CatchesUpInOrderAfterReturn`; integration variant on SQLite governance. | I2-A |
| AC-7 | `CdcConsumerRunnerTests.RestartMidBatch_RedeliversOnlyUncommitted_SameIds`. | I2-A |
| AC-9 | `CdcConsumerRunnerTests.BehindRetention_ExactlyOneGap_ThenContinues`; `CdcStreamRetentionJobTests` (age and count bounds, chunking, DEK shredding). | I2-A, I1-C |
| AC-10 | `PluginCdcReceiverHandlerTests` (throw, timeout, ignoring cancellation, abandoned cap); integration with the trust-listed fixture DLL. | I3-B |
| AC-11 | `CdcTransitionEvaluatorTests` (full matrix, data-driven), `CdcDeleteKeyRendererTests` (Clear, HMAC equals read, other mask, deny), `CdcChangeAuthorizerTests` (masked/denied in before and after, unavailable -> Unavailable for each decision input), `CdcWebhookRegistrationTests` (no consent, unknown vs denied column same text), `WebhookDeliveryHostedServiceCdcGateTests` (revoked after enqueue -> not sent). | I2-B, I2-C |
| AC-12 | `CdcDeliveryAuditBatcherTests` (no payload/key/fingerprint in details), log-scrubbing test with a capturing logger over producer, runner and handlers (no column value, key value or fingerprint at any level >= Information), metric label allow-list test. | I2-B, I2-A |

Mutation testing (Stryker, existing configuration): `CdcTransitionEvaluator`, `CdcDeleteKeyRenderer`, `CdcChangeAuthorizer` outcome mapping, fencing/CAS branches of the in-memory stream store, `CdcStreamCrypto` AAD construction. Security regression catalog rows are added for the `SEC-CDC99-nn` ids (Phase 3).

---

## 7. Least-Privilege Grants (documented in Phase 6)

- **Source (SQL Server):** `CONNECT`; `SELECT` and `VIEW CHANGE TRACKING` on each tracked table. Database settings (not grants): `CHANGE_TRACKING = ON`, `ALLOW_SNAPSHOT_ISOLATION ON` (or `AllowNonSnapshotRead`). No `VIEW DEFINITION`, no `sys.indexes` access needed.
- **Governance DB:** DML (`SELECT`, `INSERT`, `UPDATE`, `DELETE`) on the new tables; no DDL in `Mode=Verify`.

---

## 8. Risks

| ID | Risk | Mitigation |
|---|---|---|
| R-1 | Write amplification in the governance DB (event + image + watermark + head per change). | Batched inserts per poll (one round trip per engine via table-valued/multi-row insert), retention, `RetentionMaxEvents`, per-table intervals, append-duration metric. External broker remains a documented non-scope. |
| R-2 | Images hold an encrypted copy of every changed, not-yet-deleted row for the life of the stream. | Catalogued columns only, `image` DEK, purge on `Disabled`, documented sizing. A max-age option is deferred (it would turn precise move-outs into `beforeUnknown`; owner may add it later). |
| R-3 | KEK loss or misconfiguration makes streams unreadable. | Fail closed, explicit status code, `discardUnreadable` resync, multi-KEK rotation without re-encryption. |
| R-4 | No live group resolution for receiver identities (P-11). | Bounded snapshot (webhooks), operator-declared identities (plugins); consent revocation is live; Phase-3 condition on maximum snapshot age. |
| R-5 | Snapshot isolation unavailable at customer sites. | Explicit `AllowNonSnapshotRead` opt-out with documented risk. |
| R-6 | Breaking changes (event id, webhook table filter, GraphQL fields, history replay removed, F-GOV-21 §6.1 exception removed). | Allowed (no production); called out in Phase-6 docs. |
| R-7 | `ClusterPartitionedService` with many tables/consumers creates many lease renewals. | Leases renew at `ttl/3`; caps (`MaxConsumersPerStream`); partitions without work back off to their interval. |
| R-8 | `beforeUnknown` deletes for filtered receivers and deletes with unresolved tenant are suppressed. | Documented; consumers resync by governed read; server-side snapshot is non-scope. |
| R-9 | Enforcer refactoring regresses the existing non-stream channel path. | Behaviour-preserving extraction with the existing enforcer tests unchanged; Phase-5 diff review. |
| R-10 | Plugin code ignoring cancellation leaks threads. | Abandoned-handler cap per receiver, consumer `failed`, metric; operator must fix/unload the plugin. |

---

## 9. Phase-3 Review Focus

Cross-tenant isolation of the shared stream (tenant scope, AAD, Shared semantics), fail-closed tri-state at delivery, revocation latency (consent epoch, snapshot age, outbox send-time gate), key management, event-id and fingerprint opacity, webhook SSRF/HMAC unchanged paths, plugin trust and isolation limits, DoS bounds (consumers, in-flight, payload, abandoned handlers), gap/watermark events not hiding data loss, audit without payload.
