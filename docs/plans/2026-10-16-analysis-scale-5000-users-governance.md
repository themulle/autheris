# Analysis: Governability of the Access Model at 5,000 Users

**Document ID:** `ANALYSIS-SCALE-5000-GOV`
**Date:** 2026-10-16
**Status:** PROPOSED (analysis complete; no production code changed)
**Author:** Solution Architect (csharp-architect role)
**Branch:** `feat/scale-5000-users` (base `origin/main` `e8c86435`)
**Parent Plan:** [00-master-plan-overview.md](00-master-plan-overview.md)
**Companion:** runtime performance analysis by the performance expert (separate document on the same branch). This document covers
governability and manageability of the access model and its architectural scaling limits. Where a finding overlaps
(repository query shapes, caches), it is marked **[shared]** and the performance document owns the measurements.

---

## 0. Question and Short Answer

**Owner question:** the company has 5,000 users and many tables and access rules. Can Autheris govern this efficiently and with
good performance, and which technical changes are needed?

**Short answer:** not in its current form. The enforcement core is sound: zero trust, deny overrides, mask by default for sensitive
columns, per-table epochs, effective-access explanation. The problem is the **grant model**. The only thing that can *grant* read
access is a **per-table consent row**, and it has no scope inheritance, no tag selectors and no working attribute-parameterised row
filters. Every other policy object (Casbin, access profiles, virtual filters) can only restrict. For 5,000 users the number of rules
therefore grows with *grantees × tables × filter values* and not with *business policies*. Joiner/mover/leaver handling, directory
integration, recertification and the admin surfaces are all built around single consents. The governance store also has no
indexes on its hottest tables.

The changes are tractable (section 4). The core change is a **scoped access-policy layer** (RBAC + ABAC, tag and pattern selectors,
user-attribute row filters) placed on top of the existing consent resolution, plus directory-driven lifecycle. With these changes,
5,000 users need on the order of **10² policies instead of 10⁵ consent rows**.

### 0.1 Sizing assumptions used below

| Parameter | Value (assumed, typical for a 5,000-user manufacturing/enterprise tenant) |
| :--- | :--- |
| Users | 5,000 (plus ~200 service principals) |
| Catalog tables | 2,000 across ~10 domains / ~80 schemas |
| Business groups (AD/Entra) relevant to data access | ~60 |
| Row-filter dimensions | region (5), plant/site (~40), cost center (~300) |
| Avg. tables per user | ~40 |
| Avg. column rules per consent | ~10 |

| Model | Consent rows | Child rows (column rules + row filters) |
| :--- | ---: | ---: |
| Per-user consents (what the request/approval flow produces today) | ~200,000 | ~2,000,000 |
| Group consents today (no inheritance, literal row filters per plant) | 60 groups × ~300 tables × up to 40 plant variants → **18,000 – 700,000** | 10× |
| Target model (section 3) | **~100 – 300 access policies** (+ exceptions) | small |

---

## 1. How Access Is Granted Today (evidence)

| Mechanism | Grants? | Scope | Grantee | Evidence |
| :--- | :--- | :--- | :--- | :--- |
| Consent (`CONSENTS` + column rules + row filters) | **Yes, the only grant** | exactly one table (`TableId`) | User, Group, Role, ServicePrincipal | `src/Autheris.Domain/Model/ConsentModels.cs:68-92`; zero trust in `src/Autheris.Application/Services/ConsentResolutionService.cs:48-52` |
| Casbin ABAC (file CSV) | No, restrict only (runs only after a consent Allow) | patterns | sub / role | `src/Autheris.Application/Policy/TableAccessPolicy.cs:81`, `:320-329`; file-based policy `src/Autheris.Api/Extensions/DependencyInjection/GatewaySecurityServiceExtensions.cs:56-80` |
| Access profile (F-GOV-11) | No, masking exemption / extra row filter on an Allow | table patterns (`md.*`) | single user subject | `TableAccessPolicy.cs:265-269` ("never creates" an Allow); `src/Autheris.Application/Policy/TableAccessPolicy.Consent.cs:176` (user SID only) |
| Virtual filter profile (F-GOV-09) | No, mandatory AND filter | patterns + `UncoveredPolicy` | one grantee per profile (user/group/role) | `src/Autheris.Domain/Model/VirtualFilterModels.cs:399-405`; `src/Autheris.Application/VirtualFilters/MandatoryRowFilterResolver.cs:354` |
| ReBAC (OpenFGA) | No, gate (`can_query`) | per object | tuples | `TableAccessPolicy.Consent.cs:16-31` |
| Internal roles (`ROLES`, `ROLE_MEMBERS`, `GetTransitiveRolesAsync`) | Not used | n/a | n/a | defined `src/Autheris.Infrastructure/Persistence/PostgreSqlGovernanceRepository.Catalog.cs:766`, no caller in `src/` |

