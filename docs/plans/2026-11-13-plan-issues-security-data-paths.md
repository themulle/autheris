# Plan: open issues, security hardening (data paths, audit, integrations)

- Date: 2026-11-13 · Base: `origin/main` @ `10400ec4` · Phase 2 (csharp-architect) · Plan status: PLANNED, Phase 3 pending
- Issues: #473, #475, #476, #485, #491, #503, #505, #508
- Parent: [master plan](00-master-plan-overview.md) · [simplification WP specs](simplification/README.md) · [PRD](2026-11-05-req-project-simplification.md) section 2.0 (OD-1..OD-45, binding; a later OD wins) · freeze rule PRD 5.1 · PR limit 400 net added `src/` lines
- Freeze: every work package here is a security fix or hardening of a core path; it needs no owner approval (PRD 5.1 exception). A PR that grows a freeze ratchet (option, `*Enabled` flag, endpoint) carries `freeze-exception` and updates the committed baseline.
- Disclosure (SEC-SIMPL-01): advisories are named only by GHSA id and neutral component.

## Summary

| Issue | Verdict | WP / spec | Open on main | Size | Phase 3 |
|---|---|---|---|---|---|
| #473 | NEW PLAN (assessment routed the shutdown part to WP-SIMPL-20; WP-20c merged without it) | WP-OI-SEC-DP-1, -2 | both items | S+S | yes |
| #475 | COVERED | [WP-SIMPL-36](simplification/WP-SIMPL-36-security-lane.md) sub-package 5, PR 5c | all items (not started) | M | covered by SEC-SIMPL review; delta no |
| #476 | NEW PLAN (assessment: before or inside WP-SIMPL-29; spec has no steps) | WP-OI-SEC-DP-4 | all items | S | yes |
| #485 | PARTLY COVERED / part OBSOLETE | Arrow (WP-16, #540) and ITSM (WP-12, #546) deleted; rest WP-OI-SEC-DP-5..7 | 9 of 11 items | M (3 PRs) | yes (DP-5 only) |
| #491 | PARTLY COVERED | procedures kill switch done (WP-SIMPL-39 PR 1); rest WP-OI-SEC-DP-8, -9 | 6 of 7 items | M (2 PRs) | yes |
| #503 | PARTLY COVERED | `RebacEndpointFilter` deleted by WP-SIMPL-46 PR 5; rest WP-OI-SEC-DP-10..13 | 6 of 7 items | L (4 PRs) | yes |
| #505 | PARTLY COVERED | [WP-SIMPL-45](simplification/WP-SIMPL-45-governed-write-access.md) PR 3 (merged), PR 5; gap WP-OI-SEC-DP-14 | outcome audit, budget, read-only reads | S (gap) | yes (gap) |
| #508 | COVERED | WP-SIMPL-36 sub-package 4, PR 4c | all 5 items (not started) | M | covered; delta check |

## #473 Audit: in-flight batch is lost on shutdown; redaction misses common secret keys and nested values

