# Product Requirements & Security Scope: Post-v1.5.0 Code Review & GitHub Code Scanning Remediation

**Document ID:** `REQ-SEC-REV-V15`  
**Date:** November 4, 2026  
**Status:** PROPOSED 📝  
**Implementation Plan:** [`docs/plans/2026-11-04-plan-v15-review-and-codeql-remediation.md`](2026-11-04-plan-v15-review-and-codeql-remediation.md) (`PLAN-SEC-REV-V15`)  
**Category Document:** [`docs/plans/security.md`](security.md) (`PLAN-SECURITY-REVIEW`)  
**Origin Plan:** [`docs/plans/00-master-plan-overview.md`](00-master-plan-overview.md) (Track 17)

---

## 1. Executive Summary & Problem Statement

A comprehensive review of all 46 commits since `v1.5.0` (`v1.5.0` → `v1.6.0` → `v1.7.0` → `HEAD`, 421 modified production files) together with an audit of all **61 open GitHub Code Scanning (CodeQL) alerts** (`https://github.com/themulle/autheris/security/code-scanning`) identified:

1. **33 Code Review Findings across `v1.5.0..HEAD`**:
   - **3 Critical** (`CRIT-01..03`):
     - `CRIT-01`: Cross-realm / cross-tenant escalation and erasure of per-user `BasicAuth` / `ForwardAuth` tenant claims in `EnterpriseClaimsTransformation`, `IdentityRealmRegistry`, and `TenantStamping`.
     - `CRIT-02`: Relay `*Connection` fields bypass list cost multiplication (`effectiveRows * totalChild`) and `UnindexedRootListSurchargeMultiplier` in `QueryCostAnalyzerRule`.
     - `CRIT-03`: MCP GraphQL guardrail (`CatalogGraphQlMap.ResolveDocumentTablesAsync`) fails to traverse `edges -> node` on `*Connection` fields, bypassing MCP table-level ABAC, consent, and four-eyes checks for nested relation tables.
   - **5 High** (`HIGH-01..05`):
     - `HIGH-01`: Middleware ordering (`SubjectLockoutMiddleware` before `SecurityContextResolutionMiddleware`) and unguarded `securityContext.TenantId` reads break claim-less `ClusterAdmin` (`X-Tenant-ID` and global operations) and authenticated `[AllowAnonymous]` requests.
     - `HIGH-02`: User-supplied `orderBy` on Relay `*Connection` fields is compiled into SQL `ORDER BY` but ignored in keyset seek predicates, key directions, and HMAC cursor `RequestHash`.
     - `HIGH-03`: Four index-usability bypasses / false positives in `IndexUsabilityEvaluator` (`orderBy` masking unindexed `where`, GraphQL `$where`/`$orderBy` variable bypasses in static validation and runtime `IValueNode` extraction, `OR`/`NOT`/non-SARGable operators treated as indexed, and hardcoded `visibleColumns: null`).
     - `HIGH-04`: DI `ICatalogRelationRegistry` instance is separate from static `CatalogRelationRegistry.Current` populated by `CatalogGraphQlTypeModule`, causing a `10x` vs `1x` multiplier mismatch between validation and runtime cost evaluation.
     - `HIGH-05`: `GatewayExceptionHandler` echoes raw `GatewayInvalidQueryException.Message` into `ProblemDetails.Title` and drops `e.ErrorCode` (including `AGGREGATION_PRIVACY_RESTRICTED`).
   - **13 Medium** (`MED-01..13`), **8 Low** (`LOW-01..08`), and **4 Nit** (`NIT-01..04`) findings across persistence deduplication (`AuditSqlCore`, `CatalogPhysicalIndexStore`, SQLite repository parity), tenancy/realms/dbt owners, GraphQL connections, and catalog/dbt physical key sync.
