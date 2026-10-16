# PRD: REQ-CDC-99 - Governed CDC Fan-Out (Durable Stream, Per-Receiver Authorization) and Change-Tracking Hardening

**Document ID:** `REQ-CDC-99`
**Date:** 2026-10-15
**Status:** PRD READY - next: Phase 2 (Solution Architect)
**Author:** Enterprise Product Manager (Phase 1)
**Source:** GitHub issue #99 ("Feature: extend the change-tracking poller (F-CDC-02): webhooks and C# DLL as receivers, one producer for many consumers, hardening"; written in German against v1.1.10)
**Branch:** `feat/issue-99-cdc-fanout` (base `main` `21dfd399`)
**Parent Plan:** [00-master-plan-overview.md](00-master-plan-overview.md)
**Related feature docs (read-only references):** [F-CDC-02 MSSQL change tracking](../features/f-cdc-02-mssql-change-tracking.md), [F-EXT-01 plugins, events & webhooks](../features/f-ext-01-generic-plugin-events-and-webhooks.md), [F-GOV-21 declared table keys](../features/f-gov-21-declared-table-keys-and-read-ordering.md), [F-GOV-12 governance storage / schema shape verification](../features/f-gov-12-multi-engine-governance-storage.md#schema-shape-verification), [P05 subscriptions](../features/p05-subscriptions-realtime.md)

**Binding project constraints:** no upgrade or repair migrations (new governance tables go into the `0001` baseline DDL of SQLite, PostgreSQL and SQL Server in place, plus the schema-shape snapshots under `src/Autheris.Infrastructure/Persistence/Migrations/Shape/`; the caller supplies the newest schema); not in production, breaking changes allowed; least privilege on sources (`SELECT` + `VIEW CHANGE TRACKING` only); primary keys come from the declared table definition (F-GOV-21), never from runtime discovery.

---

## 1. Verification of the Issue Against the Current Code (`main` `21dfd399`)

| # | Issue statement (v1.1.10) | Current state | Consequence |
|---|---|---|---|
| V1 | Poller reads `CHANGETABLE(CHANGES ...)`, detects I/U/D via the PK, stores the watermark in `autheris_cdc_watermarks`, publishes to `ICdcEventChannel`, delivered via `onTableChanged`. | Confirmed (`MssqlChangeTrackingPoller`, `GovernanceDbMssqlWatermarkStore`, `Subscription.SubscribeToTableEventsAsync`). Watermark writes are fenced (`TryAdvanceWatermarkAsync(table, version, fencingToken)`), only complete change versions are committed. | Base exists; durability of the *events* is missing (see V5). |
| V2 | Webhooks via `CdcWebhookDeliveryService` / `CLOUDEVENT_SUBSCRIPTIONS`. | Confirmed. Per subscription the event is authorised as the creator's **frozen principal snapshot** through `IStreamRlsPolicyEnforcer`; masked CloudEvent enqueued in the durable outbox (HMAC, retries, dead letters); opaque id `cdc-<hash>`. | Webhook path is governed, but fed from an at-most-once channel (enqueue failure = event lost, documented). |
| V3 | `CdcEvent`s do not reach `IPluginEventListener` plugins. | Confirmed. `IPluginEventListener<TEvent>` requires `TEvent : IGatewayEvent`; `CdcEvent` is not an `IGatewayEvent`; plugins are bound to the generic `IGatewayEventBus`, which broadcasts **unfiltered** to all listeners. | New receiver type needed; the generic event bus must **not** be used for CDC (it would bypass per-receiver authorization). |
| V4 | Poller runs on exactly one replica (lease `cdc:mssql:poll`). | Confirmed: one lease for **all** tables; tables are polled sequentially in one job; one global `PollingIntervalMilliseconds`/`BatchSize`, no query timeout. | A slow table delays all others; no per-table tuning. |
| V5 | (implicit) consumers are independent. | **Not true.** `InMemoryCdcEventChannel` uses per-subscriber bounded channels (1000) with `DropOldest` plus a 50-event replay history; `RedisCdcEventChannel` is Redis Pub/Sub (at-most-once). No offsets, no retention, no replay after restart. | AC-3 (catch-up after outage) is impossible today; silent loss on slow consumers. |
| V6 | Expired watermark needs manual `DELETE`. | Confirmed (`CdcWatermarkExpiredException`, backoff, runbook in F-CDC-02 §3.3 with manual SQL). | Must become an event + automatic, controlled resync. |
| V7 | PK requirement. | Checked lazily at first poll by querying `sys.indexes`/`sys.index_columns` on the **source**. F-GOV-21 §6.1 lists this as a sanctioned exception. | **Violates the declared-keys rule** (OD-6 of REQ-GOV-94). Replace with the declared primary key, validated at activation; remove the exception from F-GOV-21. |
| V8 | (not in issue) data minimisation. | Poller joins `T.*` - every source column, including columns not in the catalog, is materialised and published. | Read only catalogued columns (explicit, validated column list). |
| V9 | (not in issue) tenant attribution. | Tenant is guessed from columns named `TenantId`/`tenant_id`; the catalog already declares `TableMetadata.TenantColumnName`. Untenanted events are dropped by webhooks. | Use the declared tenant column; fail closed when it is required but missing. |
| V10 | Delete payload. | Delete events carry the PK columns in `After` (row data is gone); the native `EventId` embeds clear PK values (`{domain}_{schema}_{table}_{version}_{pk}`), forwarded to GraphQL subscribers unchanged. | Event id must be opaque on every path; delete key rendering per receiver. |
| V11 | Consistency of the read. | `CHANGETABLE` and the join run under the default isolation level; min-valid-version is checked before, not inside, the read. | Microsoft's documented pattern (snapshot isolation, validate inside the transaction) is not followed. |
| V12 | Revocation. | Webhook: already-enqueued outbox rows are still sent after revocation (F-EXT-01 §4.4.1). Snapshot freezes roles/groups. | Must re-check at send time; bind receivers to a live identity. |

---

## 2. Benchmark Against Best Practice

| Practice | Reference | Adopted as |
|---|---|---|
| Single log, many independent consumers with own committed offsets; start position `latest` / `earliest` / explicit offset | Kafka consumer groups, `auto.offset.reset`, Debezium | Durable per-table stream with monotonically increasing stream offset; per-consumer offset; start position `Latest`, `Earliest`, `FromOffset`. |
| Retention bounded by time and size; a consumer behind the log start gets an explicit out-of-range signal, never silent skipping | Kafka `retention.ms`/`retention.bytes`, `OffsetOutOfRange` | Retention by age and by event count per stream; explicit `gap` control event carrying the resume offset. |
| Transactional outbox: state change and "message to send" committed atomically | Outbox pattern (Richardson), Debezium outbox router | Stream rows and the source watermark are written in **one governance-database transaction**; consumer offset advance and webhook outbox insert in one transaction. |
| Fencing tokens on every write of a lease holder | Kleppmann, "How to do distributed locking"; ZooKeeper/etcd epochs | Fenced producer lease per tracked table; fenced consumer lease per durable consumer; the token is checked inside the write transaction. |
| At-least-once delivery with stable, idempotent event ids | CloudEvents `id`, Kafka idempotent consumers | Opaque event id derived from (stream, offset), identical for every receiver and every redelivery. |
| Snapshot isolation for change tracking reads; validate `CHANGE_TRACKING_MIN_VALID_VERSION` inside the same transaction | Microsoft Docs "Work with change tracking" | Read under `SNAPSHOT`; validate inside the transaction. |
| Re-snapshot on lost history | Debezium `snapshot.mode=when_needed` | Watermark-expired control event + automatic restart at the current version; consumer resync via governed reads (snapshot mode is a later option). |
| Per-subscriber authorization at read/delivery time, not at write time; topic ACLs alone are insufficient for row/column-level data | Confluent RBAC + field-level encryption, Materialize/Hasura live queries (per-session permission evaluation) | Authorization of each event per receiver identity at delivery, with the same decision chain as a governed read; the shared stream is never a receiver-facing surface. |
| Backpressure via pull + bounded in-flight, poison message to dead letter after N attempts | Kafka `max.poll.records`, SQS redrive policy | Pull-based consumers, bounded in-flight per consumer, per-event timeout, dead letter after N attempts. |

### Rejected anti-patterns

- **AP-1 Fan-out through a broadcast bus** (`IGatewayEventBus` / Redis Pub/Sub) as the data path: at-most-once, no offsets, and one listener sees every tenant's raw events. The channel remains only a wake-up signal ("stream X advanced"), never a carrier of row data.
- **AP-2 One poll per consumer** ("each webhook polls the source"): multiplies source load; violates AC-2.
- **AP-3 Authorising only at registration**: revocations would not apply to events already in the stream; violates the issue's "check at delivery" rule.
- **AP-4 Storing raw row values in clear in the shared stream** and filtering only in the consumer: any bug or new consumer type becomes a bypass; violates data-at-rest expectations.
- **AP-5 Keys-only stream with re-fetch per receiver**: see decision D-2.
- **AP-6 Advancing the watermark before the events are durable** or silently resetting it: loss without a signal.
- **AP-7 Runtime key discovery from source catalogs** (`sys.indexes`): needs metadata rights beyond the least-privilege grant and contradicts F-GOV-21.
- **AP-8 Native event ids containing key values** reaching receivers: leaks clear keys to receivers that must not read them.
- **AP-9 Unbounded per-consumer queues / drop-oldest**: silent loss; replaced by offsets + explicit gap.
- **AP-10 Exactly-once promises to external receivers**: not achievable over HTTP; the contract is at-least-once with idempotent ids.

---

## 3. Decisions on Open Questions (best practice, binding for Phase 2)

| ID | Question | Decision | Rationale |
|---|---|---|---|
| D-1 | Where does the durable stream live? | In the **governance database** (all three engines), new baseline tables (names indicative, final names Phase 2): `autheris_cdc_streams` (one row per tracked table: stream id, source, table, status, producer fencing token, retention bounds, head/tail offsets), `autheris_cdc_stream_events` (stream id, offset, change version, operation, kind = `change`/`gap`/`watermark_expired`/`resync`, protected payload, key fingerprint, produced at), `autheris_cdc_consumers` (receiver registry), `autheris_cdc_consumer_offsets` (consumer id, stream id, committed offset, fencing token), `autheris_cdc_dead_letters` (consumer id, stream id, offset, attempts, last error code - no payload). | It is the only mandatory durable store (Redis is optional); it allows the atomic write of stream rows + watermark (D-4) and of offset + outbox row. Volume is bounded by retention. |
| D-2 | Keys + version only vs protected values in the stream | **Protected values in the stream.** The producer stores the after-image (catalogued columns only) and, when known, the before-image, encrypted at rest (AES-256-GCM, envelope key per stream, associated data = stream id + offset, KEK from the secret store like `cdc-webhook-kek`). Receivers never read the stream directly; every delivery decrypts and filters/masks per receiver identity at delivery time. Keys-only + re-fetch is rejected. | (a) AC-1 requires the same change with the same id for all receivers; re-fetch at different times yields different values. (b) Re-fetch costs one source query per receiver and row (AP-2). (c) Move-in/move-out needs a before-image, which change tracking does not supply. (d) Change tracking already gives "state at poll time", so stored values are no less current than a re-fetch at the producer. Risk of data at rest is mitigated by encryption, column minimisation and bounded retention (crypto-shredding of expired envelope keys is optional, see R-3). |
| D-3 | Before-image for row-filter transitions | The producer keeps a compacted **last-image store** per stream (`autheris_cdc_row_images`: stream id, key fingerprint, protected last image, offset), maintained in the same transaction as the stream write and deleted on delete events. An event carries `before` = last image (or `beforeUnknown` for the first change of a key since activation). | Gives exact per-receiver transitions: visible→visible = update, invisible→visible = insert, visible→invisible = delete, invisible→invisible = nothing. |
| D-4 | Watermark vs stream write | Stream rows, last images and the watermark are written in **one transaction** whose first statement verifies the producer fencing token. A crash before commit re-reads the same versions; a crash after commit produces nothing twice. | Exactly-once production into the stream; at-least-once only on the delivery side. |
| D-5 | Event id | `evt-<opaque>` derived from (stream id, offset) (e.g. base32 of stream id + offset, or HMAC); identical on GraphQL, webhook (CloudEvent `id`) and plugin. The native `{table}_{version}_{pk}` id is removed. | AC-1; AP-8. |
| D-6 | Key fingerprint | HMAC-SHA-256 of the canonical declared PK values with a per-tenant server key (never plain SHA-256: low-entropy keys are brute-forceable). Internal only (last-image lookup); never sent to receivers. | Lookup without clear keys in indexes. |
| D-7 | Delete payload per receiver | A delete carries operation, table, event id, stream offset and the PK columns **rendered exactly as a read would render them for that receiver**: `Clear` → value; HMAC masking rule → the same HMAC value a read returns (allows correlation); any other mask or deny → the column is omitted, and the event carries `keyRedacted: true`. Row data is never included; the event schema and docs state this. A delete is emitted only if the before-image was visible to the receiver, or (before unknown) if the receiver may read the table without a row filter. | Issue §"Delete"; consistent with read semantics; no existence oracle for rows outside the receiver's row scope. |
| D-8 | Start position | `Latest` (default), `Earliest` (oldest retained), `FromOffset(n)`. Durable consumers persist it at registration; GraphQL subscriptions accept an optional `fromOffset` argument (resume token) and default to `Latest`. A requested offset older than the retained tail yields a `gap` event first. The current `before_subscription` timestamp filter and the in-memory 50-event history replay are removed. | Kafka `auto.offset.reset`; deterministic resume. |
| D-9 | Retention | Per stream: `RetentionHours` (default 72) and `RetentionMaxEvents` (default 1,000,000), whichever is reached first; purged by a fenced cluster job. Retention never waits for slow consumers. | Bounded storage; explicit gap instead of unbounded growth. |
| D-10 | Gap handling | A consumer whose offset is below the retained tail receives one `gap` control event (`fromOffset`, `resumeOffset`, `reason = retention`), its offset moves to `resumeOffset`, and the gap is audited and counted. | Kafka `OffsetOutOfRange` made explicit. |
| D-11 | Expired source watermark | Policy `OnWatermarkExpired`: `Restart` (default) writes a `watermark_expired` control event into the stream (carrying stored and min-valid versions, never data), then continues at `CHANGE_TRACKING_CURRENT_VERSION()` in the same transaction; `Halt` stops the stream and requires `POST /api/v1/cdc/streams/{id}/resync` (admin, audited). No manual SQL in either case. Consumers resync by a governed full read (REST/OData/GraphQL, optionally `changesSince`, F-DATA-09) and continue after the control event. | Issue AC-4; Debezium `when_needed` semantics without a server-side snapshot in the first increments. |
| D-12 | Primary key source | The **declared** primary key of the table definition (F-GOV-21). Activation (`TrackedTables` load and every config reload) fails closed for the table when: no declared PK; table not in catalog / inactive / unavailable; change tracking not enabled (`CHANGE_TRACKING_MIN_VALID_VERSION(OBJECT_ID(...))` is NULL); or the declared PK columns do not equal the key columns returned by `CHANGETABLE` (checked from the result-set schema of `SELECT TOP (0) ... FROM CHANGETABLE(CHANGES t, 0)` - needs only `VIEW CHANGE TRACKING` + `SELECT`). The `sys.indexes` query and the F-GOV-21 §6.1 exception are removed; the architecture test covers `src/Autheris.Extensions/Cdc/**`. | AP-7; least privilege; clear message at activation, not at first poll. |
| D-13 | Producer topology | One fenced lease **per tracked table** (`cdc:mssql:poll:{domain}.{schema}.{table}`), so tables are independent and can be spread over replicas; still exactly one producer per table. Per-table `PollingIntervalMilliseconds`, `BatchSize`, `QueryTimeoutSeconds` with global defaults. | Issue §3 "source load"; isolation of slow tables. |
| D-14 | Read consistency | `CHANGETABLE` and the join run in one `SNAPSHOT` transaction; min-valid-version is validated inside it. Requires `ALLOW_SNAPSHOT_ISOLATION ON` on the source database (a database setting, not a grant); activation reports its absence; `AllowNonSnapshotRead` (default `false`) permits `READ COMMITTED` with a documented risk. | Microsoft guidance. |
| D-15 | Column set | The join selects only the catalogued columns of the table (explicit, validated identifier list) - never `T.*`. | Data minimisation (V8). |
| D-16 | Tenant attribution | From the declared `TenantColumnName`; tables without one belong to the single tenant configured for the data source. If `RequireTenantColumn` is set and the column is missing, activation fails. Untenanted events are never delivered. | V9; fail closed. |
| D-17 | Receiver identity | Every durable receiver binds to one identity (person, technical user or service account SID). Webhooks: the identity is chosen at registration (default: the creator) and the registering admin must be allowed to act for it. Plugins: the identity is declared per listener in configuration (`Gateway:Plugins:CdcReceivers[]`: plugin trust-list entry, listener type, identity SID, tables, operations, columns), never self-declared by the DLL. Groups and roles are resolved **at delivery** through the existing principal/group resolution; where no live resolution is available the existing snapshot with `CreatorSnapshotMaxAgeDays` remains the fallback. | Issue "identity"; V12. |
| D-18 | Plugin contract | New plugin-facing `GovernedCdcChange : IGatewayEvent` (event id, table, operation, stream offset, masked `before`/`after`, `keyRedacted`, kind incl. `gap`/`watermark_expired`), delivered through `IPluginEventListener<GovernedCdcChange>` by a dedicated per-receiver dispatcher - not through `IGatewayEventBus`. The internal `CdcEvent` is never handed to plugins. | AP-1; V3. |
| D-19 | Per-receiver filter | Table (exact), operations (subset of insert/update/delete), optional column projection. The projection only narrows: it is intersected with the receiver's readable columns; requesting a denied column is rejected at registration with the same message as an unknown column (no existence oracle). | Issue §1. |
| D-20 | Delivery guarantees and ordering | At-least-once per receiver, in stream order per table; consumers deduplicate on event id. Head-of-line: after `MaxAttempts` (default 5) an event goes to the dead letter (reference only) and the offset advances. Webhooks: offset advance and outbox insert in one transaction; the outbox keeps its retry/dead-letter/circuit semantics. | AP-10; issue backpressure. |
| D-21 | GraphQL subscriptions | Ephemeral consumers: one shared tail reader per stream and node reads the stream once and fans out in memory to local subscriptions, each with a bounded buffer; a subscriber that overflows is completed with a `gap`/`RESYNC_REQUIRED` error instead of silently dropping events. Authorization per subscriber per event as today. | Bounded governance-DB load; no silent loss. |
| D-22 | Other CDC sources | The stream and consumer model is source-agnostic, but this REQ moves only MSSQL change tracking onto it. PostgreSQL logical replication (F-CDC-03), Debezium and `POST` ingestion keep the channel path until a follow-up REQ. | Scope control. |

---

## 4. Scope and Non-Scope

**In scope:** MSSQL change tracking producer hardening (D-12 to D-16); durable per-table stream with retention, gap and watermark-expired control events; durable consumer framework (offsets, start position, leases, backpressure, dead letters, metrics); webhook and GraphQL receivers on the stream; C# DLL plugin receivers; per-receiver identity, filters, masking and row-scope transitions; audit; admin endpoints for streams and consumers; feature docs F-CDC-02, F-EXT-01, F-GOV-21 §6.1, P05 updated in Phase 6.

**Non-scope:** server-side initial snapshot / re-snapshot into the stream (possible later: `StartPosition = Snapshot`); PostgreSQL logical replication, Debezium and HTTP ingestion on the durable stream (D-22); exactly-once delivery to external receivers; cross-table transactional ordering; external brokers (Kafka) as stream backend; schema evolution of tracked tables beyond "catalog change triggers re-activation".

---

## 5. Functional Requirements

### 5.1 Producer (MSSQL change tracking)

- **FR-P1** Exactly one producer per tracked table at any time (fenced per-table lease, D-13). AC-2 holds independent of receiver and replica count.
- **FR-P2** Activation checks (D-12, D-14, D-16) run at startup and on every configuration or catalog change; a failing table is not polled, the reason is logged once with a stable code (`CDC_NO_DECLARED_PK`, `CDC_PK_MISMATCH`, `CDC_TRACKING_DISABLED`, `CDC_TABLE_UNAVAILABLE`, `CDC_SNAPSHOT_ISOLATION_OFF`, `CDC_TENANT_COLUMN_MISSING`), exposed in the stream status endpoint and metric `autheris_cdc_stream_activation_failed`.
- **FR-P3** Per-table options with global defaults: `PollingIntervalMilliseconds` (min 250), `BatchSize` (1..5000), `QueryTimeoutSeconds` (default 30), `RetentionHours`, `RetentionMaxEvents`, `OnWatermarkExpired`.
- **FR-P4** Batch boundaries as today (never commit inside a change version).
- **FR-P5** Stream rows, last images and watermark are committed in one fenced transaction (D-4). A fencing rejection aborts the transaction; nothing is written.
- **FR-P6** Expired watermark → `watermark_expired` control event + policy (D-11); metric `autheris_cdc_watermark_expired_total` kept.
- **FR-P7** `ICdcEventChannel` carries only a wake-up signal (stream id, head offset) for the stream; no row data on the MSSQL path.

### 5.2 Stream

- **FR-S1** Offsets are dense and strictly increasing per stream; events are immutable.
- **FR-S2** Retention purge (D-9) is a fenced cluster job; purge never deletes events above the oldest offset that any consumer is currently *processing* within the same transaction (no torn read).
- **FR-S3** Stream status endpoint `GET /api/v1/cdc/streams` (admin): table, status, activation code, head/tail offset, watermark, last poll, lag of the source - no payload.
- **FR-S4** `POST /api/v1/cdc/streams/{id}/resync` (admin, audited) for `Halt` policy and manual restart.

### 5.3 Consumers and receivers

- **FR-C1** Receiver kinds: GraphQL subscription (ephemeral), CloudEvents webhook (durable), C# DLL plugin listener (durable). Unlimited receivers per table subject to configured caps (`MaxConsumersPerStream`, default 100; existing `MaxSubscriptionsPerTenant`).
- **FR-C2** Each durable consumer has its own offset, lease with fencing (one processing replica at a time), start position (D-8), filter (D-19), state (`active`, `paused`, `failed`), and is independent of all others (AC-3).
- **FR-C3** Backpressure: bounded in-flight events per consumer (`MaxInFlight`, default 100; for webhooks: pending outbox rows of the subscription), per-event handler timeout (`HandlerTimeoutSeconds`, default 30, plugins), dead letter after `MaxAttempts` (D-20).
- **FR-C4** Metrics per durable consumer (bounded label set = consumer id, capped by FR-C1): `autheris_cdc_consumer_lag_events`, `autheris_cdc_consumer_oldest_unprocessed_age_seconds`, `autheris_cdc_consumer_failures_total{reason}`, `autheris_cdc_consumer_dead_letters_total`, `autheris_cdc_consumer_gaps_total`, `autheris_cdc_consumer_delivered_total`, `autheris_cdc_consumer_denied_total`.
- **FR-C5** Admin endpoints: list consumers with offsets/lag/state; pause/resume; reset offset (`Latest`/`Earliest`/`FromOffset`, audited); list and replay dead letters (replay re-authorises as of now).
- **FR-C6** Webhook receivers: existing `CLOUDEVENT_SUBSCRIPTIONS` becomes a durable consumer (columns for identity SID, start position, column projection added to the baseline in place); CloudEvent `id` = D-5 event id; HMAC signing, outbox retries, dead letters and circuit pause unchanged.
- **FR-C7** Plugin receivers: D-17/D-18. A throwing or timing-out handler is retried with backoff, then dead-lettered; it never affects other receivers, the producer or the gateway process. Plugin unload stops its consumers; their offsets persist.
- **FR-C8** GraphQL: D-21; argument `fromOffset` optional; each delivered message carries `eventId`, `offset`, `operation`, masked payload, `keyRedacted`, and control kinds (`gap`, `watermark_expired`).
- **FR-C9** All receivers of the same change get the same event id and stream offset (AC-1).

---

## 6. Security Requirements (all delivery paths: subscription, webhook, plugin)

- **SR-1 Identity binding.** Every receiver acts as exactly one identity (D-17). A receiver without a resolvable identity receives nothing.
- **SR-2 Table consent at registration.** Registration is rejected (400/403 with the same text as for an unknown table, no existence oracle) when the identity has no consent for the table; a plugin receiver failing this check is not bound and the rejection is audited.
- **SR-3 Authorization at delivery.** Each event is authorised per receiver at delivery time with the same chain as a governed read (`IStreamRlsPolicyEnforcer`: tenant, consent, Casbin, ReBAC, mandatory/virtual filters, row filter, masking). Revoked or expired consent takes effect for the next event delivered, including events already in the stream. Webhook outbox rows enqueued before a revocation are re-checked at send time (table-level consent of the identity) and dropped (`revoked`) if denied.
- **SR-4 Row scope.** A row the identity may not read produces no event. Transitions per D-3: out→in = insert, in→out = delete (rendered per D-7), out→out = nothing. Row filters that cannot be evaluated in memory (virtual filters with subqueries) drop the event (fail closed, existing behaviour).
- **SR-5 Columns.** Masked columns are delivered masked by the column's rule; denied columns are absent; the same applies to `before` and `after`. The per-receiver column projection never widens access.
- **SR-6 Delete key.** D-7. No row data in deletes.
- **SR-7 Fail closed.** If any decision input is unavailable (catalog, consent store, identity resolution, KEK, decryption failure), nothing is delivered for that receiver, the offset does **not** advance, the consumer retries with backoff, and the incident is logged (reason code only) and counted. A persistent failure surfaces as consumer state `failed` - never as skipped events.
- **SR-8 No bypass via the stream.** The stream tables are not exposed through any data API, WebSQL, OData, GraphQL type, MCP tool or admin endpoint payload; stream payloads are encrypted (D-2); only the delivery pipeline decrypts, in memory, per event. Governance-DB tables are excluded from the governed catalog (system tables, F-GOV-21 §4.4).
- **SR-9 Opaque identifiers.** Event ids (D-5) and key fingerprints (D-6) reveal no key values; logs never contain payloads, keys or enforcer reason text above Debug.
- **SR-10 Audit without payload.** Per receiver (batched like `CDC_WEBHOOK_DELIVERY_BATCH`): delivered, denied, revoked, dead-lettered, gap, with receiver id, identity SID, table, row count, result, offset range. Registration, rejection, deletion, pause/resume, offset reset, dead-letter replay and stream resync are audited individually.
- **SR-11 Least privilege.** Source login: `CONNECT`, `SELECT` on tracked tables, `VIEW CHANGE TRACKING` on tracked tables. No catalog key queries (architecture test extended to `Autheris.Extensions/Cdc`). Governance login needs DML on the new tables only (no DDL in `Mode=Verify`).
- **SR-12 Plugin trust.** Only SHA-256 trust-listed plugins in an isolated load context may be bound; the identity comes from operator configuration, not from the plugin.
- **SR-13 Resource bounds.** Caps on consumers per stream, in-flight events, payload size (existing `MaxPayloadBytes`; oversize → dead letter `bounds`, never truncation), retention.

---

## 7. Acceptance Criteria

Issue criteria (verbatim intent) and refinements:

- **AC-1** For one source change, a GraphQL subscription, a webhook and a C# DLL plugin (all permitted) receive the same event id and stream offset.
- **AC-2** With 10 consumers on one table, the source sees exactly one `CHANGETABLE` query per poll interval (plus none per consumer); verified by counting statements on the source connection.
- **AC-3** A consumer that is down does not delay the others (their delivery latency stays within one poll interval); on return it receives every retained event from its committed offset, in order, then continues live.
- **AC-4** An expired watermark produces a `watermark_expired` event in the stream and, under policy `Restart`, polling continues at the current version without any manual SQL; under `Halt`, the documented resync endpoint restarts it.
- **AC-5** A table without a declared primary key, with a declared PK that differs from the change-tracking key, without change tracking, or (default) without snapshot isolation is rejected at activation with its stable code; no `sys.indexes` query is issued (statement capture + architecture test).
- **AC-6** Failover mid-batch: killing the producer between reading and committing produces no lost and no duplicated stream events (offsets dense, change versions continuous).
- **AC-7** Restart during delivery: a durable consumer restarted mid-batch redelivers at most the uncommitted events, with identical event ids; receivers can deduplicate.
- **AC-8** Two consumers with different speeds: the fast one stays live; the slow one lags and catches up; neither drops events while within retention.
- **AC-9** A consumer behind retention receives exactly one `gap` event with the resume offset, then continues.
- **AC-10** A plugin handler that throws or exceeds its timeout is retried, then dead-lettered; other receivers and the producer are unaffected; the process does not crash.
- **AC-11** Security: registration without table consent is rejected; consent revoked after enqueue stops delivery of already-streamed events (and of pending outbox rows); a row moving out of the receiver's row filter arrives as a delete, moving in as an insert, never-visible rows produce nothing; masked columns arrive masked, denied columns are absent (before and after); a delete contains the key only if clear (or as the read's HMAC); an unresolvable decision delivers nothing and does not advance the offset.
- **AC-12** Every delivery, denial, revocation, gap and dead letter is audited per receiver without payload; no payload or key value appears in logs or metrics labels.
- **AC-13** The new tables exist in the `0001` baseline DDL of SQLite, PostgreSQL and SQL Server and in the schema-shape snapshots; shape verification passes on a fresh database; `0001` checksum constants unchanged.
- **AC-14** No row data travels over `ICdcEventChannel`/Redis Pub/Sub for the MSSQL path.

