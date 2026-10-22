# Solution Architecture Review & Strategic Remediation Roadmap

**Document ID:** `PLAN-ARCH-REVIEW`  
**Location:** `docs/plans/architecture.md`  
**Status:** R0–R5 COMPLETE ✅ · R6 DELIVERED ✅ (optional freeze-window unit-test split `WP-ARC-C-19` & folder pass `WP-ARC-B-11` deferred)  
**Last Updated:** 2026-10-15 · **Baseline:** `feat/impl-wave-5-6`  
**Parent Plan:** [`00-master-plan-overview.md`](00-master-plan-overview.md)  
**Implementation Plans:** delivered and removed (see git history); global order in [`implementation/00-implementation-roadmap.md`](implementation/00-implementation-roadmap.md)  
**Role:** Solution Architect & Engineering Lead  
**Scope:** Full-solution architectural inspection across all production assemblies (`Autheris.Api`, `Autheris.Application`, `Autheris.Domain`, `Autheris.Infrastructure`, `Autheris.Extensions`, `Autheris.GraphQL`, `TrinoSqlEngine`), test suites, and deployment configurations.

---

## 1. Executive Summary

The review produced 121 findings (26 High, 62 Medium, 33 Low) across six slices, synthesized into ten themes. The solution graph is acyclic and follows the Onion Architecture rule (Domain → Application → Infrastructure / Extensions / GraphQL → Api). The original review text is in git history (`git show d8db81cc^:docs/plans/2026-10-14-architecture-code-review/`).

Implementation Waves 1–6 delivered R0 (correctness and trust), R1 (unified query pipeline), R2 (HA, cluster state, webhook outbox, CDC fan-out), R3 (persistence, dialect registry, package boundaries, audit group commit), R4 (options split, validation consolidation, API pipeline, RFC 7807 `ApiProblems`, policies, endpoint decomposition), R5 (lock-free template cache, non-reflection AST walk, streaming core/egress, catalog version/lookup, async Redis initializer, unified diagnostics), and R6 (shared test foundation, `ScheduledHostedService`, `Autheris.slnx` removal, god-class decomposition).

---

## 2. Themes: Status Summary

| Theme | Status | Delivered WPs & Remaining Optional Freeze Windows |
|---|---|---|
| **T1** One governed query pipeline | **Done ✅** | Shared `IGovernedTableReadPlanner`, `GovernedQueryStages`, `SqlTableReadExecutor`, endpoint use-case services (`B-07`); `TreeSqlCompiler` is the documented exception (`G-6`). |
| **T2** Unified audit contract | **Done ✅** | `IAuditWriter`, `AuditPolicyTable`, chain-violation latch, `R-2` direct call migration, `AuditGroupCommitSink` & single-transaction `RecordAuditEventsAsync` (`A-07`), shared `AuditChainEngine` (`B-14`). |
| **T3** Thin Application layer via ports | **Done ✅** | `ISqlDialectProvider` & `ISqlDialectRegistry` (`B-09`); `prometheus-net` removed from `Autheris.Application`, `McpStdioRunner` moved to `Autheris.Api`, `FileSystemUniversalDocStore` moved to `Autheris.Infrastructure`, `PackageBoundaryArchitectureTests` (`B-10`). Optional folder/interface pass (`B-11`) deferred to a dedicated freeze window. |
| **T4** Persistence & versioned migrations | **Done ✅** | Versioned migration runner (`0001_baseline_governance_schema`), readiness-gated `SqliteGovernanceConnectionProvider` & WAL pooling (`B-12`, `B-13`), per-aggregate governance store registrations & `AuditChainEngine` (`B-14`), `OwnerTenantId` on `TableMetadata` (`G-11`). |
| **T5** Cluster & HA semantics | **Done ✅** | `ClusterStateUnavailableException`, `IClusterStateTopology`, renewable leases & fencing tokens (`A-11`), HA async jobs & per-tenant caps (`A-12`), `RedisCdcEventChannel` & cluster subscription cap in `CdcSubscriptionGovernor` (`A-13`), `CasbinDecisionCache` & `PolicyEpochGuard` (`A-14`). |
| **T6** Eventing, webhooks & plugins | **Done ✅** | Durable `IWebhookOutbox` & `WebhookDeliveryHostedService` (`A-02`), `CdcWebhookDeliveryService` (`A-03`), compiled plugin listener delegates (`R-1`); `A-16` skipped per `G-13`. |
| **T7** Configuration & god classes | **Done ✅** | `GatewayOptions` split into area files (`Security/`, `Resilience/`, `Storage/`, `Protocols/`, `Integrations/`, `Tenancy/`) with `GatewaySections` & `GatewayOptionsConsumptionRatchetTests` (`C-01`), `IValidateOptions<GatewayOptions>` validators (`C-02`), decomposition of `CasbinEnforcementService`, `VirtualFilterAdministrationService`, `TableAccessPolicy`, and `AstSecurityVisitor` (`C-09`, `C-10`). |
| **T8** API surface standardization | **Done ✅** | Named middleware classes, `DomainScopeRewriteMiddleware`, `McpPathResolver` (`C-04`), RFC 7807 `ApiProblems` & `ApiErrorCodes` with legacy `error` field compatibility (`C-05`, `G-9`), `GatewayPolicies` (`C-06`), endpoint partial decomposition & `DevPortalAssets` (`C-07`), `VersionedEndpointRouteBuilderExtensions` (`/api/v1` + `MapLegacyAlias`, `C-08`). |
| **T9** Hot-path performance | **Done ✅** | Parser worker pool (`C-11`), lock-free `CompiledSqlTemplateCache` & compiled AST child enumeration (`C-12`), true `StreamRowsAsync` streaming in `GovernedSqlExecutor`/`SqlTableReadExecutor`/`SqlConnector` (`C-13`), zero-copy `WrittenMemory` in `BoundedResponseBufferStream` (`C-14`), `ICatalogVersionProvider` & `TenantCatalogLookup` (`C-15`), `IRedisConnectionProvider`/`RedisConnectionInitializer` & unified `GatewayDiagnostics` (`C-16`). |
| **T10** Engineering hygiene | **Done ✅** | `Autheris.Tests.Common` (`FakeEpochValidationService`, `C-17`), `ScheduledHostedService` & `CatalogDiscoveryService` `IHttpClientFactory` (`C-22`), `Autheris.slnx` deleted (`G-10`), `TrinoSqlEngine` packaging (`C-20`). Physical split of `Autheris.Tests.Unit` (`C-19`) is reserved for an isolated test-freeze window. |