2. **61 Open GitHub Code Scanning (CodeQL) Alerts**:
   - **4 High Alerts**:
     - Alert `#210` (`cs/cleartext-storage-of-sensitive-information` in `src/Autheris.Application/Audit/AuditWriter.cs:70`): CodeQL flags `entry.EventType` logging when constants like `TrustedDocumentsReplaced` / `TrustedDocumentsRetired` flow into `_logger.LogError` / `_logger.LogWarning`.
     - Alert `#99` (`cs/cleartext-storage-of-sensitive-information` in `src/Autheris.Api/Security/BasicAuthenticationHandler.cs:262`): CodeQL tracks taint from `storedHashToVerify` / `HashPasswordArgon2id` / `HashPasswordPbkdf2` through `PasswordHasher.VerifyPassword`'s `logError` callback into `Logger.LogError`.
     - Alerts `#182` & `#183` (`cs/user-controlled-bypass` in `src/Autheris.Application/Sql/Services/FederatedDuckDbExecutionService.cs:212, 263`): CodeQL flags inline `rawSql.Contains(...)` / `request.Sql.Contains(...)` conditions guarding sensitive execution paths.
   - **56 Medium `cs/log-forging` Alerts** across 16 C# files where caller-supplied or external strings reach `ILogger` without explicit CR/LF sanitization (`\r`, `\n`).
   - **1 Medium `js/identity-replacement` Alert** (`#2` in `benchmarks/load/configs/apollo-server/index.js:59`): `.replace(/%/g, '%')` replaces `'%'` with itself.

---

## 2. Requirements Catalogue

### 2.1 Tenancy, Identity Realms & Owner Verification (`REQ-REV-TEN`)
- **`REQ-REV-TEN-01` (Fixes `CRIT-01`):** `EnterpriseClaimsTransformation` and `IdentityRealmRegistry` must resolve the caller's `IdentityRealm` deterministically from `"iss"` (for JWTs) or `identity.AuthenticationType` / `RealmKind` (for `Basic`, `BasicSession`, `ForwardAuth`, `Negotiate`), honor `IdentityRealmsOptions.DefaultRealm`, never fall back to an arbitrary `_realmsById.Values.FirstOrDefault()`, and never overwrite handler-validated per-user `tenant_id` claims (`BasicAuth.Users[i].TenantId`, `ForwardAuth` with `TrustUpstreamTenant = true`) when a realm uses `Unbound` or scheme-delegated tenant binding.
- **`REQ-REV-TEN-02` (Fixes `HIGH-01`):** Claim-less `ClusterAdmin` requests (both with `X-Tenant-ID` and global tenant-less calls) and authenticated requests to `[AllowAnonymous]` endpoints must traverse `SubjectLockoutMiddleware`, `SecurityContextResolutionMiddleware`, `FinOpsBudgetMiddleware`, `TenantRegistryAdminEndpoints.ResolveCollisionAsync`, and `TokenRevocationEndpoints.RevokeAsync` without throwing `TenantUnresolvedException`, and `resolve-collision` must be reachable by a `ClusterAdmin` to resolve a colliding tenant.
- **`REQ-REV-TEN-03` (Fixes `MED-05`, `MED-06`, `MED-07`, `MED-08`, `LOW-05`, `NIT-01`, `NIT-02`):**
  - Re-evaluate the dbt data-owner self-assignment guard (`I393B-SC-05`) after `DataOwnerAssignmentSqlCore.Resolve` resolves `LookupOnly` strings or existing `DATA_OWNERS` rows (`MED-05`).
  - Preserve `TenantStamping` idempotency across `ClaimsNormalizer.Normalize` cloning and avoid mutating the input `ClaimsPrincipal` in place (`MED-06`).
  - Restrict `ScimDataOwnerIdentityVerifier` to realms with `Provisioning == RealmProvisioningMode.Scim` (`MED-07`).
  - Enforce `SEC-SC-21` and `TenantId.TryParse` across all realm tiers in `IdentityRealmOptionsValidator` (`MED-08`).
  - Ensure `IndividualGrantOptions` runs `.ValidateOnStart()` so `IndividualGrantLimits.Current` is initialized at startup (`LOW-05`).

### 2.2 GraphQL Relay Keyset Connections & Index-Aware Cost Model (`REQ-REV-GQL`)
- **`REQ-REV-GQL-01` (Fixes `CRIT-02`):** `QueryCostAnalyzerRule` must recognize Relay `*Connection` fields as paginated list roots (`isList = true`, default page size `100`), apply `UnindexedRootListSurchargeMultiplier` to unindexed root `*Connection` fields, and multiply child selections under `edges -> node` by `effectiveRows` (`first`) without double-multiplying synthetic `edges` / `node` wrappers.
- **`REQ-REV-GQL-02` (Fixes `CRIT-03`):** `CatalogGraphQlMap.ResolveDocumentTablesAsync` must unwrap `edges -> node` (including inline fragments and fragment spreads) on `*Connection` root fields so `AiDataGuardrailService` enforces MCP table-level guardrails on all nested relation tables.
- **`REQ-REV-GQL-03` (Fixes `HIGH-02` — Owner Decision 2026-11-04):** Relay `*Connection` fields retain the `orderBy` argument (`REQ-GQL-420-01`) but restrict `orderBy` strictly to the table's resolved keyset columns (primary key or first eligible unique index via `BulkStreamKey.TryResolve`):
  - Supplying any non-keyset column (or out-of-order keyset columns) in `orderBy` on a `*Connection` field is rejected post-authorization (`SEC-RG-08`) with governed error code `INVALID_QUERY` and fixed message.
  - Caller-supplied per-column directions (`ASC`/`DESC`) on the keyset columns override the default physical directions consistently across SQL `ORDER BY` (`GraphQlTreeBuilder.BuildConnectionTree`), keyset seek predicate generation (`BuildTreeSeekFilter`), and HMAC cursor context binding (`KeysetCursorContextFactory.ForGraphQl` `Direction` and `ComputeRequestHash`), preventing cross-`orderBy` cursor replay and preventing full-table re-sorts on non-key columns.
