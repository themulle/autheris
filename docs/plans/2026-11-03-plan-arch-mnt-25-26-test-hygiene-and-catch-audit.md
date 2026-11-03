# Implementation Plan & Catch-Site Security Audit: ARCH-MNT-25 & ARCH-MNT-26

**Document ID:** `PLAN-ARCH-MNT-25-26`  
**Date:** November 3, 2026  
**Status:** COMPLETED & VERIFIED  
**Requirements:** [`docs/plans/2026-11-03-req-arch-mnt-25-26-test-hygiene-and-catch-audit.md`](2026-11-03-req-arch-mnt-25-26-test-hygiene-and-catch-audit.md) (`REQ-ARCH-MNT-25-26`)  
**Parent Plan:** [`docs/plans/2026-10-26-plan-architecture-maintainability.md`](2026-10-26-plan-architecture-maintainability.md)  
**Origin Plan:** [`docs/plans/00-master-plan-overview.md`](00-master-plan-overview.md) (Track 11)

---

## 1. Architecture & Execution Plan (`ARCH-MNT-25`)

### 1.1 Test File & Class Renames (26 Suites)

All 26 round-, date-, wave-, and increment-named test files are renamed via `git mv` alongside their public class and constructor identifiers:

| # | Former Path & Class | New Feature-Descriptive Path & Class |
|---:|---|---|
| 1 | `tests/Autheris.Extensions.Tests/Round4OpenMetadataTests.cs` | `tests/Autheris.Extensions.Tests/OpenMetadataReconcileAndAlationHardeningTests.cs` |
| 2 | `tests/Autheris.Extensions.Tests/SecurityReview20261002ExtensionsTests.cs` | `tests/Autheris.Extensions.Tests/LakehouseAndCatalogExtensionsSecurityTests.cs` |
| 3 | `tests/Autheris.Tests.Unit/Catalog/Wave3CatalogOwnershipTests.cs` | `tests/Autheris.Tests.Unit/Catalog/CatalogOwnershipAndTierVisibilityTests.cs` |
| 4 | `tests/Autheris.Tests.Unit/Governance/Issue158Inc1DescriptionOptInTests.cs` | `tests/Autheris.Tests.Unit/Governance/ConsentDescriptionOptInLoadingTests.cs` |
| 5 | `tests/Autheris.Tests.Unit/Governance/Issue158Inc1ExplainDescriptionTests.cs` | `tests/Autheris.Tests.Unit/Governance/EffectiveAccessConsentDescriptionTests.cs` |
| 6 | `tests/Autheris.Tests.Unit/Governance/Wave2GovernanceTests.cs` | `tests/Autheris.Tests.Unit/Governance/DeclarativeConsentAndAccessIntrospectionTests.cs` |
| 7 | `tests/Autheris.Tests.Unit/GraphQL/Wave4ColumnSemanticsAndTemporalTests.cs` | `tests/Autheris.Tests.Unit/GraphQL/ColumnSemanticsAndTemporalMetadataTests.cs` |
| 8 | `tests/Autheris.Tests.Unit/Procedures/ProcedureLimitsInc2Tests.cs` | `tests/Autheris.Tests.Unit/Procedures/ProcedureRowBudgetAndScanCapTests.cs` |
| 9 | `tests/Autheris.Tests.Unit/Procedures/ProcedureSecurityInc1Tests.cs` | `tests/Autheris.Tests.Unit/Procedures/ProcedureExecutionSecurityTests.cs` |
| 10 | `tests/Autheris.Tests.Unit/Round4EgressTests.cs` | `tests/Autheris.Tests.Unit/OutboundEgressAllowlistHardeningTests.cs` |
| 11 | `tests/Autheris.Tests.Unit/Round4OpenMetadataCoreTests.cs` | `tests/Autheris.Tests.Unit/MarkerConsentRevocationAndSecretRedactionTests.cs` |
| 12 | `tests/Autheris.Tests.Unit/Round4ParserGatewayTests.cs` | `tests/Autheris.Tests.Unit/WebSqlParserGatewaySecurityTests.cs` |
| 13 | `tests/Autheris.Tests.Unit/Security/RepeatedReviewRound2Tests.cs` | `tests/Autheris.Tests.Unit/Security/CasbinPolicyParsingAndAuthHardeningTests.cs` |
| 14 | `tests/Autheris.Tests.Unit/Security/RepeatedReviewRound3Tests.cs` | `tests/Autheris.Tests.Unit/Security/DynamicMaskingAndConnectorGuardTests.cs` |
| 15 | `tests/Autheris.Tests.Unit/SecurityReview20261002EndpointTests.cs` | `tests/Autheris.Tests.Unit/RestMcpHitlAndWebhookSecurityTests.cs` |
| 16 | `tests/Autheris.Tests.Unit/SecurityReview20261002ExtensionsCoreTests.cs` | `tests/Autheris.Tests.Unit/SqlEndpointDbtSyncAndCatalogMergeSecurityTests.cs` |
| 17 | `tests/Autheris.Tests.Unit/SecurityReview20261002GraphQLTests.cs` | `tests/Autheris.Tests.Unit/GraphQlSubscriptionAndFederationSecurityTests.cs` |
| 18 | `tests/Autheris.Tests.Unit/SecurityReview20261002HostingTests.cs` | `tests/Autheris.Tests.Unit/HostingAuthAndMiddlewareSecurityTests.cs` |
| 19 | `tests/Autheris.Tests.Unit/SecurityReview20261002InfraTests.cs` | `tests/Autheris.Tests.Unit/InfrastructureAndPluginSecurityTests.cs` |
| 20 | `tests/Autheris.Tests.Unit/SecurityReview20261002PolicyCoreTests.cs` | `tests/Autheris.Tests.Unit/PolicyCoreConsentUnionAndRlsSecurityTests.cs` |
| 21 | `tests/Autheris.Tests.Unit/SecurityReview20261002WebSqlTests.cs` | `tests/Autheris.Tests.Unit/WebSqlExecutorAndConnectorSecurityTests.cs` |
| 22 | `tests/Autheris.Tests.Unit/SecurityWave2ExpertTests.cs` | `tests/Autheris.Tests.Unit/DevPresetAndGovernanceFailClosedSecurityTests.cs` |
| 23 | `tests/Autheris.Tests.Unit/Wave1GatewayHardeningTests.cs` | `tests/Autheris.Tests.Unit/PreAuthRateLimitAndProtocolKillSwitchTests.cs` |
| 24 | `tests/Autheris.Tests.Unit/Wave2MarketFeaturesTests.cs` | `tests/Autheris.Tests.Unit/ResourceGroupAndObservabilityFeatureTests.cs` |
| 25 | `tests/Autheris.Tests.Unit/Wave3MarketFeaturesTests.cs` | `tests/Autheris.Tests.Unit/HitlStepUpAndArrowSerializationFeatureTests.cs` |
| 26 | `tests/TrinoSqlEngine.Tests/SecurityReview20261002SqTests.cs` | `tests/TrinoSqlEngine.Tests/SqlParserLexerAndRewriterDifferentialTests.cs` |