---

## 8. Test Strategy

- **Unit (TDD, `tests/Autheris.Tests.Unit`)**: offset/start-position/gap logic; retention calculation; transition matrix (before/after visibility × operation, incl. `beforeUnknown`); delete key rendering per masking rule; event-id and fingerprint derivation (opacity, stability); activation-check classification; per-table option validation (`IValidateOptions`); fail-closed paths of the delivery pipeline (each decision input throwing); plugin dispatcher timeout/exception isolation; backpressure limits.
- **Integration (Testcontainers SQL Server + governance DB on SQLite/PostgreSQL/SQL Server)**: AC-2 statement counting; AC-6 failover mid-batch (fault injection between read and commit, lease takeover with higher fencing token); AC-7 restart during delivery; AC-8 two consumers at different speeds; AC-9 retention gap; AC-4 expired watermark (shrink CT retention / force cleanup); AC-5 least-privilege login with only `SELECT` + `VIEW CHANGE TRACKING` (and proof that a missing grant fails clearly); AC-10 throwing plugin from a trust-listed test DLL; AC-1 three receiver kinds end-to-end; AC-13 shape snapshots per engine.
- **Security regression**: add issue #99 ids to the security regression traceability catalog (SR-1..SR-13 → tests); no-oracle checks for registration errors; log/metric scrubbing assertions.
- **Architecture tests**: no source key-catalog queries in `Autheris.Extensions/Cdc/**`; plugins never receive `CdcEvent`; stream tables not referenced by data/API projects except the delivery pipeline.
- **Mutation testing** (Stryker) on the transition matrix, fencing checks and the fail-closed delivery decision.