- **`REQ-REV-GQL-04` (Fixes `HIGH-03`):** `IndexUsabilityEvaluator` must:
  1. Require both `where` and `orderBy` (when both are supplied) to be index-backed rather than letting an indexed `orderBy` mask an unindexed `where`.
  2. Extract runtime variable values when `context.VariableValues` holds HotChocolate `IValueNode` (`ObjectValueNode`, `ListValueNode`) instances, and treat unresolved `VariableNode` arguments on `where`/`orderBy` during static validation conservatively as unindexed.
  3. Require all branches of `or` to be index-backed and treat `not` and non-SARGable operators (`contains`, `endsWith`, `neq`, `nin`) on a column as not satisfying an index prefix.
  4. Pass the table's exposed column names (`visibleColumns`) from `QueryCostAnalyzerRule` into `IndexUsabilityEvaluator.EvaluateField` (`SEC-COST-421-02`).
- **`REQ-REV-GQL-05` (Fixes `HIGH-04`, `LOW-07`, `LOW-08`):** Unify the DI `ICatalogRelationRegistry` singleton with `CatalogRelationRegistry.Current` so validation and runtime cost analysis share identical relation cardinalities; pass `_options.Value.Analytics.Aggregation` in `CostAndQuotaMiddleware.RejectIfOverGatewayComplexity`; and use `CachedColumns(tbl)` in `AstSecurityVisitor.MaskGuards.AggregateAndDerived.cs`.
- **`REQ-REV-GQL-06` (Fixes `MED-09`, `MED-10`):** Align `ConnectionGraphQlTypes.ResolveConnectionAsync` with `CatalogGraphQlTypeModule.ResolveRootQueryAsync` (`SEC-RG-08` post-auth validation, pre-execution `SEC-412` masked-column check, `RegisterMaskedColumns`, `AutherisRowsReturned` row-count tracking without `2N + 2` overcounting, accurate `totalCount` or removal/documentation of page-scoped count, complete exception mapping, `decimal` PK precision via `TryGetDecimal`, `BulkStreamKey.TryResolve` reuse, `BindNullOperand` in `WriteTreeFilter`, and fragment traversal).