### 1.2 Shared Test Fakes Consolidation (`tests/Autheris.Tests.Common/`)

1. **`FakeEpochValidationService`:** Replaced 9 duplicate private `StubEpochValidationService` classes across `ComprehensiveSecurityAttackVectorTests`, `ItsmClientsAndRecertificationTests`, `ItsmOutboxTests`, `RestMcpHitlAndWebhookSecurityTests`, `SecurityAttackVectorTests`, `SecurityReviewG5Tests`, `AtomicApprovalFourEyesTests`, `ConsentFourEyesSensitivityTests`, and `SecurityReviewRemediationCoverageTests`.
2. **`StaticOptionsMonitor<T>`:** Extended with `SetCurrent(T)` and replaced 7 duplicate private `TestOptionsMonitor<T>` classes across `AuthenticationSecurityTests`, `SecurityFindingsRemediationTests`, `ClassificationEngineTests`, `ClassificationWorkflowStateMachineTests`, `ClassificationWorkflowTests`, `WormConfigurationAuditServiceTests`, and `WormConfigurationStartupAuditorTests`.
3. **`StubHttpMessageHandler`:** Added an async-responder constructor overload and linked `StubHttpMessageHandler.cs` into `Autheris.Extensions.Tests.csproj` (preserving the frozen `project-edges.txt` graph). Replaced 9 duplicate private HTTP handler stubs across `OpenMetadataReconcileAndAlationHardeningTests`, `DeclarativeHttpTenantPushdownTests`, `ExtensionsRegistrationTests`, `InfrastructureAndPluginSecurityTests`, `ItsmClientsAndRecertificationTests`, `LineagePdfAndOpenLineageTests`, `PurviewTokenCacheTests`, `DatasourceSecretNamespaceTests`, and `ResponseSizeLimitingHandlerTests`.
4. **`CapturingLogger` & `CapturingLogger<T>`:** Added [`tests/Autheris.Tests.Common/CapturingLogger.cs`](../../tests/Autheris.Tests.Common/CapturingLogger.cs) (with thread-safe `Entries`, `Messages`, `Lines`, `Warnings`, and `Count(LogLevel)`), linked it into `Autheris.Extensions.Tests.csproj`, and replaced 7 duplicate logger fakes in `OpenMetadataReconcileAndAlationHardeningTests`, `DeclarativeHttpTenantPushdownTests`, `PurviewTokenCacheTests`, `CatalogVisibilityServiceTests`, `ColdPathTimingTests`, `GatewayRoleRightAssignmentReportTests`, `McpDatasetDisplayNameTests`, and `EmptyListConfigurationValidatorTests`.

