# Implementation Plan: Security Wave C, part 2 (SR-L-13 .. SR-L-24, SR-D-06 .. SR-D-09)

- **Document ID:** `IMPL-SEC-WAVE-C-2`
- **Parent plans:** [`../00-master-plan-overview.md`](../00-master-plan-overview.md), [`../security.md`](../security.md) (section 4, Wave C Low findings)
- **Scope:** `SR-L-13` .. `SR-L-24`, `SR-D-06` .. `SR-D-09`
- **Date:** 2026-10-14
- **Base:** `main` @ `317e65b8` (branch `docs/implementation-plans-2026-10-14`)
- **Phase:** 2 (Solution Architect). Phase 3 (security audit) should review WP-SEC2-01, -04, -05, -06 before implementation.
- **Detailed finding text:** `git show d8db81cc^:docs/plans/2026-10-14-security-code-review/<file>.md` (finding IDs `SR-*`).

Neighbouring scopes (referenced, not planned here): Security Wave C part 1 (SR-L-01..12); Architecture R0+R2 (webhook outbox T6, revocation fail-closed T5); Architecture R4 (ProblemDetails T8); Architecture R1+R3 (unified pipeline T1, masking); Architecture R6 (build props, CI pinning); Features (#28, #37, #38, REST paging).

---

## 1. Verification table

| ID | Status on main | Evidence |
|---|---|---|
| SR-L-13 | **Open** | `Autheris.Application/Events/Services/CloudEventWebhookDispatcher.cs:99-105` signs only `payloadJson` (`X-Autheris-Signature`), no timestamp header, no timestamp in MAC input. The sibling `WebhookDispatcherService.cs:92-93,128-129` already signs `{timestamp}.{payload}` and sends `X-Autheris-Timestamp`; the CloudEvents dispatcher was missed. Headers (`ce-*`, tenant) are not covered by either MAC. |
| SR-L-14 | **Open** (3 sub-items) | (a) `WebhookDispatcherService.cs:175-178,128` returns `""` for an empty secret and still sends `X-Autheris-Signature-256: sha256=`. (b) Full `TargetUrl` (may embed tokens/userinfo) is logged at `WebhookDispatcherService.cs:109,141,147,151,159,172` and `CloudEventWebhookDispatcher.cs:131,146`. (c) `Api/Endpoints/StreamingCdcEndpoints.cs:143-145` echoes `ex.Message` of the egress check to the client; `Application/Security/EgressUrlPolicy.cs:29,37,87` messages contain the resolved IP address (internal network scanning oracle). The dispatcher result message itself is already generic (`CloudEventWebhookDispatcher.cs:154`, `:78`). |
| SR-L-15 | **Open** | `Domain/Events/GatewayEventModels.cs:154` `DocumentDepositedEvent.SourceFilePath` filled with the absolute `result.FilePath` at `Api/Endpoints/DocIngestionEndpoints.cs:118-126`; `PolicyDeniedEvent.Reason` (`GatewayEventModels.cs:120`) is filled with verbatim `policyDecision.DeniedReasons` at `Application/Mcp/Services/AiDataGuardrailService.cs:415-422` and reaches webhooks and plugin subscribers. |
| SR-L-16 | **Open** | `Application/Catalog/Services/CatalogDiscoveryService.cs:324-325` uses `HttpClient.GetStringAsync` (unbounded body) after only a 30 s timeout. |
| SR-L-17 | **Already resolved** | `GraphQL/Federation/SubgraphSecurityValidator.cs:123-132` rejects unparsable and out-of-range epoch values with `return false` (comment `SR-L-17`) before `FromUnixTimeSeconds`; no exception path remains. The "verify HMAC before parsing timestamp" wording of the plan is not required (the signed payload needs the timestamp string only, drift check is a cheap pre-filter). Residual: no dedicated regression test found (`grep SR-L-17 tests` finds none) -> add test-only item in WP-SEC2-03. |
| SR-L-18 | **Open** | Both watchers swallow errors: `GraphQL/Subscriptions/WebSocketAuthInterceptor.cs:410-413` (log only, returns `false`) and `GraphQL/Subscriptions/Subscription.cs:158-168` (`catch (Exception)`, comment "Fail open"). Connect-time checks at `WebSocketAuthInterceptor.cs:107,209` and `Subscription.cs:81` are fail-closed by the same service contract (`ITokenRevocationService`, `Redis...:107`). |
| SR-L-19 | **Open** | `Extensions/Lineage/OpenLineageClient.cs:61-67` filters only `node.Id.Contains(':')`; node IDs are table GUID strings (`Extensions/Dbt/DbtMetadataIngestionService.cs:384-387`), so the filter never fires and all tenants' nodes are pushed. `LineageNode` (`Domain/Model/GovernanceV2Models.cs:51`) and `LineageGraphStore` (`Infrastructure/Lineage/LineageGraphStore.cs`) carry no tenant at all; `UpdateGraph` replaces the whole global graph. |
| SR-L-20 | **Open** | `Extensions/Cdc/DebeziumCdcParser.cs:60-70` reads only the literal `tenant_id` key. `StreamingCdcEndpoints.cs:75-82` then stamps the caller tenant onto events without a tenant value, i.e. a row with `TenantId`/`tenantId` is attributed to the caller. `TableMetadata.TenantColumnName` (`Domain/Model/TableModels.cs:141-153`) already implements the tolerant lookup but is not used here. |
| SR-L-21 | **Open** | `GraphQL/Types/QueryTypes.cs:337-355` (open schema) and `:392-407` (global admin or `AllowAuthenticatedCatalogDiscovery`) return every table from `GetAllTablesAsync` with `AccessStatus = "GRANTED"`; only the consent path (`:410-440`) applies `CatalogVisibility.FilterForSubject(..., tenantId)`. `TableMetadata` has no tenant field; tenancy is expressed via consents (`Consent.TenantId`). |
| SR-L-22 | **Open** | `Extensions/Lakehouse/Services/DeltaLakeDataSourceExecutor.cs:108` `LoadSnapshotAsync` (full `_delta_log` replay) runs before the 501 gate at `:118-122`. |
| SR-L-23 | **Open** | `GraphQL/Types/MutationTypes.cs:~706-727` `SyncOpenMetadataAsync` forwards `result.Warnings` verbatim; `Extensions/OpenMetadata/OpenMetadataSyncService.cs:90,146,165,353,443,469,490` put `ex.Message` and OpenMetadata FQNs / table identifiers into warnings. Role check uses raw `ClaimTypes.Role` strings (`MutationTypes.cs:~717`) instead of `ClusterAdminPolicy.IsCanonicalClusterAdmin` (`Domain/Security/ClusterAdminPolicy.cs:18`) as used in `QueryTypes.cs:389-390`. Table-to-tenant mapping exists internally (`tableTenantMap`, `OpenMetadataSyncService.cs:120-127`) but the caller tenant is never compared. |
| SR-L-24 | **Partially resolved** | DML side is covered: `TrinoSqlEngine/Ast/Visitors/AstSecurityVisitor.MaskGuards.cs:78-84,110-128` (`RejectWholeRowReferencesInDml`), tests `tests/TrinoSqlEngine.Tests/Compiler/TypedGuardMutationTests.cs:166-169`. SELECT side: projection masking is re-verified post-compile by `Ast/Security/SecurityCoverageVerifier.cs:279-281,388-419` (`VerifyMasks` rejects wildcards and raw masked columns), but it only inspects `ColumnReference.Name.SimpleName` against the masked-column set. A bare whole-row reference in a projection of a masked table (`SELECT t FROM t`, `to_json(t)`, `ROW(t.*)`-style) is not explicitly rejected on the SELECT path. Needs a probing red test to classify as real gap vs. parser-rejected. The legacy `RlsListener.cs` no longer exists (AST X1 cutover). |
| SR-D-06 | **Partially resolved** | `Api/Extensions/GatewayApplicationBuilderExtensions.cs:356` already `MapMetrics().RequireAuthorization()`; Helm `templates/networkpolicy.yaml:16` and `values.production.yaml:21-26` restrict scrape peers. Open: `templates/servicemonitor.yaml:11-16` has no `authorization`/`bearerTokenSecret`/`tlsConfig`, so with auth required the scrape returns 401 (or operators disable auth); `values.yaml:28-30` pod annotations advertise an unauthenticated scrape. `configmap.yaml` does not wire required production settings (see WP-SEC2-09). |
| SR-D-07 | **Open** | `Directory.Build.props:17` defaults `AutherisIncludeTestAuth` to `true`. Only `.github/workflows/docker-publish.yml:59` and `release.yml:69` pass `false`. `Dockerfile*` copy `dist/publish/` (built by those workflows, ok), but `deploy/containers/gqlgateway-api/Containerfile:31` runs `dotnet publish` without the flag, so the benchmark image ships the handler. Runtime guard exists (`GatewayServiceCollectionExtensions.cs:129-131`, Development only) which limits severity. |
| SR-D-08 | **Open** | `deploy/containers/reverse-proxy/Containerfile` has no `USER` (nginx master runs as root, listens on 8080 so root is unnecessary); `deploy/podman-compose.yaml:241` and `deploy/containers/gqlgateway-api/appsettings.Benchmark.json:68` use `TrustServerCertificate=True`; `podman-compose.yaml:230-237` derive HMAC/webhook secrets from one shared `GATEWAY_HMAC_SECRET` fallback. Benchmark-only. |
| SR-D-09 | **Partially resolved** | `global.json` pins `10.0.401` but with `rollForward: latestFeature`; all workflows use `dotnet-version: '10.0.x'` (`ci.yml:26,64`, `codeql.yml:34`, `policy-lint.yml:26`, `release.yml:33`, `docker-publish.yml:40`, `sql-compiler-gate-nightly.yml:40,122`); actions themselves are already SHA-pinned; runtime image digests are pinned (`Dockerfile:6`, `gqlgateway-api/Containerfile:7,34`) but carry the floating `10.0` tag. `NuGetAuditLevel` is `moderate` (`Directory.Build.props:12`) while `NU1901-NU1904` are errors. |

Already resolved: **SR-L-17** (test-only follow-up). Partially resolved: SR-L-24, SR-D-06, SR-D-09.

---

## 2. Work packages

### WP-SEC2-01: CloudEvents webhook signing and error hygiene (SR-L-13, SR-L-14)

- **Goal:** Replay-resistant, uniformly signed outbound webhooks; no URL secrets or resolved IPs leaked via logs or client errors.
- **Design:**
  1. New `Autheris.Application/Webhooks/WebhookSignature.cs` (static): `Sign(string secret, DateTimeOffset ts, string eventId, string eventType, string payload)` returning lowercase hex over canonical `"{ts:O}\n{eventId}\n{eventType}\n{payload}"` (covers timestamp and the signed headers); `TryVerify(...)` with 5-minute window and `CryptographicOperations.FixedTimeEquals` for the documented receiver reference and tests. Both dispatchers call it, removing the duplicated MAC code (`WebhookDispatcherService.ComputeHmacSha256` stays as a thin public wrapper for compatibility).
  2. `CloudEventWebhookDispatcher.cs:98-105`: add `X-Autheris-Timestamp`, `X-Autheris-Event-Id`, `X-Autheris-Event-Type`; signature header becomes `X-Autheris-Signature-256: sha256=...` over the canonical string. Keep emitting the legacy `X-Autheris-Signature` (old MAC over body only) for one release behind option `Gateway:Webhooks:EmitLegacySignature` (default `true` in this release, documented as deprecated); remove next release. Add `x-autheris-timestamp` and `x-autheris-signature-256` to `ForbiddenCustomHeaders` (`:158-162`).
  3. `WebhookDispatcherService.cs:127-131`: when the secret is empty do not send any signature header (currently `sha256=`); fail-closed variant: if `Gateway:Webhooks:RequireSecret` (default `true` in non-Development) and the subscription secret is empty, drop the delivery with a warning instead of sending unsigned.
  4. Log hygiene: new `Autheris.Application/Security/UrlRedactor.cs` `Redact(Uri)` returning `scheme://host[:port]/` (no userinfo, path, query). Replace `{TargetUrl}` logging in both dispatchers and `{Url}` in `CloudEventWebhookDispatcher.cs:131,146`. Stop logging the full exception for SSRF rejections (`WebhookDispatcherService.cs:109`: pass `ex.GetType().Name` only).
  5. Client errors: `StreamingCdcEndpoints.cs:143-145` returns fixed `"Target URL blocked by egress policy."`; the detail is logged server-side with the redacted URL. Introduce `EgressUrlPolicy` `SecurityException` messages without the IP (`Application/Security/EgressUrlPolicy.cs:29,37,87` use host name only for loopback/private class; the resolved address goes to `Exception.Data["ResolvedIp"]` for operator logs). Other callers (`DatasourceTestingService.cs:180`, `TrafficShadowingService.cs:157`, `CatalogDiscoveryService.cs:315`) are covered automatically; verify none surface `ex.Message` to non-admin callers.
- **Affected files:** the two dispatchers, new `WebhookSignature.cs`, `UrlRedactor.cs`, `EgressUrlPolicy.cs`, `StreamingCdcEndpoints.cs`, `Autheris.Domain/Configuration` webhook options class (new `EmitLegacySignature`, `RequireSecret`), `docs/features/f-ext-01-generic-plugin-events-and-webhooks.md` (documentation phase).
- **Compatibility:** Receivers verifying `X-Autheris-Signature-256` over the body only (`WebhookDispatcherService` today signs `{timestamp}.{payload}`) break when the canonical string changes. To avoid a second break, canonical form for `WebhookDispatcherService` stays `"{timestamp}.{payload}"` and only the CloudEvents dispatcher adopts it; the extra header coverage (event id/type) is therefore NOT added (see Open Decision D1). Breaking: unsigned delivery is refused when `RequireSecret=true`.
- **Security (fail-closed):** no secret -> no delivery (non-dev); constant-time compare in verifier; 5-minute window enforced by verifier helper and documented.
- **Overlap:** the R0+R2 webhook outbox (T6) will rewrite the dispatch loop. This WP deliberately confines itself to signature/log/error helpers (`WebhookSignature`, `UrlRedactor`) that the outbox worker can reuse; coordinate merge order (see section 4).
- **Tests (red first):** `tests/Autheris.Tests.Unit/Webhooks/CloudEventWebhookSignatureTests.cs` - `Dispatch_SignsTimestampAndPayload_ReplayedWithOldTimestampFailsVerification` (fails today: no timestamp header). `WebhookDispatcherEmptySecretTests` - `EmptySecret_DoesNotSendSignatureHeader` / `RequireSecret_EmptySecret_DropsDelivery`. `UrlRedactorTests` (userinfo, token query, IPv6). `EgressUrlPolicyMessageTests` - exception message does not contain the resolved IP. `tests/Autheris.Tests.Integration/CdcSubscriptionEndpointTests` - POST with private-IP target returns generic body.
- **Acceptance:** all outbound webhook requests carry timestamp + signature that a reference verifier rejects after 5 min; no `sha256=` empty header; no URL path/query or IP in logs/responses (asserted via `FakeLogger`).
- **Effort:** M. **Risk:** medium (receiver compatibility, outbox overlap).

### WP-SEC2-02: Event payload redaction (SR-L-15)

- **Goal:** Domain events delivered to webhooks/plugins never carry absolute filesystem paths or verbatim ABAC denial reasons.
- **Design:**
  1. `DocumentDepositedEvent.SourceFilePath` -> replace with `RelativePath` (`{appId}/{category}/{docId}`) computed from request values, never `result.FilePath`. Keep the old property for one release marked `[Obsolete]` and always populated with the redacted relative form (never absolute) so plugin binaries still bind. Update `DocIngestionEndpoints.cs:118-126`.
  2. `PolicyDeniedEvent.Reason` (`GatewayEventModels.cs:120`): publish a stable machine code (`POLICY_DENIED`, optional policy id) instead of `string.Join("; ", DeniedReasons)`; full reasons stay in the audit log (`AiDataGuardrailService.cs:405-407` already audits). Add `ReasonCode`.
  3. Central safety net: `Autheris.Domain/Events/EventRedaction.cs` `Sanitize(IGatewayEvent)` invoked in the event-bus publish path used by webhook/plugin forwarders (`Application/Events` forwarder; locate via `IEventBus` implementations) that strips strings matching absolute-path patterns (`^/`, `^[A-Za-z]:\\`). Defence in depth; primary fix is at the producers.
- **Affected files:** `Domain/Events/GatewayEventModels.cs`, `Api/Endpoints/DocIngestionEndpoints.cs`, `Application/Mcp/Services/AiDataGuardrailService.cs`, event bus forwarder, plugin contracts (`Autheris.Extensions` consumers of the two events - `grep` before change).
- **Compatibility:** payload shape change for webhook consumers of `doc.deposited` and `policy.denied` (field replaced/deprecated); note in release notes.
- **Security:** allow-list style (codes) rather than redaction of free text; events default to minimal data.
- **Tests:** `tests/Autheris.Tests.Unit/Events/EventPayloadRedactionTests.cs` - `DocDeposit_Event_ContainsNoAbsolutePath` (red: currently equals `FilePath`), `PolicyDenied_Event_ContainsNoDeniedReasonText`; `EventRedactionTests` for the safety net.
- **Acceptance:** serialising both events never contains the doc-lake root or ABAC policy text.
- **Effort:** S-M. **Risk:** low.

### WP-SEC2-03: Bounded OpenAPI spec download and federation-signature regression test (SR-L-16, SR-L-17)

- **Goal:** Cap remote spec ingestion; lock in the already-fixed SR-L-17.
- **Design:** In `CatalogDiscoveryService.RegisterDatasourceAsync` (`:315-326`) replace `GetStringAsync` with `SendAsync(..., ResponseHeadersRead)`; reject on `Content-Length > max`, then stream through a bounded reader (reuse `EndpointSecurity.TryReadBodyAsync`-style logic; extract a shared `BoundedHttpContentReader.ReadStringAsync(HttpContent, long maxBytes, ct)` into `Autheris.Application/Security`). New option `Gateway:Catalog:MaxSpecDownloadBytes` (default 10 MiB). Also disable auto-redirect or re-validate each hop via the existing `SecureOutboundHttp` handler (verify it re-resolves per hop). Overflow -> `ArgumentException` mapped by the existing error path with a generic message.
- **Affected files:** `CatalogDiscoveryService.cs`, new reader helper, catalog options class.
- **Compatibility:** specs larger than 10 MiB now rejected; configurable.
- **Security:** fail-closed on missing/invalid Content-Length plus streaming cap (chunked bypass).
- **Tests:** `tests/Autheris.Tests.Unit/Catalog/CatalogDiscoverySpecSizeTests.cs` - `SpecUrl_BodyLargerThanCap_IsRejected` (chunked response, no Content-Length) red first; `SubgraphSecurityValidatorTests.OutOfRangeTimestamp_ReturnsFalseWithoutThrowing` (green regression; `long.MaxValue`, `long.MinValue`, `"9999999999999999999"`).
- **Acceptance:** download aborted at cap + 1 byte; memory bounded.
- **Effort:** S. **Risk:** low.

### WP-SEC2-04: Revocation watcher fail-closed (SR-L-18)

- **Goal:** A subscription/WebSocket must not outlive a revocation that cannot be verified.
- **Design:** New `Autheris.GraphQL/Subscriptions/RevocationWatchPolicy` (options `Gateway:Subscriptions:RevocationCheckMaxConsecutiveFailures`, default 3, and `RevocationCheckFailClosed` default `true`). In `WebSocketAuthInterceptor.cs:402-418` (`CheckAsync`) and `Subscription.cs:153-170` (`CheckRevocationAsync`) count consecutive errors; at the threshold treat as revoked (abort the HttpContext / `cts.Cancel()`), log once at Warning with a correlation id, reset counter on success. Use a tiny shared struct `RevocationFailureCounter` in `Autheris.GraphQL/Subscriptions/` so both watchers behave identically. `OperationCanceledException` from connection close stays benign.
- **Compatibility:** behaviour change: a long revocation-store outage (Redis) now closes live subscriptions after roughly 3 ticks. Can be disabled via option.
- **Security:** fail-closed by default; threshold avoids flapping on a single transient error (Open Decision D2: threshold 1 vs 3).
- **Overlap:** Architecture R0+R2 T5 (revocation service fail-closed semantics in `RedisTokenRevocationService`/`ITokenRevocationService`). Service-level fail-closed (throw vs return true) is owned by T5; this WP only handles the watcher reaction and works with either contract.
- **Tests:** `tests/Autheris.Tests.Unit/GraphQL/RevocationWatcherFailClosedTests.cs` - `WebSocketWatch_ServiceThrowsNTimes_AbortsConnection` and `Subscription_ServiceThrowsNTimes_CancelsStream` (red today: never aborts), `SingleTransientError_DoesNotAbort`, `SuccessResetsCounter`.
- **Acceptance:** both watchers terminate after the configured number of consecutive failures.
- **Effort:** S-M. **Risk:** low-medium (availability tradeoff).

### WP-SEC2-05: Extension-layer tenant and DoS fixes (SR-L-19, SR-L-20, SR-L-22)

Three independent sub-items in `Autheris.Extensions` (disjoint files, may be split across developers).

- **SR-L-19 OpenLineage tenant filter.** Add `string? TenantId` to `LineageNode` (`Domain/Model/GovernanceV2Models.cs:51`, default null, appended parameter -> source compatible). `DbtMetadataIngestionService` (`:387`) sets it from the ingestion tenant. `OpenLineageClient.PushLineageGraphAsync` (`:61-67`) filters `string.Equals(node.TenantId, tenant.Value, OrdinalIgnoreCase)`; nodes with null tenant are pushed only when `tenant == TenantId.LegacySingleTenant` (fail-closed). Remove the dead `Contains(':')` branch. Downstream-ID references to nodes of other tenants are dropped from the emitted dataset facets. `LineageGraphStore.UpdateGraph` currently replaces the entire graph, so a second tenant's dbt run wipes the first's; change to per-tenant replace (`UpdateGraph(TenantId, nodes)` overload merging into a `FrozenDictionary` keyed `{tenant}:{id}` is rejected to keep GUID ids stable; instead retain other tenants' nodes in `UpdateGraph`). Verify callers of `ILineageGraphStore` first (`LineageImpactAnalyzerService.cs:89`).
- **SR-L-20 Debezium tenant column.** `DebeziumCdcParser.Parse(string body)` gains `Parse(string body, string? tenantColumn)` and a normalised fallback identical to `TableMetadata.TenantColumnName` (strip `_`/`-`, case-insensitive `tenantid`). `StreamingCdcEndpoints.cs:56` resolves the table's `TenantColumnName` from the metadata repository (by `TableIdentifier`) and passes it. Fail-closed additions: if before and after carry different tenant values -> reject 400; for non-admin callers, if the registered table has a tenant column but the row value is absent/empty -> reject (no silent stamping with the caller tenant); the stamping branch (`:75-82`) remains only for tables registered without a tenant column and `RequireTenantColumn=false`.
- **SR-L-22 Delta gate first.** In `DeltaLakeDataSourceExecutor.cs` move the 501 decision ahead of `LoadSnapshotAsync` (`:108`): `if (_demoData?.Enabled != true) throw new GatewayNotImplementedException(...)` as the first statement after predicate validation (keeps the tenant-predicate mismatch check at `:94-103` first so isolation errors keep precedence). Behaviour change: outside demo mode empty Delta tables now also answer 501 instead of an empty result (consistent with "not implemented", Open Decision D3). Additionally cap log replay in `DeltaLogReader` (`Extensions/Lakehouse`) by `MaxLogEntries` for the demo path.
- **Compatibility:** `LineageNode` record gains a trailing optional parameter; `DebeziumCdcParser.Parse(string)` stays as overload. Delta: 501 earlier.
- **Tests:** `tests/Autheris.Extensions.Tests/OpenLineageClientTenantFilterTests.cs` (`Push_OtherTenantNodes_AreExcluded`, `Push_UntenantedNodes_ExcludedForNonLegacyTenant`) red first; `tests/Autheris.Tests.Unit/Cdc/DebeziumTenantColumnTests.cs` (`TenantId_PascalCase_IsExtracted`, `ConflictingBeforeAfterTenant_Rejected`, `NonAdmin_RowWithoutTenantValue_ForTenantTable_Rejected`); `tests/Autheris.Extensions.Tests/DeltaLakeGateOrderTests.cs` (`NonDemo_ThrowsNotImplemented_WithoutReadingDeltaLog` using a throwing/recording `IDeltaMetadataReader` stub).
- **Acceptance:** no cross-tenant lineage in push payload; CDC events never get a wrong tenant; Delta log is not touched outside demo mode.
- **Effort:** M-L (lineage store change is the long pole). **Risk:** medium (lineage per-tenant merge semantics).

### WP-SEC2-06: Tenant-scoped GraphQL catalog and sanitised `syncOpenMetadata` (SR-L-21, SR-L-23)

- **Goal:** `catalog` honours tenant isolation on every path; `syncOpenMetadata` leaks no exception text or foreign tenants' table names.
- **Design (SR-L-21):** Replace the three branches in `QueryTypes.cs:337-440` by one call to `CatalogVisibility.VisibleTablesAsync(allTables, principal, tenantId, consentRepository, contractDef)` (`Application/Services/CatalogVisibility.cs:189-217`, already used by REST) and add a tenant gate for the admin/discovery/open paths: a table is tenant-visible if the tenant has at least one consent on it (ALLOW or DENY record) or the table is in `Gateway:Catalog:SharedTables` / the tenant-registry mapping (see Open Decision D4 - `TableMetadata` has no owner tenant, so a source of truth for "table belongs to tenant" must be chosen). Cluster admins (`ClusterAdminPolicy.IsCanonicalClusterAdmin`) keep the global view; GovernanceAdmin becomes tenant-scoped. Open-schema (`IsOpenSchemaAllowed`) remains unauthenticated but only in Development (verify config validator; if reachable in production, require the shared-table allow-list there). `AccessStatus = "GRANTED"` is computed with `ComputeAccessStatus` instead of hard-coded.
- **Design (SR-L-23):** In `MutationTypes.SyncOpenMetadataAsync` (`~680-727`): authorise with `ClusterAdminPolicy.IsCanonicalClusterAdmin(principal) || role GovernanceAdmin` via the shared helper; derive caller tenant (`principal.GetTenantId()`); extend `IOpenMetadataSyncService.SyncPermissionsAsync` with an optional `TenantId? restrictToTenant` so a non-cluster-admin sync only touches tables whose `tableTenantMap` entry equals the caller tenant (filter at `OpenMetadataSyncService.cs:120-127`). Warnings returned to GraphQL are mapped to stable codes plus counts (`OM_TABLE_MAP_FAILED: 3`); full text stays in server log. `OpenMetadataSyncResult` keeps detailed warnings for the (admin-only) REST/log path; add `SanitizedWarnings` property used by the mutation. Return failures as `GraphQLException` with code `SYNC_FAILED` and no message detail (aligns with the R4 ProblemDetails work, T8; use whatever error-mapping helper R4 delivers, otherwise inline).
- **Compatibility:** non-cluster-admin GovernanceAdmins see fewer tables in `catalog` (intentional breaking change in multi-tenant deployments; single-tenant (`LegacySingleTenant`) unaffected); `syncOpenMetadata.warnings` content changes from free text to codes.
- **Security:** fail-closed on missing tenant claim (`SecurityException` -> UNAUTHORIZED); no table names from other tenants in any message.
- **Overlap:** R4 (T8) for the error shape; R1+R3 not involved. SR-L-21 shares `CatalogVisibility` with Features REST paging: coordinate on `CatalogVisibility.cs`.
- **Tests:** `tests/Autheris.Tests.Unit/GraphQL/CatalogTenantIsolationTests.cs` - `GovernanceAdmin_SeesOnlyTenantTables`, `DiscoveryMode_DoesNotLeakOtherTenantTables`, `ClusterAdmin_SeesAll` (red first); `SyncOpenMetadataSanitisationTests` - `SyncFailure_ReturnsNoExceptionText`, `NonClusterAdmin_OtherTenantTable_NotSynced`, `RawClusterAdminRoleStringWithoutCanonicalClaims_Denied`; `tests/Autheris.Extensions.Tests/OpenMetadataSyncTenantFilterTests.cs`.
- **Acceptance:** two-tenant fixture: tenant A caller never receives tenant B table identifiers in `catalog` or `syncOpenMetadata` output.
- **Effort:** L. **Risk:** medium-high (semantics of table ownership, D4).

### WP-SEC2-07: SELECT-side whole-row mask guard (SR-L-24)

- **Goal:** Keep projection rewrite as the primary defence and add an explicit whole-row guard on the SELECT path, closing the verifier's blind spot.
- **Design:** (1) Probe first: add red/probe tests for `SELECT t FROM t`, `SELECT o FROM t o`, `SELECT to_json(t) FROM t`, `SELECT CAST(t AS varchar) FROM t`, `SELECT t.* FROM t` (qualified wildcard already caught), correlated subquery returning the row; record which are rejected by the parser/dialect and which compile. (2) In `AstSecurityVisitor.MaskGuards.cs` factor the existing whole-row detection (`:78-84`) into a method used for SELECT projections and function arguments when `HasMaskingForTable(table)` (new option default `RejectWholeRowReferencesInSelect = true`, mirrors `RejectWholeRowReferencesInDml`, `AstSecurityVisitorOptions.cs:40`). (3) `SecurityCoverageVerifier.VerifyMasks` (`:388-419`) additionally throws `SecurityCoverageException` when a projected expression contains a `ColumnReference` whose simple name equals the table name or alias of a masked table (second line of defence independent of the visitor).
- **Compatibility:** queries selecting a whole-row alias of a table with masked columns start failing (such queries would otherwise return unmasked data). Config flag allows opt-out for tests only.
- **Security:** fail-closed; the verifier remains the authoritative post-compile gate.
- **Tests:** `tests/TrinoSqlEngine.Tests/Compiler/SelectWholeRowMaskTests.cs` (Category `SqlGate`) - red tests per probe above; add to `tests/Autheris.Tests.SqlCompilerGate` corpus. If probes show all forms are already rejected, the WP reduces to committing the tests (status -> Already resolved).
- **Acceptance:** no SELECT-side whole-row form over a masked table compiles.
- **Effort:** S-M. **Risk:** low (hot file shared with Architecture R1+R3 masking/unified pipeline, see dependencies).

### WP-SEC2-08: Build and CI pinning (SR-D-07, SR-D-09)

- **Goal:** TestAuth is opt-in; CI uses an exact SDK; audit threshold tightened.
- **Design:**
  1. `Directory.Build.props:17` default `false`. Test projects that need it (`tests/Autheris.Tests.Integration`, `tests/Autheris.Extensions.Tests`, see `ODataTests.cs`, `ExtensionsSecurityAuditRemediationTests.cs`, integration tests) opt in via `<AutherisIncludeTestAuth>true</AutherisIncludeTestAuth>` in their csproj; because `Autheris.Api` compiles the `#if AUTHERIS_TEST_AUTH` code, opt-in must be set on the Api build used by tests: set the property in a `tests/Directory.Build.props` imported before the root props, or in `docker-compose.dev.yml`/dev scripts for local runs. Dev-run documentation updated (`README.md` quick start). Release/publish workflows already pass `false`; remove the redundant flag after verification and add an architecture test that fails when `AUTHERIS_TEST_AUTH` is defined for `-c Release` publish output (scan for the TestAuth handler type in the published assembly).
  2. `deploy/containers/gqlgateway-api/Containerfile:31` add `-p:AutherisIncludeTestAuth=false`.
  3. `global.json`: `rollForward` -> `latestPatch` (exact band 10.0.4xx) and bump deliberately via Dependabot/Renovate. All seven workflow `setup-dotnet` steps: replace `dotnet-version: '10.0.x'` with `global-json-file: global.json`.
  4. SDK/aspnet container images keep digests, add the patch tag next to the digest (`10.0.401@sha256:...`) so a digest refresh is reviewable.
  5. `NuGetAuditLevel` -> `low` (keep `NuGetAuditMode=all`); `NU1901-NU1904` stay errors, so this makes low-severity advisories visible warnings treated as errors via `TreatWarningsAsErrors`; confirm the current tree builds clean first (Open Decision D5, otherwise `moderate` + documented exception list).
- **Overlap:** Architecture R6 hygiene (build props, CI pinning) owns general props cleanup; this WP is limited to the two security-relevant properties. Agree on a single PR touching `Directory.Build.props` (see section 4).
- **Compatibility:** local dev builds lose TestAuth unless opted in (breaking for developer workflow and integration tests; documented). Per repository memory: verify with a Release build with warnings as errors before push.
- **Tests:** `tests/Autheris.Tests.Architecture/ReleaseBuildExcludesTestAuthTests.cs` (red first: Release default build contains the handler); existing integration tests must stay green after opt-in.
- **Acceptance:** `dotnet publish -c Release` without flags produces an assembly without `AUTHERIS_TEST_AUTH` code; CI uses exactly the `global.json` version.
- **Effort:** S-M. **Risk:** medium (test/dev wiring).

### WP-SEC2-09: Helm metrics and benchmark stack hardening (SR-D-06, SR-D-08)

- **Goal:** Authenticated, restricted metrics scrape in Kubernetes; benchmark stack closer to production.
- **Design (SR-D-06):** `templates/servicemonitor.yaml`: add `authorization` (Bearer from `monitoring.serviceMonitor.tokenSecret`) and optional `tlsConfig`/`scheme: https`, values `monitoring.serviceMonitor.auth.type: bearer|mtls|none`; production values default `bearer` and `helm template` fails (`required`) when `none` with `values.production.yaml`. Gateway side: confirm which scheme `MapMetrics().RequireAuthorization()` accepts (JWT bearer policy) and document a dedicated `MetricsScraper` role/claim or a static `Gateway:Metrics:ScrapeToken` policy (small `GatewayPolicies.MetricsScrape` policy; new work if only generic authorization exists). Remove `prometheus.io/*` pod annotations from `values.yaml:28-30` in the production overlay (annotation scraping bypasses auth). Wire required production config in `configmap.yaml` (`Gateway__Authentication__*`, trusted-proxy list, `Gateway__RequireTenantColumn`, metrics policy) via values with `required` guards instead of leaving them to the operator.
- **Design (SR-D-08):** `deploy/containers/reverse-proxy/Containerfile` -> `nginxinc/nginx-unprivileged` (digest-pinned, port 8080 already non-privileged) or `USER nginx` with writable `/var/cache/nginx`, `/var/run`; compose `read_only: true` + `cap_drop: [ALL]` for the proxy; `podman-compose.yaml:241` and `appsettings.Benchmark.json:68` drop `TrustServerCertificate=True` and mount the SQL Server CA (generate a self-signed CA in `deploy/scripts/generate-env.sh`); separate secrets per consumer (remove the single `GATEWAY_HMAC_SECRET` fallback `:230-237`, generate distinct values in `generate-env.sh`); `deploy/scripts/verify-hardening.sh` extended to assert non-root and TLS validation.
- **Compatibility:** Helm: ServiceMonitor values schema changes (new keys, existing `enabled` flag unchanged). Benchmark: new CA step in `generate-env.sh`; benchmark numbers may shift slightly with TLS validation.
- **Security:** production render must fail rather than expose unauthenticated metrics; no placeholder secrets.
- **Tests:** no .NET tests; `helm template` / `helm lint` checks scripted in `deploy/scripts/verify-hardening.sh` (plus kubeconform if available); `tests/Autheris.Tests.Architecture` unchanged. Red check: `helm template -f values.production.yaml` currently succeeds with an unauthenticated ServiceMonitor; after change it must fail unless auth is set.
- **Acceptance:** production Helm render contains authenticated ServiceMonitor and no annotation scrape; benchmark proxy runs as non-root; no `TrustServerCertificate=True` in `deploy/`.
- **Effort:** M. **Risk:** low (not on the runtime path), medium for benchmark TLS wiring.

---

## 3. Dependencies

Between WPs:

- WP-SEC2-01 and WP-SEC2-02 both touch the event/webhook path (`WebhookDispatcherService`, event bus forwarder) -> serialize 01 then 02, or merge reviews.
- WP-SEC2-06 (catalog tenant) is independent of the others but its D4 decision gates the SR-L-21 half.
- WP-SEC2-08 should land before WP-SEC2-09 only if the benchmark Containerfile edit (`-p:AutherisIncludeTestAuth=false`) is wanted in the same release; otherwise independent.
- WP-SEC2-05 SR-L-20 depends on the CDC endpoint being stable (`StreamingCdcEndpoints.cs`, also touched by WP-SEC2-01 at `:143`) -> serialize 01 before 05, or split the endpoint edit.

External (referenced by finding ID, not planned here):

- **SR-L-13/14 vs Architecture R0+R2 (webhook outbox, T6):** the outbox rewrites the dispatch loop; this plan supplies `WebhookSignature`, `UrlRedactor` and sanitised errors. Implement WP-SEC2-01 first (or in lockstep) and have the outbox worker consume the helpers; otherwise the outbox must re-implement the timestamped MAC.
- **SR-L-18 vs Architecture R0+R2 (revocation fail-closed, T5):** T5 defines the service contract on backend failure; WP-SEC2-04 handles watchers only. If T5 makes `IsRevokedAsync` return `true` on failure, the counter becomes a no-op safety net.
- **SR-L-23 vs Architecture R4 (ProblemDetails, T8):** error-shape helper from R4 should be used for the sanitized GraphQL/REST failure; until delivered, inline fixed codes.
- **SR-L-24 (and SR-L-11) vs Architecture R1+R3 (unified pipeline, T1) and Security Wave C part 1 (SR-L-01..12, esp. SR-L-11):** `AstSecurityVisitor.MaskGuards.cs`, `SecurityCoverageVerifier.cs`, `GovernedSqlCompiler.cs` are hot files; merge WP-SEC2-07 before the pipeline refactor lands or rebase behind it.
- **SR-D-07/SR-D-09 vs Architecture R6 (build props, CI pinning):** same files (`Directory.Build.props`, `.github/workflows/*`, `global.json`); one owner for the PR, the other rebases.
- **SR-L-21 vs Features (REST paging, `CatalogVisibility`):** coordinate on `CatalogVisibility.cs` signatures.
- **SR-L-19 vs Features #37/#38:** dbt/lineage ingestion changes (`DbtMetadataIngestionService`, `LineageNode`) may collide with the semantic-layer and dbt features.

---

## 4. Recommended sequence

| Batch | WPs | Rationale | Parallelism |
|---|---|---|---|
| 1 (quick, isolated) | WP-SEC2-03, WP-SEC2-07, WP-SEC2-08 | Small, little overlap with each other; 07 should precede the R1+R3 pipeline refactor; 08 coordinates with R6. | Fully parallel (separate files). |
| 2 (webhook cluster) | WP-SEC2-01, then WP-SEC2-02 | Shared event/webhook files; 01 must precede/accompany the R0+R2 outbox (T6). | Serialize 01 -> 02. |
| 3 (extensions/tenant) | WP-SEC2-04, WP-SEC2-05 | 04 is independent (GraphQL subscriptions) and can run in parallel with 05. 05 sub-items L-19/L-20/L-22 are disjoint and can be split across developers (L-20 after 01 because of `StreamingCdcEndpoints.cs`). Follow T5 decision for 04. | 04 parallel with 05; 05's three sub-items parallel. |
| 4 (needs decision) | WP-SEC2-06 | Blocked by D4; largest behaviour change. | Alone (touches `QueryTypes.cs`, `MutationTypes.cs`, OpenMetadata sync). |
| 5 (deploy) | WP-SEC2-09 | No code dependencies; can start any time, listed last because it needs the metrics auth decision (D6) and staging validation. | Parallel with any batch. |

Hot files requiring serialization: `StreamingCdcEndpoints.cs` (01, 05), `Directory.Build.props`/workflows (08 vs R6), `AstSecurityVisitor*.cs`/`SecurityCoverageVerifier.cs` (07 vs R1+R3, SR-L-11), `QueryTypes.cs`/`MutationTypes.cs` (06 vs R4).

---

## 5. Open decisions

- **D1 (signature canonical form):** Should the extra headers (event id, event type) be covered by the MAC? Recommendation: no for now; sign `"{timestamp}.{body}"` in both dispatchers (event id/type are inside the signed CloudEvents body anyway), keeping one verification recipe for receivers.
- **D2 (revocation threshold):** abort at 1 failure or after 3 consecutive? Recommendation: 3 consecutive failures (about 3 check intervals), configurable, `fail-closed` on.
- **D3 (Delta 501):** is answering 501 for empty Delta tables outside demo mode acceptable? Recommendation: yes, the executor cannot read data anyway.
- **D4 (table-to-tenant ownership for the catalog):** `TableMetadata` has no tenant; options: (a) derive from consents (tenant sees tables it has any consent record on, plus an explicit `SharedTables` list), (b) add `OwnerTenantId` to table metadata (schema migration), (c) tenant-registry mapping. Recommendation: (a) now, (b) as a follow-up in the master plan.
- **D5 (NuGet audit level):** `low` vs staying `moderate`. Recommendation: `low` if the tree builds clean; otherwise keep `moderate` and track exceptions.
- **D6 (metrics auth mechanism):** bearer token (Prometheus `authorization`) vs mTLS. Recommendation: bearer with a dedicated metrics scraper policy for Helm; mTLS only where a service mesh already provides it.
- **D7 (legacy `X-Autheris-Signature`):** keep for one release or drop immediately? Recommendation: keep one release behind `EmitLegacySignature`, announce removal.
