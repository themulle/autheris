# Plan: Scale to 5,000 Users (Governance + Performance), Phase 2 and Phase 3

**Document ID:** `PLAN-SCALE-5000`
**Date:** 2026-10-16
**Status:** PROPOSED. Wave 1 is ready for Phase 4. Wave 2 is partly blocked on the owner (section 5).
**Author:** Solution Architect (Phase 2), Security Expert (Phase 3, section 7)
**Branch:** `feat/scale-5000-users`
**Parent Plan:** [00-master-plan-overview.md](00-master-plan-overview.md)
**Inputs:**
- [2026-10-16-analysis-scale-5000-users-governance.md](2026-10-16-analysis-scale-5000-users-governance.md) (findings B1–B4, H1–H6, M1–M5, open-source §6)
- [2026-10-16-analysis-scale-5000-users-performance.md](2026-10-16-analysis-scale-5000-users-performance.md) (findings B-1..B-3, H-1..H-6, M-1..M-4, open-source §8)

---

## 0. Binding Rules for Every Package

| Rule | Consequence for this plan |
| :--- | :--- |
| **No upgrade or repair migrations** (owner decision 2026-10-15) | Every schema change extends the `0001` baseline **in place** on all three engines (`SqliteBaselineSchema.cs`, `PostgreSqlBaselineSchema.cs`, `SqlServerBaselineSchema.cs`) and updates the shape snapshots `Migrations/Shape/governance-shape.{sqlite,postgresql,sqlserver}.json`. `GovernanceSchemaShapeSnapshotTests` must stay green. The migration checksum stays unchanged. Existing databases are reported by the shape verifier, and the caller (Talos) supplies the newest schema. |
| **Fail closed** | Every new failure path (Redis, directory, attribute source, compile error, store outage) denies, or treats a cached decision as stale. It never allows. |
| **Do not touch** `src/TrinoSqlEngine/Ast/Visitors/AstSecurityVisitor.cs` | No package needs to change it. New row predicates enter only through `RowPolicyFactory` / typed `IRowPolicy` inputs. A reviewer rejects any diff to that file. |
| **6-phase lifecycle** | Phase 4 = Sonnet TDD developers (never Opus), strict red-green-refactor. Phase 5 review. Phase 6 feature docs in `docs/features/`. |
| **Fast CI** | Unit tests in `tests/Autheris.Tests.Unit`. Engine-specific checks extend the existing Testcontainers contract suites in `tests/Autheris.Tests.Integration` (shared DB servers, WP-TS-01). New Redis Cluster and load tests are tagged `[Trait("Category","Scale")]` and run outside the per-PR gate. |
| **Licences** | Only MIT, Apache-2.0 or BSD dependencies, pinned in `packages.lock.json`. No GPL, AGPL or third-party BSL code. |

---

## 1. Merge Hotspots with Parallel Branches

| Branch | Overlap | Rule |
| :--- | :--- | :--- |
| `feat/issue-99-cdc-fanout` | All three baseline schemas and all three shape JSON files (new CDC stream tables). `AuditEventTypes.cs`. Streaming options and DI (`GatewayServiceCollectionExtensions.cs`). | `WP-PF-02` (CDC subscription counter) and `WP-PF-15` (CDC per-shape decisions) start **after** issue-99 merges, or rebase onto it. Baseline and shape edits in this plan are owned by **Lane A only**. After a rebase, regenerate the shape JSON from a fresh database instead of hand-merging it. |
| `feat/issue-107-rowfilter-keyset-cache` | Baselines and shapes (`CONSENT_ROW_FILTERS` key-set columns, `TABLE_ROW_FILTER_STRATEGIES`). `ConsentModels.cs` (`ConsentRowFilter`), `IGovernanceRepository.cs`, `RowPolicyFactory`/policy keys, `RlsOptions.cs`, `DatabaseOptions.cs`, epoch-related key-set caching. | `WP-SC-01` adds only indexes (no column changes to `CONSENT_ROW_FILTERS`). `WP-SC-04` must not add fields to `ConsentRowFilter` (it reuses `ValueSource`/`UserAttribute`) and lands **after** issue-107. `WP-PF-01`/`-07`/`-09` touch `EpochValidationService` and consent cache keys: coordinate with 107's epoch usage and rebase before Phase 5. |
| `feat/bl-06-open-telemetry` | DI composition (`Api/Extensions/DependencyInjection/*`), logging, metrics (meters, counters). | New counters (cache hit/miss, audit batch size, directory latency) are added through the **existing** meter or Prometheus helpers, without new DI registration styles. DI edits stay minimal, one line per service. If bl-06 merges first, switch to its meter abstraction during rebase. |

Shared-file ownership inside this plan: **Lane A** owns all `Migrations/*` and `Shape/*` files and `*GovernanceRepository.Consent.cs`/`.AccessProfiles.cs`. No other lane edits them. A lane that needs a schema change files it with Lane A.

---

## 2. Work Package Overview (ordered)

**Wave 1: quick wins, no owner input needed** (target: about 2 weeks with 3 parallel developers)