What already scales well (keep it):

- **Group/role grantees on consents.** `GranteeMatcher` matches user, group SIDs, roles and SID aliases (`ConsentResolutionService.cs:290-297`).
- **Deny overrides and safe consent blending** (SEC H-11, `ConsentResolutionService.cs:80-139`). Precedence is deterministic and documented.
- **Mask by default for catalog-sensitive columns.** An unmask requires an explicit Clear (`src/Autheris.Domain/Interfaces/TableAccessDecision.cs:413-434`). This is close to tag-based masking.
- **Per-table policy epochs.** A consent change bumps one table epoch inside the write transaction (`PostgreSqlGovernanceRepository.Consent.cs:490-495`), so invalidation is fine-grained.
- **Explainability and simulation:** `EffectiveAccessExplanationService`, `PolicySimulationService`, bundled consent requests (F-GOV-20).

---

## 2. Findings Ranked by Impact

Legend: **Effort** S ≤ 3 days, M ≤ 2 weeks, L > 2 weeks (one developer, including TDD).

### BLOCKER

#### B1. No scoped or inheritable grants: the rule count grows with grantees × tables (Effort L)

- **Evidence:** `Consent.TableId`/`TableIdentifier` is a single table (`ConsentModels.cs:71-72`). Every query loads consents by exact
  table (`PostgreSqlGovernanceRepository.Consent.cs:36-43`). The objects that do support patterns (access profiles, virtual filter
  profiles, Casbin) cannot grant (`TableAccessPolicy.cs:265-269`, `:320-329`). Catalog tags and classifications (F-GOV-08/13) do
  not appear in grant resolution at all. `TableAccessDecision.cs:424-427` only reads `IsSensitive` and masking rules.
- **Consequence at 5,000 users:** "Controlling may read all `finance.*` except PII" needs one consent per group per table. A new
  table in `finance` is invisible to everyone until an administrator creates N consents (no inheritance, so onboarding a table
  becomes a ticket per group). Audits cannot answer "which policy grants this?" in business terms, because the answer is
  thousands of near-identical rows. Rule explosion: 18k–700k rows (section 0.1).
- **Change:** introduce an **Access Policy** aggregate (see section 3) with a *resource selector* (domain / schema / table glob and/or
  catalog tag or classification predicate), a *subject selector* (groups, roles, service principals, optional attribute
  condition), a *column policy* expressed by tag (`pii:* → Mask`, `restricted → Deny`) and *row-filter templates*. Resolve it at
  decision time into the same `Consent`-shaped inputs that `ConsentResolutionService` already understands. The proven precedence
  and blending logic then stays in one place. Keep per-table consents as the *exception* channel (time-bound, four-eyes).

#### B2. User-attribute row filters are not implemented and always deny (Effort M)

- **Evidence:** `ValueSource = "USER_ATTRIBUTE"` is part of the model (`ConsentModels.cs:51-52`), but both builders short-circuit to a
  constant: `src/Autheris.Application/Services/RowFilterSqlBuilder.cs:283-290` (`1 = 0` for Allow) and
  `src/Autheris.Application/Sql/Policy/TypedRowFilterBuilder.cs:137-141`. The only attribute templating lives in the Casbin file
  layer, which restricts only and has a fixed attribute set (Department, Region, Clearance, Purpose:
  `src/Autheris.Application/Governance/CasbinEnforcementService.SubjectAttributes.cs:82-106`).
- **Consequence:** the standard pattern for scaling row-level security (one rule `plant_id IN user.plants`, as in Unity Catalog row
  filters with `is_account_group_member`/mapping tables, Immuta `@attributeValuesContains`, or Ranger `${{USER.region}}`) cannot be
  expressed as a grant. Each plant, region or cost-center value needs its own consent (×40 to ×300 per table).
