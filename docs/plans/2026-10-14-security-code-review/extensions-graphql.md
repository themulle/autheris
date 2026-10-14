# Security review: src/Autheris.Extensions and src/Autheris.GraphQL

Read-only review. No Critical or High findings were verified in this scope; the previously remediated paths (OData filter pushdown, tree compiler, webhook HMAC/replay, SSRF-hardened egress, Lakehouse location guards) held up. The items below are new, each traced through callers.

### Medium — OData `$metadata` / service document fail open when the access check throws
- **File:** `src/Autheris.Extensions/OData/ODataHandler.cs:116-164`
- **CWE:** CWE-636 (Not Failing Securely), CWE-209
- **Confidence:** Confirmed
- **Issue:** In `GetAuthorizedTablesAsync` both `CheckTableAccessAsync` calls are wrapped in bare `catch { }` blocks (lines 119-138). If both throw, `decision` stays `null`. The next gate is `if (decision != null && !decision.IsAllowed) continue;`, which does not trigger for `null`. The column filter then evaluates `decision != null ? ... : tableMeta.Columns` (line 146-148), so the table is added with ALL columns, including Deny-level columns and their `Core.Description` / `Core.LongDescription` annotations. The outer `catch (Exception)` at line 166 is fail-closed, but the inner swallow makes it unreachable for exactly the exceptions that matter (policy engine, consent store, DB, Casbin failures, and even `OperationCanceledException`). The result is then cached for 60s per user (`odata:tables:*`, `odata:csdl:*`, `odata:servicedoc:*`).
- **Exploit scenario:** During a transient consent/governance DB error or policy-engine fault, any authenticated tenant user (even one with zero grants) calls `GET /odata/v4/$metadata`. The response contains every table of their tenant domain and of the `default` domain, with full column lists and descriptions, including tables and columns that are Deny for the caller. Repeating the call while the fault lasts keeps refreshing the cache. A deliberately induced fault (for example connection-pool exhaustion through parallel heavy queries by the same user) widens the window.
- **Remediation:** Treat `null` as deny:
  ```csharp
  TableAccessDecision? decision = null;
  try { decision = await _executionService.CheckTableAccessAsync(principal, tableMeta, recordAudit: false, ct); }
  catch (OperationCanceledException) { throw; }
  catch (Exception ex) { _logger.LogWarning(ex, "..."); continue; }   // fail closed, do not try a second overload
  if (decision is null || !decision.IsAllowed) continue;
  ```
  Remove the second fallback call, never cache a result produced after an error, and do not swallow cancellation.

### Medium — GraphQL root field `table(domain, name, schema, first, after)` has an unbounded `after` (OFFSET)
- **File:** `src/Autheris.GraphQL/Types/QueryTypes.cs:24-55` (sink: `src/Autheris.Application/Services/GatewayExecutionService.cs:~208-211`, `src/Autheris.Application/Services/SqlDataSourceExecutor.cs:352-405`)
- **CWE:** CWE-770 (Allocation of Resources Without Limits), CWE-400
- **Confidence:** Confirmed
- **Issue:** `Query.GetTableAsync` is a public method on `Query`, so it is always part of the schema as `table(...)`, not only in demo mode. `after` (Int, up to 2147483647) goes unchecked into `ExecuteTableQueryAsync` -> `execArgs["offset"]` / `DataSourceExecutionContext.Offset` -> `OFFSET @gql_offset` (SQL executor only applies `Math.Max(0, offset)`). The other entry points cap offsets: OData `MaxSkip = 100_000` (`ODataHandler.cs:30`, O6), the tree path `MaxAllowedOffset` (`GovernedTreeQueryService.cs:190`). The query cost analyzer only prices `first`, so `after` is free (the `table` field costs roughly `first` units).
- **Exploit scenario:** Any user with read consent on a large table sends:
  `{ table(domain:"finance", schema:"dbo", name:"big_fact", first:1000, after:2147483647) { jsonRows } }`
  The database must read and discard up to the whole table (index scan on the ORDER BY key) per request. A few concurrent requests per user (limited only by `MaxConcurrentReadsPerUserAndTable`, which may be 0) produce sustained DB load. With RLS the filtered scan is even more expensive.