| Order | WP | Source finding | Title | Effort | Lane |
| ---: | :--- | :--- | :--- | :---: | :---: |
| 1 | `WP-SC-01` | Gov B4 + Perf H-2.1 | Consent indexes, sargable table key, subject/table filter in SQL (indexed consent query) | S–M | A |
| 2 | `WP-PF-01` | Perf B-2 | Epoch reads safe on Redis Cluster (hash tags or per-slot batching) | S | B |
| 3 | `WP-SC-02` | Gov H4 (bug) | Manifest: role-grantee idempotency + transactional apply | S | C |
| 4 | `WP-PF-03` | Perf H-1.1/1.2 | Compiled `sub_rule` delegates + precompiled object patterns on the policy snapshot | S | B |
| 5 | `WP-PF-04` | Perf B-1.1–1.3, 1.5 | Batched audit insert, async tail/anchor, wire `AuditGroupCommitSink`, one event per request | S–M | C |
| 6 | `WP-SC-03` | Gov B3 | Persisted access profiles on PostgreSQL/SQL Server, group/role matching, deterministic precedence | M | A |
| 7 | `WP-PF-06` | Perf H-3 | Bounded caches + correct `Size` units | S | B |
| 8 | `WP-PF-02` | Perf B-3 | CDC subscription counter per subject + per-tenant cap (after issue-99) | S | C |
| 9 | `WP-PF-05` | Perf H-2.2 | Pipelined L2 consent reads (no per-table loop) | S | B |
| 10 | `WP-PF-07` | Perf M-3 | Staleness budget for non-sensitive tables, batched epochs, fused rate-limit/quota Lua | S | B |
| 11 | `WP-PF-08` | Perf H-6 | Basic-auth: cluster-shared verified marker, longer success cache, IP limiter after auth, PATs | S–M | C |

**Wave 2: structural** (some packages are blocked on the owner, see section 5)

| Order | WP | Source | Title | Effort | Lane | Blocked on owner? |
| ---: | :--- | :--- | :--- | :---: | :---: | :--- |
| 12 | `WP-PF-09` (absorbs `WP-SC-11`) | Perf H-1.3–1.5, H-2.3; Gov M1 | Decision keys by policy fingerprint, sharded LRU (BitFaster), single-flight, per-tenant invalidation, table consent snapshot | M | B | no |
| 13 | `WP-PF-10` | Perf H-5 | `CatalogSnapshot` + incremental audit integrity monitor under lease | M | C | no |
| 14 | `WP-SC-04` | Gov B2 | `USER_ATTRIBUTE` row filters as bound parameters; claims provider first | M | B | **partly**: Q3 (authoritative attributes) for non-claim sources |
| 15 | `WP-SC-05` | Gov H2 | `IDirectoryService` (Graph/LDAP), overage, grantee validation, principal search | M | C | **yes**: Q1 (IdP) |
| 16 | `WP-SC-06` | Gov B1, M5 | Access Policy aggregate (selectors by pattern and tag), resolver, policy-set epoch | L | A + B | **partly**: Q4 (per-user standing grants) decides the exception policy, not the core |
| 17 | `WP-SC-07` | Gov H1, H6 | Subject lifecycle (`revoke-all`, transfer), SCIM receiver or delta sync, group owners with inheritance | M | C | **yes**: Q2 (SCIM vs pull); `revoke-all` API is not blocked |
| 18 | `WP-SC-08` | Gov H3, M2 | Recertification campaigns, bounded expiry query, typed validity timestamps, history archival | M | A | **partly**: Q5 (cadence and auto-revoke defaults; build configurable) |
| 19 | `WP-SC-09` | Gov H4 | Manifest v2 (selectors, plan/diff, prune by `ManagedBy`) | M | C | after `WP-SC-06` |
| 20 | `WP-SC-10` | Gov H5 | Paged admin query API, entitlement report, audit cursor | M | A | expansion of groups to users needs `WP-SC-05` |
| 21 | `WP-PF-11` | Perf H-4 | Indexed ReBAC store (or OpenFGA adapter) | M | B | decision: is ReBAC primary? (Q6) |
| 22 | `WP-PF-12` | Perf M-1 | Async parse, Stage-1 cache, array IN-lists, template cache size and fingerprint | M | B | no (must not touch `AstSecurityVisitor.cs`) |
| 23 | `WP-PF-15` | Perf M-4 | CDC per-shape decisions, async-job shared result storage, Redis connect initializer | M | C | after issue-99 |
| 24 | `WP-SC-12` | Gov M3, M4 | Restrictive ABAC rules in the governance store; remove or adopt `ROLES`/`ROLE_MEMBERS` | M | A | after `WP-SC-06` |
| 25 | `WP-PF-13` | Perf B-1.4 | Partitioned audit chains | M–L | C | **needs a dedicated Phase-3 sign-off** before Phase 4 |
| 26 | `WP-PF-14` | Perf M-2 | End-to-end streaming on the remaining egress paths (Arrow record batches) | M–L | B | no |

---

## 3. Lanes (parallel Sonnet developers)