- **Change:** an `ISubjectAttributeProvider` (token claims first, directory-synced attributes second, optional mapping table in a
  source system) that feeds **bound parameters** into `TypedRowFilterBuilder` (no string interpolation). The fail-closed rule stays:
  a missing attribute matches no rows. Multi-valued attributes map to `IN`. Include the attribute values in the decision cache
  context hash.

#### B3. Access profiles are not persisted on PostgreSQL / SQL Server and match only the user SID (Effort M)

- **Evidence:** `src/Autheris.Api/Extensions/DependencyInjection/GatewayGovernanceServiceExtensions.cs:78` and `:85` register
  `useRepositoryAccessProfiles: false`, which selects `InMemoryAccessProfileRepository` (`:234`). Only SQLite has a store
  (`SqliteGovernanceRepository.AccessProfiles.cs`), although the PostgreSQL baseline creates `access_profiles` and
  `access_profile_assignments` (`PostgreSqlBaselineSchema.cs:300-327`). The lookup uses `query.UserSid` only
  (`TableAccessPolicy.Consent.cs:176`), so `SubjectType = Group` assignments accepted by the bulk endpoint
  (`GovernanceEndpoints.Access.cs:208`) never match. Several matching profiles resolve with `FirstOrDefault` (`TableAccessPolicy.Consent.cs:225`).
- **Consequence:** the enterprise engines that a 5,000-user tenant would use (PostgreSQL / SQL Server, several replicas) lose all
  profiles on restart, and each replica holds different profiles (a bulk grant lands only in the memory of the replica that served
  it). Group-based exemptions silently do nothing. Precedence between overlapping profiles depends on insertion order.
- **Change:** implement the repository for PostgreSQL and SQL Server (the DDL already exists) and match assignments against user,
  groups and roles. Make precedence deterministic (`Strict` > `Default` > `Unmasked`, i.e. most restrictive wins, unless an explicit
  four-eyes `Unmasked` exception names the subject), and add tests for each governance engine.

#### B4. The governance store has no indexes on consent tables, and two hot queries scan everything [shared] (Effort S + M)

- **Evidence:** the baselines index `CONSENTS` only on `consent_request_id` (`PostgreSqlBaselineSchema.cs:259`,
  `SqlServerBaselineSchema.cs:283`). There is no index on `CONSENTS(table_id)`, `(grantee_sid)` or `(tenant_id)`, and none on
  `CONSENT_COLUMN_RULES(consent_id)`, `CONSENT_ROW_FILTERS(consent_id)`, `COLUMN_MASKING_RULES(table_column_id)` or
  `ROLE_MEMBERS(member_sid)`. Table lookups use `LOWER(source_name) = LOWER(@domain) ...`
  (`PostgreSqlGovernanceRepository.Consent.cs:41`, `...Catalog.cs:722`), which cannot use `ux_tables_natural`.
  `GetAllActiveConsentsForSubjectsAsync` selects **all non-revoked consents of the tenant** and filters subjects in C#
  (`PostgreSqlGovernanceRepository.Consent.cs:124-160`). It is called by catalog visibility on every schema, catalog and MCP listing
  request (`src/Autheris.Application/Services/CatalogVisibility.cs:254-256`, `McpDatasetCatalog.cs:481`, `QueryTypes.cs:240,415`).
  The per-table query also loads the consents of *all* grantees of the table and filters in memory (`...Consent.cs:64-67`).
  Validity timestamps are `TEXT` and are parsed per row (`...Consent.cs:57-58`).
- **Consequence:** with 10⁵ consent rows each catalog listing performs a full scan plus a hydration of the matching rows, and each
  consent cache miss scans `CONSENTS`. Cold start after a deploy (5,000 users × first request) produces a burst of full scans.
- **Change:** extend the `0001` baseline DDL on all three engines (owner decision: no upgrade migrations; the caller supplies the
  newest schema) with the following indexes: `CONSENTS(tenant_id, table_id, is_revoked)`, `CONSENTS(tenant_id, grantee_sid, is_revoked)`,
  `CONSENTS(tenant_id, role_name)`, `CONSENT_COLUMN_RULES(consent_id)`, `CONSENT_ROW_FILTERS(consent_id)`,
  `COLUMN_MASKING_RULES(table_column_id)`, `ROLE_MEMBERS(member_sid)`, `TABLE_OWNERS(data_owner_id)`. Normalise the table natural key
  to lower case on write (or use a `citext`/CI collation) so lookups are sargable. Push the subject filter into SQL
  (`grantee_sid = ANY(@subjects) OR (grantee_type = 'Role' AND role_name = ANY(@roles))`). Store validity as native timestamps.
  The shape verifier (F-GOV-12) must learn the new indexes.