---

## 2. Catch-Site Security Audit Report (`ARCH-MNT-26` / `REV-07`)

### 2.1 Audit Scope & Methodology
All `catch` clauses across `src/Autheris.Domain`, `src/Autheris.Application`, `src/Autheris.Infrastructure`, `src/Autheris.Api`, `src/Autheris.GraphQL`, `src/Autheris.Extensions`, and `src/TrinoSqlEngine` were scanned. Out of 875 total `catch` clauses, 421 sites did not immediately rethrow or call a logger inside the catch block and were inspected line-by-line against Zero-Trust and Fail-Closed invariants.

### 2.2 Classification of Audited Catch Sites

#### Category A — Explicit Fail-Closed Security & Policy Guards (Verified Safe)
These sites catch exceptions during authentication, tenant resolution, policy evaluation, row-level security, dynamic masking, egress validation, or AST compilation and immediately return a deny decision, reject scheme, redacted value, or unavailable state:
* **Ingress & Authentication (`Autheris.Api`):**
  - `SecurityContextResolutionMiddleware.ResolveGuard` (lines 55, 69): Returns `null` on options/guard failure; `DenyCollidingTenantAsync` treats `guard == null` as `unresolved` and denies the request with 403 (`TENANT_ID_COLLISION`).
  - `EndpointSecurity.cs:89`: Catches `UnauthorizedAccessException` when an authenticated principal lacks a SID and returns `null` (treated as 401 unauthenticated, `RV-02`).
  - `RealmSchemeSelector.cs:40` & `GatewayAuthSchemes.cs:42`: Malformed bearer token routes to `GatewayAuthSchemes.RejectScheme` / default JWT rejection.
  - `PasswordHasher.cs` (lines 190, 228, 273): Any format or cryptographic error during password hash verification returns `false`.
  - `TrustedDocumentsOnlyMiddleware.cs:270`: Returns `Fail(AppRejectionReason.RegistryError)`.
  - `BasicAuthAttemptGuard.cs`: Shared success-marker cache failures return `null` (forcing full password verification on the replica); Redis lockout lookup/increment failures fall back to the bounded per-process `_failures` lockout state.