| Lane | Owns (exclusive write) | Wave 1 sequence | Wave 2 sequence |
| :--- | :--- | :--- | :--- |
| **A: Persistence and schema** | `Infrastructure/Persistence/Migrations/**` (baselines + shape JSON), `*GovernanceRepository.Consent.cs`, `*GovernanceRepository.AccessProfiles.cs` (new for PG/SQL Server), `GatewayGovernanceServiceExtensions.cs` (access-profile registration only) | SC-01 → SC-03 | SC-06 (persistence half) → SC-08 → SC-10 → SC-12 |
| **B: Caches, Redis, policy runtime** | `Infrastructure/Cache/**`, `Application/Governance/CasbinEnforcementService*.cs`, `Application/Governance/Caching/**`, `Domain/Options/Storage/RedisOptions.cs`, `Application/VirtualFilters/MandatoryRowFilterResolver.cs` (memo only), `Application/Services/ColumnMaskingProvider.cs` (cache only) | PF-01 → PF-03 → PF-06 → PF-05 → PF-07 | PF-09 → SC-04 → SC-06 (resolver half) → PF-11 → PF-12 → PF-14 |
| **C: Audit, CDC, auth, governance services** | `Infrastructure/Persistence/*GovernanceRepository.Audit.cs`, `Application/Auditing/**`, `GraphQL/Subscriptions/CdcSubscriptionGovernor.cs`, `Application/Governance/Services/DeclarativeConsentApplyService.cs`, `Api/Security/Basic*`, `Api/Endpoints/Governance*.cs` | SC-02 → PF-04 → PF-02 → PF-08 | PF-10 → SC-05 → SC-07 → SC-09 → PF-15 → PF-13 |

Synchronisation points:
- **S1:** SC-01 merged, then PF-05/PF-09 build on the indexed query.
- **S2:** PF-06 merged, then PF-09 reuses the bounded-cache helper.
- **S3:** SC-06 needs SC-04 (attribute binding) and SC-01. Lane A delivers the schema and repository, Lane B the resolver. The contract `IAccessPolicyResolver` is merged first as interface-only.

---

## 4. Package Details

Each package lists **contract**, **files**, **tests** and **done criteria**. TDD: the first commit of every package is the failing test.

### WP-SC-01: Consent indexes and indexed consent query (Lane A, S–M)