### HIGH

#### H1. No joiner/mover/leaver automation (Effort M)

- **Evidence:** there is no revoke-by-subject, no SCIM endpoint and no directory sync (no match for `scim|leaver|offboard|deprovision` in `src/`).
  Revocation is per consent id (`src/Autheris.GraphQL/Types/MutationTypes.cs:443-451`, `IConsentRepository.RevokeConsentAsync`). Token
  revocation exists (`/api/admin/tokens/revoke`) but does not touch consents.
- **Consequence:** at roughly 10% annual turnover (about 500 leavers and 1,000 movers per year), per-user consents stay valid until
  `ValidTo` (default 365 days in manifests, `DeclarativeConsentApplyService.cs:128`). A rehired or reassigned account keeps old grants.
  Movers accumulate rights ("privilege creep"), and auditors will flag this.
- **Change:** (1) make **group grants the default** so that leaving the AD/Entra group revokes access with the next token. (2) Add a
  subject lifecycle API `POST /api/v1/governance/subjects/{sid}/revoke-all` (transactional, audited, epoch bump per affected table) and
  a "transfer/clone" for movers. (3) Add an optional SCIM 2.0 `Users`/`Groups` receiver or an Entra/AD delta sync that calls (2) on
  `active=false` or deletion.

#### H2. Identity resolution relies only on the token; principal search reads only the BasicAuth config (Effort M)

- **Evidence:** group SIDs come only from token claims (`src/Autheris.Api/Security/ClaimsNormalizer.cs:128-140`). Entra group
  overage (`_claim_names`/`hasgroups` when a user has more than 200 groups in a JWT, or 6 in implicit flow) is not handled: no match
  in `src/`. `PrincipalResolverService` searches only `Authentication.BasicAuth.Users` (`src/Autheris.Application/Catalog/Services/PrincipalResolverService.cs:42-45`).
  Grantee SIDs in consents and manifests are not validated against a directory (`DeclarativeConsentApplyService.cs:83-91`).
- **Consequence:** in a 5,000-user Entra tenant, heavy group users (often managers and admins) receive a token **without** a groups
  claim and fail closed on every group grant. Administrators cannot search users or groups when granting, and a mistyped group id
  produces a grant that silently matches nobody.
- **Change:** an `IDirectoryService` (Microsoft Graph `transitiveMemberOf` / `getMemberObjects`, LDAP `tokenGroups` for on-prem AD)
  with a bounded per-user membership cache (TTL about 15 min, invalidated by H1 events). Handle overage by resolving through Graph.
  Validate grantees on write (unknown → 400), and back admin principal search with the directory (paged, `$search`, Graph throttling
  aware: batch requests and exponential backoff on 429).

#### H3. Recertification is per consent, scans all expired history and is not campaign-based (Effort M)

- **Evidence:** `GetExpiringConsentsAsync` selects `valid_to <= @threshold` without a lower bound and without excluding consents that
  already expired (`PostgreSqlGovernanceRepository.Consent.cs:776`). It compares ISO strings. The workflow enqueues **one ITSM ticket per
  consent** (`src/Autheris.Application/Workflows/ConsentRecertificationWorkflowService.cs:72-117`). Deduplication uses a per-process
  dictionary plus only the first 100 pending outbox messages (`:68-69`).
- **Consequence:** with 10⁵ per-user consents and 365-day validity, about 550 tickets per day reach data owners, and long-expired
  consents re-trigger every 24 h. In practice owners rubber-stamp. Group grants (one per policy) would be recertified as a single item.
- **Change:** a **campaign model** (ISO 27001 A.5.18 / SOX style): periodic campaigns per data owner or domain that bundle items by
  *policy × group* (or *table × grantee* for exceptions). Each item supports approve, revoke and delegate decisions. Unanswered items
  are revoked automatically after the deadline (configurable, fail-closed). Restrict the query to `now <= valid_to <= threshold`, use
  durable dedupe via a campaign item table, and archive expired consents (M2).