- **Remediation:** Validate in the resolver (and preferably in `ExecutePageCoreAsync` so every caller inherits it): `if (after < 0 || after > options.GraphQL.MaxAllowedOffset) throw new GraphQLException(...INVALID_QUERY...)`. Alternatively remove `table` from the schema (`[GraphQLIgnore]`) since the generated catalog fields already cover table reads.

### Medium — Federation context-header signature does not bind the request body and uses an ambiguous canonical form
- **File:** `src/Autheris.Application/Federation/Services/SubgraphContextPropagationService.cs:54-129` (signer); `src/Autheris.GraphQL/Federation/SubgraphSecurityValidator.cs:45-93` (verifier)
- **CWE:** CWE-345 (Insufficient Verification of Data Authenticity), CWE-347
- **Confidence:** Likely
- **Issue:** (1) The HMAC covers `tenant:userSid:ts:nonce:subgraph:roles:method:path` only. The GraphQL body (query + variables), `Content-Length`, and the `Authorization` header are not signed, so a signed identity header set can be replayed with a different operation. (2) Fields are joined with `:` without escaping or length prefixes (`userSid`, `roles`, `path`, `tenant` can contain `:`), so a different tuple can produce the same string. (3) The signed `roles` string is sorted (`OrderBy`, line 71) but the roles header actually sent is `string.Join(",", roles)` unsorted (line 73), so verifiers that recompute from the header will intermittently fail, and verifiers that sort before checking accept a header value that differs from what was signed. (4) Roles are comma-joined, so a role name containing `,` is indistinguishable from several roles. (5) Nonce replay cache is a process-local static (`SeenNonces`), so with N subgraph replicas the same signed request is accepted N times inside the 60s window. Subgraph destinations are only checked with `enforceHttps:false`, so plain-HTTP subgraphs are allowed.
- **Exploit scenario:** An actor positioned between gateway and a subgraph (plain HTTP, service mesh without mTLS, or a compromised sidecar) captures a request for a GovernanceAdmin user, keeps the `X-Autheris-*` headers and swaps the body for `{ allEmployeeSalaries { ... } }` to the same path/method. Signature validation passes for the first replica receiving the nonce; replicas with a separate nonce cache accept repeated use for up to 60s.
- **Remediation:** Sign a length-prefixed/canonical encoding (for example `HMAC(key, tenant|len|...)` or a JSON canonical form) that also includes `SHA256(body)`, the HTTP version-independent target URL, and the sorted roles list that is actually sent. Send the sorted list. Store nonces in the shared `IDistributedClusterStateProvider`. Require HTTPS (or mTLS) for subgraph URLs outside Development.

### Low — `SubgraphSecurityValidator.ValidateSignature` throws on out-of-range timestamps before any signature check
- **File:** `src/Autheris.GraphQL/Federation/SubgraphSecurityValidator.cs:45-51`
- **CWE:** CWE-248 (Uncaught Exception), CWE-20
- **Confidence:** Confirmed
- **Issue:** `long.TryParse(timestampStr)` accepts values such as `99999999999999`; `DateTimeOffset.FromUnixTimeSeconds` then throws `ArgumentOutOfRangeException` (valid range is about year 0001 to 9999). The method is documented to return `bool`, so callers do not expect an exception, and this runs before HMAC verification, i.e. with unauthenticated input.
- **Exploit scenario:** A subgraph embedding this validator receives `X-Autheris-Timestamp: 99999999999999`; the request faults with a 500 (and a noisy exception log) instead of being rejected as 401.
- **Remediation:** Bound the value first (`timestamp is < 0 or > 253402300799` returns false) or use `try/catch (ArgumentOutOfRangeException)` returning false.