### 2.3 Persistence, Migrations & Dialect Parity (`REQ-REV-PERS`)
- **`REQ-REV-PERS-01` (Fixes `MED-01`, `MED-02`):**
  - Reject `anchor.Sequence < 0` in `AuditSqlCore.TryLoadVerifiedAnchor` and verify anchors when `anchor.Sequence >= 0` in `AuditSqlCore.VerifyAuditRowsAsync`.
  - Track `Overflow` in `CatalogPhysicalIndexStore.ReadBatchAsync` and fail closed (dropping the table's physical indexes with a warning) when `MaxRowsPerTable` or `MaxPhysicalIndexesPerTable` is exceeded.
- **`REQ-REV-PERS-02` (Fixes `MED-03`, `MED-04`, `LOW-01..04`):**
  - Bring `SqliteGovernanceRepository.Consent.cs` (`GetActiveConsentsByConsentRequestIdAsync`, `GetExpiringConsentsAsync`, `DeleteConsentRequestAsync`) and `SqliteGovernanceRepository.Catalog.cs` (`GetAllTablesAsync` `RowFilterEvaluation`, `UpsertTableMetadataAsync`) into 100% behavioral parity with `ConsentSqlCore` and `CatalogSqlCore`.
  - Fix the off-by-one PK + 32 unique indexes cap and preserve non-unique indexes on tables without a PK in `GovernedCatalogProvider.PhysicalKeys.cs` (`LOW-01`), update `targetColDocSource` on `LongDescription` overwrite (`LOW-02`), normalize comments before `DynamicDropConstraint` in `StepSqlLint` (`LOW-03`), and bump `CATALOG_VERSION` / publish `CatalogChangedEvent` in `ReplacePhysicalIndexesAsync` (`LOW-04`).

### 2.4 Catalog, dbt Ingestion & API Error Contracts (`REQ-REV-CAT`)
- **`REQ-REV-CAT-01` (Fixes `HIGH-05`, `LOW-06`):**
  - `GatewayExceptionHandler` must never echo `GatewayInvalidQueryException.Message` into `ProblemDetails.Title` and must preserve `e.ErrorCode` via `ApiErrorCodes.Resolve`.
  - Sanitize `DbtSyncResult.ErrorMessage` / `Warnings`, guard non-finite floats in `DatasetDataEndpoints.WriteValue`, and validate `?tenantId=` in `RebacEndpoints.GetTuplesAsync`.
- **`REQ-REV-CAT-02` (Fixes `MED-11`, `MED-12`, `MED-13`, `NIT-03`):**
  - `PhysicalKeyRefreshService` must record `AuditEventTypes.CatalogPhysicalIndexesChanged` (`RecordRequiredAsync`) before persisting non-unique indexes, invalidate the table epoch when non-unique indexes change, and only increment `indexesUpdated` after persistence succeeds.
  - `DbtMetadataIngestionService.PhysicalKeys` must fail closed (`return false`) when `_auditWriter is null`, record `"KEY_DIRECTION_AUDIT_FAILED"` in `warnings` on audit failure, and run drift detection even when `model.DeclaredKeys` is null.
  - Enforce tenant scope in `POST /api/v1/catalog/datasources/{id}/refresh-physical-keys` and preserve `existingMeta.Indexes` + `existingMeta.PhysicalIndexes` in `IngestGovernanceStreamAsync` and `IngestManifestStreamAsync`.

### 2.5 GitHub Code Scanning (CodeQL) Remediation (`REQ-REV-CODEQL`)
- **`REQ-REV-CODEQL-01` (Fixes High Alerts `#210`, `#99`, `#182`, `#183`):**
  - `#210` (`AuditWriter.cs:70, 89`): Map `entry.EventType` through a closed/safe event-type classifier or omit raw string taint before logging so CodeQL `cs/cleartext-storage-of-sensitive-information` does not trigger on `AuditEventTypes` constants.
  - `#99` (`BasicAuthenticationHandler.cs:262` / `PasswordHasher.cs`): Replace the string callback `Action<string>? logError` on `PasswordHasher.VerifyPassword` with a strongly typed enum `Action<PasswordVerificationError>? onError` so no string derived from `storedHash` taint-flows into `Logger.LogError`.
  - `#182`, `#183` (`FederatedDuckDbExecutionService.cs:212, 263`): Move the reserved placeholder prefix check into `GovernedSqlExecutionService.NormalizeClientParameters` (throwing `WebSqlPolicyException` inside the normalizer) so no inline user-controlled condition guards the execution flow (`cs/user-controlled-bypass`).
- **`REQ-REV-CODEQL-02` (Fixes 56 Medium `cs/log-forging` Alerts & 1 Medium `js/identity-replacement` Alert `#2`):**
  - Introduce a zero-allocation / low-overhead `LogSanitizer.Sanitize(string? value)` helper (`value?.Replace("\r", string.Empty).Replace("\n", string.Empty)`) and apply it at all 56 flagged `ILogger` call sites across the 16 files reported by CodeQL (`AccessAuditMiddleware`, `GatewayExceptionHandler`, `DatasourceTestingService`, `FocusCostAccountingService`, `ClassificationEngine`, `ClassificationWorkflowStateMachine`, `DifferentialPrivacyEngine`, `SchemaSunsettingService`, `AsyncQueryJobManager`, `EnvoyExtAuthzService`, `InMemoryRebacStore`, `ZanzibarRebacEvaluator`, `ItsmWebhookHandler`, `IcebergRestCatalogFederationService`, `RedisLockPubSubChannel`, `RedisEventBus`, `RedisRebacStore`, `DefaultEnvironmentSecretProvider`, `RedisClusterStateProvider`).
  - Remove the redundant `.replace(/%/g, '%')` in `benchmarks/load/configs/apollo-server/index.js:59` (Alert `#2`).