- Verdict: NEW PLAN. The 2026-11-05 assessment split it (redaction to the security lane, shutdown loss to WP-SIMPL-20); WP-20c (#630) unified the writer into `GovernanceRepository.Audit.cs` but kept the defect, and no security-lane sub-package lists #473.
- Verified state on main:
  - `src/Autheris.Infrastructure/Persistence/GovernanceRepository.Audit.cs` `ProcessAuditChannelAsync` (:452-547): `_auditLock.WaitAsync(_auditCts.Token)` sits outside the inner `try`, `catch (OperationCanceledException) { break; }` and `Task.Delay(..., _auditCts.Token)` leave the loop; the outer `catch (OperationCanceledException)` is followed by `batch.Clear()` (:517) before the drain, so up to 250 dequeued entries are lost without dead letter or `AuditDeadLetterCounter`.
  - `src/Autheris.Application/Audit/AuditGroupCommitSink.cs` `DrainLoopAsync` (:152-186): on shutdown cancellation during `FlushBatchAsync(batch, entries, shutdownToken)` the `finally` clears `batch` without completing the held `Completion` sources (verify): required-audit callers wait until their own token fires and the entries are not persisted.
  - `src/Autheris.Domain/Audit/AuditDetailsBuilder.cs` (:14-30, :46): exact-match `HashSet` lookup; `client_secret`, `x-api-key`, `connection_string`, `passwd`, `set-cookie` pass; non-string values (dictionaries, lists, anonymous objects) are stored unsanitised.
- Root cause: cancellation of the shutdown token is treated as "stop" instead of "flush what is held"; redaction keyed on whole-key equality and on top-level strings only.
- Design:
  - DP-1: one private helper `PersistOrDeadLetterAsync(batch, reason)` used by the main loop and the drain; on any cancellation the held batch is written with `CancellationToken.None` under a fixed 5 s timeout (constant, no option), on failure dead-lettered and counted; if the dead-letter write also fails, log Critical with the count and add to a new counter `AuditLostOnShutdownCounter` (never silent). `batch.Clear()` only after a persist or dead-letter outcome. `AuditGroupCommitSink`: on shutdown, flush the held batch with `CancellationToken.None`, then complete every held and queued `Completion` (success or `AuditUnavailableException`); no completion source is left pending.
  - DP-2: normalise the key (lower-case, strip `-`, `_`, `.`), redact when it contains one of `secret, token, password, passwd, pwd, apikey, key, cookie, authorization, bearer, credential, connectionstring, privatekey`; keep a small allow-list of non-secret keys that would match (`keyset`, `primarykey`, `key_columns`, `idempotency_key_hash`) pinned by test. Sanitise nested `IDictionary`, `IEnumerable` and JSON elements recursively (depth 4, beyond that `[TRUNCATED]`), strings through `SanitizeAndTruncate`. The audit hash input fields are unchanged (SEC-SIMPL-24).
- Work packages: WP-OI-SEC-DP-1 `fix(audit): persist or dead-letter the in-flight batch on shutdown` (~80 src); WP-OI-SEC-DP-2 `fix(audit): substring and nested redaction` (~90 src). Independent; DP-1 first.
- Tests (`[Trait("Category","Security")]`):
  - `GovernanceRepositoryAuditShutdownTests.Cancel_during_batch_write_persists_or_dead_letters_every_entry` (PostgreSQL and SQL Server contract fixture), `.Cancel_while_waiting_for_audit_lock_keeps_batch`, `.Dead_letter_failure_increments_lost_counter_and_logs_critical`.
  - `AuditGroupCommitSinkTests.Shutdown_mid_flush_completes_every_waiter`, `.Shutdown_mid_flush_persists_held_batch`.
  - `AuditDetailsBuilderRedactionTests.Redacts_by_normalised_substring` (Theory: `client_secret`, `x-api-key`, `connection_string`, `passwd`, `set-cookie`, `X-Authorization`), `.Redacts_nested_dictionary_and_array_values`, `.Keeps_allow_listed_non_secret_keys`, `.Depth_beyond_limit_is_truncated`, `.Control_characters_in_nested_strings_are_replaced`.
- Dependencies: none (WP-20 merged). Coordinate with WP-SIMPL-45 PR 5 (audit sequence) only through the shared writer.
- Risks: over-redaction of diagnostic keys (mitigated by the pinned allow-list); a 5 s flush prolongs shutdown (bounded, below the host shutdown timeout of the Helm chart).
- Size: S + S (~170 src, ~260 test, 2 PRs).

## #475 Table cache applies CDC mutations as ToString() string literals

- Verdict: COVERED by WP-SIMPL-36 sub-package 5, step 3 / PR 5c (DuckDB and table cache stay core, OD-15). Not started (no branch).
- Verified on main: `src/Autheris.Infrastructure/Caching/TableCache/DuckDbPersistentTableCacheStore.cs` `ApplyMutationsAsync` (:421, values :450, :464) and `InMemoryTableCacheStore.cs` (:247, :269, :283-284, :298) still build `'{v.ToString()}'` literals, `"pk" = NULL` predicates and only `LogWarning` on failure.
- Gap to the spec (add to PR 5c): null-key mutations are rejected (not `IS NOT DISTINCT FROM`, a cache key is never null for a declared key) and a failed mutation invalidates the table through the existing invalidation path; test names `TableCacheMutationTests.Typed_values_round_trip_under_de_DE` (decimal, DateTime, DateTimeOffset, bool, byte[]), `.Null_key_mutation_is_rejected_and_table_invalidated`, `.Failed_mutation_invalidates_table` (`[Trait("Category","Security")]`, real DuckDB, integration project per WP-SIMPL-22).
- Size: M (~150 src, ~200 test, 1 PR).

## #476 CDC custom query poller enforces read-only only on SQLite and PostgreSQL

- Verdict: NEW PLAN (WP-SIMPL-29 requires the fix "before or inside" the events extraction but has no steps; WP-29 PR 1 #642 only moved the GDPR report).
- Verified on main: `src/Autheris.Extensions/Cdc/CustomQuery/CustomQueryPollerService.cs` `EnforceReadOnlySemanticsAsync` (:371-404) sets `PRAGMA query_only` for SQLite, a `READ ONLY` transaction for PostgreSQL and returns `null` for every other provider (:404); called at :131 before `QueryTemplate`.
- Root cause: provider switch with a permissive default.
- Design: exhaustive switch over the admitted data-source dialects (OD-12): PostgreSQL `SET TRANSACTION READ ONLY`; Oracle `SET TRANSACTION READ ONLY`; SQL Server: transaction that is always rolled back plus `ApplicationIntent=ReadOnly` on the poller connection string; SQLite keeps `query_only` until WP-SIMPL-31; any other provider (including Databricks, Q-SECDP-6) throws `InvalidOperationException` (fail closed). Startup: a poller whose data source dialect is unsupported fails options validation, not at the first poll.
- Work package: WP-OI-SEC-DP-4 `fix(cdc): read-only guard for every poller provider, fail closed` (~80 src). Must merge before the WP-SIMPL-29 events-module PR.
- Tests (`[Trait("Category","Security")]`): `CustomQueryPollerReadOnlyTests.Unsupported_provider_throws`, `.Unsupported_provider_fails_options_validation`, `.Oracle_sets_read_only_transaction_before_query`, `.SqlServer_rolls_back_and_sets_read_only_intent`; integration `CustomQueryPollerReadOnlyIntegrationTests.Write_in_query_template_is_refused_by_database` (PostgreSQL, SQL Server; Oracle in the nightly suite).
- Risks: SQL Server has no session-level read-only mode; the rollback-only transaction is the effective guard (documented in the code comment), the read-only login stays the operator control.
- Owner question: Q-SECDP-6. Size: S (~80 src, ~150 test, 1 PR).

## #485 Hardening backlog (streaming errors, validation, caches, sync-over-async, DI validation)

- Verdict: PARTLY COVERED / part OBSOLETE.
- Per item on main:
  - ArrowFlight mid-stream error: OBSOLETE, `ArrowFlightSqlEndpoints` deleted by WP-SIMPL-16 (#540); remaining `ArrowExportEndpoints.cs` is out of the issue's scope.
  - ITSM non-English text: OBSOLETE, `Itsm/` deleted by WP-SIMPL-12 (#546).
  - Async job `format` unvalidated: OPEN. `AsyncQueryJobManager.cs:300` lower-cases any string; `AsyncJobEndpoints.cs:164-166` uses it in the file name.
  - TrustedDocuments double parse: OPEN. `TrustedDocumentsOnlyMiddleware.cs:71-90` parses, prints and hashes every request document.
  - Transport gate without document: OPEN. `TransportGateMiddleware.cs:42-76` calls `_next` when `OperationDocumentInfo.Document` is null and scans only `OperationType.Query` (:98).
  - `BoundedCache.TryRemove(key, out value)` returns `default` (`src/Autheris.Application/State/Caching/BoundedCache.cs:70-74`): OPEN. `GetOrAddAsync` (:98-112) shares one factory task; caller cancellation captured by the factory closure fails all waiters (verify per call site).
  - `SubgraphResultMasker.MaskResultData` sync-over-async (:265-289): OPEN; no caller outside the class (grep), so the sync overloads are deleted.
  - Rewriter analysis cache: OPEN. `GovernedSqlRewriter.cs:73` `BoundedCache<string, SqlQueryMetadata>(4_096)`, statements up to `RestApiOptions.MaxQueryLength` 64,000 chars.
  - dbt webhook dedup: OPEN. `DbtWebhookReceiver.cs:75` static `ConcurrentDictionary`, O(n) prune above 10,000 (:160-173), `Malformed payload: {ex.Message}` (:109); WP-SIMPL-28 PR 1 (#637) did not change it.
  - `SqlIdentifierHelper` `""` unescape: FIXED (`src/TrinoSqlEngine/SqlIdentifierHelper.cs:58, :91`); dotted quoted identifier in pre-check metadata: verify with the test below, fix only if red.
  - DI scope validation: OPEN. `Program.cs:49` `ValidateScopes = IsDevelopment()`; unit tests validate partial compositions only.
  - Non-English text: OPEN in `Dockerfile:10-12, :27`; `docs/configuration-guide.md:614` verify.
- Work packages:
  - WP-OI-SEC-DP-5 `fix(graphql): transport gate denies a missing document and scans all operation roots` (~60 src). Security.
  - WP-OI-SEC-DP-6 `fix: input validation and bounded caches` (~180 src): async job format allow-list `json|csv` -> 400 `invalid_format`; dbt dedup in a `BoundedCache` with TTL (24 h, 10,000 entries) and fixed message `Malformed payload.`; `BoundedCache.TryRemove` returns the removed value and `GetOrAddAsync` runs the shared factory with `CancellationToken.None` while each waiter uses `WaitAsync(ct)`; delete sync `MaskResultData`; rewriter skips caching statements above 8,192 chars (constant); trusted documents cache hash by exact UTF-8 bytes (bounded, 1,024 entries) before parse.
  - WP-OI-SEC-DP-7 `test: full-host DI scope validation; English-only text` (~10 src): integration test builds the production host with `ValidateScopes = true` and `ValidateOnBuild = true`; translate the Dockerfile comments.
- Tests: `TransportGateMiddlewareTests.Missing_document_is_denied`, `.Mutation_root_on_disallowed_source_is_denied`, `.Subscription_root_on_disallowed_source_is_denied` (`[Trait("Category","Security")]`); `AsyncJobEndpointTests.Unknown_format_returns_400`, `.Overlong_format_returns_400`; `DbtWebhookReceiverTests.Malformed_payload_message_is_fixed`, `.Dedup_entries_expire_and_stay_bounded`; `BoundedCacheTests.TryRemove_returns_removed_value`, `.First_caller_cancellation_does_not_fail_other_waiters`; `GovernedSqlRewriterCacheTests.Statement_above_cap_is_not_cached`; `TrustedDocumentStoreTests.Same_bytes_hit_hash_cache_without_parse`; `SqlIdentifierHelperTests.Quoted_identifier_with_dot_is_not_a_qualified_name`; `HostCompositionScopeValidationTests.Production_host_has_no_captive_dependencies`.
- Dependencies: DP-6 dbt part before the WP-SIMPL-28 extraction PR; DP-5 independent.
- Risks: the full-host scope test may reveal existing captive dependencies (fix them in the same PR or record them as a follow-up issue; never exclude them silently).
- Size: M (~250 src, ~350 test, 3 PRs).

## #491 Remove or secure unwired code paths and add kill switches

- Verdict: PARTLY COVERED. Procedures kill switch FIXED by WP-SIMPL-39 PR 1: `ProcedureEndpointRoutes.cs:33-35` maps only when `SqlEndpoints:Procedures:Enabled`.
- Per item on main:
  - MCP stdio: OPEN. `src/Autheris.Api/Mcp/McpStdioRunner.cs:46` creates a session from caller-chosen `servicePrincipalId`/tenant via `McpProtocolHandler.CreateSession(string, string)` (`src/Autheris.Application/Mcp/Services/McpProtocolHandler.cs:48`); registered in `GatewayMcpServiceExtensions.cs:61`, no host entry point invokes it (grep).
  - `GovernedTableCacheExecutionService`: OPEN, registered (`TableCacheServiceCollectionExtensions.cs:47`), no consumer.
  - `TableCacheAdminEndpoints.cs:15`: OPEN, `MapTableCacheAdminEndpoints` never called.
  - dbt file helpers: OPEN, test-only callers: `DbtMetadataIngestionService.IngestManifestFileAsync` (:238, prefix denylist :255-285), `DbtExposurePublisher.ExportExposuresFileAsync` (:67, `File.WriteAllTextAsync` :81), `DbtContractValidator.ValidateContractsFileAsync` (:22). Correction to the issue: `GenerateExposuresYamlAsync` and the contract validator are wired through `DbtEndpoints.cs:86, :195`; the exposure YAML is built by string concatenation (`DbtExposurePublisher.cs:21-60`).
  - `TableStreamEndpoints.cs:164, :170`: OPEN, `User.IsInRole("admin")` bypasses inactive and hidden checks on bulk/stream.
  - Async jobs: OPEN, `MapAsyncJobEndpoints()` mapped unconditionally (`GatewayApplicationBuilderExtensions.cs:294`); `AsyncJobOptions` has no `Enabled`.
- Design: DP-8 deletion-only PR (net negative): `McpStdioRunner`, `IMcpStdioRunner`, the `CreateSession(string, string)` overloads without authentication, `GovernedTableCacheExecutionService`, `TableCacheAdminEndpoints`, the three file-based dbt helpers and their path validators (their tests move to the endpoint-level tests or are deleted with the code; security tests are accounted for per SEC-SIMPL-03). DP-9: exposure YAML through the YamlDotNet serializer (stays core) with quoted scalars; `TableStreamEndpoints` uses the shared catalog visibility helper (hidden objects only for admins and the data owner, OD-19/WP-41) instead of the role string; async jobs behind `Gateway:AsyncJobs:Enabled` in the `IReadPathSwitches` pattern (routes, background worker, MCP async tool listing).
- Work packages: WP-OI-SEC-DP-8 `chore(security): delete unwired MCP stdio, table cache admin and dbt file paths` (net about -600 src); WP-OI-SEC-DP-9 `fix(security): exposure YAML serializer, stream visibility helper, async job kill switch` (~120 src, `freeze-exception` for the new switch).
- Tests (`[Trait("Category","Security")]`): `UnwiredCodeArchitectureTests.No_unauthenticated_mcp_session_factory`, `DbtExposureYamlTests.Quote_and_newline_in_name_cannot_inject_yaml`, `TableStreamVisibilityTests.Bulk_and_stream_match_single_row_visibility_for_non_admin`, `.Role_named_admin_without_admin_right_sees_no_hidden_table`, `AsyncJobKillSwitchTests.Disabled_switch_returns_404_for_job_routes`, `.Disabled_switch_stops_worker_and_hides_mcp_tool`.
- Dependencies: DP-8 before the WP-SIMPL-28 dbt extraction PR; WP-SIMPL-42 step 13 drops the MCP stdio usage scope if Q-SECDP-1 confirms deletion. DP-9 stream part after WP-SIMPL-46 PR 3 (#683) to avoid conflicts.
- Owner questions: Q-SECDP-1, Q-SECDP-2. Size: M (DP-8 net negative, DP-9 ~120 src, ~250 test, 2 PRs).

## #503 Integration hardening (plugins, OpenLineage, OpenJev, OLAP default, WebSocket staleness)

- Verdict: PARTLY COVERED. Rule 5 of the assessment required the plugin fix before WP-SIMPL-25; WP-25 merged (#548) without it, so it is now a standalone security PR. `RebacEndpointFilter` body cap: closed by WP-SIMPL-46 PR 5 (deletes the filter, OD-30); no work here.
- Per item on main:
  - Plugins: OPEN. `src/Autheris.Infrastructure/Plugins/PluginManager.cs:165` unsorted `Directory.GetFiles`; `ReadVerifiedBytes` (:232) outside the per-file `try` (:236); `_plugins[pluginInstance.Name] = ...` (:271) overwrites silently.
  - OpenLineage: OPEN. `src/Autheris.Extensions/Lineage/OpenLineageClient.cs:120` `http://localhost:5000` fallback; response body logged (:143).
  - OpenJev: OPEN. `OpenJevClient.cs`, `JustificationTriageService` registered (`GatewayGovernanceServiceExtensions.Catalog.cs`, `LineageExportServiceCollectionExtensions.cs`), no consumer.
  - OLAP default: OPEN. `DuckDbOlapOptions.Enabled = true` (`src/Autheris.Domain/Options/Storage/DistributedCacheOptions.cs:59`); anonymous callers allowed on Quickstart (`DuckDbOlapEndpoints.cs:56-58`), no route-level `RequireAuthorization`.
  - Inert settings: `IDelegationCapability.SourceEnforcesRowSecurity/Masking` never read (only `DatabricksOboTokenProvider.cs:64-65`); `Table.EnforcementMode` persistence: verify, hand to WP-SIMPL-06/32 if inert.
  - WebSocket: PARTLY FIXED. Session is capped at token `exp` (`WebSocketAuthInterceptor.ResolveSessionExpiry` :106, :323); the timer re-checks only revocation (:481); group SIDs captured at `connection_init` stay effective.
- Design: DP-10 plugins: ordinal sort, `ReadVerifiedBytes` inside the per-file `try`, duplicate plugin names reject both (log Error, load neither), one bad DLL never aborts the others. DP-11: OpenLineage disabled when no endpoint is set, URL through `EgressUrlPolicy`, https outside Development, log status code only; delete OpenJev and `JustificationTriageService` with registrations. DP-12: `DuckDbOlap:Enabled` default `false`, Quickstart opts in; route group `RequireAuthorization()` (Q-SECDP-3); delete `SourceEnforces*`. DP-13: on the revalidation timer re-resolve canonical effective group SIDs (WP-SIMPL-43 token plus SCIM) and close the socket when they differ (Q-SECDP-5).
- Work packages: WP-OI-SEC-DP-10 `fix(plugins): deterministic load, duplicate rejection, per-file isolation` (~60 src); WP-OI-SEC-DP-11 `fix(lineage): OpenLineage egress policy; delete OpenJev triage` (~40 src, net negative); WP-OI-SEC-DP-12 `fix(olap): default off and authenticated; delete inert enforcement flags` (~40 src); WP-OI-SEC-DP-13 `fix(graphql): WebSocket sessions follow group changes` (~90 src).
- Tests (`[Trait("Category","Security")]`): `PluginManagerTests.Duplicate_plugin_name_loads_neither`, `.Load_order_is_ordinal`, `.Unlisted_dll_does_not_abort_other_plugins`; `OpenLineageClientTests.Http_endpoint_refused_outside_development`, `.Unset_endpoint_disables_push`, `.Error_body_not_logged`; `DuckDbOlapEndpointTests.Disabled_by_default`, `.Anonymous_caller_gets_401_on_quickstart`; `WebSocketAuthInterceptorTests.Group_removal_closes_session_on_revalidation`, `.Unchanged_groups_keep_session`.
- Dependencies: DP-11 before the WP-SIMPL-29 Lineage extraction; DP-13 after WP-SIMPL-43 PR 3b (merged) and after WP-SIMPL-46 PR 3 (#683); DP-12 independent (OD-15 keeps OLAP core).
- Owner questions: Q-SECDP-3, Q-SECDP-5. Size: L (~230 src, ~400 test, 4 PRs).

## #505 DML hardening: durable audit, write-protected columns, read-only transactions, write budget

- Verdict: PARTLY COVERED by [WP-SIMPL-45](simplification/WP-SIMPL-45-governed-write-access.md) and WP-SIMPL-36 sub-packages 2-3.
- Per item on main:
  - Write-protected columns: FIXED by WP-SIMPL-45 PR 3 (#653): `TableModels.cs` `WriteProtected`, `AstSecurityVisitor.TypedDml.cs`, `WriteGrantEvaluator.cs`.
  - Intent record before execution: FIXED (`GovernedSqlExecutor.cs:535` `WebSqlDmlIntent` via `RecordRequiredAsync`, `GovernedSqlExecutor.Audit.cs:60`).
  - `MaxAffectedRows <= 0` with `AllowDml`: FIXED, startup error in `WebSqlDmlOptionsValidator.cs:29-31`.
  - Outcome record after commit swallowed: OPEN, `RecordCommittedDmlAuditAsync` (`GovernedSqlExecutor.Audit.cs:78-90`) logs Critical; covered by WP-SIMPL-45 PR 5 step 9 (outcome before commit, key set, value hash).
  - Write budget: OPEN, covered by WP-SIMPL-45 PR 5 step 10.
  - DB-side read-only reads: OPEN; WP-SIMPL-45 step 11 separates a `WriteConnection` (SELECT never uses it) but the read connection itself is not opened read-only. Gap: WP-OI-SEC-DP-14.
- Design (gap only): `DbSessionContextInitializer` (`src/Autheris.Application/Services/DbSessionContextInitializer.cs`) opens user reads in a read-only transaction where the engine supports it: PostgreSQL `SET TRANSACTION READ ONLY`, Oracle `SET TRANSACTION READ ONLY`; SQL Server and Databricks rely on the read-only login (WP-45 privileges report).
- Work package: WP-OI-SEC-DP-14 `fix(sql): read-only transaction for user reads` (~60 src), after WP-SIMPL-45 PR 5 (shares the executor).
- Tests (`[Trait("Category","Security")]`): `ReadOnlyReadSessionTests.Select_runs_in_read_only_transaction_on_postgresql`, `.Select_runs_in_read_only_transaction_on_oracle` (nightly), `.Volatile_function_write_is_refused_by_database_on_read_path`; the issue's `Dml_AuditFailureAfterCommit_IsNotSilent` is owned by WP-45 PR 5.
- Owner question: Q-SECDP-4. Size: S (~60 src, ~120 test, 1 PR).

## #508 MCP hardening

- Verdict: COVERED by WP-SIMPL-36 sub-package 4, PR 4c (#506 and #507 of the same sub-package merged as #646, #647). Not started.
- Verified on main (all items open): `GatewayMcpQueryExecutor.cs:42, :75` optional `IPersistedToolValidator?`; `PersistedToolValidator.cs:76-79` checks undeclared parameters only when `properties` exists, top level only; prompts `explore_dataset` / `audit_access_compliance` interpolate `dataset` and `principal` raw (`GatewayMcpServer.cs:337-363`); `GatewayMcpQueryExecutor.cs:404` fabricated owner `data-governance@autheris.local`; `SemanticMcpCompiler.VisibleTablesAsync` returns all tables without principal and visibility service (`src/Autheris.Application/Mcp/Services/SemanticMcpCompiler.cs`, last branch); `McpEndpoints.cs:195` `g.First().Value` collapses multi-valued claims.
- Gap: none in scope; sequencing only: PR 4c after WP-SIMPL-46 PR 3 (#683 edits `GatewayMcpQueryExecutor.cs`); `VisibleTablesAsync` empty-list fix must keep the WP-41 discovery service path. Tests as named in the issue plus `McpPromptTests.Dataset_argument_with_newline_is_rejected`.
- Size: M (~200 src, 1 PR).

## Open owner questions

| Id | Question | Recommended default |
|---|---|---|
| Q-SECDP-1 | #491: delete the unauthenticated MCP stdio runner, or keep it with authentication? (WP-SIMPL-42 step 13 lists it as a usage scope) | Delete; MCP is served over HTTP by the SDK; WP-SIMPL-42 drops the stdio scope |
| Q-SECDP-2 | #491: async job kill switch key and default | `Gateway:AsyncJobs:Enabled`, default `true` (behaviour kept, kill switch for operators), in `IReadPathSwitches`; ratchet baseline updated under `freeze-exception` |
| Q-SECDP-3 | #503: OLAP engine default and anonymous Quickstart access | `DuckDbOlap:Enabled=false` by default, Quickstart opts in; authentication required in every profile |
| Q-SECDP-4 | #505: open user reads in a database read-only transaction in addition to the WP-45 read/write connection split | Yes for PostgreSQL and Oracle; SQL Server and Databricks rely on the read-only login and the privileges report |
| Q-SECDP-5 | #503: WebSocket behaviour when effective group SIDs change during a session | Close the socket on the next revalidation tick (client reconnects with fresh claims); no in-place claim swap |
| Q-SECDP-6 | #476: which providers may run custom query pollers | PostgreSQL, SQL Server, Oracle (and SQLite until WP-SIMPL-31); Databricks and any other provider refused at startup |

## Phase 3 review scope

- Required (csharp-security-expert): WP-OI-SEC-DP-1 and -2 (audit integrity, redaction allow-list), -4 (poller guards per engine), -5 (transport gate roots), -8 and -9 (deletions keep every security test accounted for, SEC-SIMPL-03; visibility helper), -10 (plugin loading), -11 (egress policy), -12 (OLAP auth default), -13 (WebSocket session termination), -14 (read-only reads).
- Delta check only: #475 (WP-SIMPL-36 PR 5c, add the null-key and invalidation conditions above) and #508 (WP-SIMPL-36 PR 4c, re-verify line numbers after WP-SIMPL-46 PR 3).
- Not needed: WP-OI-SEC-DP-6 and -7 (robustness and test-only), unless the reviewer wants the dbt message change in scope.