#### H4. Declarative manifests are per table, additive, non-atomic and not idempotent for role grantees (Effort M)

- **Evidence:** one policy names exactly one table (`DeclarativeConsentApplyService.cs:59-61`). Consents that are missing from the
  manifest are never pruned (the service only revokes superseded consents of the same grantee, `:115-125`). Revoke and create are
  separate calls without a surrounding transaction (`:119`, `:191`). For `GranteeType.Role` the lookup passes an empty subject list
  (`:93-98`), and the repository returns early on an empty subject set (`PostgreSqlGovernanceRepository.Consent.cs:29-30`). As a
  result **every apply of a role policy creates a duplicate consent**. The access-profile bulk endpoint creates one profile per single
  subject and loads all tables to count matches (`GovernanceEndpoints.Access.cs:190-196`).
- **Consequence:** policy-as-code cannot become the system of record. Drift accumulates, role grants duplicate on every CI run, and
  a partially failed apply leaves revoked grants without their replacements.
- **Change:** manifest v2 that uses the B1 selectors, with a `plan` (diff: create / update / revoke) and an `apply` that runs in one
  transaction with optional `prune: true` scoped to the manifest's `managedBy` label (the same pattern as `ManagedBy` on virtual filters,
  `VirtualFilterModels.cs` "Origin of a row managed by a file repository"). Fix the role lookup now (S, independent of v2).

#### H5. Admin surfaces have no listing, search, paging or reverse lookup (Effort M)

- **Evidence:** the governance routes offer create (bulk, apply, bundle), approve/reject and single revoke, but **no list of consents**
  by table, grantee or owner (`src/Autheris.Api/Endpoints/Governance*.cs`). `GET /api/v1/profiles` returns all profiles unpaged
  (`GovernanceEndpoints.Access.cs:325`). Audit queries cap at `limit = 1000` without a cursor (`IGovernanceRepository.cs:102-108`). The
  effective-access explanation works per user × table only. There is no admin UI in `src/Autheris.Api/UI`.
- **Consequence:** the two questions an auditor asks ("who can read table X, and why?" and "what can user Y read?") cannot be
  answered at 5,000 users without database access.
- **Change:** a paged admin query API (keyset pagination, filters `table`, `grantee`, `policy`, `owner`, `expiresBefore`, `effect`)
  over REST, GraphQL and MCP. Add an **entitlement report**: for a table, expand policies and group grants to effective subjects via the
  directory (H2); for a subject, list effective tables. Export the report as CSV/Parquet for audits. Add a cursor to the audit query.

#### H6. Data ownership is per table and per person (Effort S–M)

- **Evidence:** `IsAuthorizedApproverForTableInternalAsync` joins `TABLE_OWNERS` → `DATA_OWNERS` by exact table and individual
  `ad_sid` (`PostgreSqlGovernanceRepository.Catalog.cs:714-724`). Delegations are also per person (`DATA_OWNER_DELEGATIONS`). There
  is no group owner and no schema-level or domain-level owner.
- **Consequence:** 2,000 tables need 2,000+ owner assignments. Approvals stall when an owner leaves (orphaned tables), and approval
  load cannot be distributed within a data-steward team.
- **Change:** owners as **groups** (a data-steward team) with inheritance domain → schema → table (most specific wins). Detect
  orphaned owners on directory leaver events (H1) and escalate to the domain owner.

### MEDIUM

#### M1. Decision cache is keyed per user although decisions depend on the subject context [shared] (Effort S–M)

- **Evidence:** `BuildCacheKey(tenant, userSid, table, contextHash)` (`src/Autheris.Infrastructure/Cache/ConsentCacheService.cs:483-488`).
  The context hash already covers groups, roles and scopes (`TableAccessPolicy.Consent.cs:40`).
- **Consequence:** 5,000 users × ~40 tables yields about 200k L1/L2 entries and 200k cold misses after each table epoch bump, although
  users of the same groups receive identical decisions under a group-based model.
- **Change:** when no applicable consent or policy is user-specific (no user grantee, no user attribute), key the entry on the
  *policy-relevant subject fingerprint* (the matched grantee set plus the attribute values that are used) instead of the user SID.
  That way users share entries. Measure the effect in the performance track.