### Low — Revocation/expiry watchers fail open indefinitely when the revocation service errors
- **File:** `src/Autheris.GraphQL/Subscriptions/Subscription.cs:153-170`, `src/Autheris.GraphQL/Subscriptions/WebSocketAuthInterceptor.cs:402-418`
- **CWE:** CWE-636, CWE-613 (Insufficient Session Expiration)
- **Confidence:** Confirmed
- **Issue:** Both the per-subscription timer and the WebSocket `RevocationWatch` log and continue when `IsRevokedAsync` throws ("Fail open on transient check errors"). There is no consecutive-failure budget. Role and group changes are also never re-evaluated for the session lifetime (the `ClaimsPrincipal` is captured at connect/subscribe time; only consent/policy decisions are refreshed per event).
- **Exploit scenario:** A user whose token was revoked (offboarding) keeps an open `onTableChanged` subscription for up to 8 hours (`DefaultMaxSessionLifetime`) while the revocation backend (Redis/DB) is unavailable or throwing; every tick fails open and events continue to flow (still filtered by consent/RLS, but with the revoked identity).
- **Remediation:** Count consecutive failures and abort the connection after N (for example 3) failed checks; or abort if the last successful check is older than 2 intervals. Shorten the max lifetime for sessions without an `exp` claim.

### Low — OpenLineage push claims tenant filtering but exports every node (IDs never contain `:`)
- **File:** `src/Autheris.Extensions/Lineage/OpenLineageClient.cs:61-73`; trigger `src/Autheris.Api/Endpoints/GovernanceEndpoints.cs:~478-491`
- **CWE:** CWE-359 / CWE-200 (cross-tenant exposure)
- **Confidence:** Likely
- **Issue:** SEC SG-30 filters with `if (node.Id.Contains(':') && !node.Id.StartsWith("{tenant}:"))`. Lineage node IDs are built from `TableIdentifier.ToString()` (`domain.schema.table`, `TableIdentifier.cs:34`), which never contains `:`, so the filter never excludes anything. The whole in-memory graph (all tenants/domains) is pushed under `namespace = tenant`. The endpoint is open to any `GovernanceAdmin` or plain `DataOwner` (`HasAnyRole`), i.e. tenant-scoped roles.
- **Exploit scenario:** A DataOwner of tenant A calls `POST /api/lineage/openlineage/sync`. Dataset names, node types and graph edges of tenants B..N are written into the OpenLineage backend namespace `A`. If the backend (Marquez/Collibra lineage) exposes namespaces to tenant A's readers, they see foreign dataset names.
- **Remediation:** Filter on the real tenant relationship (domain == tenant or a tenant tag on the node) rather than on a `:` prefix, restrict the endpoint to global governance administrators (as the dbt endpoints do after review E-7), and add a unit test with real `domain.schema.table` IDs.

### Low — Debezium parser derives the tenant only from a `tenant_id` column; other tenant columns are silently replaced with the caller's tenant
- **File:** `src/Autheris.Extensions/Cdc/DebeziumCdcParser.cs:60-70`; consumer `src/Autheris.Api/Endpoints/StreamingCdcEndpoints.cs:~72-92`
- **CWE:** CWE-639, CWE-1220
- **Confidence:** Likely
- **Issue:** The parser reads `tenant_id` (case-insensitive) from `after`/`before`. The MSSQL poller accepts `TenantId` or `tenant_id`, and catalog metadata allows other tenant column names (`TenantColumnName`, `ResolveTenantColumn`). A Debezium row whose tenant column is `tenantId`, `TenantID` is camel/Pascal, or `org_id` yields `TenantId = null`; the ingestion endpoint then stamps the event with the caller's tenant (`cdcEvent with { TenantId = callerTenant }`) for non-cluster-admin callers. The row's actual tenant value is never compared.
- **Exploit scenario:** A tenant-A-scoped connector streams a shared multi-tenant table (rows for A and B). Rows for B carry `TenantId: "B"` and are labeled "A", so `StreamRlsPolicyEnforcer`'s tenant check (event tenant == subscriber tenant) passes for A subscribers, and tenant B's data is delivered to tenant A's `onTableChanged` subscribers (subject to table consent).
- **Remediation:** Resolve the tenant column from catalog metadata for the target table, compare it with the caller's tenant (reject on mismatch), and never default a missing tenant to the caller's tenant when the table has a tenant column.