---

## 9. Risks

| ID | Risk | Mitigation |
|---|---|---|
| R-1 | Governance DB becomes a high-volume log store (write amplification: stream + last image + watermark). | Retention bounds, batch inserts per poll, per-table caps, metrics; external broker backend is a documented non-scope escape hatch. Phase 2 sizes indexes and purge. |
| R-2 | Last-image store holds a protected copy of every changed row (until delete). | Encryption, catalogued columns only, purge on table deactivation; Phase 2 decides whether images also expire with retention (then `beforeUnknown` falls back to D-7 rules). |
| R-3 | Key management for stream encryption (rotation, KEK loss = unreadable stream). | Envelope keys with key id (unlike webhook `v1`), rotation without re-creation; KEK loss → streams fail closed and require resync (documented). |
| R-4 | Live group/role resolution for service accounts may not exist for all identity providers. | D-17 fallback to bounded snapshot; Phase 3 to confirm. |
| R-5 | `ALLOW_SNAPSHOT_ISOLATION` may be unavailable at customer sites. | Explicit opt-out with documented risk (D-14). |
| R-6 | Breaking changes: event id format, removal of history replay and `before_subscription`, webhook subscription schema, GraphQL message shape, F-GOV-21 exception removed. | Allowed (no production); called out in Phase 6 docs. |
| R-7 | Per-consumer metrics label cardinality. | Capped consumer count; consumer id only. |
| R-8 | `beforeUnknown` deletes for filtered receivers are suppressed (D-7), so a receiver may keep a row that moved out before its first observed change. | Documented; resync via governed read; server-side snapshot (non-scope) closes it later. |