---

## 3. Remediation Waves (R0–R6)

| Wave | Focus | Status | Summary |
|---|---|:---:|---|
| **R0** | Correctness & Trust | **DONE ✅** | Delivered (including remainders `R-1..R-4`). |
| **R1** | Unified Pipeline | **DONE ✅** | Delivered (`B-01..B-08`, `G-6` parity guards). |
| **R2** | HA & State | **DONE ✅** | Delivered (`A-02`, `A-03`, `A-11..A-14`). |
| **R3** | Persistence & Dialects | **DONE ✅** | Delivered (`A-07`, `B-09`, `B-10`, `B-12..B-14`, `G-11`). |
| **R4** | API & Options | **DONE ✅** | Delivered (`C-01`, `C-02`, `C-04..C-08`). |
| **R5** | Performance | **DONE ✅** | Delivered (`C-11..C-16`). |
| **R6** | Engineering Hygiene | **DONE ✅** | Delivered (`C-09`, `C-10`, `C-17`, `C-18`, `C-20..C-22`, `G-10`); optional `C-19` test-project split reserved for a dedicated merge-freeze window. |

---

## 4. Architecture Test Guardrails

1. **Domain Isolation:** `Autheris.Domain` references zero external packages beyond the explicit allowlist.
2. **Application Decoupling:** `Autheris.Application` does not reference `prometheus-net`, `McpStdioRunner` lives in `Autheris.Api`, and `FileSystemUniversalDocStore` lives in `Autheris.Infrastructure` (`PackageBoundaryArchitectureTests`).
3. **DI & Options Cleanliness:** No usage of `IServiceProvider.GetService()` for audit repositories in `Autheris.Api` (`AuditUsageRulesTests`), and `GatewayOptions` file size and consumption are guarded by `GatewayOptionsConsumptionRatchetTests`.
4. **GraphQL Encapsulation:** `HotChocolate.*` is restricted to `Autheris.GraphQL` and composition roots.
5. **No Production Test Doubles:** No production class references `Null*` repositories or test mocks (`NoProductionNullTestDoublesArchitectureTests`).
6. **No Constructor I/O:** Governance repositories use `SqliteGovernanceConnectionProvider` and `ReadinessGatedLock` without constructor DDL (`GovernanceRepositoryConstructorPurityTests`).

The cluster-state contract is `IDistributedClusterStateProvider`.