### Low — GraphQL `catalog` query is not tenant-scoped for admin/discovery/open-schema paths
- **File:** `src/Autheris.GraphQL/Types/QueryTypes.cs:326-393`
- **CWE:** CWE-200, CWE-639
- **Confidence:** Likely
- **Issue:** `GetAllTablesAsync` is not tenant-filtered (`PostgreSqlGovernanceRepository.Catalog.cs:137`). For `isOpenSchema`, `isGlobalAdmin` (tenant-scoped `GovernanceAdmin` role counts), and `AllowAuthenticatedCatalogDiscovery`, every table of every domain is returned with column names, description, sensitivity and tags. OData `$metadata` restricts to `domain == tenant || "default"` (ODataHandler.cs:106-111), and the Iceberg federation service filters by domain == tenant, so the behaviours are inconsistent.
- **Exploit scenario:** With `Catalog:AllowAuthenticatedCatalogDiscovery=true` in a multi-tenant deployment, a user of tenant A runs `{ catalog { domain schema tableName columns description sensitivity } }` and enumerates tenant B's tables and columns.
- **Remediation:** Apply the same domain/tenant scoping used by OData for non-cluster administrators in all three branches; reserve the unscoped listing for canonical ClusterAdmin.

### Low — Delta executor replays the entire `_delta_log` on every request, before the "not implemented" gate
- **File:** `src/Autheris.Extensions/Lakehouse/Services/DeltaLakeDataSourceExecutor.cs:108-120`; `src/Autheris.Extensions/Lakehouse/Services/DeltaMetadataReader.cs:47-236`
- **CWE:** CWE-400, CWE-770
- **Confidence:** Likely
- **Issue:** `LoadSnapshotAsync` loops `while (true)` over every `NNNN.json` commit (each read up to `MaxReadBytes`=64 MB, no checkpoint use, no cap on version count, no caching, remote S3/Azure round trips) and happens at line 108, before the EXT-4 check at line 117 that answers 501 outside demo mode. So outside demo mode every governed request against a Delta table does the full log replay and then returns 501.
- **Exploit scenario:** A user with consent on a Delta table with thousands of commits issues repeated OData/GraphQL requests; each causes thousands of sequential object-store GETs (and megabytes of JSON parsing) with no result.
- **Remediation:** Move the demo-mode/not-implemented check before the snapshot load, cap the number of commits per call, use `_last_checkpoint`, and cache snapshots per table version with a short TTL.

