# REQ-SIMPL: Project Simplification Program (PRD)

- Phase: 1 (Product Manager). Date: 2026-11-05. Base: origin/main fe0e131d.
- Status: Draft for owner decision. Tracked by GitHub issues #509-#515.
- Related: [2026-10-26-plan-architecture-maintainability.md](2026-10-26-plan-architecture-maintainability.md) (ARCH-MNT; REV-05 / ARCH-MNT-22 dialect dedup, section 2.4 dead options). This PRD references it and does not duplicate it; where ARCH-MNT already specifies the technical approach, REQ-SIMPL only sets the product-level target and scope.
- Constraints: English only; Autheris is not in production (breaking changes allowed, no compatibility shims); schema migrations forward-only with v1.5.0 as frozen base; no code changes in this phase.
- Evidence: "verified 2026-11-05" means re-measured by the PM on origin/main fe0e131d (corrections to the external review are applied).

## 1. Problem and Baseline

A critical whole-project review (10 findings) concluded that Autheris is an AI-generated feature catalog rather than a product: technically ambitious, with solid fail-closed guards, architecture ratchets and fuzzing, but without a defined core, with unverified claims, and with one human maintainer. The PM re-measured and confirmed the findings with the corrections listed below.

### 1.1 Verified baseline (verified 2026-11-05)