- **Contract:** `IConsentRepository` signatures are unchanged. Behaviour is identical (same consents returned for the same inputs).
- **Schema (3 baselines + 3 shapes):**
  - `CONSENTS(tenant_id, table_id, is_revoked)`
  - `CONSENTS(tenant_id, grantee_sid_norm, is_revoked)`, where `grantee_sid_norm` is the upper-invariant SID written by the repository (the C# path compares case-insensitively today). Alternative: an expression index `UPPER(grantee_sid)` on PG and SQLite and a CI collation on SQL Server. Choose one and document it in the PR.
  - `CONSENTS(tenant_id, role_name)`
  - `CONSENT_COLUMN_RULES(consent_id)`, `CONSENT_ROW_FILTERS(consent_id)`, `COLUMN_MASKING_RULES(table_column_id)`, `ROLE_MEMBERS(member_sid)`, `TABLE_OWNERS(data_owner_id)`
  - Normalised lower-case natural-key columns or expression indexes for `TABLES(source_name, schema_name, table_name)`, so that `LOWER(...) = LOWER(@p)` is sargable. On SQL Server, a computed persisted column plus index.
- **Query:** `GetActiveConsentsForSubjectsAsync` resolves `table_id` once (cached metadata) and filters `table_id = @id AND tenant_id = @t AND is_revoked = 0 AND (grantee_sid_norm IN (@subjects…) OR grantee_type = 'Role')`. `GetAllActiveConsentsForSubjectsAsync` filters `(grantee_sid_norm IN (…) OR (grantee_type='Role' AND role_name IN (…)))` in SQL. Parameter lists are chunked (SQL Server 2,100-parameter limit). PG uses `= ANY(@arr)`. Validity filtering stays in C#; typed timestamps come in SC-08.
- **Files:** the three `*BaselineSchema.cs`, three shape JSON files, `{Sqlite,PostgreSql,SqlServer}GovernanceRepository.Consent.cs`, `CreateConsentAsync` (writes `grantee_sid_norm`).
- **Tests:**
  - Unit: `GovernanceSchemaShapeSnapshotTests` (regenerated snapshots).
  - New `ConsentQueryEquivalenceTests` on SQLite in-memory: a matrix of user/group/role/alias SIDs × mixed case × tenants × revoked/expired/future-deny, asserting the same result set as the pre-change query, which is kept as a test oracle.
  - Integration: extend the PG/SQL Server contract suites with the same matrix, plus one `EXPLAIN`/plan assertion that the index is used (PG only, cheap).
- **Done:** the matrix is green on 3 engines; the shape verifier accepts a fresh database.

### WP-PF-01: Epoch reads safe on Redis Cluster (Lane B, S)

- **Contract:** `IEpochValidationService.AreEpochsValidAsync` is unchanged.
- **Change:** in `Infrastructure/Cache/EpochValidationService.cs`, replace the multi-key `StringGetAsync(RedisKey[])` with pipelined single-key GETs (the pattern of `RedisTokenRevocationService.cs:139-140`), or group the keys by `{tenant}` hash tag. Pipelined GETs are preferred, because changing key names would invalidate every running epoch. On any exception, all pending tables are invalid (unchanged).
- **Tests:**
  - Unit: a fake `IDatabase` that throws on multi-key commands spanning slots. Assert one command per key and fail-closed on error.
  - `[Category=Scale]` integration against a 3-shard Redis Cluster container (LT-8 precondition).

### WP-SC-02: Manifest role idempotency and transactional apply (Lane C, S)

- **Contract:** `IDeclarativeConsentApplyService.ApplyManifestAsync` is unchanged.
- **Change:** for `GranteeType.Role`, load existing consents by `(table, role_name, tenant)`. Add `IConsentRepository.GetActiveRoleConsentsAsync(TableIdentifier, string roleName, TenantId, DateTimeOffset)`, a default interface method implemented by Lane A in the three repositories (the only Lane A file touched, coordinated through the interface PR). Revoke and create run in one repository transaction: add `ReplaceConsentsAsync(IReadOnlyList<Guid> revoke, Consent create, Sid actor, string reason)`, with a single epoch bump.
- **Tests:** unit with a fake repository: applying the same role manifest twice gives `unchanged = 1` and no duplicate. Simulate a fault between revoke and create: no state change, and the audit records the failure.

### WP-PF-03: Compiled sub_rule delegates (Lane B, S)

- **Change:**
  - Compile every `sub_rule` once per `PolicySnapshot` into a `FrozenDictionary<string, CompiledSubRule>`, using DynamicExpresso `ParseAsDelegate` with typed parameters.
  - A compile failure is stored as a **failed** entry with these semantics: an allow rule does not match; a deny rule matches (fail closed).
  - Precompile object glob and `:param` patterns into `Regex` (`RegexOptions.NonBacktracking | CultureInvariant`, with a match timeout).
  - Index rules by literal object prefix.
- **Files:** `CasbinEnforcementService.SubjectAttributes.cs`, `.Masking.cs` (pattern part), `.Columns.cs` (candidate selection), snapshot type.
- **Tests:**
  - An equivalence test over the existing Casbin fixtures (old interpreter path as oracle, removed after the merge).
  - A fail-closed test for compile errors on allow and deny rules.
  - A test that interpreter options forbid assignment and type access.
  - `CasbinDecisionBenchmark` in `benchmarks/` (not CI).

### WP-PF-04: Batched audit writes (Lane C, S–M)

- **Change:**
  - One multi-row insert per batch: PG `unnest` or binary COPY; SQL Server TVP; SQLite multi-values in one transaction.
  - Hashes are computed in memory from a tail read once, under the existing advisory lock.
  - Tail read and anchor I/O become async. The anchor is signed every N batches or every T ms (config, defaults 16 / 250 ms), and Tier-A events are acknowledged only after commit and anchor.
  - `AuditGroupCommitSink` is wired for Tier-A events.
  - The middleware and executor events are de-duplicated: the executor event becomes the single record, and the middleware marks `derived`.
- **Files:** `*GovernanceRepository.Audit.cs` (3 engines), `Application/Auditing/AuditGroupCommitSink.cs`, `AuditChainAnchorStores.cs`, `AccessAuditMiddleware.cs`.
- **Tests:**
  - Unit: `AuditGroupCommitSinkTests` extended (ordering, batching, failure propagates to every waiter).
  - A chain verification test after 1,000 concurrent writes (SQLite).
  - Integration: the existing audit contract tests on PG/SQL Server plus `VerifyAuditHashChainAsync` after a batched write.
  - A crash-between-commit-and-anchor test: the verifier detects the unanchored tail, and Tier-A is not acknowledged.

### WP-SC-03: Persisted access profiles (Lane A, M)

- **Change:**
  - Implement `IAccessProfileRepository` in `PostgreSqlGovernanceRepository.AccessProfiles.cs` and `SqlServerGovernanceRepository.AccessProfiles.cs` (the tables already exist in the baselines) and set `useRepositoryAccessProfiles: true` for both engines.
  - `GetProfilesForSubjectsAsync(TenantId, IReadOnlyCollection<string> subjects, IReadOnlyCollection<string> roles)` (new; the single-subject method becomes a default) matches `subject_type` User/Group/Role.
  - `ResolveActiveAccessProfileAsync` passes user, alias SIDs, groups and roles.
  - Precedence is deterministic: `Strict` > `Default` > `Unmasked`. `Unmasked` wins only when assigned to the user directly (not via a group) and approved through four-eyes. Ties are broken by `profile_id` ordinal.
- **Files:** two new repository partials, `TableAccessPolicy.Consent.cs` (profile resolution only), `AccessProfileCache.cs` (key includes the group/role hash), `GatewayGovernanceServiceExtensions.cs:78,85`.
- **Tests:**
  - Contract tests on 3 engines (shared base class like `CatalogKeyDefinitionContractTestsBase`).
  - Unit precedence matrix.
  - A test that a group-assigned `Unmasked` profile does not unmask.
  - Multi-replica simulation (two repository instances, one database) showing consistency.
  - Source outage → `AccessProfileSourceUnavailableException` → deny.

### WP-PF-06: Bounded caches (Lane B, S)

- **Change:**
  - Bounded caches (size + sliding TTL) for the VF memo, the repository metadata cache, the masking regex cache and the Basic-auth success cache.
  - Fix `AccessProfileCache` `entry.Size` to byte estimates, or move it to a dedicated count-based cache.
  - Introduce one shared helper, `BoundedCache<TKey,TValue>`, on **BitFaster.Caching** `ConcurrentLru` (MIT; new dependency, pinned).
- **Tests:** unit for capacity, eviction and TTL per cache, and that eviction yields a miss and never a stale allow.

### WP-PF-02: CDC subscription counters (Lane C, S; after issue-99)

- **Change:** counter key `cdc:subs:{tenant}:{sid}` (hash-tagged `{tenant}`), plus `MaxSubscriptionsPerTenant` (default 2,000). Increment, check and decrement happen in one Lua script with a TTL heartbeat against leaks. If Redis is unavailable, the new subscription is rejected.
- **Tests:** extend `CdcSubscriptionGovernorGql34Tests` (per-subject versus per-tenant limits, leak recovery, Redis down rejects).

### WP-PF-05 and WP-PF-07: Redis round trips (Lane B, S each)

- **WP-PF-05:** read L2 for all missing tables in one pipelined batch (`ConsentCacheService.cs:131-136`).
- **WP-PF-07:** `LocalStalenessBudgetMilliseconds` defaults to 150 for tables with `IsHighlySensitive = false` and stays 0 for sensitive tables. Epoch GETs are batched per request (via PF-01). The rate-limit and quota Lua scripts are fused.
- **Tests:** unit for budget selection by sensitivity, the pub/sub invalidation path within the budget, and the number of fused-script calls.

### WP-PF-08: Basic auth at scale (Lane C, S–M)

- **Change:**
  - The verified marker is `HMAC(serverKey, username ‖ credentialVersion ‖ passwordHash)` stored in Redis with a TTL (default 300 s). It never contains or derives from the plaintext.
  - The marker is invalidated on password change, lockout and token revocation.
  - The IP limiter applies only to unauthenticated or failed attempts.
  - Optional PATs: high-entropy tokens, SHA-256 lookup, scoped and expiring, stored in the governance database (Lane A schema).
- **Tests:** unit for the marker (no plaintext), invalidation, the limiter scope and the lockout interplay.

### WP-PF-09: Fingerprint keys, sharded cache, single-flight (Lane B, M; absorbs WP-SC-11)

- **Contract:** `IConsentCacheService` and `CasbinDecisionCache` keys get a `SubjectShape` (a hash of the sorted groups, roles, scopes and *used* attribute values). The user SID is part of the shape **only** if any candidate consent, profile, rule or virtual filter for the table references the user (user grantee, `r.sub`, user attribute) or IP-dependent attributes.
- **Change:**
  - Single-flight per (tenant, table, shape).
  - A per-replica table consent snapshot refreshed once per epoch bump.
  - Per-tenant invalidation instead of a global `Clear()`.
- **Tests:**
  - Unit: two users with identical groups share an entry.
  - Two users differing in any used attribute, alias SID or user grantee never share an entry (property-based test over a generated grantee matrix).
  - Concurrent misses run one evaluation.

### WP-PF-10: CatalogSnapshot (Lane C, M)

An immutable `CatalogSnapshot` per tenant, versioned by `ICatalogVersionProvider`. Request-path `GetAllTablesAsync` callers (OData, OpenAPI, MCP, Trino metadata, Arrow Flight, bulk endpoint) read the snapshot. The audit integrity monitor becomes incremental from a checkpoint under a cluster lease. **Tests:** version-bump visibility, tenant isolation of the snapshot, and checkpoint resume.

### WP-SC-04: User-attribute row filters (Lane B, M; after issue-107)

- **Contract:** `ISubjectAttributeProvider.GetAttributesAsync(SecurityPrincipalContext, CancellationToken) → IReadOnlyDictionary<string, IReadOnlyList<string>>`. The first implementation reads verified token claims through a configured claim map (`Gateway:Rls:AttributeClaims`). Directory and HR sources follow after owner Q3.
- **Change:** `TypedRowFilterBuilder` and `RowFilterSqlBuilder` emit `column IN (@p…)`/`= @p` with bound values for `ValueSource = USER_ATTRIBUTE`. A missing or empty attribute keeps today's constants (`1 = 0` allow / `1 = 1` deny). The cardinality cap is configurable (default 1,000), and an exceeded cap is treated as missing (fail closed). The attribute values used feed `ComputeSubjectContextHash`. `ConsentRowFilter` is **not** changed (issue-107 owns it).
- **Tests:**
  - Unit: allow and deny polarity with present, missing and empty attributes.
  - An injection corpus in attribute values (bound, never inlined).
  - A cache-key separation test.
  - A typed-policy coverage test through `RowPolicyFactory` (the compiler path proves coverage without touching `AstSecurityVisitor.cs`).

### WP-SC-05: Directory service (Lane C, M; blocked on Q1)

- **Contract:** `IDirectoryService` with `GetTransitiveGroupsAsync(subject)`, `SearchPrincipalsAsync(query, page)`, `ResolveAsync(id)` and `GetAttributesAsync(subject)`. Implementations: Microsoft.Graph + Azure.Identity, and S.DS.Protocols for LDAP. The membership cache is bounded with a TTL of 15 min.
- **Change:** Entra overage claims (`_claim_names`/`hasgroups`) are resolved through Graph in `ClaimsNormalizer`/`SecurityContextFactory`. If the resolution fails, the request gets no groups plus a metric. Grantee validation on consent, profile and policy writes rejects unknown ids with 400. `PrincipalResolverService` uses the directory, with paging.
- **Tests:** unit with a fake Graph handler (overage, 429 retry, timeout → no groups). An integration test against an OpenLDAP container may be added only if fast; otherwise `[Category=Scale]`.

### WP-SC-06: Access Policy aggregate (Lanes A + B, L)

- **Contract:**
  - `AccessPolicy` contains: id, tenant, name, effect (Allow/Deny/Restrict), `ResourceSelector` (domain/schema/table globs + tag predicate), `SubjectSelector` (groups/roles/service principals + optional attribute condition), `ColumnPolicy` (tag → level), `RowTemplates` (consent row-filter shape with `USER_ATTRIBUTE`), validity, owner, `ManagedBy`, approval status.
  - `IAccessPolicyResolver.ResolveForTable(tenant, table, subjectContext) → IReadOnlyList<Consent>` (synthetic, read-only consents). These are merged with stored consents **before** `ConsentResolutionService`, whose precedence stays unchanged.
  - Policy-set epoch per tenant.
- **Schema (Lane A):** `ACCESS_POLICIES`, `ACCESS_POLICY_SELECTORS`, `ACCESS_POLICY_COLUMN_RULES`, `ACCESS_POLICY_ROW_TEMPLATES` in the three baselines and shapes, with four-eyes approval reusing `FilterApprovalStatus`.
- **Resolver (Lane B):** an in-memory snapshot of all policies per tenant (expected 10²–10³), matched by precompiled globs plus a tag index from catalog classification. Rules:
  - Unclassified columns under a tag-based column policy default to **Mask**.
  - Columns flagged `IsSensitive` stay masked unless a policy column rule says Clear and the policy is four-eyes approved.
- **Endpoints:** CRUD plus `POST …/policies/{id}/dry-run`, which returns the matched tables, sensitive columns and effective subjects count.
- **Tests:**
  - The full SEC H-11 blending suite re-run with synthetic consents.
  - A selector over-match test (a new table appears, and its tags change).
  - The deny-overrides matrix.
  - Epoch: a policy change invalidates exactly the tables matched by the old or new selector.
  - Approval workflow tests.

### WP-SC-07: Subject lifecycle and ownership (Lane C, M; SCIM part blocked on Q2)

- **Not blocked:** `POST /api/v1/governance/subjects/{sid}/revoke-all` and `…/transfer`: transactional, audited, epoch bump per table, GovernanceAdmin only, dry-run. Group owners: `TABLE_OWNERS` accepts a group SID, with inheritance domain → schema → table (Lane A schema) and orphan detection.
- **Blocked:** either a SCIM 2.0 receiver (`/scim/v2/Users`, `/Groups`, PATCH `active=false` → revoke-all) or a Graph delta poller, depending on Q2.
- **Tests:** idempotent revoke-all, no effect on other tenants, SCIM bearer authentication and replay, and owner inheritance resolution.

### WP-SC-08: Recertification campaigns and history (Lane A, M; defaults blocked on Q5)

- **Schema:** typed `valid_from`/`valid_to` (`TIMESTAMPTZ`/`DATETIMEOFFSET`/ISO-UTC text on SQLite), `CONSENTS_HISTORY`, `RECERT_CAMPAIGNS` and `RECERT_ITEMS` (unique per campaign × item, durable dedupe).
- **Change:** the expiry query becomes `now <= valid_to <= threshold`. Items are grouped by owner × policy/group. Auto-revoke on deadline is configurable, and **extension never happens automatically**.
- **Tests:** campaign generation idempotency, auto-revoke, expired consents never re-triggered, and archival preserving the audit references.

### WP-SC-09, WP-SC-10, WP-SC-12, WP-PF-11..15

These follow the governance analysis §2 (H4, H5, M3/M4) and the performance analysis §3 (H-4, M-1, B-1.4, M-2, M-4). A detailed package plan is written when each package enters Phase 4. Each must satisfy the matching `SEC-SC` conditions in section 7.

---

## 5. Blocked on the Owner

| Q | Question | Blocks | What can proceed meanwhile |
| :--- | :--- | :--- | :--- |
| Q1 | IdP: Entra ID, on-prem AD, or hybrid? | `WP-SC-05` (Graph vs LDAP first) | Interface plus fake implementation, and overage detection (deny with metric) |
| Q2 | SCIM push from Entra, or Autheris pulls deltas? | SCIM/delta part of `WP-SC-07` | `revoke-all`/`transfer` API, group owners |
| Q3 | Authoritative user attributes (plant, region, cost center) and their source | Directory/HR providers in `WP-SC-04` | Claims-based provider |
| Q4 | Disallow per-user standing grants after migration (exceptions only)? | Exception policy in `WP-SC-06`, request flow defaults | Core aggregate and resolver |
| Q5 | Recertification cadence and auto-revoke default | Defaults of `WP-SC-08` | Campaign engine with configuration |
| Q6 | Will ReBAC be a primary grant path? | `WP-PF-11` (native index vs OpenFGA) | — |

---

## 6. Open-Source Decisions (consolidated)

| Adopt now | Adopt when unblocked | Borrow (concepts / MIT code with NOTICE) | Reject |
| :--- | :--- | :--- | :--- |
| BitFaster.Caching (MIT) in `WP-PF-06`/`-09`; HybridCache (MIT) pilot in `WP-PF-09` | Microsoft.Graph + Azure.Identity (MIT), S.DS.Protocols (MIT) in `WP-SC-05`; OpenFGA (Apache-2.0) if Q6 = primary | Ranger/Unity Catalog policy model (`WP-SC-06`), SCIM schemas from the MIT `SCIMReferenceCode` (`WP-SC-07`), Zanzibar indexing (`WP-PF-11`), RFC 6962 checkpoints (`WP-PF-13`) | OPA/Cedar/Cerbos as runtime PDP, Permify (AGPL), Rsk SCIM (commercial), Bitwarden SCIM (AGPL/proprietary), NBomber (commercial), Trillian/immudb as dependencies |

---

## 7. Security Review (Phase 3)

**Reviewer:** Security Expert (csharp-security-expert role). **Scope:** all packages above. **Method:** threat review of each
contract against the fail-closed principle, tenant isolation, segregation of duties and audit integrity.

### 7.1 Binding Conditions

| ID | Applies to | Condition |
| :--- | :--- | :--- |
| `SEC-SC-01` | SC-01 | The SQL subject filter must be **semantically identical** to the C# filter: case-insensitive SID match (normalised column or CI index), role consents included, alias SIDs included, deny consents with a future `ValidFrom` still returned for the per-table query (POL-8 TTL bound). The tenant predicate is mandatory on every request path. A null tenant is allowed only for the documented legacy overloads, which no request path may call (architecture test). |
| `SEC-SC-02` | SC-01, SC-03, SC-06, SC-08 | Schema changes only in the `0001` baselines with regenerated shape snapshots. No `ALTER`, no data rewrite at startup. The shape verifier must fail closed on a missing index or column. |
| `SEC-SC-03` | SC-02, SC-09 | Manifest apply is atomic (revoke and create in one transaction, with one epoch bump). Prune affects only rows with the manifest's `ManagedBy` label in the caller's tenant. Dry-run is the default in v2. Every change writes a Tier-A audit event. The caller must hold a governance-admin role, and the service identity is never inferred from a missing SID (no `GITOPS-SYNC` fallback for interactive callers). |
| `SEC-SC-04` | SC-03 | `Unmasked` is never created by bulk paths, requires four-eyes approval, and is effective only via a direct user assignment. Group/role-assigned profiles can only tighten. Precedence is most-restrictive-wins. A repository outage denies (`AccessProfileSourceUnavailableException`). Profiles are tenant-scoped in every query. |
| `SEC-SC-05` | PF-01, PF-05, PF-07 | Any Redis error or partial batch result marks the affected decisions stale (re-evaluate). Keys include the tenant. The staleness budget is 0 for `IsHighlySensitive` tables and at most 250 ms otherwise. A revocation and a Deny consent take effect within the budget (test). |
| `SEC-SC-06` | PF-02 | Counters are atomic (Lua). A missing heartbeat cannot permanently block or unblock a subject. Redis unavailability rejects new subscriptions. Per-tenant caps cannot be exhausted by one subject (per-subject cap ≤ tenant cap). |
| `SEC-SC-07` | PF-03 | A compiled delegate failure: allow rule → no match; deny rule → match. The interpreter disables assignment, `typeof`, reflection and member access outside the exposed context types (whitelist). Regex patterns use `NonBacktracking` or a match timeout, where a timeout counts as no-match for allow and match for deny. The snapshot swap is atomic: no decision mixes two snapshots. |
| `SEC-SC-08` | PF-04, PF-13 | Chain order equals commit order. Tier-A events are acknowledged only after commit **and** anchor. The anchor deferral is bounded (N batches / T ms) and an unanchored tail is detected by the verifier. `AuditUnavailableException` semantics are unchanged. Event de-duplication must not drop the DENY event of any request. `WP-PF-13` (partitioned chains) **must not start Phase 4** before a dedicated Phase-3 review of the new evidence model. |
| `SEC-SC-09` | PF-06, PF-09 | Eviction only produces misses. No cached security decision exists without an epoch and a tenant in its key. A shape-shared entry is used only when no candidate input references the user, alias SIDs, IP or an attribute not in the shape. A property-based test proves that two subjects with different effective inputs never share an entry. |
| `SEC-SC-10` | PF-08 | The cluster marker never contains or derives solely from the plaintext password (keyed HMAC including a credential version). It is invalidated on password change, lockout, disablement and token revocation. Failed attempts still hit the IP limiter and lockout. PATs are stored only as SHA-256 hashes, are scoped and expiring, revocable, and audited on creation and use. |
| `SEC-SC-11` | SC-04 | Attribute values are **always bound parameters**, never inlined. Attributes come only from verified token claims or the directory provider, never from request headers, query strings or bodies. Missing, empty or over-cardinality attributes are fail-closed (`1 = 0` allow / `1 = 1` deny). The values used are part of the cache shape. The compiler coverage proof stays green, and `AstSecurityVisitor.cs` is unchanged (CI diff check). |
| `SEC-SC-12` | SC-05 | Graph and LDAP accounts are least privilege (`GroupMember.Read.All`, `User.Read.All`, read-only bind). Secrets come from the key vault provider. An overage that cannot be resolved means no groups (deny) plus a metric and a log entry without PII. The membership cache TTL is at most 15 min and is purged on lifecycle events. Directory search results are tenant-filtered and paged, with rate limits. |
| `SEC-SC-13` | SC-06 | Activation requires a dry-run whose result (matched tables, sensitive columns, subject count) is stored with the approval. Four-eyes approval is required for policies matching sensitive or classified objects or granting Clear on sensitive columns. Deny overrides. SEC H-11 blending is preserved for synthetic consents. Unclassified columns default to Mask under tag-based column policies. A selector or tag change re-evaluates through the policy-set epoch. Policies never bypass ReBAC, virtual filters or Casbin restrictions. |
| `SEC-SC-14` | SC-07 | Lifecycle endpoints can only revoke or transfer, never grant (transfer requires the target to pass a normal approval). Calls are idempotent, audited, tenant-scoped and dry-run capable. SCIM uses a dedicated credential (bearer secret, optional mTLS), replay protection, rate limits and the strict RFC 7644 subset. Unknown operations get 400 and change nothing. |
| `SEC-SC-15` | SC-08 | An unanswered recertification item results in revoke after its deadline. Nothing is ever extended automatically. Campaign decisions are made only by authorised owners or delegates (existing approver checks), never by the grantee. Archival keeps the WORM audit references verifiable. |
| `SEC-SC-16` | SC-10, PF-10 | Admin queries and the entitlement report are restricted to GovernanceAdmin (all tables of the tenant) and data owners (own tables only), with keyset paging and a maximum page size. Each call is audited (`CatalogRead`). The catalog snapshot is per tenant, with no cross-tenant index. |
| `SEC-SC-17` | PF-11, SC-12 | The new store or engine is proven equivalent to the current evaluator on the existing ReBAC and Casbin fixtures before cut-over. Moving Casbin rules into the database requires four-eyes approval and audit for rule changes. |
| `SEC-SC-18` | PF-12, PF-14 | Array parameters keep the values bound. The template-cache key includes the policy fingerprint. Streaming keeps masking and row policies applied before the first byte leaves (no unmasked buffering). No change to `AstSecurityVisitor.cs`. |
| `SEC-SC-19` | all | New dependencies are MIT, Apache-2.0 or BSD only, pinned in `packages.lock.json`, and recorded in third-party notices. A cache, backplane or directory outage must not change any decision to Allow. |

### 7.2 Mandatory Tests (minimum; in addition to each package's list)

1. `ConsentQueryEquivalenceTests` on 3 engines (SEC-SC-01).
2. `GovernanceSchemaShapeSnapshotTests` regenerated, and the verifier fails on a missing index (SEC-SC-02).
3. Manifest atomicity fault-injection test and role idempotency test (SEC-SC-03).
4. Access-profile precedence matrix including a group-assigned `Unmasked` (SEC-SC-04).
5. Redis fault-injection for epochs (multi-slot error, timeout, partial results) → stale (SEC-SC-05).
6. Compiled sub_rule fail-closed tests for allow and deny, and a sandbox test rejecting `typeof`/assignment (SEC-SC-07).
7. Audit chain verification after concurrent batched writes, and the crash-before-anchor test (SEC-SC-08).
8. Property-based cache-shape separation test (SEC-SC-09, SC-04).
9. `USER_ATTRIBUTE` injection corpus and missing-attribute polarity test (SEC-SC-11).
10. Architecture test: no request path calls the tenant-less consent overloads, and no diff to `AstSecurityVisitor.cs` (SEC-SC-01, SEC-SC-11).

### 7.3 Verdict

**APPROVED WITH CONDITIONS** for Wave 1 (`WP-SC-01..03`, `WP-PF-01..08`), provided SEC-SC-01..10 and SEC-SC-19 are met and the
mandatory tests 1–7 and 10 are green.

**Wave 2:** the contracts are approved in principle. `WP-SC-06` (Access Policy) and `WP-PF-13` (partitioned audit chains) each
require a **dedicated Phase-3 re-review of the detailed package plan** before Phase 4. The packages blocked on the owner are
re-reviewed when the owner's answers change their design (Graph vs LDAP, SCIM vs pull).