### Low — `syncOpenMetadata` mutation exposes raw exception text and cross-tenant table names to tenant-scoped admins; global effects from tenant-scoped roles
- **File:** `src/Autheris.GraphQL/Types/MutationTypes.cs:680-800`; `src/Autheris.Extensions/OpenMetadata/OpenMetadataSyncService.cs:85-92,146,165`
- **CWE:** CWE-209, CWE-269
- **Confidence:** Likely
- **Issue:** `ReloadSchemaAsync`, `SyncOpenMetadataAsync`, `SyncDataCatalogAsync` accept the raw `GovernanceAdmin`/`ClusterAdmin`/`DeveloperAdmin` role strings (not `ClusterAdminPolicy.IsCanonicalClusterAdmin`, so ForwardAuth/tenant-prefix rules are bypassed for "ClusterAdmin") but act gateway-wide (all tenants' tables, consents, epochs). `SyncPermissionsAsync` returns `ex.Message` in `Warnings` (`[ex.Message]` at line 90, `Skipped OpenMetadata table {FQN}` for other tenants' tables), which the mutation passes straight to the client (`Warnings = result.Warnings.ToList()`). Exceptions from the secured egress stack carry details such as `SSRF / DNS Rebinding Defense: Outbound connection to restricted IP address '10.x.x.x'` or `Connection refused (om.internal:8585)`.
- **Exploit scenario:** A tenant-A `GovernanceAdmin` calls `mutation { syncOpenMetadata(dryRun:true) { warnings } }` while OpenMetadata is unreachable and receives the internal host/IP/port of the OpenMetadata server and FQNs of other tenants' tables.
- **Remediation:** Return generic warnings to the caller (log details server side), require `IsCanonicalClusterAdmin` for gateway-wide operations (or scope the sync to the caller's tenant), and use the same role helper as the REST admin endpoints.

## Verified strengths
- OData `$filter` is parsed by a recursive-descent parser with length (4096) and depth (32) limits, all literals become parameters, identifiers are `QuoteIdentifier`-ed and `ReferencedColumns` are re-validated as `Clear` in both `GatewayExecutionService.ValidateFilter` and `SqlDataSourceExecutor`; `$orderby`/`$select` are whitelisted by regex and validated against effective column access with identical errors for unknown and denied columns (no masked-column oracle). `$skip` is capped, `$top` clamped, no `$expand`/`$apply`/`$search` surface exists.
- GraphQL tree path: `ResolveClearColumn` enforces Clear access for filter, sort and join columns, per-table access is resolved per operation and memoized per (operation, tenant, SID), aggregate row budget plus depth (10) plus offset caps are enforced, mutations check tenant and separation of duties, error filter whitelists codes and masks everything else, introspection and SDL are off by default.
- Cost analyzer handles aliases, fragments (cached, spread-capped), variables (runtime values and defaults) and root-field counts; CostAndQuota middleware applies per request tier plus token bucket.
- Subscriptions: per-event tenant check, per-event consent/ABAC/ReBAC/RLS/masking via `StreamRlsPolicyEnforcer`, per-subject subscription limit, cdc_all restricted to governance admins, session expiry and revocation timers on WebSocket and SSE; connect-time subject/tenant consistency between upgrade and token; CSWSH defense.
- Webhooks (ITSM, catalog, dbt): HMAC-SHA256 with constant-time compare, timestamp-bound signatures, per-instance secrets, replay caches (local plus cluster), tenant-bound ticket lookup.
- Outbound clients use `SecureOutboundHttp` (no redirects, resolved-IP pinning, metadata hosts blocked, proxy disabled); response bodies are size-bounded; credentials resolved via secret references; TLS validation only relaxed by a DANGER flag.
- Lakehouse: traversal/NUL/percent checks, bucket and container allowlists, symlink rejection in the local provider, size-bounded reads, tenant proof via partition/min=max stats and per-row tenant check.
- MCP executor: identity rebuilt from the authenticated caller, read-only enforcement, single read-only query for `query_graphql`, structured and non-leaky errors.

## Areas not covered
- HotChocolate server defaults not visible in this repo (request batching `MaxBatchSize`, incremental delivery, GET operations, persisted-operation middleware internals in `Autheris.Api`); no runtime testing of batching/alias amplification beyond static cost analysis.
- `GraphQlEnumerationShieldMiddleware`, `TrustedDocumentsOnlyMiddleware`, `ErrorSanitizingFilter` behaviour for every error code, and Api-layer endpoint authorization policies were only read where they intersect findings.
- `Autheris.Application` internals (AccessPolicy/DecideAsync, ConsentResolution, TreeSqlCompiler value coercion, GovernedConnectorReader) were sampled, not audited; SQL dialect specifics (for example `LIKE ... ESCAPE '\'` on non-ANSI-string dialects).
- `DbtMetadataIngestionService` (1353 lines) governance-ratchet logic, `IcebergMetadataReader`/`AzureBlobStorageProvider` internals, `DataHub` client (no DataHub client found in this tree), `OpenJevClient` classification path and `Governance` hook were skimmed only.
- Lakehouse Parquet row reads are not implemented (rows are synthesized from metadata in demo mode), so RLS-on-Parquet could not be assessed; the S3 virtual-hosted bucket derivation (`.s3` index) was reviewed but only matters for operator-controlled locations.
- Backstage export and Dbt exposures endpoints return unfiltered (global) catalog data to global governance/catalog-sync roles; judged by-design but not tenant-scoped.