---

## 10. Increments (each independently mergeable, each through Phases 2-6)

| Inc | Title | Content | Acceptance |
|---|---|---|---|
| **I1** | Producer hardening and durable stream | D-1 (stream + last-image tables), D-2 encryption, D-3, D-4, D-5, D-6, D-9, D-11, D-12..D-16; per-table lease and options; activation checks; retention job; stream status/resync endpoints; channel becomes wake-up signal; a compatibility tail reader feeds the existing GraphQL and webhook paths from the stream (now with opaque ids) so nothing regresses; F-GOV-21 exception removed. | AC-2, AC-4, AC-5, AC-6, AC-13, AC-14 |
| **I2** | Durable consumers, webhooks and GraphQL on the stream | Consumer registry, offsets, leases, start positions, gap events, backpressure, dead letters, per-consumer metrics and admin endpoints; webhook subscriptions as durable consumers with identity binding, registration-time consent, send-time revocation check, per-receiver filters; GraphQL shared tail reader with `fromOffset`; transition matrix and delete rendering for all paths; audit. | AC-1 (subscription + webhook), AC-3, AC-7, AC-8, AC-9, AC-11, AC-12 |
| **I3** | C# DLL plugin receivers | `GovernedCdcChange`, operator-configured plugin receivers with identity, dedicated dispatcher (timeout, retry, dead letter, isolation), registration checks, audit. | AC-1 (all three kinds), AC-10, AC-11/AC-12 for plugins |

Recommended order I1 → I2 → I3; I2 and I3 share the consumer framework from I2.