* **Authorization, Casbin ABAC, Consents & Virtual Filters (`Autheris.Application`):**
  - `CasbinEnforcementService.Columns.cs:542` & `.Masking.cs:200,219,278,350`: Any evaluation or regex timeout sets `allowed = false` / `failClosedMatch` / `ObjectPatternMatcher(null, null, true)`.
  - `GrantPolicy.cs:24`: Catches non-OOM exceptions and returns `GrantDecision.Deny(GrantErrorCodes.Indeterminate, ...)`.
  - `CallerAttributeBinder.cs:119`: Returns `PolicyPredicate.DenyAll`.
  - `GraphRoleProvider.cs:105`, `LdapsRoleProvider.cs:79`, `ScimStoreRoleProvider.cs:41`, `RoleResolver.cs:122`: Mark role resolution as `FailedResult` / `degraded = true` (`SEC-SC-12`, denied by PDP).
  - `LockRoleCacheInvalidationHostedService.cs:41`: If targeted cache invalidation throws on a SCIM lock event, calls `cache.InvalidateAll()` (fail-closed).
  - `TableAccessPolicy.cs` (lines 194, 306, 459), `.Consent.cs` (lines 148, 231, 257), `.Rls.cs:47`: Return `TableAccessDecision.Denied(...)`.
  - `MandatoryRowFilterResolver.cs` (lines 233, 264, 301, 355, 554, 560): All non-cancellation exceptions return `MandatoryFilterOutcome.Deny(...)`.
  - `ColumnMaskingProvider.cs:224` & `DynamicTableType.cs:117`: Return `"REDACTED"` / `null` when masking evaluation throws.
  - `LegacyTextRowFilterEvaluator.cs:51,132`, `PolicyPredicateRowEvaluator.cs:58,64`, `StreamingRowFilterAstEvaluator.cs:76`: Return empty row sets / `false` on any evaluation failure.
  - `TotpEnrollmentService.cs:48,83,124` & `TotpVerificationService.cs:256`: Return `null` / `StoreUnavailable` / `false`.
  - `GovernancePolicyFactory.cs:406`: Records `new GovernanceRecord(null, Failed: true)` (never conflated with "no record") after rethrowing `OperationCanceledException`.
  - `AiDataGuardrailService.cs:482`: Sets `visibleKeys = null`, which yields `Array.Empty<GoldenQuery>()` (`MCP-15`).
* **AST SQL Compiler & Security Visitor (`TrinoSqlEngine`):**
  - `AstSecurityVisitor.MaskGuards.cs:377,389`: Any failing lookup returns `null` / `true` ("uncertainty fails toward the guard running").
  - `AstSecurityVisitor.Support.cs:324`: Scrubbing failure falls back to `FullyRedactedStatement`.
  - `TimeZoneCallValidator.cs:216,238` & `TimeZoneRegistry.cs:107`: Fail toward rejecting/disabling unknown time zone calls.
  - `ParseWorkerPool.cs:166`: On `OutOfMemoryException` or `ThreadStartException`, decrements live count so the caller times out and rejects the query (fail-closed).
* **Key Vault & Cryptographic Providers (`Autheris.Infrastructure`, `Autheris.Domain`):**
  - `KeyVaultCdcKekProvider.cs:107` & `KeyVaultWebhookSecretProtector.cs:138`: Return `CdcKekLookup.Unavailable` / `null` without surfacing secret names in exception messages.
  - `PostgreSqlGovernanceRepository.cs:159,185`, `SqlServerGovernanceRepository.cs:163`, `SqliteGovernanceRepository.cs:321`: Key resolution failures throw `InvalidOperationException` in non-Development/non-Test environments and only allow ephemeral fallback in `Development`/`Test`.
  - `AsymmetricChainAnchorService.cs:110` & `AuditChainAnchorStores.cs:764`: Signature verification failures return `false`.