#### M2. Consent history is never archived; validity stored as text (Effort S)

- **Evidence:** reads filter `is_revoked = 0` only, and expired rows stay in the hot table and are parsed and discarded in C#
  (`PostgreSqlGovernanceRepository.Consent.cs:53-62`). Columns `valid_from`/`valid_to` are `TEXT` (`PostgreSqlBaselineSchema.cs:106-107`).
- **Change:** native timestamp columns (baseline), a partial or filtered index on active rows, and a retention job that moves
  expired and revoked consents (with their children) to `CONSENTS_HISTORY` after N days. The WORM audit chain keeps the evidence.

#### M3. Two policy stores and languages without a unified view (Effort M)

- **Evidence:** consents, profiles and virtual filters live in the governance database, while Casbin rules live in a CSV file per node
  (`GatewaySecurityServiceExtensions.cs:56-80`, `CasbinEnforcementService.cs:111-114`) with hard-coded attribute placeholders.
- **Consequence:** an effective-access answer needs two sources. Changes to the Casbin file bypass four-eyes approval and the
  governance audit trail. Generic attributes (plant, cost center) cannot be used without code changes.
- **Change:** store restrictive ABAC rules in the governance database as part of the Access Policy aggregate (effect `Restrict`), with
  a generic `${attr.<name>}` binding via B2. Keep Casbin as the evaluation engine where it remains useful, but load its rules from the
  store.

#### M4. Internal role model is dead code (Effort S)

- **Evidence:** `ROLES`, `ROLE_MEMBERS` and `GetTransitiveRolesAsync` exist on all engines (`PostgreSqlGovernanceRepository.Catalog.cs:756-780`)
  but nothing calls them. Role consents match only token roles (`PostgreSqlGovernanceRepository.Consent.cs:149-156`).
- **Change:** either remove the role model or turn it into the explicit **business role layer** of the target model, which maps
  directory groups to business roles (`Controlling-Reader` ← `GG-FIN-CTRL-*`). Do not keep both half-states.

#### M5. Invalidation granularity for scoped objects (Effort S–M, with B1)

- **Evidence:** table epochs are fine-grained for consents. An access-profile change invalidates the whole tenant
  (`GovernanceEndpoints.Access.cs:259`, `:405` → `InvalidateTenantAsync`), and virtual filters use one global generation (F-GOV-09).
- **Consequence:** a schema-wide policy implemented as N table epoch bumps causes an N-key invalidation storm. Tenant-wide flushes at
  5,000 users cause cold-miss bursts.
- **Change:** introduce a **policy-set epoch** (one counter per tenant for the Access Policy aggregate, plus the existing table epoch).
  Decisions record both, and a policy change bumps one counter and only the tables its selector matches lazily (validated at read time,
  as the Casbin epoch already does, F-SEC-06).

---

## 3. Proposed Target Model

Best-practice reference (Unity Catalog, Immuta, Privacera/Ranger, Snowflake): **RBAC for coarse grants, ABAC for fine-grained
constraints, tags as the binding between policy and data, policy-as-code as the system of record, groups from the IdP as the only
subject unit for standing access, and individual grants only as time-bound, approved exceptions.**

```
Directory (Entra ID / AD)  --groups, attributes (SCIM / Graph delta)-->  Subject context (user, groups, roles, attrs)
                                                                                   |
Catalog (domains, schemas, tables, columns + tags/classification)                  |
            |                                                                      v
            +--> ACCESS POLICY (policy-as-code, four-eyes, ManagedBy)  -->  Resolver -> Consent-shaped inputs
                  resource selector: domain/schema glob | tag predicate            |
                  subject selector : groups | business roles | attr condition      v
                  effect          : Allow | Deny | Restrict                 ConsentResolutionService (unchanged precedence:
                  columns         : by tag (pii:* -> Mask, secret -> Deny)  deny overrides, blending SEC H-11, mask by default)
                  rows            : template  plant_id IN ${attr.plants}           |
                  validity, owner, recertification cadence                         v
            +--> EXCEPTIONS = today's per-table consents (time-bound,      Virtual filters / ReBAC / Casbin-from-store (restrict)
                  four-eyes, recertified per item)                                  |
                                                                                    v
                                                                            Decision cache keyed by policy fingerprint
```