| Metric | Baseline |
|---|---|
| src C# | 302,461 lines, 1,903 files (Application 131,614; Infrastructure 52,174; Api 38,950; TrinoSqlEngine 25,698; Extensions 19,830; Domain 19,602; GraphQL 14,593) |
| tests C# | 382,125 lines (Unit 268,440; Integration 36,715; TrinoSqlEngine.Tests 33,692; Architecture 10,659; Extensions.Tests 10,031; Parity 9,805; SqlCompilerGate 5,772; Simulation 4,883; Fuzz 1,090) |
| HTTP endpoints | 275 Map{Get,Post,Put,Delete,Patch,Methods} calls, plus GraphQL and MCP |
| Application layout | 49 top-level folders; duplicates Audit/+Auditing/, Catalog/+DataCatalog/; Dbt/ and OpenMetadata/ also in Extensions |
| DI / background | 549 DI registrations, 39 hosted services |
| Image / cold start | 373 MB, P50 4.52 s (non-R2R). Configured R2R variant: 441 MB uncompressed (170 MB compressed), cold P50 5.58 s, warm P50 2.97 ms, P99 10.24 ms (non-R2R: 1.17 / 3.47 ms). R2R is worse on every metric but `PublishReadyToRun=true` is still set (Autheris.Api.csproj:39) |
| Options | 173 `*Options` classes, ~1,173 settable properties, 96 `public bool *Enabled`, 46-line unconsumed-options baseline (~45 entries), 335 of 937 property names undocumented, appsettings.json 82 keys |
| Governance persistence | SqlServer / PostgreSql / Sqlite repositories, 26 partials each, 5,276 / 5,243 / 5,241 lines (~15,800); Persistence folder 37,605 lines; migrations triplicated |
| SQL engine | TrinoSqlEngine 25,698 + tests 33,692 + gate 5,772 + Fuzz + 3 Stryker configs; SqlAstBuilder 1,673; AstSecurityVisitor 13 partials / 3,234; 8 `TargetSqlDialect` values, 6 generators; parity suite SQLite-centric (33 vs 1 DuckDB); master plan lists SQL Server/PostgreSQL/DuckDB parity baselines as open; ADR-017 has no build-vs-buy |
| Tests | 14,333 [Fact]/[Theory]; Unit 10,782 vs Integration 906; 4,159 Substitute.For; 986 Received/DidNotReceive; 92 Task.Delay/Thread.Sleep; 76 unit files use SqliteConnection/DuckDBConnection directly; 11 use TestServer/WebApplication.Create; 155 loose files in Unit root with review-style names |
| Docs | 96 feature docs; 4 "Not implemented" docs still indexed (f-ai-09, f-ai-10, f-gov-06, f-perf-10); 37-39 "Status: [Done] (100% GA)" (correction: not 44); 310 `file:///root/autheris/` occurrences; 23 broken relative links; non-English concept doc (1,326 lines) |
| Code comments | ~1,860 plan/ticket-id comment lines, >= 521 distinct ids; 339 plan files deleted (dangling references) |
| Process | 1,544 commits in 31 days, 232 in one day; +1.55M / -474k lines; 539 fix vs 286 feat; 101 security-fix commits; single human identity (four identities) |
| Versioning | 56 tags v1.0.0..v1.7.0 in 4 weeks; Directory.Build.props 1.2.1; Chart appVersion "1.0.0" and default tag falls back to it (non-upgradable: v1.5.0 is oldest migration base); no CHANGELOG |
| Claims | README:56-58 claims 64,500 req/s, 1.82 ms P99, 0 B (HotChocolate stated 16.6.7 Native AOT; actual 16.7.3, no PublishAot); benchmarks/load/results holds only .gitkeep; only measurement is r2r-spike-report.md |
| Misdescribed components | "Arrow Flight SQL for JDBC/ODBC" is HTTP POST /api/v1/flight/sql/* with Arrow IPC (no gRPC/Flight; JDBC/ODBC cannot connect); f-sec-04 names OpenFgaRebacClient.cs / IRebacAuthorizationService.cs (absent; OpenFgaApiUrl/StoreId are baselined as unconsumed) |

## 2. Product Core (REQ-SIMPL-01 foundation)

### 2.0 Owner decisions (binding)

- **OD-1 (binding, owner, 2026-11-05):** SQLite is dropped as the governance/metadata catalog store. Only SQL Server and PostgreSQL remain as governance databases. Scope is the governance/metadata store only; SQLite as a governed data source is a separate open decision (D14). Requirements: REQ-SIMPL-12 and REQ-SIMPL-05.
- **OD-2 (owner request, 2026-11-05; recommendation, default taken):** evaluate and, by default, execute full SQLite removal (data source, parity, quickstart, tests), staged: governance store first, then tests/parity, then the data-source dialect. Evidence favours removal for the product (parity and CI are SQLite-centred but PostgreSQL fixtures exist) but the trade-offs are real (no zero-dependency quickstart, slower DB tests, transition parity gap); the owner can stop after step 1 or 2. See REQ-SIMPL-13.
- **OD-3 (binding, owner, 2026-11-05):** WebSQL DML (`RestApi:AllowDml`, opt-in, off by default) stays in the core. The DML advisories and #505 are fixed in the core security lane; the write-grant fix must not rely on Casbin, which is deleted (WP-SIMPL-14).
- **OD-4 (binding, owner, 2026-11-05):** catalog search, including semantic search and embeddings, is core. #501 and the embeddings part of #488 are FIX-IN-CORE.
- **OD-5 (binding, owner, 2026-11-05):** the ITSM integration and the header-based break-glass/justification flow (`JustificationAndBreakGlassInterceptor` and its options and headers) are deleted. Access-profile break-glass assignments are a separate feature and stay. The ITSM-related parts of #507 and #485 are OBSOLETE-BY-REMOVAL.
- **OD-6 (binding, owner, 2026-11-05):** the CI time target counts queue time: wall-clock from push to the last required check, median, < 30 min for PRs (definition of #523). Replaces the tentative D7 split.
- **OD-7 (binding, owner, 2026-11-05; GHSA-78j7-3gh6-7cqx, ReBAC defaults):** ReBAC is enforced on all paths as soon as a tenant has tuples; `EnforceOnQueryPaths`/`EnforceOnStreaming` are replaced by one switch; startup fails if ReBAC is enabled with tuples but a path is exempted.
- **OD-8 (binding, owner, 2026-11-05; GHSA-4qx8-fv2p-q8r2, audit archive):** the unwired WORM archive export is deleted; startup fails if `Audit:Worm:Enabled` is still set; retention purge stays with signed purge checkpoints and anchors; long-term archiving is an operator responsibility (SIEM export, database backup) and may return later as a module.
- **OD-9 (binding, owner, 2026-11-05; GHSA-mgv5-4r69-mwww, admin scoping):** global operations only for the canonical `ClusterAdmin`; `GovernanceAdmin` is always tenant-bound; a token without a tenant claim falls back to the default tenant and is never global. This differs from the model in the in-progress `fix/security-batch-5`, which must be adjusted before merge. It also settles the role model behind GHSA-9229-wx6j-wvm9 and GHSA-v5c2-29pv-cc86.
- **OD-10 (binding, owner, 2026-11-05; GHSA-qx32-576x-5cq2, GDPR report):** the GDPR disclosure report stays in core (with its authorization fix); the rest of Lineage may become a module.
- **OD-11 (binding, owner, 2026-11-05):** governed aggregation with small-group suppression and time buckets stays in core; differential-privacy noise is deleted. #467 and #468 are OBSOLETE-BY-REMOVAL; GHSA-rxr4-mf28-p2hv is solved by small-group suppression alone.
- **OD-12 (binding, owner, 2026-11-05):** Oracle database support MUST be kept and Databricks is an important data source and MUST be kept. The Oracle and Databricks SQL dialect generators stay in core as governed data sources (incl. DatabricksOboTokenProvider / Databricks executor and Oracle connection support; Oracle.ManagedDataAccess.Core stays a core dependency). The governance store remains PostgreSQL + SQL Server only (OD-1). Overrides earlier DELETE classifications and D5/ADR-019 where they conflict.
- **OD-13 (binding, owner, 2026-11-05):** the PostgreSQL wire protocol endpoint proposal (REQ-PG-WIRE-01, backlog rank 5) is dropped (REJECTED).
- **OD-14 (binding, owner, 2026-11-05, revised):** the Snowflake dialect generator is deleted (no value for Databricks or the integration tests; only generator unit tests and "non-admitted dialect" examples use it, which switch to another non-admitted value). The ANSI generator is kept as a generator-only dialect (not admitted as a data source per `SqlDialectMapper` / `DialectAdmissionTests`). Supersedes the open D16.
- **OD-15 (binding, owner, 2026-11-05):** cross-source joins in the engine are core functionality (for example a Databricks table joined with a SQL Server table or with a declarative HTTP source). `FederatedDuckDbExecutionService`, `CrossSourcePlanner`, the subtree compiler/partitioner and federated staging stay in core; DuckDB.NET and the DuckDB dialect generator (federated staging SQL) stay core dependencies. Federation paths need parity and integration coverage in core. **Extended (binding, owner, 2026-11-05):** DuckDB belongs entirely in core, not only federation: the Olap engine and endpoints (`Application/Olap`, `DuckDbOlapEndpoints`), the DuckDB-backed table cache (#475 fixed in core), the DuckDB dialect generator and the DuckDB parity/integration tests. The Olap module is dropped; OData is the module pilot.
- **Forward-looking note (OD-15, no feature work during the freeze):** DuckDB is the intended future place for central views and measures (for example as the basis for data products). Nothing in this program may block a later central views/measures layer on DuckDB: the semantic metric model and metric query path stay in core (Catalog/Analytics) and stay compatible with DuckDB execution; dbt ingestion stays a module that only imports manifests into the core metric model (default taken; no conflict found once `SemanticMetricCatalogProjection` moves from `Application/Dbt` to core, see PLAN-SIMPL WP-SIMPL-28).

### 2.1 Positioning (one sentence)

Autheris is a policy enforcement gateway that sits between data consumers (applications, analysts and AI agents) and SQL databases, and enforces consent, row-level security, column masking, and tamper-evident audit on every query, using catalog sensitivity classifications as policy input.

Audience: platform/data-governance engineers at regulated organizations (GDPR, finance, health) who must expose relational data to humans and LLM agents without per-application access code. Differentiator: fail-closed enforcement shared across transports, not breadth of catalog integrations.

### 2.2 Core capabilities (KEEP set)

1. Consent (purpose-bound consent, consent requests, subject lifecycle, functional rights).
2. Row-level security (policy-derived and virtual filters, injected via the SQL AST).
3. Column masking (sensitivity-driven, incl. subgraph/federation masking).
4. Audit (hash-chained, anchored, queryable; one Audit module instead of two).
5. Policy and authorization: RBAC, ReBAC (in-memory Zanzibar evaluator), policy simulation/explain.
6. Catalog and sensitivity classification as policy input (internal catalog only).
7. Identity: OIDC/JWT, token revocation, tenancy.
8. SQL compiler (TrinoSqlEngine + AST security visitor + gate) as the enforcement point.
9. Ops baseline: health, metrics/tracing, options validation, fail-closed startup checks, governance persistence.

### 2.3 Core transports (decision, default taken)

Core transports: **REST, GraphQL, MCP** (three; each needs parity tests against the same enforcement pipeline). REST includes the Web SQL endpoint and async jobs because they are the SQL entry point.

| Transport | Decision | Justification |
|---|---|---|
| REST (incl. Web SQL, async jobs, Arrow IPC as a response format) | KEEP | Primary integration surface, simplest to secure |
| GraphQL | KEEP | Typed access, subgraph masking already central |
| MCP | KEEP | Distinguishing use case: governed data access for AI agents; HITL step-up approvals belong here |
| OData | MODULE | Own query parser (1,927 + 768 lines) and attack surface; niche demand; no parity evidence |
| Arrow "Flight SQL" over HTTP | DELETE (rename) | Misnamed; no interop with Flight clients. Keep plain Arrow IPC export as a response format (`ArrowExportEndpoints`). Delete `ArrowFlightSqlEndpoints`/`ArrowFlightSqlServer` and all JDBC/ODBC claims. Re-add a real gRPC Flight SQL server only as a future MODULE with a JDBC interop test |
| SQL endpoints (SqlEndpoints/, tenant SQL routes) | KEEP if they are the REST Web SQL path; otherwise merge into it | One SQL entry per transport |
| CDC/streaming (SSE, Cdc extension) | MODULE | Event delivery is not enforcement; heavy (Application Streaming 4,440 + Cdc 2,296) |
| Webhooks | MODULE (bundled with CDC/Events delivery module) | Delivery block already unconsumed (WebhooksOptions); SSRF/security surface |
| PG wire proposal (REQ-PG-WIRE-01) | REJECTED (OD-13, binding; do not build) | A PG wire proxy needs a second SQL front end, auth mapping and result masking in a binary protocol; large security surface; contradicts the "fewer transports" goal. Revisit only after core targets are met |
| Envoy ext-authz | MODULE | Authorization-decision endpoint; small, but a separate deployment pattern |

## 3. Feature Inventory and Classification (REQ-SIMPL-02)

### 3.1 Criteria (apply in order; any DELETE trigger decides)

- C1 Core value: contributes directly to consent, RLS, masking, audit or policy enforcement.
- C2 Security-surface cost: unauthenticated or externally reachable parsing/IO, outbound HTTP (SSRF), credential handling.
- C3 Dependency weight: native or large packages loaded in every deployment.
- C4 Test/parity coverage: has real (non-mock) tests and, for dialects, parity tests.
- C5 Evidence of use: referenced by an owner scenario, sample or integration test; no evidence plus no core value means DELETE.
- C6 Maintenance cost: LOC, churn (fix commits), duplicated code.

Rules: KEEP = C1 true. MODULE = C1 false, but clear user value, isolated behind an interface, own package and release cadence, loaded at runtime. DELETE = C1 false and (no evidence of use, or C4 false and C2/C3 high, or the feature is documented but not implemented). Modules need an explicit owner; an ownerless module is deleted at the next review.

MODULE mechanism (default taken): separate NuGet packages/assemblies discovered from a `modules/` directory via `AssemblyLoadContext` plus a signed manifest allow-list (fail closed on unknown/unsigned assembly); the host exposes stable `Autheris.Modules.Abstractions`. The Phase 2 architect decides the exact loading design (compile-time optional package references per image flavor is the acceptable fallback).

### 3.2 Classification table

Sizes are verified 2026-11-05 line counts of the owning folders (Application / Extensions).

| Area | Class | Rationale (criteria) | Operator / user impact |
|---|---|---|---|
| Consent, ConsentRequests, FunctionalRights, SubjectLifecycle | KEEP | C1 | none |
| RLS: VirtualFilters (5,944), Policy (3,158), RLS evaluation | KEEP | C1 | none |
| Masking incl. SubgraphResultMasker | KEEP | C1 | none |
| Audit + Auditing (merge into one folder) | KEEP | C1; dedupe | internal rename only |
| RBAC, ReBAC in-memory evaluator, Identity (5,007), Security, TokenRevocation, Tenancy | KEEP | C1/C2 | none; OpenFGA options deleted (never consumed) |
| Sql, TrinoSqlEngine, SqlCompilerGate | KEEP | C1; scope narrowed (see 3.3) | dialect set reduced |
| Catalog + DataCatalog (internal), classification, data contracts | KEEP | C1 (sensitivity as policy input) | Catalog/DataCatalog merged |
| Governance workflows, recertification, HITL step-up (Workflows, Mcp HITL) | KEEP | C1 | none |
| MCP (11,400) | KEEP | core transport | none |
| Governance persistence (Governance 15,981 + Infrastructure) | KEEP, restructure | C6 (REQ-SIMPL-05) | none for API; schema untouched |
| Observability/Telemetry/Diagnostics, health, Jobs | KEEP | ops baseline | none |
| Purview | DELETE | C5: no evidence, C2 outbound credentials | operators sync catalog via the generic import API or a custom module |
| Collibra | DELETE | same | same |
| Alation | DELETE | same | same |
| DataHub | DELETE | same | same |
| OpenMetadata (Extensions 1,838 + Application 328) | MODULE | most-used open-source catalog; one catalog-sync module owns it | install `autheris-module-openmetadata`; duplicate Application copy removed |
| Backstage (604) | DELETE | C1 false, C5 none, UI-portal concern | use REST API directly |
| ServiceNow / Jira (Itsm 1,062) | DELETE | C2 outbound HTTP + credentials, C5 none | use Webhooks module to push to ITSM |
| dbt (Extensions 5,692 + Application 2,342) | MODULE | metadata ingest feeds sensitivity; big, but real value | module `autheris-module-dbt`; duplicate Application copy removed |
| Databricks (connector, executor, DatabricksOboTokenProvider, dialect) | KEEP (core; OD-12, overrides earlier DELETE) | important governed data source; must reach full parity (C4) | none; parity suite required |
| Oracle (dialect, connection support, Oracle.ManagedDataAccess.Core) | KEEP (core; OD-12, overrides earlier DELETE) | Oracle support is mandatory | none; parity suite required |
| Delta/Iceberg Lakehouse (3,680, Iceberg REST catalog endpoints, Parquet.Net) | MODULE | C3 heavy, C1 false | module `autheris-module-lakehouse`; core drops Parquet.Net |
| Casbin | DELETE | C1 duplicates RBAC/ReBAC; CasbinOptions.EnforceInQueryPipeline is silently ignored (ARCH-MNT 2.4) which is a fail-open smell | use native RBAC/ReBAC policy |
| Envoy ext-authz | MODULE | see 2.3 | install module if used as mesh authorizer |
| SCIM | MODULE | provisioning, own surface; ScimOptions.AllowedIpRanges ignored (ARCH-MNT 2.4) | module must enforce its own allow-list or ship without the option |
| TOTP | MODULE (local-auth module) | C1 false; core relies on OIDC | deployments using local TOTP install module |
| Kerberos/NTLM (Negotiate) | MODULE | enterprise Windows only; platform deps | module; default image has no Negotiate |
| Embedded Garnet server (GarnetServerManager) | DELETE recommended (OPEN, D17; alternative: optional module) | C3 heavy (Microsoft.Garnet in every image); the in-process server shares memory and lifecycle with the gateway; useless for multi-replica cluster state. (It does run with password authentication and optional TLS, SEC H-01.) Use external Redis/Garnet. Keep `StackExchange.Redis` client as optional distributed cache/state | operators must run an external Redis-compatible server; `EnableEmbeddedServer` removed |
| Traffic shadowing | DELETE | C1 false, C5 none, replays user traffic (privacy risk) | none |
| OData (1,927 + 768) | MODULE | see 2.3 | install module |
| Arrow Flight SQL over HTTP | DELETE | misnamed, see 2.3 | switch to REST + Arrow IPC export |
| Arrow IPC export (ArrowExport) | KEEP (as REST format) | C1 via normal pipeline | none |
| Olap (3,342, DuckDbOlapEndpoints) and the DuckDB table cache | KEEP core (OD-15 extended) | DuckDB is a strategic core component (future central views and measures) | none |
| FinOps (278) | DELETE | C1 false, C5 none | none |
| Mesh (542) | DELETE | C1 false, C5 none | none |
| Analytics (4,464, AnalyticsEndpoints) | MODULE or DELETE per item | keep only audit/usage analytics fed by audit; rest DELETE. Phase 2 triages file by file | endpoints removed or moved |
| Webhooks (1,157) | MODULE | see 2.3 | install module; delivery options rebuilt only when consumed |
| CDC streaming (Streaming 4,440 + Cdc 2,296) | MODULE | see 2.3 | install module |
| Procedures (4,811) | MODULE (candidate) | C1 false unless it routes through the SQL gate; Phase 2 decides | n/a |
| Federation (1,101), subgraph masking | KEEP | C1 | none |
| SchemaRegistry (887), Docs (744), DevPortal | MODULE or DELETE (Phase 2 triage; default DELETE for DevPortal assets) | C1 false | n/a |
| Plugins/Extensibility (1,047) | KEEP as module host base | needed for MODULE mechanism | none |
| Lineage + LineageImpactAnalyzer (938 + 447) | MODULE | impact analysis, not enforcement | install module |
| Caching, State (distributed state) | KEEP, slim | C1 support; embedded server removed | external Redis optional |
| Dialects: SQL Server, PostgreSQL | KEEP | see 3.3 | none |
| SQLite as governance store (SqliteGovernanceRepository*, Sqlite migrations, connection provider, lint source, execution guards) | DELETE (OD-1) | binding owner decision; see REQ-SIMPL-12 | default provider changes to PostgreSQL; `Provider=Sqlite` fails closed at startup |
| SQLite as data-source dialect (SqliteDialectGenerator etc.) | OPEN (D14) | not part of OD-1 | unchanged until decided |
| Dialects: Oracle, Databricks | KEEP (OD-12) | see 3.3 | none |
| Dialect: Ansi (generator-only) | KEEP (OD-14): generator-only as today, not admitted as a data source (SqlDialectMapper, DialectAdmissionTests) | owner decision | none |
| Dialect: Snowflake generator | DELETE (OD-14) | no use outside generator unit tests | none (never admitted as a data source) |
| Cross-source federation (FederatedDuckDbExecutionService, CrossSourcePlanner, subtree compiler/partitioner, federated staging) and the DuckDB generator | KEEP core (OD-15) | cross-source joins (e.g. Databricks with SQL Server or a declarative HTTP source) are core functionality | none; DuckDB.NET stays a core dependency |
| Differential privacy | MODULE (default DELETE if no owner) | C1 adjacent (masking technique) but no evidence; keep only if an owner and tests exist | operators lose DP noise functions |
| Dev portal assets / DevEndpoints | DELETE from release builds | dev-only | none |

Feature docs map to the classification: DELETE items lose their docs; MODULE items move to the module package README.

### 3.3 Dialect scope (REQ-SIMPL-07 default taken)

Supported set = **PostgreSQL, SQL Server, Oracle and Databricks** as governed data-source dialects (OD-12; PostgreSQL + SQL Server remain the only governance-store engines per OD-1). **SQLite as a data source: removal is the default taken (D14/OD-2, REQ-SIMPL-13), executed last. Every supported dialect (including Oracle and Databricks) must have a parity baseline in CI against a real engine (Testcontainers for PostgreSQL/SQL Server/Oracle; Databricks via the strategy chosen in PLAN-SIMPL, never Pending). Snowflake, DuckDB-as-target and Ansi-as-target generators are removed (recommendation; Snowflake/Ansi open as D16). DuckDB stays only inside the Olap module and tests. OD-1 does not affect this section.

## 4. Requirements per Finding

Each requirement states scope, measurable target and acceptance criteria (AC). Targets are program exit values; stage assignment is in section 7.

### REQ-SIMPL-01 Product core and scope (finding 1)

1. Add a "Product core" section (section 2 above, condensed) to README and docs/architecture; README feature inventory (197 bullets) is replaced by a table of 3 transports, core capabilities and a link list of modules.
2. Apply section 3 classification: delete DELETE items, move MODULE items out of the host.
3. Targets: src LOC <= 180,000 (-40%) at program exit, <= 230,000 after Stage 2; HTTP endpoints (Map* calls) <= 140 (from 275); Application top-level folders <= 25 (from 49) with zero duplicate pairs; DI registrations <= 330; hosted services <= 20.
4. AC: classification table is checked in as `docs/plans/` tracker; every folder in src/Autheris.Application and src/Autheris.Extensions is either KEEP, in a module project, or deleted; architecture test fails on a new top-level Application folder not on the allow-list.
5. AC: the 4 "not implemented" feature docs and their README index lines are deleted (also covered by REQ-SIMPL-04).

### REQ-SIMPL-02 Process and sustainability (finding 2)

See section 5 for the full process requirements. Targets: net-added-LOC gate active; CODEOWNERS covers 100% of src projects/folders with at least 2 named humans or a single owner plus declared backup; plan-id comments in src: 0 dangling.

### REQ-SIMPL-03 Performance claims (finding 3; issue #511)

1. Remove all unmeasured numbers (64,500 req/s, 1.82 ms P99, 0 B, "Native AOT", Apollo/Hasura comparison) from README.md and docs/comparisons/performance-benchmarks.md; delete the comparison doc unless a protocol exists.
2. A number may appear in README/docs only if (a) a reproducible protocol is checked in under `benchmarks/` (hardware description, versions, command, dataset, warm-up, repetitions, percentile method), (b) a results file with commit SHA is committed or published as a CI artifact, (c) the doc cites both.
3. Edit the readme-copywriter skill so it never prescribes numbers (or delete the skill, see section 5.5).
4. Targets: unsupported performance claims = 0; benchmarks/load/results stays empty or is populated by a documented run.
5. AC: a CI check greps README/docs for `req/s`, `P99`, `ms` figures and requires a link to a results file; fix HotChocolate version statements by removing version numbers from marketing text.

### REQ-SIMPL-04 Documentation truth (findings 4 and part of 1; issues #512, #513)

1. Fix or delete docs that describe missing components: f-sec-04 (OpenFGA client) rewritten to the in-memory Zanzibar evaluator; Arrow Flight SQL docs deleted together with the endpoints (2.3).
2. Remove the 4 "Not implemented" docs and index lines; remove all `Status:` tags from docs/ outside docs/plans (37-39 "Done" tags, verified); no "100% GA / Production-Ready" wording.
3. Replace all 310 `file:///root/autheris/` links with relative links; fix 23 broken relative links.
4. Translate or delete docs/architecture/konzept-data-governance-und-mcp-documentation.md (English only; default: condense into the English architecture doc, delete the original).
5. Feature-doc count: <= 45 (from 96), one per surviving KEEP area and module; each module README replaces its feature doc.
6. CI: a link/path checker (relative links, referenced source paths in backticks exist), a "no Status tag in docs/" check, a non-ASCII-German heuristic is optional.
7. Protocol claims (JDBC, gRPC, wire formats) require an interop test in CI or are removed.
8. Targets: broken links = 0; `file:///` = 0; status tags in docs/ (outside plans) = 0; docs referencing non-existent source files = 0.

### REQ-SIMPL-05 Governance persistence (finding 5)

1. Reference ARCH-MNT REV-05 / ARCH-MNT-22; this PRD fixes the product decision: **one dialect-neutral governance repository** with a small SQL-fragment/dialect provider (identifier quoting, upsert, paging, boolean/time, JSON). The three per-dialect repository families go away.
2. Engine count (OD-1, binding): governance DB supports **PostgreSQL and SQL Server** only; SQLite removal is REQ-SIMPL-12 and is executed first (Stage 1), so unification starts from two families (~10,500 lines) instead of three. Dropping SQL Server too is not decided (D4; default: keep both).
3. Migrations: forward-only steps with v1.5.0 frozen base stay; the pg/sqlserver step trees may stay per engine only if generated from one neutral model; no migration rewrite of the frozen base.
4. Targets: governance repository LOC <= 5,500 after unification (from ~15,760 for three families; -65%; of which ~5,241 is removed by REQ-SIMPL-12 and ~5,000 by unification); Persistence folder <= 25,000 (from 37,605); one repository contract-test suite executed against both engines via Testcontainers instead of per-engine copies.
5. AC: duplicated-code ratio in the governance repository (ARCH-MNT metric) below 15%; an architecture test forbids per-dialect `*GovernanceRepository*` partials.

### REQ-SIMPL-12 Drop SQLite as governance store (OD-1, binding)

Scope: governance/metadata catalog store only. SQLite as a data source stays out of scope (D14).

1. Remove: `SqliteGovernanceRepository*` (26 partials, 5,241 lines), `Migrations/Steps/sqlite` (9 files, 55 lines), `SqliteBaselineSchema.cs` (838), `SqliteGovernanceMigrationRunner.cs` (123), `SqliteGovernanceConnectionProvider.cs` (257), `SqliteGovernanceLintSource.cs` (171), `SqliteExecutionGuard.cs` (81), `SqliteProviderExecutionGuard.cs` (22), SQLite audit repository/anchor store, the SQLite `_lock` serialization of audit writes, and SQLite-only branches in startup validation, health checks and connection TLS policy. Verified governance-footprint total: about 6,790 lines (measured 2026-11-05), excluding items below that need triage.
2. Triage (Phase 2): `Migrations/Shape/SqliteSchemaCatalogReader.cs` (71), `Persistence/Catalog/SqliteColumnMetadataReader.cs` (33), `SqliteTypeAffinity.cs` (30) are removed only if they serve the governance store and not SQLite-as-data-source. Not touched by this requirement: `SqliteDialectGenerator` (433), `Ast/Security/SqliteTypeAffinity` (40), `SqliteZoneOffsetTable` (135).
3. Tests: remove governance-store tests that use SQLite (46 test files mention sqlite, 6,733 lines, mixed use; Phase 2 splits governance-store vs data-source use). Governance repository, migration and audit tests run against PostgreSQL via Testcontainers (and SQL Server in the contract suite). Tests that use SQLite as a data source stay until D14 is decided.
4. New default (PostgreSQL) must replace SQLite in every dependent: `src/Autheris.Api/appsettings.json:39`, `appsettings.Development.json:42`, `deploy/helm/autheris/values.yaml` and `templates/governance-migration-job.yaml`, `deploy/podman-compose.yaml`, `deploy/README.md`, `deploy/containers/gqlgateway-api/{Containerfile,appsettings.Benchmark.json}`, `deploy/containers/governance-seed/{Containerfile,seed_governance.py}`, `deploy/containers/load-generator/k6-benchmark.js`, `deploy/run-benchmark.{sh,ps1}`, `deploy/scripts/generate_report.py`, README/quickstart, and the 43 docs outside docs/plans that mention SQLite. The quickstart/compose flow must start a PostgreSQL container and still run in one command.
5. Fail closed: with `Provider=Sqlite` (or a SQLite connection string) for governance, startup fails with a clear error naming OD-1 and the migration guide; no silent fallback. The UnknownGatewayKeyGuard/LegacySwitchGuard pattern is reused.
6. Migration/operator impact: forward-only migrations with the v1.5.0 frozen base are untouched for PostgreSQL and SQL Server. Existing SQLite governance deployments (none in production) are unsupported after the change. No data migration tool (default taken); the migration guide documents export through the admin/governance API and re-import for anyone who needs it. Communicated via CHANGELOG ("Removed"), release notes (BREAKING) and the migration guide (section 6).
7. Targets: governance persistence dialects 3 -> 2; about 6,790 governance-store src lines removed in Stage 1 (about 2.2% of src, 43% of the governance persistence duplicate), plus the removed governance-store tests; governance default provider = PostgreSQL in 100% of shipped configs; references to SQLite as a governance provider in docs = 0.
8. AC: an architecture test fails when a `Sqlite*Governance*` type or `Steps/sqlite` folder exists; startup test asserts the fail-closed error; compose/Helm quickstart passes a smoke test with PostgreSQL.

### REQ-SIMPL-06 Configuration surface (finding 6; issue #514)

1. Delete every option in unconsumed-options.txt (46 lines) and the code/docs referencing it, instead of baselining. Includes WebhooksOptions delivery block, QueryCostOptions.BaseQueryCost / CostPerRowReturned / NestedDepthFactor, RebacOptions.OpenFgaApiUrl / OpenFgaStoreId.
2. The silently ignored security flags in ARCH-MNT 2.4 (CasbinOptions.EnforceInQueryPipeline, ScimOptions.AllowedIpRanges, McpOptions.RequirePiiMasking) are either implemented and tested (fail closed) or deleted before the owning feature is classified; default: Casbin deleted; SCIM and MCP flags implemented or removed in the same PR.
3. Generate a configuration reference (all options, type, default, description, source file) from the Options classes as a CI artifact and as `docs/reference/configuration.md`; CI fails when an option has no description.
4. Targets: unconsumed baseline = 0 and the baseline file removed; `*Options` classes <= 90 (from 173); settable properties <= 600 (from ~1,173); `*Enabled` flags <= 40 (from 96); undocumented property names = 0 (from 335); an architecture test (existing ratchet) fails on any new unconsumed option with no baseline escape.
5. AC: UnknownGatewayKeyGuard, LegacySwitchGuard and IValidateOptions stay (positive findings).

### REQ-SIMPL-07 SQL engine scope (finding 7)

1. Dialect scope per 3.3; remove unsupported generators and their tests; `TargetSqlDialect` shrinks to the supported set.
2. Write an ADR (supersedes the open part of ADR-017) with an honest build-vs-buy evaluation (e.g. ANTLR/Trino grammar reuse, existing SQL parsers, SQL rewriting proxies). Outcome may confirm the compiler; the evaluation must be documented with criteria (security verifiability, dialect coverage, maintenance cost).
3. Parity suite becomes engine-real: PostgreSQL and SQL Server via Testcontainers, SQLite only as a data-source parity engine if D14 keeps it; Federation/DuckDB staging paths get parity and integration coverage in core (OD-15). Close the master-plan open baselines for SQL Server/PostgreSQL/DuckDB (DuckDB becomes N/A for core).
4. Targets: supported data-source dialects = 4 (PostgreSQL, SQL Server, Oracle, Databricks; SQLite only per D14 staging), each 100% parity-tested with a baseline gate; Oracle and Databricks generators, executor and token provider are not deleted (OD-12); TrinoSqlEngine + tests <= 45,000 lines combined (from 59,390); Stryker configs <= 2.
5. AC: CI fails if a dialect generator exists without a parity baseline file.

### REQ-SIMPL-08 Test suite (finding 8)

1. Re-balance the pyramid: security-critical behaviors (consent, RLS, masking, audit) are tested state-based against real engines (Testcontainers) rather than with Substitute.For.
2. Test mix targets: container-backed/integration tests >= 15% of [Fact]/[Theory] (from 906/14,333 = 6.3%); tests labeled "Unit" that use real DBs or hosts are moved to Integration (76 SqliteConnection/DuckDB files, 11 TestServer files); Substitute.For <= 2,000 (from 4,159) and Received/DidNotReceive <= 400 (from 986).
3. Organize by domain: root of Autheris.Tests.Unit = 0 loose test files (from 155); review/finding-named files (SecurityFindingsRemediationTests, BasicAuthDxTests, ...Ext15Tests) folded into domain test classes; no file > 800 lines.
4. Sleeps: Task.Delay/Thread.Sleep in tests <= 20 (from 92), replaced by fake time or awaitable signals; the sleep baseline file is removed.
5. Test LOC follows deletions: test LOC <= 240,000 at exit (from 382,125) and test/src ratio <= 1.4.
6. CI time targets: PR pipeline (build + unit + integration subset) <= 15 min; full pipeline <= 30 min; measured and published per PR by the workflow (baseline to be measured at Stage 0, see D7).
7. AC: deleting a feature never leaves orphaned tests; a test-naming architecture test enforces domain folders.

### REQ-SIMPL-09 Footprint and modularity (finding 9; issue #509)

1. Remove `PublishReadyToRun=true` (issue #509); enforce the R2R spike NO-GO: add a CI assertion that the Api csproj contains no PublishReadyToRun.
2. Core image excludes: embedded Garnet (if D17 confirms deletion or a module), Parquet.Net (if it is not needed by core federation), Casbin.NET and any module dependency (module packages carry their own).
3. Merge duplicate folders (Audit/Auditing, Catalog/DataCatalog) and remove duplicated Dbt/OpenMetadata copies between Application and Extensions.
4. Targets (measured by the checked-in protocol in REQ-SIMPL-03): core image <= 250 MB uncompressed (baseline 373 MB non-R2R; 441 MB as configured); cold start P50 <= 3.0 s (baseline 4.52 s non-R2R, 5.58 s as configured); warm P99 not worse than 3.47 ms; Api project package references <= 35% of today's count in the core flavor; DI registrations <= 330; hosted services <= 20.
5. AC: image-size and cold-start measurements are produced by a CI job and compared against the budget (fail at +10%).

### REQ-SIMPL-10 Versioning and releases (finding 10; issue #510)

1. Single source of truth: version derived from the git tag (assembly, NuGet, container image, Helm Chart `version` and `appVersion`) at release time; Directory.Build.props holds no hard-coded product version (e.g. MinVer or equivalent).
2. Helm default install must pull the chart's matching app version; `values.yaml` tag empty falls back to a correct appVersion set at packaging.
3. CHANGELOG.md (Keep a Changelog format) created, starting with a backfilled summary of v1.0.0..v1.7.0 grouped by minor version, then one entry per release; generated from conventional-commit/PR labels where possible.
4. Release cadence: at most one tagged release per week plus security patch releases; 56 tags in 4 weeks is not repeated.
5. Document the upgrade support window: oldest supported migration base v1.5.0 (frozen); v1.0-v1.4 are unsupported and marked as such in the CHANGELOG and runbook.
6. Targets: tags/versions inconsistent = 0 (a release-validation CI step asserts tag == assembly == chart appVersion == image tag); CHANGELOG entries for 100% of releases from the program start.

### REQ-SIMPL-13 Full SQLite removal evaluation (OD-2)

Evidence (verified 2026-11-05): Microsoft.Data.Sqlite is referenced by Infrastructure and by SqlCompilerGate, Simulation, TrinoSqlEngine.Tests, Sim.ApiHost; 26 src files use SqliteConnection (governance repo included; outside it: Persistence 8, Migrations 3, TrinoSqlEngine Emit 1, Application/Sql 2, Api middleware 1, startup checks/DI 2); 28 src lines reference `TargetSqlDialect.Sqlite`/Sqlite provider; 135+40+433 lines of data-source SQLite code (ZoneOffsetTable, TypeAffinity, SqliteDialectGenerator).

| Area | SQLite dependence today | Replacement | Effect | Recommendation |
|---|---|---|---|---|
| (a) Governed data source | SqliteDialectGenerator 433, TypeAffinity 40, ZoneOffsetTable 135, 28 dialect/provider lines, Emit/Application/Sql/middleware uses | PostgreSQL (and SQL Server) only; PostgreSQL also covers the "tiny local source" use case | about 1,000 src lines saved incl. the 8 non-repo files; Microsoft.Data.Sqlite leaves Infrastructure; dialect enum shrinks | Remove (Stage 3 of this topic, last) |
| (b) Parity suite | 33 of the parity files mention Sqlite (PostgreSql 4, SqlServer 6, DuckDb 1); job `parity-sqlite` in parity-nightly.yml; SQL Server cells Pending | PostgreSQL via Testcontainers as the reference engine (fixtures exist: ParityPostgresHostFixture, ParitySqlServerHostFixture); DuckDB only inside Olap module; SQL Server enabled instead of Pending | parity becomes container-backed, nightly job slower (container start ~10-20 s once per run; measure in Stage 0); closes the open master-plan baselines | Migrate parity to PostgreSQL first, then remove SQLite cells; no dialect without parity baseline |
| (c) Quickstart/demo/compose | Helm values, governance-migration-job, podman-compose, benchmark containers, governance-seed, k6 benchmark, run-benchmark scripts (list in REQ-SIMPL-12.4); dev appsettings | PostgreSQL container in compose and Helm; one-command quickstart retained | loses the zero-dependency start (needs a container runtime); mitigated by a documented one-command compose | Replace (done in REQ-SIMPL-12 for governance; data-source demos in this step) |
| (d) Unit/integration tests using real SQLite | Unit 83 files, Integration 25, TrinoSqlEngine.Tests 16, Parity 7, Simulation 4, Sim.ApiHost 2, SqlCompilerGate 1, Architecture 1 (139 files; 76 flagged "Unit" in REQ-SIMPL-08) | (i) in-memory fakes/stores where DB semantics do not matter (default for unit tests); (ii) PostgreSQL Testcontainers with a shared fixture (one container per test assembly, per-test schema/transaction) for DB-semantics tests; (iii) DuckDB stays where already used (9 files, Olap) | unit tests get faster (no file DB); container-backed tests get slower per test but fewer; net CI effect measured in Stage 0 | Convert in the order of REQ-SIMPL-08 |

Coordination: CI speed and structure work runs in parallel as issues #516-#522. This PRD owns only the requirement that container fixtures are shared per assembly and that the measured PR/full CI budget (REQ-SIMPL-08.6) is not exceeded; pipeline design, caching and job splitting stay in #516-#522 (no duplication).

Savings estimate: src about 7,800 lines (governance store ~6,790 per REQ-SIMPL-12 + data-source ~1,000), Microsoft.Data.Sqlite dependency in Infrastructure and 4 test projects, `parity-sqlite` job, plus test files that become fakes or containers. Risks: loss of zero-dependency quickstart; slower DB-semantics tests; parity coverage gap during transition (mitigation: PostgreSQL parity green before any SQLite parity cell is deleted); SQL Server parity still Pending (must be enabled or the dialect is dropped, never left Pending).

Requirement: full removal is staged; step 1 = governance store (REQ-SIMPL-12, binding OD-1), step 2 = tests and parity move to PostgreSQL Testcontainers/fakes, step 3 = remove the data-source dialect and the package. Targets: SQLite references in src = 0 and `Microsoft.Data.Sqlite` references = 0 at exit; real-SQLite test files = 0; parity engines = PostgreSQL + SQL Server, both non-Pending. AC: an architecture test fails on any `Microsoft.Data.Sqlite` reference.

## 5. Process Requirements (finding 2)

### 5.1 Feature freeze (REQ-SIMPL-02a)

- Scope: no new features, endpoints, options, integrations or transports on `main`. Allowed: security fixes, bug fixes, deletions, refactors that reduce LOC, tests for existing behavior, documentation correction, release/CI work for this program.
- Duration: from acceptance of this PRD until Stage 3 exit (section 7), minimum 6 weeks (default taken).
- Exit criteria: all of REQ-SIMPL-01, 03, 04, 06, 09, 10 targets met; classification table fully executed (DELETE done, MODULE extracted or deferred with owner); core README accepted by the owner.
- Exceptions: security fixes (GHSA/CVE, fail-closed regressions) need no approval; any other exception requires an owner decision recorded in the PR description with the issue link.

### 5.2 Human ownership (REQ-SIMPL-02b)

- `.github/CODEOWNERS` (exists today) is rewritten to a subsystem map: Consent, RLS/VirtualFilters, Masking, Audit, Policy/RBAC/ReBAC, Identity/Tenancy, SQL compiler/gate, Catalog/Classification, Governance persistence, GraphQL, MCP, Api/REST, Infrastructure/ops/CI, Docs, each module package.
- Rule: every subsystem has one primary human owner and one named backup (the same person may hold several subsystems until staffing exists; backups must be declared as "vacant" explicitly, never omitted). Branch protection requires CODEOWNERS review for the paths.
- AI agents may author changes; the human owner approves. Co-authored-by lines stay; the commit identity should be a single configured author (consolidate the four identities via .mailmap).
- AC: a CI check lists src/ top-level folders without a CODEOWNERS entry; result = 0.

### 5.3 PR size limit (REQ-SIMPL-02c)

- Ceiling: net added lines in `src/` <= 400 per PR (additions minus deletions, generated files and migrations excluded); tests do not count. Deletion-only and refactor PRs are unlimited as long as net is negative.
- Escape hatch: label `size-exception` plus a PR-description paragraph stating why it cannot be split and linking an owner approval; maximum two exceptions per week.
- Commit volume: no target on commit count, but a PR must be one coherent change (no "fix review findings batch N" accumulations over unrelated areas).

### 5.4 Plan-id comments in code (REQ-SIMPL-02d)

- Policy: no plan/ticket IDs (e.g. REV-05, SEC-xx, AST-X1) in code comments. Comments explain why; traceability lives in commit messages and PR descriptions. A comment may link a GitHub issue only (`#123`) when it documents a known limitation.
- Cleanup: remove the ~1,860 plan-id comment lines (>= 521 ids) in a mechanical, review-by-script PR per project, after deletions (Stage 3) so that deleted code is not cleaned twice.
- Guard: an architecture/analyzer test fails on a regex for plan ids in `src/` comments (the existing baseline-ratchet pattern, but baseline starts at the cleaned state, i.e. zero).
- Docs: plan files already deleted must not be referenced anywhere (checked by the REQ-SIMPL-04 path checker).

### 5.5 Agent and skill definitions (REQ-SIMPL-02e)

Decision (default taken): delete `.agents/agents/conversion-copywriter.md`, `qa-cro-reviewer.md`, `frontend-developer.md`, `ui-ux-architect.md` (no frontend, no conversion funnel in the product). Keep `product-manager`, `csharp-tdd-developer`, `project-manager`, `orchestrator-lead`. `readme-copywriter` agent and skill: delete (README is written from verified facts per REQ-SIMPL-03; the skill currently prescribes unverified numbers). Keep the remaining `.agents/skills` (csharp-architect, csharp-code-reviewer, csharp-performance-engineer, csharp-security-expert, csharp-tdd-developer, product-manager, project-manager). Mirrored copies under `.claude/` are removed in the same change.

### 5.6 Benchmark claims

Covered by REQ-SIMPL-03: a claim exists only with a checked-in reproducible protocol and a results artifact.

## 6. Communication and Deprecation (REQ-SIMPL-11)

1. Deprecation policy: Autheris has no production users, so removals are immediate (no deprecation window, no compatibility shims). The policy for the future is stated in the README: after the first declared production release, a feature is deprecated for >= 2 minor releases with a runtime warning and a CHANGELOG notice before removal.
2. CHANGELOG.md (REQ-SIMPL-10) carries a "Removed" and "Moved to modules" section per release of the program, each entry naming the feature, the replacement and the issue/PR.
3. Migration guide `docs/operations/migration-simplification.md` (system reference, no status text): one row per DELETE/MODULE item: what was removed, config keys that disappear (fail fast with the UnknownGatewayKeyGuard message pointing to the guide), the replacement, and how to install the module.
4. Fail-fast: a removed option or integration key in the configuration makes startup fail with a clear message (consistent with fail-closed), not silent ignore.
5. README rewrite: positioning sentence, core capabilities, three transports, modules list with install instructions, honest status ("not yet production-hardened"), no benchmark numbers without a link to a protocol, no emojis; the 197-bullet inventory is deleted.
6. Release notes per tag generated from CHANGELOG; the simplification release is announced as a breaking release (next minor with a clear "BREAKING" header; no major bump required while not in production, default taken).
7. Issues: #509-#515 are the tracking issues; each PR links its issue; #515 gets one child issue per stage; each module extraction gets an issue with an owner. The GitHub issue for each deleted integration records the reason and the restoration path (git tag of the last version containing it).
8. Before deletion, tag `pre-simplification` on the base commit so deleted code remains recoverable.

## 7. Sequencing at Product Level

### Stage 0 - Quick wins (issues #509-#514; independent, parallel lanes, small PRs)

| Issue | Content | Req |
|---|---|---|
| #509 | Remove ReadyToRun; budget assertion | 09.1 |
| #510 | Version from tag; Helm fix; CHANGELOG | 10 |
| #511 | Remove unverified performance claims | 03 |
| #512 | Fix docs describing missing components; delete 4 "not implemented" docs | 04.1-04.2 |
| #513 | Link, status tag, language hygiene + CI checkers | 04.3-04.8 |
| #514 | Delete unconsumed options; generated config reference | 06 |

Also in Stage 0 (no code deletion yet): tag `pre-simplification`; measure CI time baseline; feature freeze starts; CODEOWNERS rewrite; agent definition cleanup (5.5).
Exit: all Stage 0 ACs met; freeze active.

### Stage 1 - Delete (issue #515, stage 1)

First: REQ-SIMPL-12 (SQLite governance store, new PostgreSQL defaults, Testcontainers governance tests). Then delete the DELETE items of 3.2 (Purview, Collibra, Alation, DataHub, Backstage, Itsm, Casbin, embedded Garnet (per D17), traffic shadowing, Arrow Flight SQL endpoints, FinOps, Mesh, dev portal assets), unsupported dialect generators, their options, docs and tests. Merge duplicate folders. Exit: src LOC <= 250,000; endpoints <= 200; Application folders <= 35.

### Stage 2 - Structure

Governance persistence unification (05; ARCH-MNT-22); SQL dialect parity on real engines and ADR (07); test re-organization and mock reduction (08). Exit: src LOC <= 230,000; governance repository LOC <= 7,000; 3 dialects fully parity-tested; root of Unit tests clean.

### Stage 3 - Modules and polish

Extract MODULE items (OpenMetadata, dbt, Lakehouse, OData (pilot), CDC/Webhooks, SCIM, TOTP, Negotiate, Envoy, Lineage) behind `Autheris.Modules.Abstractions`; core image budget (09); plan-id comment cleanup (5.4); README rewrite and migration guide (06); benchmark protocol and published numbers (03). Exit: all program targets in the table below met; feature freeze lifted.

Stage order rationale: truth first (cheap, restores credibility), delete before refactor (do not refactor code that will be removed), structure before modules (modules need stable abstractions).

## 8. Target Summary (program exit)

| Metric | Baseline (verified 2026-11-05) | Target |
|---|---|---|
| src LOC | 302,461 | <= 180,000 |
| HTTP endpoints | 275 | <= 140 |
| Application top-level folders | 49 | <= 25, 0 duplicates |
| Core image / cold start P50 | 373 MB (441 MB as configured) / 4.52 s (5.58 s) | <= 250 MB / <= 3.0 s |
| Options classes / settable props | 173 / ~1,173 | <= 90 / <= 600 |
| Unconsumed options baseline | 46 lines | 0, file removed |
| Undocumented option names | 335 of 937 | 0, generated reference |
| Feature docs | 96 | <= 45; 0 broken links, 0 status tags, 0 `file:///` |
| Container-backed + integration share | 6.3% | >= 15% |
| Substitute.For / Received | 4,159 / 986 | <= 2,000 / <= 400 |
| Test LOC | 382,125 | <= 240,000 |
| CI time (PR / full) | to be measured at Stage 0 | <= 15 / <= 30 min |
| Governance repository LOC | ~15,760 (3 families) | <= 5,500 |
| Governance persistence dialects (OD-1) | 3 (SQL Server, PostgreSQL, SQLite) | 2; default provider PostgreSQL; `Provider=Sqlite` fails closed |
| Supported data-source dialects (all parity-tested) | 8 enum values / 6 generators, parity SQLite-centric | 4 (PostgreSQL, SQL Server, Oracle, Databricks; SQLite removed per D14 default), each 100% parity, none Pending |
| Release consistency | 56 tags; version 1.2.1 vs chart 1.0.0; no CHANGELOG | tag == assembly == chart == image; CHANGELOG complete |
| Unsupported perf claims | 3 headline numbers | 0 |
| Plan-id comments in src | ~1,860 lines | 0 |
| Hosted services / DI registrations | 39 / 549 | <= 20 / <= 330 |

Image size, src LOC and test LOC targets above predate OD-12: keeping Oracle (Oracle.ManagedDataAccess.Core stays in core) and Databricks raises them qualitatively; the architect recomputes the numbers, see PLAN-SIMPL section 3.

Targets are proposals sized from the deletion plan; the Phase 2 architect may revise each by up to 20% with justification, but never weaken the unconsumed-options, broken-links, status-tag and release-consistency targets (all zero).

## 9. Non-Goals

1. No new features, transports or integrations during the freeze.
2. No Flight SQL gRPC server, no PG wire protocol (REQ-PG-WIRE-01 rejected, OD-13), no JDBC/ODBC drivers.
3. No rewrite of the SQL compiler; only scope reduction and an ADR.
4. No rewrite or renumbering of the frozen v1.5.0 migration base; no support for upgrades from versions older than v1.5.0.
5. No frontend/UI work; no marketing copy.
6. No change to the fail-closed security semantics of KEEP features; simplification must not weaken any security test.
7. No hosted SaaS or multi-product split.

## 10. Risks

| # | Risk | Mitigation |
|---|---|---|
| 1 | Deleting features removes hidden dependents (tests, DI, startup checks) | Architecture tests; delete in small PRs; `pre-simplification` tag; run full suite per PR |
| 2 | Module loading adds a new attack surface (untrusted assemblies) | Signed manifest allow-list, fail closed, Phase 3 review before Stage 3 |
| 3 | Coverage/security regressions during test re-balance | Do not delete security tests; mutation score (Stryker) must not drop on KEEP code |
| 4 | Unified governance repository introduces SQL regressions | Contract tests on all engines first (Stage 2 starts with tests, then merge) |
| 5 | Bus factor stays 1 even with CODEOWNERS | Declare vacant backups; owner decision D6 on staffing |
| 6 | Owner disagrees with classification of a feature | Classification is a table with criteria; items can move class by owner decision before Stage 1 starts |
| 7 | Freeze blocks urgent work | Security exception; owner exception path |
| 8 | Cold-start/size targets not reachable after R2R removal and module extraction (worsened by keeping Oracle driver in core, OD-12) | Measure at Stage 0/1; targets revised only with measured data (up to 20%); see PLAN-SIMPL section 3 |
| 9 | Oracle and Databricks parity needs real engines/workspace in CI (cost, licensing, secrets, flakiness) | Parity strategy per dialect decided in PLAN-SIMPL; no Pending cells; nightly job acceptable if PR gate uses contract fixtures |

## 11. Open Owner Decisions (each with recommended default)

| ID | Decision | Recommended default |
|---|---|---|
| D1 | Core transports | REST + GraphQL + MCP; others MODULE/DELETE (default taken) |
| D2 | Classification of borderline areas (Analytics, Procedures, SchemaRegistry, Docs, Differential privacy, Envoy, TOTP, Lineage) | As in 3.2; items without a named module owner by Stage 1 are DELETED (default taken) |
| D3 | Module mechanism | Runtime-loaded signed assemblies; fallback: compile-time optional packages per image flavor (default taken; Phase 2 confirms) |
| D4 | Governance DB engines beyond OD-1 | Keep PostgreSQL and SQL Server behind one repository; revisit dropping SQL Server after Stage 2 |
| D5 | Supported data-source dialects | **Decided (OD-12):** PostgreSQL, SQL Server, Oracle, Databricks; SQLite per D14 staging. Overrides the earlier "PostgreSQL, SQL Server only" default and conflicting parts of ADR-019 |
| D16 | Snowflake and Ansi generators | **Decided (OD-14):** Snowflake deleted, Ansi kept as generator-only |
| D17 | Embedded Garnet server: delete, keep, or optional module? | **Open (owner).** Recommendation: delete (operators run an external Redis-compatible server); alternative: optional module. Not because of authentication (password auth, SEC H-01) but image weight, in-process memory/lifecycle coupling and no value for multi-replica state. |
| D14 (OD-2) | SQLite as governed DATA SOURCE, parity engine and test engine (full removal; separate from OD-1) | Full removal, staged (governance store, then tests/parity to PostgreSQL Testcontainers and fakes, then dialect and package); default taken; owner may stop after any step |
| D15 | Data migration tool for SQLite governance stores | None (default taken); document API export in the migration guide |
| D6 | Staffing for ownership | One primary owner with declared "vacant" backups; recruit or assign a second human before Stage 3 |
| D7 | CI-time targets | **Decided (OD-6):** PR wall-clock from push to last required check, median, < 30 min, queue time included |
| D8 | Feature freeze duration | >= 6 weeks, until Stage 3 exit (default taken) |
| D9 | PR net-LOC ceiling | 400 net added src lines with `size-exception` escape hatch (default taken) |
| D10 | Agent definitions | Delete the five without product match plus readme-copywriter skill; keep the rest (default taken) |
| D11 | Version bump for the simplification release | Next minor with BREAKING header, no major while unreleased to production (default taken) |
| D12 | Delete vs keep Casbin, traffic shadowing, Backstage, Itsm | Delete (default taken; Itsm confirmed by OD-5; reversible via the `pre-simplification` tag). Embedded Garnet moved to D17 (open). |
| D13 | Master plan link | The PM adds the link to 00-master-plan-overview.md after owner acceptance (not edited by this PRD) |
