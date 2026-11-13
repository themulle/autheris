# Phase 3 security review: open-issue plans, priority group 1

- **Date:** 2026-11-13 · **Phase:** 3 (`csharp-security-expert`) · **Base verified:** `origin/main` @ `10400ec4`
- **Reviewed:** [`2026-11-13-plan-issues-security-identity-transport.md`](2026-11-13-plan-issues-security-identity-transport.md) (#472, #483, #487, #489) and [`2026-11-13-plan-issues-security-data-paths.md`](2026-11-13-plan-issues-security-data-paths.md) (#473, #503). Owner defaults `Q-SECID-1..6` and `Q-SECDP-3, -5` are taken as binding (2026-11-13); this review only adds conditions on how they are implemented.
- **Ids:** `SEC-OI-<issue>-<n>`. Every test below carries `[Trait("Category","Security")]`. A condition is binding for Phase 4 and Phase 5.

| Issue | WP | Verdict | Developer-ready |
|---|---|---|:---:|
| #483 | WP-OI-SEC-ID-2 | APPROVED WITH CONDITIONS (3 fail-open traps) | yes, with conditions |
| #483 | WP-OI-SEC-ID-3 | APPROVED WITH CONDITIONS | yes |
| #503 | DP-10 plugins | CHANGES REQUIRED (plan weakens a fail-closed abort) | after plan amendment below |
| #503 | DP-11 OpenLineage / OpenJev | APPROVED WITH CONDITIONS | yes |
| #503 | DP-12 OLAP | APPROVED WITH CONDITIONS | yes |
| #503 | DP-13 WebSocket | APPROVED WITH CONDITIONS | no (waits for WP-SIMPL-46 PR 3, #683) |
| #473 | DP-1 shutdown batch | APPROVED WITH CONDITIONS | yes |
| #473 | DP-2 redaction | CHANGES REQUIRED (wrong layer, 119 writers bypass it) | after plan amendment below |
| #487 | WP-OI-SEC-ID-4 | APPROVED WITH CONDITIONS | yes |
| #489 | WP-OI-SEC-ID-5 (WP-43 PR 4a) | APPROVED WITH CONDITIONS; split D-1 confirmed | yes |
| #472 | WP-OI-SEC-ID-1 | APPROVED WITH CONDITIONS (light) | yes |

Scope or order changes: SEC-OI-473-4 (redaction moves to the persistence chokepoint, DP-2 grows to about 150 src), SEC-OI-503-1 (integrity failures stay fatal), SEC-OI-483-1 (effective provider, not raw config), SEC-OI-487-1 (PR A makes the key mandatory in every non-Development host, so integration hosts that run as `Production`/`Staging` must set one in the same PR). Order inside clusters is unchanged: ID-2, ID-4, ID-5, ID-1, ID-3; DP-1 before DP-2; DP-11 before the WP-SIMPL-29 Lineage extraction.

## #483 TLS and supply chain

**Verified.** `GovernanceProviderGuard.ThrowIfUnsupported` (`src/Autheris.Api/Configuration/GovernanceProviderGuard.cs:41`) does `_ = env;` and reads raw configuration through three provider aliases and five connection-string aliases (runtime and migration). `ConnectionTlsPolicy.ValidateGovernanceConnectionTls` has no caller in `src/` (only `ProductionStartupChecks.cs:160` calls `Validate`, for `DataSources:Connections`). The runtime binds `GatewayOptions.GovernanceDb` whose `Provider` defaults to `"PostgreSql"` (`GovernancePersistenceOptions.cs:7`). `DataSourceProvider.TryResolveDialect` maps an **empty provider to SQLite** (`DatabaseDialect.cs:~210`), and `Validate` returns null for SQLite (`default:`).

**Threats.**
- Fail-open: the plan passes the guard's provider to the policy. With no provider key configured the runtime uses PostgreSQL, but the policy would resolve SQLite and return null, so a plaintext or `VerifyCA` governance connection passes the new check.
- Bypass by alias or key: the guard validates the first non-empty alias while the runtime reads the bound option; `MigrationConnectionString` (DDL login) is exempt from the plan. A weak migration connection leaks the DDL login.
- Policy gaps inside `Validate`: PostgreSQL `Trust Server Certificate`, `Root Certificate`/`SSL Mode` spelling variants and SQL Server `Encrypt=Strict|Mandatory` with `TrustServerCertificate`/`HostNameInCertificate` are not tested with the VerifyFull-only rule.
- Data-source rule is startup-only and applies to configured connections. If any API path creates a data source at runtime, it never meets the policy.

**Conditions.**
- SEC-OI-483-1: the guard validates the **effective** values: the bound `GovernanceDb.Provider` (default `PostgreSql`), `ConnectionString` and `MigrationConnectionString` of the same configuration sections the runtime binds, not the first alias. If aliases disagree, startup fails. An empty/unknown provider outside Development is a startup error, never an SQLite default.
- SEC-OI-483-2: the governance rule has no SQLite or other exemption (governance supports PostgreSQL and SQL Server only); the migration connection string gets the same rule; messages name the setting, never the value.
- SEC-OI-483-3: `Validate` becomes an exhaustive switch over `DatabaseDialect` with an unknown enum value returning an error; PostgreSQL requires `VerifyFull`, a parse error fails, and `Trust Server Certificate=true` combined with any mode is rejected; SQL Server rejects `TrustServerCertificate=true` and `Encrypt=Optional` (existing) and a parse error fails.
- SEC-OI-483-4: the developer greps for runtime data-source creation (admin API, tests of connection). If one exists, the policy is applied at that path or in the connection factory, not only at startup.
- SEC-OI-483-5 (ID-3): an invalid pin (40-hex) is a startup error that names the setting; a **configured but fully rejected** pin list must never degrade to "no pin, chain validation only" silently on a non-loopback endpoint (Q-SECID-3: a matching pin still overrides chain/name errors, a non-matching one fails). `policy.json` keeps `default: reject`; `rekorPublicKeyData` must be a parseable key (an empty string is invalid for `sigstoreSigned`); the subject regex stays pinned to `docker-publish.yml` on `main` and `v*` tags.

**Tests.** `ConnectionPolicyTests`: `Governance_empty_provider_is_postgresql_and_checked`, `Governance_migration_connection_without_verifyfull_rejected`, `Conflicting_provider_aliases_fail_startup`, `Postgres_trust_server_certificate_rejected`, `SqlServer_unparseable_connection_string_rejected`, `Unknown_dialect_value_rejected` (plus the plan's names). `RedisConnectionSecurityTests.Rejected_pin_list_does_not_fall_back_to_chain_only`. `SupplyChainAndDeploymentSecurityTests.PodmanPolicy_rekor_key_parses_and_default_is_reject`.

## #503 Integration hardening

**DP-10 plugins (CHANGES REQUIRED).** Verified `PluginManager.cs:165-290`: `ReadVerifiedBytes` throws `SecurityException` for an unlisted file or hash mismatch, and the `catch (SecurityException) { throw; }` makes the whole startup abort. That abort is the fail-closed control. The plan test `Unlisted_dll_does_not_abort_other_plugins` and "one bad DLL never aborts the others" would turn a planted or tampered DLL into a warning.
- SEC-OI-503-1: integrity failures (unlisted file, hash mismatch, inconsistent manifest, outside Development) stay fatal. Only `BadImageFormatException`, `ReflectionTypeLoadException`, IO and constructor errors are isolated per file. Rename the test to `Unlisted_dll_outside_development_aborts_plugin_loading` and add `Type_load_failure_does_not_abort_other_plugins`.
- SEC-OI-503-2: collisions are decided before any instance becomes active: two-phase load (collect, detect, then register). "Load neither" covers HTTP plugins (`_plugins`, `OrdinalIgnoreCase`), **action handlers** (`_actionHandlers[...]`, `:290`, plan missed) and the already-registered built-ins: `RegisterPlugin` (`:69`) and the loader (`:271`) both write the same dictionary, so a DLL can silently replace a built-in plugin. A name already present from any source rejects the DLL's plugin. Listeners (`BindListenerToEventBus`) of a rejected assembly are not bound; the `AssemblyLoadContext` is unloaded.
- SEC-OI-503-3: constructors of colliding plugins ran before detection; accept this residual (the DLL is already hash-trusted) but never call `Name`-dependent code paths after rejection; log names and counts only.
- Tests: `PluginManagerTests.Duplicate_name_with_builtin_rejects_the_dll`, `.Duplicate_action_handler_rejects_both`, `.Rejected_assembly_listener_is_not_bound`, `.Integrity_failure_is_fatal_in_non_development`, `.Load_order_is_ordinal` (existing names otherwise kept).

**DP-11 OpenLineage (verified `OpenLineageClient.cs:115-145`: localhost default, body logged, endpoint logged).** Client already uses `AddSecureOutboundHandlers` (connect-time IP check, `AllowAutoRedirect = false`, `SecureOutboundHttp.cs:85`).
- SEC-OI-503-4: unset endpoint disables the push (no default URL); non-Development requires `https` and `EgressUrlPolicy.ValidateStatic`; the bearer key is never sent over `http` (also in Development, unless the host is loopback); the log carries status code and host only (no body, no query string, no userinfo); validation failure disables the client with one startup error, not a per-event exception.
- SEC-OI-503-5: deleting OpenJev removes registrations only; the audit and the usage-scope tests that reference it are kept or moved (SEC-SIMPL-03 accounting).
- Tests: `OpenLineageClientTests.Bearer_key_not_sent_over_http`, `.Endpoint_with_userinfo_rejected`, `.Log_contains_no_query_string`.

**DP-12 OLAP.** Verified `GatewayPolicies.cs:101` sets an authenticated-user `FallbackPolicy`; the handler (`DuckDbOlapEndpoints.cs:56-58`) is anonymous only via the Quickstart exception, which also passes an empty `ClaimsPrincipal` into `IOlapStagingService`.
- SEC-OI-503-6: remove the `IsQuickstartProfile` exception from the handler and add `RequireAuthorization()` on the group (defence in depth); an anonymous principal never reaches the staging service. `DuckDbOlap:Enabled=false` default must also hold for the Helm/appsettings samples; `GraphQlAndSwitchStartupChecks` (`:158`, Quickstart outside Development) keeps failing.
- Tests: `DuckDbOlapEndpointTests.Anonymous_caller_gets_401_in_quickstart_through_full_pipeline`, `.Staging_service_never_called_with_anonymous_principal`. Deleting `SourceEnforces*` must not drop a security test (SEC-OI-503-5 accounting).

**DP-13 WebSocket.**
- SEC-OI-503-7: failure to re-resolve group SIDs on the tick closes the socket (fail closed, same as a changed set); the comparison uses the canonical `GroupSids` of the WP-43 PR 5b resolution only (plugin-enriched SIDs excluded, SEC-AZ-14); no in-place claim swap (Q-SECDP-5). Tests: `Group_resolution_failure_closes_session`, `Plugin_enriched_groups_do_not_keep_session_alive`. Blocked until #683 is merged.

## #473 Audit

**DP-1.** Verified `GovernanceRepository.Audit.cs:452-547`: `_auditLock.WaitAsync(_auditCts.Token)` is outside the inner `try`, the inner `catch (OperationCanceledException) { break; }` and the trailing `batch.Clear()` (`:517`) drop up to 250 dequeued entries. Verified `AuditGroupCommitSink.cs:152-200`: on the non-faulted path the `finally` calls `batch.Clear()` before the drain, so completions of the held batch are never set (hang until the caller's token fires).
- SEC-OI-473-1: no dequeued entry leaves memory without persist or dead letter; the dead-letter write inside the failure path (`:~490`) is itself guarded (today an exception there faults the processor task and kills the audit writer); a final failure logs Critical with the count and increments `AuditLostOnShutdownCounter`, never the entries.
- SEC-OI-473-2: the dead-letter reason stores exception type and SQL state only, not `ex.Message` (driver messages can quote row values); the dead-letter payload (full `DetailsJson`) is subject to the same redaction as live rows (SEC-OI-473-4).
- SEC-OI-473-3: a dead-lettered entry is replayed only through the chain writer (`seq`/`prev_hash`), never inserted directly; shutdown flush uses the same lock and chain path as the live loop. `AuditGroupCommitSink` completes every held and queued completion with success or `AuditUnavailableException` before returning.
- Tests: the plan names plus `Dead_letter_write_failure_does_not_fault_the_writer`, `Dead_letter_reason_has_no_exception_message`, `Replayed_dead_letter_keeps_chain_valid`.

**DP-2 (CHANGES REQUIRED).** `AuditDetailsBuilder` (`src/Autheris.Domain/Audit/AuditDetailsBuilder.cs`) is used by 3 callers (`AccessAuditMiddleware`, `AuthFailureAggregator`, `GovernedSqlExecutor`), while **119** places assign `DetailsJson = JsonSerializer.Serialize(...)` directly (endpoints, services). Fixing the builder does not protect them.
- SEC-OI-473-4: redaction runs at the single persistence chokepoint (the entry-to-row path of `GovernanceRepository.Audit.cs`/`AuditSqlCore`, and the sink before the hash input is computed) on the `DetailsJson` string: parse, redact recursively, re-serialise. Unparseable or over-size JSON is replaced by `{"redaction":"unparseable"}` or a truncation marker, never stored raw (fail closed). The hash chain covers the stored (redacted) form; document this in the audit-hash comment (SEC-SIMPL-24 input fields unchanged).
- SEC-OI-473-5: the builder reuses the same redactor (one rule set). Allow-list is exact-match on the normalised key and closed (`keyset`, `primarykey`, `key_columns`, `idempotency_key_hash`); `idempotency_key` itself stays redacted.
- SEC-OI-473-6: DoS bounds: depth 4, at most 64 elements per collection, at most 256 nodes and one overall byte cap (existing details size limit), applied before serialisation; property names are sanitised like values (log injection through keys); the control-character set covers all C0/C1 controls, ` `, ` `, not only `\r\n\t\0\x1b`.
- SEC-OI-473-7: value-level scrub: strings that look like a connection string (`password=`, `pwd=`), a bearer/JWT token or a `scheme://user:pass@` URL are replaced even under an allow-listed key. Exception messages written under `error`/`message` keys are the main leak path.
- Tests: `AuditRedactionChokepointTests.Raw_DetailsJson_from_endpoint_is_redacted_before_persist`, `.Unparseable_details_never_stored_raw`, `.Connection_string_in_value_is_scrubbed`, `.Collection_over_cap_is_truncated`, `.Property_name_control_chars_replaced`, `.Allow_list_is_exact_match`; plus the plan's builder tests.

## #487 Cursor key and format

**Verified.** `KeysetCursorEncoder.cs:23-44` (static ephemeral key in three constructors), `BulkStreamSigningKeyOptionsValidator.cs:37` (key checked only when bulk stream is on), `ConnectionGraphQlTypes.cs:144` (`?? new KeysetCursorEncoder()`), `:303-325` (tenant claim then `ContextData["TenantId"]`; `callerSid` falls back to display name and **to an empty string**). Signature is verified before parsing (good) with `FixedTimeEquals`; error texts distinguish tamper, base64, JSON.
- SEC-OI-487-1: the key is required whenever the gateway runs outside Development (not only with bulk stream), minimum 32 bytes after trim, no placeholder values; the static field and all fallbacks are deleted; only the Development registration creates an ephemeral key (warning). The PR lists the integration hosts that run as `Production`/`Staging` and gives them a key in the same PR.
- SEC-OI-487-2: cursor v2 signs version, `kid`, `iat` and context inside the MAC input, verifies the MAC first (constant time), then parses `iat`/`kid`. `kid` is derived with a domain-separated HMAC of the key (not a bare SHA-256 prefix). Reject `iat` in the future (clock-skew allowance 5 min) as well as older than 24 h. One typed error `cursor.invalid` for every failure, no distinguishing message, timing or log text visible to the client (server log may carry the internal reason code).
- SEC-OI-487-3: GraphQL binding: `GetUserSid()` (empty or missing SID means deny, never `""`), the tenant is the same value the governed tree query authorises against (request tenant, not a client-writable `ContextData`); both claim and request tenant present but different means deny.
- SEC-OI-487-4: the PR B decode path accepts only v2; a v1-looking cursor fails with the same `cursor.invalid` (no downgrade parse).
- Tests: the plan names plus `Future_dated_iat_rejected`, `Version_downgrade_to_v1_rejected_with_same_error`, `Empty_sid_is_denied`, `Tenant_claim_mismatch_with_request_tenant_denied`, `Whitespace_key_fails_startup`.

## #489 Auth hardening (WP-OI-SEC-ID-5 = WP-43 PR 4a)

**D-1 confirmed:** step 12 has no dependency on WP-SIMPL-46 PR 3; PR 4a ships now, PR 4 keeps step 11 and AuthModeEndToEndTests. Verified `GatewayCorsCsrfMiddleware.cs:115` (`IsAllowed(originUri.Authority)`, scheme ignored), `ReadOnlyTokenMiddleware.cs:69-71` (every GET/HEAD/OPTIONS passes), `GovernanceModels.cs:112-117` (`caller` written without client id).
- SEC-OI-489-1: the scheme compared is the externally visible one: after the forwarded-headers middleware with configured known proxies, never a raw client `X-Forwarded-Proto`. In non-Development an `http` origin is rejected unless the request scheme itself is `http` and `IsInsecureTransportAllowed` is set. An unparseable origin (`null`) stays untrusted. Missing Origin and Referer on a cookie-authenticated mutation keeps today's behaviour (header requirement); do not weaken it.
- SEC-OI-489-2: `CallerContext.IsClientAsserted` is true for header labels; the token application id (`azp`/`appid`/`client_id` of a validated token, the same claim order as `McpEndpoints.cs:130-152`) is stored as `clientId`; audit stores both; no authorization, rate limit, usage or deny key reads the asserted label. A caller-supplied `caller` key inside `DetailsJson` is overwritten (the existing serializer already does that; keep a test).
- SEC-OI-489-3: lineage consumer views group by `clientId` when present; the asserted label is marked and cannot spoof another application's row; small-group suppression in `ConsumerFolding` keys on the same grouping.
- SEC-OI-489-4: the read-only allowlist test enumerates endpoint data sources (all GET/HEAD routes including GraphQL GET, `/dev`, export, SCIM, callback routes) and fails on any route not in the checked-in list; the allowlist entries each state "no side effect" and the test also asserts that `X-Original-Method`/`X-Forwarded-Method` can only restrict (existing behaviour, keep pinned).
- Tests: the plan names plus `Raw_forwarded_proto_from_untrusted_peer_ignored`, `Null_origin_rejected`, `Asserted_label_never_in_rate_limit_or_deny_key`, `Consumer_view_groups_by_client_id`, `GraphQl_get_mutation_is_rejected_for_read_only_token`.

## #472 /health/ready

**Verified** `HealthEndpoints.cs:54-100`: anonymous, dependency check per request, Development bodies expose `securityMode`/bypasses (anonymous, Development only).
- SEC-OI-472-1: the cached object is only the dependency report (bool plus component names); the HTTP body, the Development detail and the gate/warm-up states are computed per request and never cached. Draining stays uncached and is evaluated first.
- SEC-OI-472-2: single-flight with one shared 5 s timeout covering the PostgreSQL ping and the Redis ping (`GatewayHealthCheckService` has no timeout on Redis today); a timed-out or throwing probe is cached as unhealthy for the window only; the exception is logged once per probe run, not per request; no exception text in the response.
- SEC-OI-472-3: the cache is a per-process singleton with no key derived from request data (no header, host or tenant input).
- Tests: the plan names plus `Redis_ping_hang_is_bounded_by_probe_timeout`, `Cached_report_does_not_leak_into_development_detail_body`, `Warmup_and_gate_states_are_read_live`.

## Anything the plans missed (summary)

1. Governance TLS check would silently skip an unset provider (SEC-OI-483-1) and the migration login (SEC-OI-483-2).
2. Plugin integrity abort turned into a warning by the plan itself (SEC-OI-503-1); action-handler and built-in name collisions (SEC-OI-503-2).
3. 119 raw audit writers bypass the builder redaction (SEC-OI-473-4); dead-letter write can fault the writer and stores `ex.Message` (SEC-OI-473-1/2).
4. GraphQL cursor binds an empty caller SID (SEC-OI-487-3); future-dated `iat` (SEC-OI-487-2).
5. OLAP anonymous principal reaches the staging service under Quickstart (SEC-OI-503-6).

## New owner questions

| Id | Question | Recommended default |
|---|---|---|
| Q-SEC1-1 | #473 DP-2: move redaction to the persistence chokepoint (about +60 src) instead of only the builder? | Yes; without it the fix does not protect 119 writers |
| Q-SEC1-2 | #503 DP-10: may a Development-only run skip an unlisted DLL with an Error log? | Yes in Development only; fatal otherwise (SEC-OI-503-1) |
| Q-SEC1-3 | #487: non-Development hosts without a keyset key fail startup even if no bulk stream or GraphQL connection is used? | Yes (GraphQL is always mapped; one rule) |