Precedence (deterministic, documented): explicit Deny (policy or exception) > Restrict layers (virtual filters, ReBAC, restrictive
ABAC) > Allow from policies ∪ exceptions with SEC H-11 blending. Column level: Deny > Mask > Clear, with mask by default for any
column tagged sensitive unless an Allow names it Clear and the grant passed four-eyes. Access profiles become *exceptions with
masking mode*, persisted on every engine.

Lifecycle: joiner = directory group membership (no Autheris action). Mover = group change, and stale individual exceptions are flagged
at the next campaign. Leaver = directory deactivation → `revoke-all` (H1) plus owner reassignment (H6). Recertification campaigns run
per policy and owner, with exceptions per item and auto-revoke on timeout.

Expected scale with the target model: ~60 groups → ~20 business roles → ~150 access policies + ~1–3k active exceptions. The decision
cache holds ~(distinct subject fingerprints ≈ a few hundred) × tables instead of 5,000 × tables.

---

## 4. Recommended Change Plan (proposed work packages)

| WP | Finding | Change | Effort | Order / dependency |
| :--- | :--- | :--- | :---: | :--- |
| `WP-SC-01` | B4 | Baseline indexes on all engines, sargable natural key, subject filter pushed into SQL, shape verifier update | S | first; no model change |
| `WP-SC-02` | H4 (bug) | Fix role-grantee idempotency in `DeclarativeConsentApplyService`; wrap apply in one transaction | S | independent |
| `WP-SC-03` | B3 | PostgreSQL/SQL Server access-profile repository, group/role assignment matching, deterministic precedence | M | independent |
| `WP-SC-04` | B2 | `ISubjectAttributeProvider` + bound `USER_ATTRIBUTE` row filters in typed and text builders; attrs in context hash | M | before WP-SC-06 |
| `WP-SC-05` | H2 | `IDirectoryService` (Graph/LDAP), overage resolution, membership cache, grantee validation, principal search | M | before WP-SC-07/08 |
| `WP-SC-06` | B1, M5 | Access Policy aggregate (selectors by pattern and tag, column policy by tag, row templates), resolver into consent inputs, policy-set epoch | L | after WP-SC-01, -04 |
| `WP-SC-07` | H1, H6 | Subject lifecycle API (`revoke-all`, transfer), SCIM/delta-sync receiver, group owners with inheritance | M | after WP-SC-05 |
| `WP-SC-08` | H3, M2 | Recertification campaigns, bounded expiry query, durable dedupe, history archival, native timestamps | M | after WP-SC-06 |
| `WP-SC-09` | H4 | Manifest v2 (selectors, plan/diff, prune by `ManagedBy`) | M | after WP-SC-06 |
| `WP-SC-10` | H5 | Paged admin query API, entitlement report (who-can-see / what-can-I-see), audit cursor | M | after WP-SC-05/06 |
| `WP-SC-11` | M1 | Decision cache keyed by policy fingerprint | S–M | after WP-SC-06; measured with the performance track |
| `WP-SC-12` | M3, M4 | Restrictive ABAC rules in the governance store; remove or adopt the internal role model | M | last |

The quick wins (`WP-SC-01`, `-02`, `-03`) remove the operational blockers for a multi-replica PostgreSQL/SQL Server deployment within
about two weeks. `WP-SC-04` + `WP-SC-06` are the structural change that makes 5,000 users governable. Each package follows the
6-phase lifecycle (PRD → architecture plan → security audit → TDD → review → feature docs). Security focus for Phase 3:
fail-closed attribute resolution, selector over-match (a glob or tag that unexpectedly matches PII tables needs a dry-run diff and
four-eyes approval), and preserving SEC H-11 blending when policies and exceptions combine.

---

## 5. Open Questions for the Owner

1. Identity provider for the 5,000 users: Entra ID only, on-prem AD only, or hybrid? This decides the Graph vs. LDAP order in `WP-SC-05`.
2. Is SCIM provisioning from Entra acceptable, or must Autheris pull deltas itself?
3. Which user attributes are authoritative for row filters (plant, region, cost center), and where do they live (Entra extension
   attributes, HR system, a mapping table in a source database)?
4. Should individual (per-user) standing grants be disallowed by policy after the migration, so that only time-bound exceptions remain?
5. Recertification cadence and auto-revoke default (for example quarterly for PII policies and yearly otherwise, with auto-revoke after 14 days).