#### Category B — Input Validation, `TryParse` Idioms & Sanitized Error Mapping (Verified Safe)
* Narrow catches (`FormatException`, `JsonException`, `ArgumentException`, `UriFormatException`, `OverflowException`, `SyntaxException`) in `EgressAllowlist`, `OutboundPathPolicy`, `PurviewQualifiedName`, `OpenMetadataTableIdentity`, `SemanticManifestValidator`, `PolicyPredicateLinter`, `McpAdminTools`, `McpToolExecutionHandler`, and `DatasetCatalogStore` return `false` / `null` or fixed governed error codes (`NL-SC-21/31`, `ARCH-MNT-08`) so raw parser exceptions never echo secrets or connection strings to callers.
* `TableMetadataUnavailableException` catches in `TableStreamEndpoints`, `GatewayExecutionService`, `OpenApiIngestionService`, `DataCatalogSyncService`, and `DbtMetadataIngestionService` preserve `SEC-DATA178-04` (404 anti-enumeration parity) and `SEC-94-01` (never overwriting an unreadable table as a new unmasked table).

#### Category C — Best-Effort Teardown, Cancellation & Non-Blocking Telemetry (Verified Safe)
* `OperationCanceledException` and `ObjectDisposedException` catches in background workers (`GraphQlExecutorGate`, `AuditRetentionPurgeWorker`, `AsyncQueryJobBackgroundWorker`, `CdcStreamCompatFeed`, `CdcStreamTailHub`, `PostgreSqlLogicalReplicationService`), temporary file/connection cleanup (`DuckDbPersistentTableCacheStore`, `SqliteGovernanceRepository.Dispose`), and non-blocking metrics (`DisabledReadPathObserverMiddleware`, `AuthPluginRoleEscalationGuard.RecordAlertAudit`).

#### Category D — Hardened Sites in This PR (3 Narrowing + Diagnostic Fixes)
While no fail-open security vulnerability was found across `src/`, three bare `catch` blocks in data catalog / metadata parsing were narrowed and equipped with structured warning logs:
1. [`src/Autheris.Extensions/DataCatalog/CatalogWebhookHandler.cs`](../../src/Autheris.Extensions/DataCatalog/CatalogWebhookHandler.cs) (`ExtractAffectedTables`, line 427): Replaced bare `catch` with `catch (Exception ex) when (ex is JsonException or ArgumentException)` and logged a structured warning with the provider name and `ex.GetType().Name` (never logging the raw webhook payload).
2. [`src/Autheris.Extensions/DataCatalog/CollibraDataCatalogClient.cs`](../../src/Autheris.Extensions/DataCatalog/CollibraDataCatalogClient.cs) (`ParseCollibraAssetsResponse`, line 129): Replaced bare `catch` with `catch (Exception ex) when (ex is JsonException or ArgumentException)` and logged a structured warning with `ex.GetType().Name`.
3. [`src/Autheris.Infrastructure/Persistence/CatalogSqlCore.cs`](../../src/Autheris.Infrastructure/Persistence/CatalogSqlCore.cs) (`ParseMetadataDictionary`, line 101): Narrowed bare `catch` to `catch (JsonException)`.

---

## 3. Verification Checklist
- [x] `dotnet build Autheris.sln --nologo -v minimal` succeeds with 0 warnings and 0 errors.
- [x] `Autheris.Tests.Architecture` (including `ProjectReferenceGraphArchitectureTests`, `SourceSizeRatchetArchitectureTests`, `NamespaceAssemblyConsistencyTests`) passes.
- [x] `TrinoSqlEngine.Tests` (`SecurityRegressionTraceabilityTests` and `SqlParserLexerAndRewriterDifferentialTests`) passes.
- [x] `Autheris.Tests.SqlCompilerGate` (`CorpusTests`) passes.
- [x] `Autheris.Extensions.Tests` and `Autheris.Tests.Unit` pass for all renamed and consolidated test suites.
