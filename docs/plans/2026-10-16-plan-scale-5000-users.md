# Plan: Scale to 5,000 Users (Governance + Performance + Identity), Phase 2 and Phase 3

**Document ID:** `PLAN-SCALE-5000`
**Date:** 2026-10-16 (rev. 3: Q4 corrected to time-bound individual grants; connector-agnostic delegation incl. SQL Server; rev. 4: on-prem AD groups pushed via SCIM instead of LDAPS pull, section 1.7)
**Status:** PROPOSED. Wave 1 is ready for Phase 4 once the owner approves the LOC estimates (section 9). Wave 2 is defined by
the owner decisions. Two packages need a dedicated Phase-3 re-review before Phase 4 (section 8.3).
**Author:** Solution Architect (Phase 2), Security Expert (Phase 3, section 8)
**Branch:** `feat/scale-5000-users`
**Parent Plan:** [00-master-plan-overview.md](00-master-plan-overview.md)
**Inputs:**
- [2026-10-16-analysis-scale-5000-users-governance.md](2026-10-16-analysis-scale-5000-users-governance.md) (findings B1–B4, H1–H6, M1–M5, open-source §6)
- [2026-10-16-analysis-scale-5000-users-performance.md](2026-10-16-analysis-scale-5000-users-performance.md) (findings B-1..B-3, H-1..H-6, M-1..M-4, open-source §8)
- Owner decisions BL-03 of 2026-10-16 (section 1, German original in Appendix A) and the owner's follow-up on flexible authentication with customer OAuth (section 1.3)

---

## 0. Binding Rules for Every Package

| Rule | Consequence for this plan |
| :--- | :--- |
| **No upgrade or repair migrations** (owner decision 2026-10-15) | Every schema change extends the `0001` baseline **in place** on all three engines (`SqliteBaselineSchema.cs`, `PostgreSqlBaselineSchema.cs`, `SqlServerBaselineSchema.cs`) and updates the shape snapshots `Migrations/Shape/governance-shape.{sqlite,postgresql,sqlserver}.json`. `GovernanceSchemaShapeSnapshotTests` must stay green and the migration checksum stays unchanged. Existing databases are reported by the shape verifier; the caller (Talos) supplies the newest schema. |
| **Fail closed** | Every new failure path (Redis, directory, LDAPS, Graph, SCIM store, OBO token acquisition, attribute source, compile error) denies, or treats a cached decision as stale. No failure path ever allows. |
| **Do not touch** `src/TrinoSqlEngine/Ast/Visitors/AstSecurityVisitor.cs` | No package needs it. New row predicates enter only through `RowPolicyFactory` / typed `IRowPolicy` inputs. Any diff to this file is rejected in review. |
| **No hard-wired IdP or mode** (owner follow-up) | Identity is configured per **realm** (issuer). Workforce (Entra + SCIM + LDAPS + Databricks OBO) and customers (OAuth on a Graph-based customer directory) run side by side. Existing mechanisms (Entra JWT, AD FS, Kerberos/Negotiate, ForwardAuth, Basic, MCP OAuth) keep working as realms. |
| **6-phase lifecycle** | Phase 4 = Sonnet TDD developers (never Opus) working red-green-refactor, then Phase 5 review and Phase 6 feature docs in `docs/features/`. |
| **Fast CI** | Unit tests go in `tests/Autheris.Tests.Unit`. Engine-specific checks extend the existing Testcontainers contract suites in `tests/Autheris.Tests.Integration`. Redis Cluster, OpenLDAP, Graph-sandbox and load tests are tagged `[Trait("Category","Scale")]` and run outside the per-PR gate. |
| **Licences** | Dependencies must be MIT, Apache-2.0 or BSD, pinned in `packages.lock.json`. No GPL, AGPL or third-party BSL code. |
| **LOC estimate before implementation** (owner) | Section 9. Deviations above +30% in Phase 4 are reported to the owner before continuing. |

---

## 1. Owner Decisions 2026-10-16 (binding)

### 1.1 Answers to Q1–Q6

| Q | Decision (English rendering of Appendix A) | Effect on this plan |
| :--- | :--- | :--- |
| Q1 IdP | **Microsoft Entra ID** (OIDC / SAML 2.0) for authentication, MFA and Conditional Access. Tokens carry **identity claims only** (UPN, mail, object id). They carry no groups, so the group-overage problem disappears. | Group and role information no longer comes from the token. A **Role-Resolver** supplies it (`WP-SC-05`, redefined). Overage handling is dropped. |
| Q2 SCIM | **(Corrected in rev. 4, §1.7: group provisioning is on in both modes.)** **Yes, as core standard, per mode.** Entra → Databricks: full SCIM (users and groups) for Unity Catalog. Entra → Autheris: **user** provisioning on. **Group** provisioning is off in mode *Hybrid* and on in mode *Pure Entra ID*. | New SCIM 2.0 receiver (`WP-SC-07`, redefined), with Users always and Groups per realm. Deprovisioning triggers an **immediate lock**. |
| Q3 Attributes | **(Layer 2 transport corrected in rev. 4, §1.7: on-prem AD groups are pushed via SCIM, not pulled via LDAPS.)** **3-layer model.** (1) Identity: Entra ID (name, UPN, mail). (2) App function rights: on-prem AD DS / LDAP (Hybrid) or Entra App Roles (Cloud). (3) Data access rights: **Databricks Unity Catalog** (RLS mapping tables). | Autheris resolves layer 2 only, through the Role-Resolver. For Databricks, layer 3 is enforced **in Databricks** through OBO (`WP-SC-15`, new). `WP-SC-04` (`USER_ATTRIBUTE`) becomes necessary only for Autheris-enforced sources (SQL Server/PG/Oracle/customers). |
| Q4 Per-user grants | **Corrected 2026-10-16 (§1.5):** individual per-user grants **remain possible, but only time-bound**. There are no standing or permanent per-user grants. Standing access is based on roles and groups. Databricks RLS uses dynamic filters (`current_user()`, mapping tables). | `WP-SC-16` is reworked into a **time-bound individual grant policy**: a mandatory expiry on every per-user grant on every grant path, with maximum durations per realm and sensitivity, renewal rules, four-eyes approval where it applies, auto-expiry, audit, and inclusion in campaigns. C1 is revisited. |
| Q5 Recertification | **Risk-based.** Every 90 days: privileged roles, admin rights, sensitive RLS assignments. Every 365 days: regular business roles. **Event-driven:** immediate lock trigger via SCIM when a user leaves. | `WP-SC-08` gets fixed defaults. The SCIM lock lives in `WP-SC-07`. Scope split in 1.4 (Autheris recertifies its *mappings*; membership is recertified at the IdP/AD). |
| Q6 ReBAC | Not addressed by the owner. | Recommendation unchanged: native indexed store (`WP-PF-11`). OpenFGA only if ReBAC becomes a primary grant path. Role-based standing access makes this less likely. |

**Data flow (owner):**
1. Login at Entra ID (identity claims only).
2. SCIM lifecycle Entra → Autheris and Entra → Databricks.
3. ~~Autheris reads the user's AD groups by user key over **LDAPS**~~ **(rev. 4, §1.7)** The user's on-prem AD groups are **pushed** to Autheris via SCIM `/Groups` (by Entra provisioning for synced groups, or by an on-prem push agent), and Autheris maps them to UI and functional rights (for example "may export").
4. Autheris passes the user token via **OAuth 2.0 On-Behalf-Of** to Databricks, which evaluates `current_user()` with native row-level security and column masking. Unity Catalog holds the full audit trail.

**Modular Role-Resolver, two operating modes (owner):**
- *Group / Hybrid* (current): identity via Entra SCIM (users); app roles from on-prem AD groups **pushed via SCIM `/Groups`** (rev. 4, §1.7; replaces the on-prem AD/LDAP pull connector); data RLS in Databricks via token delegation.
- *Pure Entra ID* (future / SaaS): identity via SCIM (users and groups); app roles via Entra App Roles, or Entra security groups from the SCIM payload or the Graph API; data RLS as above.

### 1.2 Current State of Authentication (verified in code, 2026-10-16)

- **One Entra tenant** (`EntraIdAuthOptions`: `Instance`, `TenantId`, `Audience`, `ClientId`, `src/Autheris.Domain/Options/Security/JwtOptions.cs:158-173`) plus optional **AD FS**. When both are enabled, a Bearer token is routed by its unverified issuer to one of two JwtBearer schemes (`src/Autheris.Api/Security/GatewayAuthSchemes.cs:17-37`) and then fully validated (issuer and audience always validated, `src/Autheris.Api/Extensions/GatewayServiceCollectionExtensions.cs:325-366`).
- Further schemes: Basic (with optional session), Negotiate/Kerberos, ForwardAuth (proxy headers, `TrustUpstreamGroupSids`), the test handler and MCP OAuth protected-resource metadata (`GatewayServiceCollectionExtensions.cs:234-297`).
- **No** support for a second Entra tenant, Azure AD B2C, Entra External ID (`*.ciamlogin.com`) or generic OIDC issuers. There are **no** Graph or LDAP lookups, and groups come from token claims only (`ClaimsNormalizer.cs:128-140`).
- The Autheris tenant is taken from the `tenant_id`/`tid` claim (`src/Autheris.Domain/Common/Sid.cs:166-186`). For two Entra issuers, `tid` would map users of different directories into tenants named after their directory id. That is a cross-issuer risk once customers are added (handled in 1.4 and `SEC-SC-21`).
- Databricks sources authenticate as a **service principal** (OAuth M2M, PAT in Development only, `DatabaseOptions.cs:150-171`). There is no user delegation, and the token cache is keyed by `(host, clientId)` (`DatabricksTokenCache.cs`).
- Subject-wide token revocation already exists (`ITokenRevocationService.RevokeAsync(subject)`, `iat`-based), and the SCIM lock reuses it.

### 1.3 Owner Follow-up: Flexible Authentication, Customer OAuth

German original: "auth ist aber sehr flexibel. wir haben auch oauth für endkunden das jedoch auch wieder irgendwie auf der graph-api basiert".

**Assumptions (to confirm in Phase 1 of `WP-SC-13`):** customer users authenticate with OAuth 2.0 / OIDC at a Microsoft customer
identity directory. Most likely this is **Entra External ID** (a customer tenant, issuer `https://<tenant-id>.ciamlogin.com/<tenant-id>/v2.0`). It could also be **Azure AD B2C** (`https://<name>.b2clogin.com/<tenant-id>/v2.0/` with user-flow policies), or a second Entra tenant with B2B guests. Customer users and groups and their custom attributes are reachable through **Microsoft Graph** in that directory. Customers are **not** provisioned to Databricks, and the customer directory does not push SCIM to Autheris unless configured.

**Consequences:**
- Identity becomes a **realm registry**: N issuers side by side. Each realm defines issuer/metadata, audiences, claim mapping and user key, the Autheris tenant binding, the provisioning mode (SCIM or JIT on first login), a resolver chain for roles and attributes, an RBAC and recertification profile, and trust for OBO.
- Data sources get an **enforcement mode** plus a list of **trusted realms** for delegation. Customer access defaults to **Autheris-enforced** (consents, Access Policies, row filters and masking in Autheris, with the service principal at the source). OBO is allowed only for realms the source trusts (the workforce Entra tenant for Databricks).

### 1.4 Conflicts and How They Are Resolved

| # | Conflict | Resolution (binding for the packages) |
| :--- | :--- | :--- |
| C1 **(rev. 3)** | Clear-text (`Unmasked`) access: rev. 1 allowed only direct user assignment; rev. 2 allowed only privileged groups plus break-glass; the owner correction (§1.5) re-allows time-bound individual grants. | `Unmasked` takes effect through exactly three routes: **(a)** a **privileged role/group** mapping (for example `AUT-Unmask-<domain>`), four-eyes approved and recertified every 90 days; **(b)** a **time-bound individual `Unmasked` grant**: four-eyes (approver ≠ requester ≠ grantee, data owner of every matched table or a delegate), mandatory justification and purpose, max **30 days** (default 7), no self-renewal, Tier-A audit, listed in the next campaign; **(c)** a **break-glass** exception (max 72 h, four-eyes, possibly approved after the fact within 24 h, Tier-A audit). Customer realms: route (b) is disabled by default. `SEC-SC-04` is rewritten accordingly. |
| C2 | Autheris' own consent, row-filter and masking enforcement versus source-native RLS and masking on delegated sources (Databricks UC; SQL Server security policies): defence in depth vs double filtering. | New per-source `EnforcementMode`: `AutherisEnforced` (default, today's behaviour, service principal), `Delegated` (OBO; Autheris enforces the **coarse gate**: table visibility and allow/deny by role-based policy, ReBAC, Casbin deny, purpose and contracts; it adds **no** consent row filters and **no** Autheris column masks, because the source is authoritative for rows and masks), and `DelegatedDefenceInDepth` (delegation **plus** Autheris row filters and masks, AND-combined; for sources whose native policies are not yet complete). Double masking is harmless (masking a masked value). Double row filtering is an intersection and is documented as such. A filter on an Autheris-masked column stays rejected. Mode changes are Tier-A audited and four-eyes approved. |
| C3 | Caching on OBO sources: results depend on `current_user()` at the source, so any cross-user reuse leaks rows. | On `Delegated*` sources: (a) the decision-cache shape always includes the **user key** (`WP-PF-09` sharing disabled); (b) **every result-bearing cache** (semantic cache F-AI-10, change-detection ETags F-DATA-09, governed aggregation caches F-DATA-08, async job results, column value profiles and synthetic samples F-AI-17, catalog value profiling) is keyed by user or disabled. Profiling jobs never run with the service principal against an OBO source. (c) The issue-107 key-set cache: `KeySetCacheAllowed` **must stay `false`** for `Delegated*` sources, and the options validator **rejects** `true` at startup (fail closed). |
| C4 | Identity-only tokens versus today's group-claim-based checks (`GranteeMatcher`, consent cache context hash, virtual-filter profiles, Casbin groups, ForwardAuth). | The Role-Resolver runs in the authentication pipeline (after token validation and before `ClaimsNormalizer`) and injects **resolved** groups and roles as claims with a provenance marker. Token-borne `groups`/`roles` claims are **ignored** for realms whose `GroupSource` is not `Token`, so there is exactly one authoritative source per realm. Resolved identifiers are **realm-namespaced** (`<realm>|<group-id>`). The default workforce realm keeps un-namespaced legacy SIDs for compatibility. Downstream matchers stay unchanged. |
| C5 | OBO needs a real Entra user assertion; Basic, PAT, Kerberos, ForwardAuth and customer-realm users have none. | `Delegated*` sources deny requests without a delegable assertion from a trusted realm. There is **no fallback to the service principal** (fail closed). BI tools on Databricks data use OAuth (Entra) or the Autheris-enforced path on non-delegated sources. |
| C6 | Long-running work (async jobs, CDC subscriptions, scheduled exports) on OBO sources outlives the user token. | Default: rejected for `Delegated*` sources if the job cannot finish within the remaining assertion lifetime. MSAL long-running OBO (refresh tokens held by Autheris) is **disabled by default** and allowed only per source with Phase-3 sign-off. |
| C7 | Recertification at 90/365 days, versus group membership living in AD/Entra. | Autheris recertifies **its own artefacts**: group/role → functional-right mappings, group/role → Access Policy, access-profile ↔ group mappings, break-glass usage, data-source enforcement mode. **Membership** is recertified at the source of truth (Entra Access Reviews / AD tooling). Unity Catalog mapping tables are recertified by the data owners in Databricks. Autheris exports the entitlement report (`WP-SC-10`) as evidence for both. |
| C8 | Role-based standing access versus service principals and technical accounts. | Service principals are not users. Their standing access goes through an **app role or group**. A direct `ServicePrincipal` grantee is allowed only when the realm profile permits it. It is then time-bound (max 365 days, `WP-SC-16`) and classified privileged (90-day recertification). |

---

### 1.5 Owner Correction to Q4: Time-Bound Individual Grants (2026-10-16)

German original (Appendix A): "einzelrechte müssen möglich sein - aber eben nur zeitlich beschränkt".
Binding: per-user grants are allowed on every grant path, **always with an expiry**. Permanent per-user grants are not allowed. The
earlier strict-RBAC rejection guard is withdrawn. Standing access stays role/group based (C7, C8 unchanged).

### 1.6 Owner Input: Delegation Primarily to Databricks, also to SQL Server (2026-10-16)

German original (Appendix A): "rechte/obo primär bei databricks - jedoch auch richtung sql server denkbar".
Binding: source-side enforcement through delegated identity is a **connector capability**, not a Databricks special case. Databricks is
the first implementation (`WP-SC-15`). SQL Server is the planned second (`WP-SC-17`, new):
- Entra token authentication to Azure SQL / SQL Server 2022+ (Entra-enabled) via an OBO access token on `SqlConnection`.
- Alternatively, on-prem Kerberos constrained delegation.
- Or an Autheris-asserted identity read by native security policies.

Capabilities are **declared** per data source in configuration, never discovered at runtime (consistent with the "no source catalog
queries" rule of issue #94).

---

### 1.7 Owner Decision: On-Prem AD Groups Are Pushed via SCIM (2026-10-16, rev. 4)

German original (Appendix A): "die lokalen gruppen hätte ich per externem dienst an autheris geschickt. dieser muss evtl. bei uns im lan unter windows laufen um zugriff zu erhalten. scim ist daher als standard eventuell gut".

**Binding:**
- Autheris does **not** pull from on-prem AD. Autheris may run outside the corporate LAN (Kubernetes, cloud) and must not need an inbound path or LDAPS access to domain controllers.
- On-prem group membership reaches Autheris as a **push** over the standard SCIM 2.0 `/Groups` (and `/Users`) endpoint of `WP-SC-07`. The connection is outbound-only from the LAN.
- **Group provisioning is on in the Hybrid mode as well** (corrects Q2). The SCIM-provisioned store is the authoritative group source of the workforce realm in both modes (`GroupSource = Scim`).
- Two supported push sources, same endpoint:
  1. **Entra provisioning** (preferred when AD groups are already synced to Entra by Entra Connect / Cloud Sync): the Autheris enterprise app provisions users and assigned groups. Known limits: only groups assigned to the app, nested groups are not flattened, incremental cycle about 40 minutes (deactivation is processed with priority but is not instant).
  2. **On-prem push agent** (when nested or non-synced local groups are needed): a Windows service in the LAN reads AD (transitive membership) and acts as a SCIM client. It is a separate deliverable (`WP-SC-18`, optional) and needs no Autheris-specific protocol.
- The deprovisioning lock (Q5) follows from the same channel: `active=false` / `DELETE` from either source triggers the `WP-SC-07` lock.

**Alternatives considered and rejected:**
- *LDAPS pull from Autheris* (rev. 3): needs network reach from Autheris into the LAN, a bind account and an inbound firewall rule; does not fit a cloud/Kubernetes deployment.
- *Groups in ForwardAuth headers from an upstream proxy* (existing `ForwardAuth` scheme): solves per-request roles only. It cannot lock users with live sessions, cannot stop work that runs without a request (CDC receivers, webhooks, async jobs, PATs, time-bound grants), and gives no user directory for grants before first login, reports or recertification. It stays supported as a realm `GroupSource = Header` for deployments that accept these limits.

**Effects:** `WP-SC-05` drops `LdapsRoleProvider` from the default set (kept only as an optional provider for deployments where Autheris runs inside the LAN); `ScimStoreRoleProvider` serves both modes. `WP-SC-07` gets `/Groups` for the workforce realm and the hardening in `SEC-SC-14` (rev. 4). `WP-SC-14` four-eyes approval applies to every mapping of a pushed group to a privileged right.

---

## 2. Merge Hotspots with Parallel Branches

| Branch | Overlap | Rule |
| :--- | :--- | :--- |
| `feat/issue-99-cdc-fanout` | All baselines and shape JSONs (CDC stream tables), `AuditEventTypes.cs`, streaming options and DI. | `WP-PF-02` and `WP-PF-15` start after issue-99 merges. Baseline and shape edits are owned by **Lane A only**. After a rebase, regenerate the shape JSON from a fresh database instead of hand-merging it. |
| `feat/issue-107-rowfilter-keyset-cache` | Baselines and shapes, `ConsentModels.cs` (`ConsentRowFilter`), `IGovernanceRepository.cs`, `RowPolicyFactory`, epochs, `RlsOptions.cs`, **`DatabaseOptions.cs` (`KeySetCacheAllowed`)**. | `WP-SC-04` adds no fields to `ConsentRowFilter` and lands after 107. `WP-SC-15` adds `EnforcementMode`/`TrustedRealms` to the **same** data-source options class as 107's `KeySetCacheAllowed`; it lands after 107 and adds the cross-validation (C3). `WP-PF-01/-07/-09` rebase on 107 before Phase 5. |
| `feat/bl-06-open-telemetry` | DI composition, logging, metrics, redaction. | New counters (cache, audit batch, resolver/Graph/LDAPS latency, OBO acquisition, SCIM operations) use the existing meter or bl-06's abstraction. **Token redaction**: OBO assertions, OBO tokens, SCIM secrets and LDAPS bind credentials are added to bl-06's redaction list. If bl-06 is not merged, `WP-SC-15` ships its own unit test that no log scope or span attribute contains `Bearer`/JWT patterns. |

---

## 3. Work Packages (ordered)

### 3.1 Wave 1: Quick Wins (no owner input needed)

| Order | WP | Source | Title | Effort | Lane |
| ---: | :--- | :--- | :--- | :---: | :---: |
| 1 | `WP-SC-01` | Gov B4 + Perf H-2.1 | Consent indexes, sargable table key, subject/table filter in SQL (indexed consent query) | S–M | A |
| 2 | `WP-PF-01` | Perf B-2 | Epoch reads safe on Redis Cluster | S | B |
| 3 | `WP-SC-02` | Gov H4 (bug) | Manifest role-grantee idempotency + transactional apply | S | C |
| 4 | `WP-PF-03` | Perf H-1.1/1.2 | Compiled `sub_rule` delegates + precompiled object patterns | S | B |
| 5 | `WP-SC-16` **reworked (rev. 3)** | Owner Q4 (corrected) | **Time-bound individual grant policy** on all grant paths: mandatory expiry, max durations per realm and sensitivity, renewal rules, four-eyes, read-time clamp of legacy permanent grants | S–M | C |
| 6 | `WP-PF-04` | Perf B-1.1–1.3, 1.5 | Batched audit insert, async tail/anchor, group commit, one event per request | S–M | C |
| 7 | `WP-SC-03` | Gov B3 + C1 | Persisted access profiles on PG/SQL Server, group/role assignments plus **time-bound** user assignments, deterministic precedence, break-glass exception | M | A |
| 8 | `WP-PF-06` | Perf H-3 | Bounded caches + correct `Size` units | S | B |
| 9 | `WP-PF-02` | Perf B-3 | CDC subscription counter per subject + per-tenant cap (after issue-99) | S | C |
| 10 | `WP-PF-05` | Perf H-2.2 | Pipelined L2 consent reads | S | B |
| 11 | `WP-PF-07` | Perf M-3 | Staleness budget (non-sensitive only), batched epochs, fused Lua | S | B |
| 12 | `WP-PF-08` | Perf H-6 | Basic auth: cluster-shared verified marker, IP limiter after auth, PATs | S–M | C |

### 3.2 Wave 2: Identity and Structural (defined by the owner decisions)

| Order | WP | Source | Title | Effort | Lane | Re-review |
| ---: | :--- | :--- | :--- | :---: | :---: | :--- |
| 13 | `WP-SC-13` **new** | Owner Q1 + follow-up | **Identity realm registry**: multi-issuer validation, per-realm claim mapping / user key / tenant binding / provisioning mode / RBAC and recert profile; existing schemes migrated as realms | M–L | C | **yes** |
| 14 | `WP-SC-05` **redefined** | Owner Q1/Q3, Gov H2 | **Role-Resolver** abstraction + providers: SCIM-provisioned store (default for the workforce realm in both modes, rev. 4), Entra App Roles (token), Graph (groups/app roles/attributes, also for customer directories), static mapping, optional LDAPS (AD DS, only when Autheris runs inside the LAN); resolver chain per realm; bounded caching | M–L | C | no (conditions) |
| 15 | `WP-SC-07` **redefined** | Owner Q2/Q5, Gov H1/H6 | **SCIM 2.0 receiver** per realm (Users always, Groups per realm; workforce realm Groups on in both modes, rev. 4), **deprovisioning lock** (immediate), JIT provisioning for realms without SCIM, `revoke-all`/`transfer`, group owners with inheritance | M | A + C | no (conditions) |
| 16 | `WP-SC-14` **new** | Owner Q3 (layer 2) | **Functional-rights mapping**: resolved groups/app roles → Autheris functional rights (`GatewayRole` + feature rights such as `Export`, `Mcp.Admin`) and → grantees of Access Policies; four-eyes, audited, recertified | M | B | no (conditions) |
| 17 | `WP-SC-15` **(rev. 3)** | Owner Q3 (layer 3), §1.6, C2/C3/C5/C6 | **Connector-agnostic delegated enforcement** (`IDelegationCapability` per connector, declared capabilities, coarse gate, cache rules, 107 cross-validation) + **Databricks OBO** as first implementation | M–L | B | **yes** |
| 17a | `WP-SC-17` **new (rev. 3)** | §1.6 | **SQL Server delegation**: Entra OBO access token (Azure SQL / SQL Server 2022+), Autheris-asserted `SESSION_CONTEXT` mode, per-user pool partitioning, connection lifetime ≤ token expiry; Kerberos constrained delegation as an optional spike | M | B | **yes** (with SC-15) |
| 18 | `WP-PF-09` (absorbs `WP-SC-11`) | Perf H-1.3–1.5, H-2.3; Gov M1 | Policy-fingerprint keys (disabled on `Delegated*`), sharded LRU, single-flight, table consent snapshot | M | B | no |
| 19 | `WP-SC-06` | Gov B1, M5 | Access Policy aggregate (group/role grantees only), selectors by pattern and tag, policy-set epoch; main use on Autheris-enforced sources and as the coarse gate on OBO sources | L | A + B | **yes** (unchanged from rev. 1) |
| 20 | `WP-SC-08` | Gov H3, M2, owner Q5 | Recertification campaigns 90/365 + event-driven, scope per C7; typed validity timestamps; history archival | M | A | no |
| 21 | `WP-PF-10` | Perf H-5 | `CatalogSnapshot` + incremental audit integrity monitor | M | C | no |
| 22 | `WP-SC-04` **reduced scope** | Gov B2 | `USER_ATTRIBUTE` row filters (bound parameters) for **Autheris-enforced** sources; attributes from the realm resolver chain. **Phase-1 PRD: `REQ-GOV-129` (issue #129, [`2026-10-16-req-issues-128-129-130-websql-parameters-user-attributes-timezones.md`](2026-10-16-req-issues-128-129-130-websql-parameters-user-attributes-timezones.md) section 3): realises and replaces this package; adds the `{{caller.<name>}}` Virtual Filter placeholder, token-claim and governed attribute-table providers behind one `ICallerAttributeProvider` (adopted by `WP-SC-05`), depends on list parameters (`REQ-SQL-128`); size grows to L, owner confirmed Q-129-1 on 2026-10-16 (replacement accepted)** | M | B | no |
| 23 | `WP-SC-10` | Gov H5, C7 | Paged admin query API, entitlement report (per realm, group → policy → table; membership from resolver/SCIM store), audit cursor | M | A | no |
| 24 | `WP-SC-09` | Gov H4 | Manifest v2 (selectors, plan/diff, prune by `ManagedBy`; group/role grantees only) | M | C | no |
| 25 | `WP-PF-11` | Perf H-4, Q6 | Indexed ReBAC store | M | B | no |
| 26 | `WP-PF-12` | Perf M-1 | Async parse, Stage-1 cache, array IN-lists | M | B | no |
| 27 | `WP-PF-15` | Perf M-4 | CDC per-shape decisions (per-user on `Delegated*`), async-job shared results, Redis initializer | M | C | no |
| 28 | `WP-SC-12` | Gov M3, M4 | Restrictive ABAC rules in the store; **adopt** `ROLES`/`ROLE_MEMBERS` as the static-mapping provider of `WP-SC-05` or remove them | M | A | no |
| 29 | `WP-PF-13` | Perf B-1.4 | Partitioned audit chains | M–L | C | **yes** |
| 30 | `WP-PF-14` | Perf M-2 | End-to-end streaming (Arrow record batches) | M–L | B | no |

**Re-prioritisation compared with rev. 1:**
- Identity (`SC-13`, `SC-05`, `SC-07`, `SC-14`, `SC-15`) moves ahead of `SC-06`.
- `SC-04` is reduced in scope, because Databricks RLS moves to the source.
- `SC-16` (time-bound individual grants) is a Wave-1 package.
- `SC-15` becomes connector-agnostic, and `SC-17` (SQL Server delegation) is added.
- `SC-05`'s overage handling is dropped (identity-only tokens).

---

## 4. Lanes (parallel Sonnet developers, exclusive write ownership)

| Lane | Owns | Wave 1 | Wave 2 |
| :--- | :--- | :--- | :--- |
| **A: Persistence and schema** | `Infrastructure/Persistence/Migrations/**` (baselines + shapes), `*GovernanceRepository.Consent.cs`, `*GovernanceRepository.AccessProfiles.cs`, new `*GovernanceRepository.Identity.cs` (SCIM store, locks, realm-scoped mappings), DI line for access profiles | SC-01 → SC-03 | SC-07 (store) → SC-06 (persistence) → SC-08 → SC-10 → SC-12 |
| **B: Caches, Redis, policy runtime, data-source enforcement** | `Infrastructure/Cache/**`, `Application/Governance/CasbinEnforcementService*.cs`, `Application/Governance/Caching/**`, `RedisOptions.cs`, `Infrastructure/Databricks/**`, `Infrastructure/Persistence/Delegation/**` and the delegation part of `SqlConnectionFactory.cs` (`WP-SC-17`), data-source options **after issue-107 merges**, new `Application/Security/FunctionalRights/**` | PF-01 → PF-03 → PF-06 → PF-05 → PF-07 | SC-15 → SC-17 → SC-14 → PF-09 → SC-04 → SC-06 (resolver) → PF-11 → PF-12 → PF-14 |
| **C: Audit, CDC, authentication, identity, governance services** | `*GovernanceRepository.Audit.cs`, `Application/Auditing/**`, `CdcSubscriptionGovernor.cs`, `DeclarativeConsentApplyService.cs`, `Api/Security/**` (schemes, claims, Basic), `Api/Extensions/GatewayServiceCollectionExtensions.cs` (auth section), new `Application/Identity/**`, `Infrastructure/Identity/**`, `Api/Endpoints/Scim*.cs`, `Api/Endpoints/Governance*.cs` | SC-02 → SC-16 → PF-04 → PF-02 → PF-08 | SC-13 → SC-05 → SC-07 (endpoints) → PF-10 → SC-09 → PF-15 → PF-13 |

**Synchronisation points:**
- **S1:** SC-01 merged → PF-05/PF-09.
- **S2:** PF-06 merged → PF-09 reuses `BoundedCache`.
- **S3:** the `SC-13` contracts commit (`IIdentityRealmRegistry`, `IdentityRealm`, `IRoleResolver`, `ResolvedIdentity`) is pushed on day 1 of Wave 2. Lanes A (SCIM store), B (SC-15, SC-14) and C build against it.
- **S4:** issue-107 merged → SC-15 options change.
- **S5:** SC-04 + SC-01 → SC-06.

---

## 5. Package Details: Wave 1

Each package lists its contract, files, tests and done criteria. The first commit of every package is a failing test.

### WP-SC-01: Consent indexes and indexed consent query (Lane A)

- **Contract:** `IConsentRepository` is unchanged, and so is its behaviour.
- **Schema (3 baselines + 3 shapes):**
  - `CONSENTS(tenant_id, table_id, is_revoked)`
  - `CONSENTS(tenant_id, grantee_sid_norm, is_revoked)`: an upper-invariant SID written by the repository, or an expression/CI-collation index. Pick one and document it in the PR.
  - `CONSENTS(tenant_id, role_name)`
  - `CONSENT_COLUMN_RULES(consent_id)`, `CONSENT_ROW_FILTERS(consent_id)`, `COLUMN_MASKING_RULES(table_column_id)`, `ROLE_MEMBERS(member_sid)`, `TABLE_OWNERS(data_owner_id)`
  - A sargable lower-case natural key for `TABLES`
- **Query:** filter `table_id`, `tenant_id`, `is_revoked` and `(grantee_sid_norm IN … OR grantee_type='Role')` in SQL. Parameter lists are chunked (SQL Server allows 2,100 parameters). PG uses `= ANY(@arr)`. The validity check stays in C# (typed timestamps follow in SC-08).
- **Files:** 3 baselines, 3 shapes, 3 `*GovernanceRepository.Consent.cs`.
- **Tests:** shape snapshots; `ConsentQueryEquivalenceTests` (SQLite; matrix of user/group/role/alias SIDs × case × tenants × revoked/expired/future deny against the old query as oracle); PG/SQL Server contract tests; a PG index-usage assertion.

### WP-PF-01: Epoch reads on Redis Cluster (Lane B)

Replace the multi-key `StringGetAsync(RedisKey[])` in `EpochValidationService.cs` with pipelined single-key GETs, the pattern used in `RedisTokenRevocationService.cs:139-140`. Any error marks the tables invalid.

**Tests:** a fake `IDatabase` that rejects cross-slot commands; fail-closed on error; a `[Scale]` test against a 3-shard cluster.

### WP-SC-02: Manifest role idempotency and atomic apply (Lane C; Lane A implements one repository method)

- New `GetActiveRoleConsentsAsync(table, roleName, tenant, at)`.
- New `ReplaceConsentsAsync(revokeIds, create, actor, reason)`: one transaction, one epoch bump.

**Tests:** applying the same role manifest twice yields `unchanged=1`; fault injection between revoke and create leaves no change.

### WP-PF-03: Compiled sub_rule delegates (Lane B)

- Compile each rule once per `PolicySnapshot` (DynamicExpresso `ParseAsDelegate`, typed parameters, assignment and type access disabled) into a `FrozenDictionary`.
- On a compile failure, an allow rule does not match and a deny rule does (fail closed).
- Patterns use `NonBacktracking` with a timeout. Rules are indexed by object prefix.

**Tests:** equivalence against the old interpreter as oracle; compile-failure polarity; a sandbox test; a benchmark (not in CI).

### WP-SC-16: Time-bound individual grant policy (Lane C, rev. 3)

- **Contract:** `IGrantPolicy.Evaluate(GrantRequest r) → GrantDecision { Allowed, MaxValidTo, RequiresFourEyes, RecertClass, Reason }`.
  - `GrantRequest` carries the grantee type and id, realm, `GrantKind` (`Consent`, `AccessProfileAssignment`, `VirtualFilterProfile`, `ManifestPolicy`, `McpAdminGrant`, `BulkGrant`, `Renewal`, `BreakGlass`), the requested `ValidTo`, the sensitivity class of the matched tables/columns, the masking mode, the requester and the approvers.
  - Every grant write path calls it, and an architecture test enforces this.
- **Rule 1, mandatory expiry:** every grant whose grantee is a `User` (individual grant) must carry an explicit `ValidTo`. A missing, null or "far future" value is rejected with `400 INDIVIDUAL_GRANT_REQUIRES_EXPIRY`. There is no silent default for individual grants (the manifest default of 365 days applies to group/role grants only).
- **Rule 2, maximum durations** (best-practice defaults; a realm profile can only shorten them):

  | Class | Max duration | Default when requested via UI | Four-eyes | Recert class |
  | :--- | :--- | :--- | :--- | :--- |
  | Regular data (no sensitive columns, no PII tag) | 180 days | 90 days | data owner approval (existing) | 365-day campaign + expiry |
  | Sensitive / PII tables, or `Clear` on a sensitive column | 90 days | 30 days | **yes** (owner + second approver; approver ≠ requester ≠ grantee) | 90-day campaign |
  | Individual `Unmasked` access profile | 30 days | 7 days | **yes** | 90-day campaign |
  | Individual privileged functional right (admin, approve, export of sensitive data; `WP-SC-14`) | 7 days (just-in-time elevation) | 1 day | **yes** | 90-day campaign |
  | Break-glass | 72 h | 24 h | **yes** (may be approved after the fact within 24 h) | listed in the next 90-day campaign |
  | Service principal (C8) | 365 days | 180 days | yes for sensitive | 90-day campaign |
  | Customer realm users (`WP-SC-13`) | 90 days regular; `Unmasked` and privileged rights **disabled** by default | 30 days | as above | customer admin attestation |

- **Rule 3, renewal:** an extension is a **new approval** by an owner or delegate, never by the grantee. The new `ValidTo` is at most `now + max` of the class, and four-eyes approval is required when the class requires it. `CONSENTS.renewal_count` and `access_profile_assignments.renewal_count` (new baseline columns) are incremented. After **2 consecutive renewals** the approver is asked to convert the grant into a role/group mapping (a "recurring need → role" hint), and from the **4th** renewal onwards the system **requires** a campaign decision instead of a direct extension. `ExtendConsentExpiryAsync` and the recertification extension path go through `IGrantPolicy` (the current fixed "30 days" suggestion becomes class-dependent).
- **Rule 4, auto-expiry:** expiry is enforced at read time (`Consent.IsActive`, `AccessProfile.IsActive`, assignment `expires_at`). It is bounded in the decision cache TTL by `ComputeDecisionCacheTtl` (existing). An `INDIVIDUAL_GRANT_EXPIRED` Tier-B audit event is written by the campaign/expiry job. A notification is sent 3 days before expiry (existing recertification window).
- **Rule 5, legacy permanent grants** (no upgrade migrations, no data rewrite): at **read time** the effective expiry of an individual grant is clamped to `min(ValidTo, ValidFrom + max(class))`. For access profile assignments with `expires_at IS NULL`, it is clamped to `assigned_at + max(class)`. Grants already past the clamped expiry are inactive (fail closed). A startup report (Information log + `GET /api/v1/governance/grants/individual?clamped=true`) lists the clamped grants with a clear message ("individual grant without expiry/over limit, treated as expiring at …"). They are re-approved through the normal renewal path. Nothing is rewritten.
- **Paths covered:** consent request activation and `ApproveConsentRequestStepAsync`, GraphQL consent mutations, the declarative manifest (an individual grant needs an absolute `validUntil`; re-applying the same manifest never extends it, and a `validUntil` in the past is rejected), `/api/v1/consents/bulk`, access-profile endpoints, virtual-filter profiles, MCP `admin_plan_access`/`admin_apply_access` (`validUntil` is mandatory for user principals), bundled consent requests, `revoke-all` (unaffected) and the extension paths.
- **Files:** new `Application/Governance/Grants/GrantPolicy.cs`, `GrantClassifier.cs` (sensitivity class from catalog metadata, classification and masking mode), options `Governance:IndividualGrants`; changed: about 10 call sites, `ConsentModels.cs` (`IsActive` honours the clamped expiry; coordinate with issue-107, which also edits this file), `AccessProfile.cs`, the 3 repositories (`renewal_count` read/write), `ConsentRecertificationWorkflowService.cs`, 3 baselines + shapes (Lane A).
- **Tests:**
  - A unit matrix over class × realm × duration × four-eyes.
  - Missing expiry → 400 on every path (parameterised over the path list).
  - Renewal never by the grantee; renewal cap and campaign hand-off.
  - Legacy permanent consent or assignment is clamped and becomes inactive after the clamp (SQLite + contract tests).
  - Manifest re-apply never extends.
  - Architecture test: every grant writer calls `IGrantPolicy`.

### WP-PF-04: Batched audit writes (Lane C)

As in rev. 1:
- Multi-row insert (PG `unnest`/COPY, SQL Server TVP, SQLite multi-values), with hashes computed in memory from one tail read.
- Async tail and anchor I/O; the anchor is signed every N=16 batches or T=250 ms.
- Tier-A events are acknowledged only after the anchor is written.
- `AuditGroupCommitSink` is wired up, and middleware and executor events are de-duplicated.

**Tests:** extended `AuditGroupCommitSinkTests`; chain verification after concurrent writes; crash-before-anchor detection.

### WP-SC-03: Persisted access profiles, group/role assignment (Lane A)

- Implement `IAccessProfileRepository` for PG and SQL Server (the tables exist) and set `useRepositoryAccessProfiles: true`.
- Assignments to groups/roles are standing. Assignments to users are **time-bound** individual grants under `WP-SC-16` (`expires_at` is mandatory, and `Unmasked` is at most 30 days with four-eyes approval).
- New `GetProfilesForSubjectsAsync(tenant, groups, roles)`.
- Precedence: `Strict` > `Default` > `Unmasked`. `Unmasked` takes effect only through the three C1 routes: a profile↔group mapping with `ApprovedBy ≠ CreatedBy`, an unexpired four-eyes individual grant, or an active break-glass exception. Ties are broken by `profile_id` ordinal.

**Tests:** contract tests on 3 engines; precedence matrix; unapproved `Unmasked` stays inactive; two-instance consistency; outage → deny.

### WP-PF-06 / -02 / -05 / -07 / -08

Unchanged from rev. 1 (performance analysis H-3, B-3, H-2.2, M-3, H-6):
- `BoundedCache` on **BitFaster.Caching**.
- CDC counters per subject plus a per-tenant cap, in one Lua script; Redis down → reject.
- Pipelined L2 reads.
- A staleness budget of 150 ms for non-sensitive tables, 0 for `IsHighlySensitive` **and 0 for `Delegated*` sources**.
- Basic auth: the HMAC marker (with credential version) is shared through Redis; the IP limiter applies only to failed attempts.
- **PATs belong to a realm** (`local` or the owning realm). A PAT carries the owner's **resolved** roles at use time (re-resolved, never frozen into the token).

---

## 6. Package Details: Wave 2 (identity first)

### WP-SC-13: Identity realm registry (Lane C, re-review)

- **Contracts:**
  - `IdentityRealm`: `Id`; `Kind` (`EntraWorkforce`, `EntraExternalId`, `AzureAdB2C`, `GenericOidc`, `Adfs`, `Kerberos`, `ForwardAuth`, `Basic`, `Pat`); `Issuers`; `MetadataAddress`; `Audiences`; `AllowedClientIds`; `UserKeyClaim` (for example `oid` or `sub`); `UpnClaim`; `TenantBinding` (`Fixed(tenantId)` or `ClaimMapped(claim, allowlist map)`, never the raw `tid`); `GroupSource` (`Token` | `Resolver` | `None`); `ResolverChain` (ordered provider ids); `Provisioning` (`Scim` | `Jit` | `None`); `RbacProfile`; `RecertProfile`; `AllowedGatewayRoles` (admin roles can be excluded, for example for customers); `Delegable` (whether OBO may use this realm's assertions).
  - `IIdentityRealmRegistry.ResolveByIssuer(string issuer)`.
- **Authentication:** one JwtBearer scheme **per JWT realm**, each with its own metadata and keys. The policy scheme routes a Bearer token by its unverified `iss` to exactly one realm scheme, generalising `GatewayAuthSchemes.SelectJwtScheme`. An unknown issuer is rejected. Every token is fully validated (issuer, audience, lifetime, signature) **by the selected realm's scheme only**. B2C/External ID metadata URLs are supported. After validation, an `autheris:realm` claim is stamped from the scheme, never from the token.
- **Compatibility:** `Authentication:EntraId`, `Adfs`, `BasicAuth`, `ForwardAuth` and Kerberos are mapped to implicit realms at startup, so existing configuration keeps working unchanged. The new config section is `Authentication:Realms:<id>`.
- **Tenant binding:** `GetTenantId` uses the realm binding when `autheris:realm` is present. The raw `tid` claim is used only by the legacy implicit realm. A customer realm can never bind to a workforce tenant (startup validation).
- **Files:** new `Domain/Options/Security/IdentityRealmOptions.cs`, `Application/Identity/IdentityRealmRegistry.cs`, `Api/Security/RealmSchemeSelector.cs`; changed `GatewayServiceCollectionExtensions.cs` (auth section), `GatewayAuthSchemes.cs`, `ClaimsNormalizer.cs`, `Sid.cs` (`GetTenantId`), `EntraTokenPolicy.cs`, `GatewayMcpOAuth` (one resource-metadata entry per realm), options validators.
- **Tests:**
  - A realm routing matrix: workforce, customer, AD FS and unknown issuers.
  - A token from realm A presented with realm B's audience is rejected.
  - Issuer spoofing: a token whose `iss` is realm A but which is signed by B's key is rejected.
  - Tenant binding cannot be overridden by the `tid` claim, the `X-Tenant-ID` header or ForwardAuth.
  - A customer realm requesting `GovernanceAdmin` is stripped and denied.
  - Legacy configuration produces identical principals (golden tests).

### WP-SC-05: Role-Resolver and providers (Lane C)

- **Contracts:** `IRoleResolver.ResolveAsync(ResolvedIdentity id, IdentityRealm realm, ct) → RoleResolution { Groups, AppRoles, Attributes, Source, ResolvedAt, ExpiresAt }`. `IRoleProvider` is chained per realm. Results are merged as a union of groups and roles. Attributes are merged first-wins, and conflicts are logged.
- **Providers:**
  - `LdapsRoleProvider` (**optional since rev. 4**, not in the default chain; only for deployments where Autheris can reach the domain controllers): S.DS.Protocols, LDAPS only (StartTLS or 636; certificate validation required; plain LDAP refused). User lookup by `userPrincipalName`, `objectGUID` or `mail` mapped from the Entra user key. Transitive groups via `tokenGroups` or the matching-rule-in-chain OID `1.2.840.113556.1.4.1941`. A read-only service account with its secret in the key vault. Paged queries.
  - `EntraAppRoleProvider`: reads `roles` from the validated token (app roles are signed, small and not subject to overage). Used in Pure Entra mode.
  - `GraphRoleProvider`: Microsoft.Graph `transitiveMemberOf`, `appRoleAssignments` and selected extension attributes, for the workforce directory (Pure Entra) **and for customer directories** (External ID/B2C tenants, each with its own app registration and least-privilege application permissions). Uses `$batch`, honours `Retry-After`, and has a circuit breaker.
  - `ScimStoreRoleProvider`: groups from the SCIM-provisioned store (`WP-SC-07`). **Default group source of the workforce realm in both modes (rev. 4, §1.7).** Membership pushed by Entra provisioning or the on-prem push agent.
  - `StaticMappingRoleProvider`: configuration or `ROLES`/`ROLE_MEMBERS` (`WP-SC-12`), for Basic/PAT and test realms.
- **Caching:** a bounded per-replica cache keyed by `(realm, userKey)`. TTL is 15 min for workforce and 5 min for customers (configurable, max 60). Invalidated by SCIM events and by the lock. A cluster-wide L2 entry (HMAC-framed like the consent L2) prevents a Graph or LDAP stampede across replicas.
- **Failure handling (fail closed):** a provider failure or an open circuit means the result is **no groups or roles from that provider**. A cached result is used only within its TTL, never beyond. There is a metric plus a PII-free log entry.
- **Pipeline:** an `IClaimsTransformation` (extending `EnterpriseClaimsTransformation`) calls the resolver once per request (cached) and injects claims with provenance (C4).
- **Files:** new `Application/Identity/RoleResolver.cs` and `Identity/Providers/*.cs` (5 providers), `Infrastructure/Identity/Ldap/*`, `Infrastructure/Identity/Graph/*`; changed `EnterpriseClaimsTransformation.cs`, `ClaimsNormalizer.cs`, `SecurityContextFactory.cs`, `PrincipalResolverService.cs` (directory-backed search per realm). New dependencies: Microsoft.Graph, Azure.Identity, System.DirectoryServices.Protocols.
- **Tests:**
  - Unit: provider fakes; chain merge; token `groups` ignored when `GroupSource=Resolver`; namespacing; circuit open → no groups; TTL expiry → re-resolve; Graph 429 with retry.
  - `[Scale]`: an OpenLDAP container with nested groups over LDAPS; a Graph mock server.

### WP-SC-07: SCIM receiver, lock, JIT, lifecycle (Lane C endpoints, Lane A store)

- **Endpoint:** `/scim/v2/{realmId}/Users` and `/Groups` (Groups only if the realm enables them), plus `/ServiceProviderConfig`, `/Schemas` and `/ResourceTypes`. This is the RFC 7643/7644 subset that the Entra provisioning service uses: `GET` with `filter=userName eq …` / `externalId eq …`, `POST`, `PATCH` (replace/add/remove, including `active`), `PUT` and `DELETE`.
- **Groups for the workforce realm (rev. 4):** `/Groups` is enabled in the Hybrid mode too. Membership changes (PATCH `members` add/remove) invalidate the resolver cache of every affected user key through the same cluster pub/sub as the lock. Group `externalId` and display name are stored; mappings (`WP-SC-14`) reference the stable `externalId` (or AD `objectGUID`/SID sent by the push agent), never the display name.
- **Authentication:** a dedicated `Scim` scheme per realm. Either a bearer **secret** (key vault, stored as SHA-256, two active secrets for rotation, constant-time compare) or OAuth client credentials from a configured issuer with the `scim.provision` role. It is **never** reachable with user tokens. Optional IP allowlist (Azure `AzureActiveDirectory` service tag). Rate limited.
- **Store (Lane A, baselines + shapes):** `IDENTITY_USERS` (realm, user_key, externalId, userName, active, deactivated_at), `IDENTITY_GROUPS`, `IDENTITY_GROUP_MEMBERS`, `IDENTITY_LOCKS` (realm, user_key, reason, locked_at, source). Indexed by `(realm, user_key)` and `(realm, externalId)`.
- **Deprovisioning lock (event-driven, owner Q5):** `active=false` or `DELETE` triggers, in one transaction:
  - an `IDENTITY_LOCKS` row
  - `ITokenRevocationService.RevokeAsync(subject)`
  - a cluster pub/sub that evicts resolver, decision, OBO token and session caches for that user key
  - `revoke-all` of break-glass exceptions and per-user artefacts
  - a Tier-A audit event

  Authentication middleware checks the lock (bounded cache, 0 s staleness on pub/sub, and a lock-store outage → 503 fail closed). Reactivation needs an explicit `active=true` from SCIM and an audit event.
- **JIT provisioning** (realms with `Provisioning=Jit`, typically customers): a user row is created on first valid login, groups come from the resolver, nothing is granted. Optionally, inactive JIT users are deactivated after N days (default 90).
- **Lifecycle API (not blocked):** `POST /api/v1/governance/subjects/{realm}/{userKey}/revoke-all` and `…/transfer` (transfer of ownership only, never of grants).
- **Owners:** `TABLE_OWNERS` accepts group grantees, with inheritance domain → schema → table and orphan detection on lock events.
- **Tests:**
  - The SCIM compliance suite: Entra's request shapes as fixtures.
  - Authentication: a user token, a wrong realm's secret or an expired secret gets 401.
  - Lock propagation: an in-flight token is rejected on the next request, and two replicas are simulated.
  - Lock-store outage → 503.
  - Idempotent PATCH; JIT creates no grants; replay of the same PATCH is harmless.

### WP-SC-14: Functional rights mapping (Lane B)

- **Contracts:**
  - `FunctionalRight` is the existing `GatewayRole` values plus new feature rights (`Data.Export`, `Data.AsyncJobs`, `Mcp.Use`, `Mcp.Admin`, `Governance.Approve`, …).
  - `IFunctionalRightMapper.Map(RoleResolution, realm) → IReadOnlySet<FunctionalRight>` reads a mapping table `FUNCTIONAL_RIGHT_MAPPINGS(realm, group_or_role, right, approved_by, approved_at, recert_class)` (Lane A schema).
  - Endpoint authorization uses `RequireFunctionalRight(...)`. Existing role policies are re-expressed as rights, with no behaviour change for existing roles.
- **Rules:** four-eyes approval for mappings to privileged rights (admin, approve, unmask, export of sensitive data). A realm's `AllowedGatewayRoles` caps what can be mapped, so customer realms can never receive admin rights. Mappings are recertified per C7.
- **Tests:** golden tests that existing role-based endpoint access is unchanged; customer realm cannot map to admin; an unapproved privileged mapping is inactive; per-endpoint right coverage (architecture test: every `Map*` endpoint has a policy).

### WP-SC-15: Connector-agnostic delegated enforcement + Databricks OBO (Lane B, re-review)

- **Connector capability (rev. 3):** `IDelegationCapability` is implemented per connector. Its properties are **declared** in data-source configuration and validated at startup. Autheris never discovers them at runtime and never queries source catalogs (`sys.security_policies` etc.):
  - `SupportedDelegationKinds` ⊆ {`TokenDelegation` (OBO), `KerberosConstrainedDelegation`, `AssertedIdentity` (service connection + `SESSION_CONTEXT`)}
  - `SourceEnforcesRowSecurity` and `SourceEnforcesMasking`: the operator attests that native policies cover the source (comparable to issue-107's `KeySetCacheAllowed` attestation)
  - `SupportsCorrelationTag` (Databricks query tags; SQL Server `SESSION_CONTEXT(N'autheris_correlation_id')` + `Application Name`)
  - `ConnectionModel` (`Stateless` HTTP for Databricks; `Pooled` for SQL Server)
- **Contract:** `IDelegatedCredentialProvider.GetAsync(DelegationContext ctx, ct)` returns a connector-specific credential (bearer token, SQL access token, Kerberos context or asserted identity). `EnforcementMode` and `TrustedRealms` are generic data-source options. A connector without the declared capability cannot be set to `Delegated*` (validator).


- **Options** (data-source options class shared with issue-107):
  - `EnforcementMode` (`AutherisEnforced` | `Delegated` | `DelegatedDefenceInDepth`, default `AutherisEnforced`)
  - `TrustedRealms` (non-empty for `Delegated*`)
  - `Obo` block: `ClientId`, `ClientCertificateRef` or `ClientSecretRef` (prefer a certificate), `Scopes` (Azure Databricks: `2ff814a6-3304-4ab8-85cb-cd0e6f879c1d/.default`; an assumption confirmed in Phase 1), `AllowLongRunning` (default `false`)
- **Startup validation (fail closed):**
  - `Delegated*` requires `TrustedRealms`, every trusted realm must be `Delegable`, and it requires `KeySetCacheAllowed == false` (issue-107, C3).
  - Profiling, value-profile and semantic-cache features must be disabled or user-keyed for the source.
- **Flow:**
  - The incoming realm token must be an Entra access token whose `aud` is the Autheris API and whose `scp` is the delegated scope. ID tokens and tokens without `scp` are rejected.
  - MSAL.NET `ConfidentialClientApplication.AcquireTokenOnBehalfOf(scopes, new UserAssertion(rawToken))`.
  - MSAL's token cache is in memory only, partitioned by assertion hash, and bounded by `BoundedCache` (`WP-PF-06`). The entry TTL is `min(token exp, assertion exp)`.
  - There is no refresh-token persistence unless `AllowLongRunning` is set and signed off.
  - Errors map as follows: `invalid_grant`/`interaction_required`/`consent_required` → 401 with a claims challenge; other errors → 503. The service principal is **never** used as a fallback.
  - `DatabricksStatementExecutionClient` gets an `IDatabricksCredentialProvider` (`ServicePrincipal` | `OnBehalfOf(user)`). The token cache key becomes `(host, clientId, assertionHash)` for OBO.
- **Policy path:** `TableAccessPolicy` branches by mode (C2).
  - On `Delegated`: coarse gate only (role-based allow/deny, ReBAC, Casbin deny, contract, purpose), with no consent row filter and no Autheris masks. The catalog still hides tables that the gate denies.
  - On `DelegatedDefenceInDepth`: the full Autheris decision plus OBO.
  - The decision cache shape includes the user key (C3). Result-bearing caches are user-keyed or bypassed.
- **Audit:** Autheris records the realm, user key, source, mode and a correlation id, and sends the correlation id as a Databricks query tag so that it can be matched with Unity Catalog audit records. **No token or assertion appears in logs, telemetry, audit or exceptions** (bl-06 redaction plus a unit test).
- **Files:** new `Infrastructure/Databricks/DatabricksCredentialProvider.cs`, `Infrastructure/Identity/Obo/OboTokenService.cs`, `Application/Policy/EnforcementModeGate.cs`; changed `DatabricksStatementExecutionClient.cs`, `DatabricksTokenCache.cs`, `DatabaseOptions.cs` (after 107), options validators, `TableAccessPolicy.cs`, `ConsentCacheService.cs` (key shape), the semantic cache / value profiling / async job services (bypass checks). New dependency: Microsoft.Identity.Client (MIT).
- **Tests:**
  - Validator rejects `KeySetCacheAllowed=true` and empty `TrustedRealms`.
  - A Basic/PAT/customer principal is denied on a `Delegated` source (no service-principal fallback).
  - Two users with identical groups never share a decision or a cached result on a `Delegated` source.
  - OBO failure mapping.
  - Token redaction in logs and spans.
  - An expired assertion is rejected for an async job.
  - Mode change requires four-eyes.
  - `[Scale]`: a Databricks mock (WireMock-style HttpMessageHandler) asserting the `Authorization` header carries the OBO token, never the service-principal token.

### WP-SC-17: SQL Server delegated enforcement (Lane B, re-review with SC-15; rev. 3)

- **Delegation kinds**, in order of preference:
  1. **`TokenDelegation`**: an Entra OBO access token for the scope `https://database.windows.net/.default` (Azure SQL and SQL Server 2022+ with Entra authentication via Azure Arc; to be confirmed in Phase 1), set on `SqlConnection.AccessToken` by the data-source connection factory (`SqlConnectionFactory.cs`).
  2. **`AssertedIdentity`**: the existing service connection, plus `sp_set_session_context` with `autheris_user_key`/`realm`, set **read-only** (`@read_only = 1`), and native security policies (`CREATE SECURITY POLICY` with predicates on `SESSION_CONTEXT`). `DbSessionContextInitializer` already sets a user SID today. This kind trusts Autheris as the identity asserter, so `SEC-SC-25` restricts it to sources whose session context is set read-only and that grant the service login no other path.
  3. **`KerberosConstrainedDelegation`** (on-prem, optional **spike**): S4U2Self/S4U2Proxy for the AD account mapped from the Entra user key. This builds on the container Windows authentication from #102 / F-AUTH-04 (`UseManagedNtlm`, gss-ntlmssp), but needs gssproxy/MIT krb5 S4U support on Linux and an AD delegation configuration. It is not in the default scope and is estimated separately.
- **Source-side RLS:** native security policies on `USER_NAME()`/`SUSER_SNAME()`/`ORIGINAL_LOGIN()` (token or Kerberos kinds) or `SESSION_CONTEXT(N'autheris_user_key')` (asserted kind). Dynamic data masking or column permissions per Entra group (Entra groups are SQL principals). Coverage is declared, never discovered (§1.6).
- **Connection pooling at 5,000 users:**
  - With `TokenDelegation` or KCD, each identity needs its own physical connection. SqlClient pools by connection string plus credential/access token. This means one pool per user and token, and new pools on every token refresh, which fragments the pools.
  - Mitigations: a **per-user pool partition** (an explicit partition key in the connection string, never a shared `AccessTokenCallback` pool across users); `Max Pool Size` per partition of 2; `Min Pool Size=0`; an LRU cap on live partitions per replica (default 300) with `SqlConnection.ClearPool` on eviction; a global semaphore on concurrent delegated connections per source (default 100 per replica, sized against the server's max connections divided by the number of replicas).
  - With `AssertedIdentity`, the shared pool stays (no fragmentation), and the session context is reset on every checkout (as today).
- **Token lifetime per connection:** SQL Server validates the token **at login only**. A pooled connection therefore stays authenticated after the token expires. The physical connection lifetime is set to at most the token expiry (`Connection Lifetime` / explicit removal), so that no connection outlives its token. A SCIM lock or revocation clears the user's partition (`ClearPool`) through the lock pub/sub (`WP-SC-07`).
- **Plan cache on the source:** Autheris emits schema-qualified names, so plans are shared across logins. Security-policy predicates are inline TVFs evaluated at run time, so per-user plans are not needed. Risks: per-user default schemas (avoided by qualification) and `SESSION_CONTEXT` values used as literals (avoided: the predicate reads them as functions). Load test LT-9 (new) measures source plan-cache growth and login rate.
- **Caching and #107:** the same rules as C3 apply. There is no cross-user sharing of decisions or results on delegated sources, including `AssertedIdentity`, because the source filters per session user. `KeySetCacheAllowed=true` is rejected at startup (SEC-RF107-01 (b)/(c)).
- **Audit correlation:** the correlation id goes into `SESSION_CONTEXT(N'autheris_correlation_id')` (read-only) and `Application Name=Autheris`. SQL Server Audit records the delegated login (token/KCD) or the session context (asserted). Autheris audits the realm, user key, source, kind and correlation id.
- **Files:** new `Infrastructure/Persistence/Delegation/SqlServerDelegatedCredentialProvider.cs`, `SqlServerPoolPartitioner.cs`, `SqlServerDelegationCapability.cs`; changed `SqlConnectionFactory.cs`, `DbSessionContextInitializer.cs` (read-only session keys for the asserted kind), data-source options validators, `ConnectionCleanupService`/lock subscriber.
- **Tests:**
  - Unit: the validator rejects `Delegated*` without the declared capability, `KeySetCacheAllowed=true`, and an empty `TrustedRealms`.
  - The pool partition key differs per user, never shared; LRU eviction clears the pool; connection lifetime ≤ token expiry; a lock clears the partition.
  - `[Scale]` SQL Server container: a security policy on `SESSION_CONTEXT` with a read-only key; two users never see each other's rows through pooled connections; an attempt to overwrite a read-only key fails.
  - An Azure SQL token test only in a manual/nightly lane (requires an Entra tenant).

### WP-SC-08: Recertification campaigns (Lane A)

- **Cadence (owner Q5):** 90 days for the privileged class (admin and governance rights, `Unmasked`/break-glass usage, individual grants of the sensitive/unmasked/privileged classes, sensitive policy mappings, `Delegated*` mode settings, service-principal grantees). 365 days for regular business-role mappings. Event-driven items come from the SCIM lock and from mover events (group change on a privileged mapping).
- **Scope:** per C7. Unanswered items are revoked after their deadline (default 14 days, configurable). Nothing is extended automatically.
- **Customer realm defaults (best practice):** 365-day attestation by the **customer's own administrator** for business roles, and 90 days for any privileged customer role. JIT users inactive for 90 days are deactivated. Customer access ends with the contract (realm disable → lock-all).
- **Other changes (rev. 1):** typed validity timestamps, `CONSENTS_HISTORY`, `RECERT_CAMPAIGNS`/`RECERT_ITEMS`, and the bounded expiry query.

### WP-SC-06, -04, -10, -09, -12 and WP-PF-09..15

- These follow rev. 1, with these changes:
  - Standing grantees are group/role. Individual grants are time-bound (`WP-SC-16`).
  - Group identifiers are realm-namespaced (C4).
  - On `Delegated*` sources `WP-SC-06` acts as the coarse gate only.
  - `WP-SC-04` applies only to Autheris-enforced sources, and its attributes come from the realm resolver chain.
  - `WP-SC-10` reports per realm (group → right/policy → table, membership from the resolver or SCIM store).
  - `WP-PF-09` shape sharing is disabled on `Delegated*` sources.
  - `WP-PF-15` evaluates CDC per user on `Delegated*` sources.
- A detailed package plan is written when each package enters Phase 4.

---

## 7. Open-Source Decisions (consolidated, rev. 2)

| Adopt now | Adopt with Wave 2 | Borrow (concepts / MIT code with NOTICE) | Reject |
| :--- | :--- | :--- | :--- |
| BitFaster.Caching (MIT) in `WP-PF-06`/`-09`; HybridCache (MIT) pilot in `WP-PF-09` | **Microsoft.Identity.Client** (MSAL, MIT) for OBO in `WP-SC-15`; **Microsoft.Graph** + **Azure.Identity** (MIT) in `WP-SC-05`; **System.DirectoryServices.Protocols** (MIT) for LDAPS in `WP-SC-05`; **Microsoft.Identity.Web** (MIT) optional for multi-scheme helpers in `WP-SC-13`; OpenFGA (Apache-2.0) only if Q6 changes | Ranger/Unity Catalog policy model (`WP-SC-06`); SCIM schemas and request handling from MIT `AzureAD/SCIMReferenceCode` (`WP-SC-07`); Zanzibar (`WP-PF-11`); RFC 6962 (`WP-PF-13`) | OPA/Cedar/Cerbos as runtime PDP; Permify (AGPL); Rsk SCIM (commercial); Bitwarden SCIM (AGPL/proprietary); NBomber (commercial); Duende IdentityServer (commercial licence; not needed, because Autheris is a relying party, not an IdP) |

---

## 8. Security Review (Phase 3, rev. 2)

**Reviewer:** Security Expert (csharp-security-expert role). **Scope:** all packages, including the new identity packages.
**Method:** threat review of each contract against fail closed, tenant and realm isolation, segregation of duties, token handling and audit integrity.

### 8.1 Binding Conditions

Conditions rewritten in rev. 2 are marked **(rev. 2)**. New conditions are **(new)**.

| ID | Applies to | Condition |
| :--- | :--- | :--- |
| `SEC-SC-01` | SC-01 | The SQL subject filter is semantically identical to the C# filter: case-insensitive SIDs, role and alias consents, future-dated deny consents for the TTL bound. A tenant predicate is mandatory on request paths (architecture test). |
| `SEC-SC-02` | SC-01/-03/-06/-07/-08/-14 | Schema changes only in the `0001` baselines, with regenerated shape snapshots and no startup rewrites. The verifier fails closed on a missing index, table or column. |
| `SEC-SC-03` | SC-02, SC-09 | Atomic apply, prune limited to `ManagedBy` and tenant, dry-run default in v2, Tier-A audit, governance-admin caller, no `GITOPS-SYNC` fallback for interactive callers. |
| `SEC-SC-04` **(rev. 3)** | SC-03, SC-16 | `Unmasked` takes effect only through the three C1 routes: (a) a profile ↔ privileged group/role mapping approved by a second administrator (`ApprovedBy ≠ CreatedBy`, both non-null), 90-day recertification; (b) a **time-bound individual grant**, ≤ 30 days, four-eyes (approver ≠ requester ≠ grantee, owner of every matched table or a delegate), justification + purpose, Tier-A audit, never self-renewed; (c) **break-glass**, ≤ 72 h, four-eyes (may be approved after the fact within 24 h), Tier-A audit. Every route appears in the next 90-day campaign. Customer realms: (b) is disabled by default. Profiles other than `Unmasked` can only tighten. Precedence is most-restrictive-wins. A store outage denies. |
| `SEC-SC-05` | PF-01/-05/-07 | Redis errors or partial results mark decisions stale. Keys include the tenant. The staleness budget is 0 for sensitive tables **and for `Delegated*` sources** and ≤ 250 ms otherwise. Revocations and denies take effect within the budget. |
| `SEC-SC-06` | PF-02 | Atomic Lua counters, leak-safe heartbeats, Redis down → reject, per-subject cap ≤ tenant cap. |
| `SEC-SC-07` | PF-03 | Compile failure: allow rule → no match, deny rule → match. Sandboxed interpreter, regex timeouts with the same polarity, atomic snapshot swap. |
| `SEC-SC-08` | PF-04, PF-13 | Chain order equals commit order. Tier-A is acknowledged only after commit and anchor. Bounded anchor deferral, with the unanchored tail detected. DENY events are never de-duplicated away. PF-13 needs a dedicated review before Phase 4. |
| `SEC-SC-09` **(rev. 2)** | PF-06, PF-09, SC-15 | Eviction only produces misses, and no decision is cached without epoch and tenant. A shape-shared entry is used only when no input references the user, aliases, IP or unshared attributes, **and never on `Delegated*` sources**, where the key always includes the realm-qualified user key. A property-based separation test is required. |
| `SEC-SC-10` | PF-08 | The Basic marker is a keyed HMAC with a credential version, never derived from the plaintext alone. It is invalidated on password change, lock, disablement and revocation. Failures hit the limiter. PATs are hashed, scoped, expiring and audited, **bound to a realm, and re-resolve roles at use (no frozen privileges)**. |
| `SEC-SC-11` **(rev. 2)** | SC-04 | Attribute values are always bound parameters. Attributes come only from the realm's resolver chain or verified token claims, never from request headers, query or body. Missing, empty or over-cardinality values are fail-closed. The values are part of the cache shape. `AstSecurityVisitor.cs` stays unchanged. |
| `SEC-SC-12` **(rev. 2; applies only if the optional LDAPS provider is used, rev. 4)** | SC-05 | **LDAPS only** (certificate validation on, no simple bind over plaintext, no `ldap://`). Read-only least-privilege bind account. Graph application permissions are least privilege per directory (`User.Read.All`, `GroupMember.Read.All`, `AppRoleAssignment.Read.All` read only; never `Directory.ReadWrite.*`), with secrets or certificates from the key vault. A provider failure or open circuit means **no** roles or groups from that provider; cached results are never used past their TTL (max 60 min, default 15 workforce / 5 customer). Token-borne `groups`/`roles` are ignored when `GroupSource ≠ Token`. Resolved identifiers are realm-namespaced. LDAP filters escape user input (RFC 4515), and lookup keys come from validated claims only. Logs are free of PII. |
| `SEC-SC-13` | SC-06 | Dry-run stored with the approval. Four-eyes for policies matching sensitive objects or granting Clear. Deny overrides. H-11 blending preserved. Unclassified columns are masked. Policy-set epoch on selector/tag change. **Group/role grantees only**. On `Delegated*` sources the policy acts as a gate only and never relaxes source-side restrictions. |
| `SEC-SC-14` **(rev. 2, extended rev. 4)** | SC-07 | **(rev. 4)** Because the SCIM credential can change group membership and thereby rights, it is treated as privileged: one credential per push source (Entra provisioning, push agent) with its own audit identity; mTLS or bearer secret plus an IP allowlist on the LAN egress NAT or the Azure service tag; every group create/delete and membership change is audited (Tier-A for groups mapped to privileged rights); a membership change never grants a right on its own, only through a four-eyes-approved mapping (`WP-SC-14`); a mass change (configurable, e.g. > 10 % of members of a privileged group in one cycle) is held and alerted. **SCIM authentication is isolated:** a dedicated scheme per realm; user tokens are rejected; the secret is stored hashed with constant-time compare and supports two-secret rotation (or client credentials with a dedicated role); rate limits and an optional source allowlist; every operation is Tier-A audited. SCIM can create, update and deactivate identities and groups but **never grants permissions** (it never writes consents, policies or functional-right mappings). **Deprovisioning lock:** effective on the next request on every replica (pub/sub plus a lock check in the auth middleware); a lock-store outage → 503; reactivation needs an explicit SCIM `active=true` and an audit event. A SCIM call in realm A cannot touch realm B. JIT provisioning creates identities only. |
| `SEC-SC-15` | SC-08 | Unanswered items are revoked, never extended. Decisions only by authorised owners or delegates, never by the grantee. Archival keeps audit references verifiable. **(rev. 2)** Cadence: 90 days privileged / 365 days regular, plus event-driven items from SCIM locks and privileged mover events. |
| `SEC-SC-16` | SC-10, PF-10 | Admin and entitlement queries for GovernanceAdmin (tenant) and owners (own tables), keyset paging, audited. Per-tenant catalog snapshot. **(rev. 2)** Reports never cross realms or tenants; customer admins see only their own realm. |
| `SEC-SC-17` | PF-11, SC-12 | Equivalence proof before cut-over. Database-stored Casbin rules are four-eyes and audited. |
| `SEC-SC-18` | PF-12, PF-14 | Bound array parameters, fingerprint in the template key, masks applied before the first byte leaves, no change to `AstSecurityVisitor.cs`. |
| `SEC-SC-19` | all | MIT, Apache-2.0 or BSD only, pinned, listed in the notices. Cache, backplane, directory, LDAPS, Graph, SCIM-store or OBO outages never yield Allow. |
| `SEC-SC-20` **(rev. 3)** | SC-16 | Every grant write path (list in `WP-SC-16`) calls `IGrantPolicy` (architecture test). Individual grants without an explicit expiry, or beyond the class maximum, are rejected (400) on every path. Realm profiles can only shorten maxima. Renewals are new approvals, never by the grantee, capped per class, with campaign hand-off from the 4th renewal. Legacy grants without an expiry or over the limit are **clamped at read time** (`min(ValidTo, ValidFrom + max)`), never treated as permanent and never rewritten. They are reported at startup. A clamp or classification error denies. Manifests never extend individual grants on re-apply. |
| `SEC-SC-21` **(new)** | SC-13 | **Cross-issuer confusion:** routing by unverified `iss` selects **one** realm scheme, and that scheme alone validates signature, issuer, audience, lifetime and (for Entra) token type and version. An unknown `iss` is rejected. Audiences are realm-specific, and a token valid for realm A is never accepted by realm B (test). The `autheris:realm` claim is stamped by the server and any token-supplied value is stripped. **Tenant isolation:** the tenant comes from the realm binding only, never from `tid`, headers or ForwardAuth for non-legacy realms. A customer realm can never bind to a workforce tenant (startup validation), and `AllowedGatewayRoles` caps admin rights (customers: none). B2C/External ID metadata must be HTTPS. Legacy implicit realms produce identical principals (golden tests). |
| `SEC-SC-22` **(new)** | SC-14 | Mappings to privileged rights need four-eyes and 90-day recertification. Customer realms cannot map to admin, governance, unmask or approval rights. Every endpoint has an explicit right or policy (architecture test). An unknown right denies. Re-expressing today's roles as rights leaves existing access unchanged (golden tests). |
| `SEC-SC-23` **(rev. 3)** | SC-15, SC-17 | **OBO token handling:** the incoming token must be an access token for the Autheris API (`aud`) with a delegated `scp`; ID tokens and app-only tokens (no `scp`) are rejected. Only realms listed in `TrustedRealms` with `Delegable=true` are used. The OBO client authenticates with a certificate (preferred) or a key-vault secret. The MSAL cache is in memory, bounded, partitioned by assertion hash, with TTL ≤ token expiry; it is never written to Redis or disk unless long-running OBO is explicitly signed off. **Tokens, assertions and authorization headers never appear in logs, traces, metrics, audit, exceptions or problem details** (bl-06 redaction plus a test). There is no service-principal fallback for delegated requests. `KeySetCacheAllowed=true` and user-independent result caches are rejected for `Delegated*` sources at startup. Mode changes are four-eyes and Tier-A audited. The correlation tag (Databricks query tag / SQL Server session context) carries only a correlation id. Delegation is allowed only for connectors whose capability is **declared** and validated at startup. A connector without a declared capability cannot be set to `Delegated*`. |
| `SEC-SC-25` **(new, rev. 3)** | SC-17 | **SQL Server delegation:** (1) pooled connections are partitioned per realm-qualified user key; a physical connection is never reused for another identity, and there is no shared `AccessTokenCallback` pool across users. (2) Connection lifetime ≤ token expiry; a lock or revocation clears the user's partition. (3) `AssertedIdentity` sets session keys with `@read_only = 1`, resets them on every checkout, and is allowed only when the operator declares that the service login has no access path that bypasses the security policy. (4) A global per-source cap on delegated connections; exhaustion → 503, never a fallback to the shared identity. (5) KCD is not enabled without a separate Phase-3 review. (6) `KeySetCacheAllowed=true` is rejected, and user-independent result caches are rejected. |
| `SEC-SC-24` **(new)** | SC-05, SC-13 (Graph for customers) | **Graph rate limits:** per-directory client with `$batch` (≤ 20), `Retry-After` honoured, exponential backoff, and a circuit breaker (fail closed per `SEC-SC-12`). Lookups are cached per `(realm, userKey)` and stampede-protected (single-flight plus HMAC-framed L2). One customer directory's throttling or outage never affects another realm (isolated clients and breakers). Graph credentials per customer directory are separate (no shared multi-tenant secret across customers unless an explicit multi-tenant app is approved). |

### 8.2 Mandatory Tests (minimum)

1. `ConsentQueryEquivalenceTests` on 3 engines (SEC-SC-01).
2. Shape snapshots regenerated, and the verifier fails on missing objects (SEC-SC-02).
3. Manifest atomicity fault injection and role idempotency (SEC-SC-03).
4. Access-profile matrix: an unapproved `Unmasked` group mapping is inactive; an individual `Unmasked` grant without four-eyes or over 30 days is rejected; break-glass expiry (SEC-SC-04).
4a. **Individual-grant policy:** missing expiry → 400 on every grant path; class maxima; renewal by the grantee rejected; legacy permanent consents and assignments clamped and inactive after the clamp; manifest re-apply never extends (SEC-SC-20).
5. Redis fault injection for epochs, and a zero budget on `Delegated*` (SEC-SC-05).
6. Compiled sub_rule polarity and sandbox (SEC-SC-07).
7. Audit chain after concurrent batched writes, and crash-before-anchor (SEC-SC-08).
8. Property-based cache-shape separation, **including a `Delegated*` no-sharing case** (SEC-SC-09).
9. `USER_ATTRIBUTE` injection corpus and polarity (SEC-SC-11).
10. Architecture tests: tenant-less overloads unused; `AstSecurityVisitor.cs` unchanged; every grant write path calls the guard; every endpoint has a right/policy (SEC-SC-01/-11/-20/-22).
11. **Realm routing and confusion matrix** (cross-audience, cross-key, unknown issuer, `tid`/header tenant override attempts, customer admin-role stripping, legacy golden principals) (SEC-SC-21).
12. **LDAPS enforcement** (plain `ldap://` refused, invalid certificate refused, RFC 4515 escaping) and **resolver fail-closed** (circuit open, TTL expiry) (SEC-SC-12).
13. **SCIM auth isolation and lock propagation** (user token → 401, cross-realm → 404/403, lock effective on the next request across two simulated replicas, lock-store outage → 503) (SEC-SC-14).
14. **OBO:** service-principal fallback impossible; non-delegable principal denied; token redaction in logs and spans; startup rejection of `KeySetCacheAllowed=true` on `Delegated*`; per-user result caches on `Delegated*` (SEC-SC-23).
15. **Graph throttling isolation** between two customer realms (SEC-SC-24).
16. **SQL Server delegation:** per-user pool partitions, never shared; connection lifetime ≤ token expiry; a lock clears the partition; read-only session context cannot be overwritten; cap exhaustion → 503; validator rejects an undeclared capability and `KeySetCacheAllowed=true` (SEC-SC-25, SEC-SC-23).

### 8.3 Verdict (rev. 2)

- **Wave 1** (`WP-SC-01..03`, `WP-SC-16`, `WP-PF-01..08`): **APPROVED WITH CONDITIONS** (SEC-SC-01..10, -19, -20; tests 1–8, 4a and 10).
- **Wave 2:**
  - `WP-SC-05`, `WP-SC-07`, `WP-SC-08`, `WP-SC-14`, `WP-SC-04`, `WP-SC-10`, `WP-SC-09`, `WP-PF-09..12`, `WP-PF-14`, `WP-PF-15`: approved with conditions; contracts as specified here.
  - **Dedicated Phase-3 re-review of the detailed package plan before Phase 4:** `WP-SC-13` (realm registry: authentication core change), `WP-SC-15` + `WP-SC-17` (delegated enforcement: new trust boundaries to Databricks and SQL Server; KCD needs its own review), `WP-SC-06` (Access Policy: unchanged from rev. 1), `WP-PF-13` (partitioned audit chains).

---

## 9. LOC Estimates (owner requirement before implementation)

**Method and assumptions:**
- LOC means non-blank, non-comment C# and SQL lines (XML doc comments excluded). Generated shape JSON files are **excluded** (they are regenerated, roughly 50–150 lines per engine per schema change).
- Estimates come from the file lists above, the size of comparable existing code (for example `PostgreSqlGovernanceRepository.Consent.cs` per-method sizes, `DatabricksStatementExecutionClient.cs` 561 lines, `ClaimsNormalizer.cs` 212, `ConsentRecertificationWorkflowService.cs` 194, `GovernanceEndpoints.Access.cs` 463) and the repository's test-to-production ratio of about 1.2–1.5×.
- Three-engine persistence work is counted for all three repositories.
- Accuracy is ±30%.
- "Files new / changed" counts production and test files together. Feature docs (Phase 6) are not included.

### 9.1 Wave 1

| WP | Prod LOC | Test LOC | Files new | Files changed | Notes |
| :--- | ---: | ---: | ---: | ---: | :--- |
| SC-01 | 350 | 450 | 2 | 9 | 3 × DDL ≈ 40, 3 × query ≈ 70, write path ≈ 20 |
| PF-01 | 60 | 150 | 1 | 1 | |
| SC-02 | 180 | 250 | 1 | 5 | 3 repository methods + service |
| PF-03 | 300 | 400 | 3 | 4 | |
| SC-16 (rev. 3) | 550 | 700 | 4 | 14 | policy + classifier ≈ 250, ~10 call sites, clamp in 3 repos, `renewal_count` |
| PF-04 | 600 | 700 | 2 | 8 | 3 engines × ≈ 150 |
| SC-03 | 750 | 900 | 4 | 6 | 2 repositories × ≈ 220, break-glass ≈ 80, time-bound user assignment ≈ 50 |
| PF-06 | 250 | 300 | 2 | 5 | + BitFaster package |
| PF-02 | 150 | 200 | 1 | 3 | incl. Lua |
| PF-05 | 60 | 100 | 1 | 1 | |
| PF-07 | 150 | 200 | 1 | 4 | |
| PF-08 | 450 | 600 | 5 | 6 | PAT store ≈ 200 |
| **Wave 1 total** | **≈ 3,850** | **≈ 4,950** | **27** | **66** | about 8,800 LOC; 3 lanes ≈ 2–3 weeks |

### 9.2 Wave 2

| WP | Prod LOC | Test LOC | Files new | Files changed | Notes |
| :--- | ---: | ---: | ---: | ---: | :--- |
| SC-13 realm registry | 1,200 | 1,600 | 9 | 9 | multi-scheme setup, compatibility mapping, validators |
| SC-05 Role-Resolver + providers | 1,200 | 1,400 | 13 | 5 | rev. 4: LDAPS provider optional and not estimated (≈ 300 / 300 if built); Graph ≈ 350, chain/cache ≈ 300 |
| SC-07 SCIM + lock + JIT + lifecycle + owners | 1,750 | 2,100 | 15 | 10 | SCIM ≈ 700, store 3 engines ≈ 450, lock ≈ 200; rev. 4: workforce groups, cache invalidation per membership change, mass-change hold ≈ 150 |
| SC-18 on-prem push agent (optional, rev. 4) | 600 | 500 | 6 | 0 | Windows worker service: AD transitive groups → SCIM client; only if Entra provisioning is not sufficient |
| SC-14 functional rights | 700 | 900 | 7 | 12 | many endpoint policy edits |
| SC-15 delegated enforcement (connector-agnostic) + Databricks OBO | 1,050 | 1,300 | 10 | 10 | capability layer ≈ 100, MSAL ≈ 250, policy branch ≈ 200, cache bypasses ≈ 200 |
| SC-17 SQL Server delegation (token + asserted identity) | 800 | 1,000 | 5 | 6 | pool partitioner ≈ 200, credential provider ≈ 200; **KCD spike excluded** (est. 600 / 700 if pursued) |
| PF-09 fingerprint keys, sharded cache | 700 | 900 | 3 | 6 | |
| SC-06 Access Policy | 2,500 | 3,000 | 14 | 8 | L |
| SC-08 recert campaigns + typed timestamps (+ individual-grant items) | 1,600 | 1,600 | 8 | 12 | timestamp change touches all consent reads on 3 engines |
| PF-10 CatalogSnapshot | 600 | 700 | 3 | 12 | many call sites |
| SC-04 attribute row filters | 400 | 600 | 2 | 4 | reduced scope |
| SC-10 admin query + entitlement report | 900 | 1,000 | 6 | 5 | |
| SC-09 manifest v2 | 800 | 900 | 4 | 3 | |
| PF-11 indexed ReBAC | 700 | 900 | 3 | 3 | |
| PF-12 compile path | 900 | 1,100 | 3 | 6 | no `AstSecurityVisitor.cs` change |
| PF-15 CDC shapes, async jobs | 700 | 800 | 3 | 6 | after issue-99 |
| SC-12 ABAC in store, roles | 600 | 800 | 4 | 5 | |
| PF-13 partitioned audit chains | 1,200 | 1,500 | 4 | 6 | after re-review |
| PF-14 streaming | 1,800 | 2,000 | 6 | 14 | |
| **Wave 2 total** | **≈ 20,100** | **≈ 24,100** | **122** | **142** | about 44,200 LOC (rev. 4; excludes optional SC-18) |
| of which identity + delegation (SC-13/05/07/14/15/17) | 6,700 | 8,300 | 59 | 52 | the owner-driven core (rev. 4) |

**Overall (rev. 4):** about 23,950 production LOC and about 29,050 test LOC (about 53,000 in total, excluding the optional push agent SC-18). With three Sonnet lanes, Wave 1 takes about 2–3 weeks and the
identity core of Wave 2 about 5–6 weeks (including the two re-reviews). The rest of Wave 2 adds about 6–8 weeks and can be cut
according to measured need (load test LT-1..8).

---

## Appendix A: Owner Decisions BL-03, German Original (verbatim, 2026-10-16)

"BL-03 Backend" means Autheris.

| Frage | Architekturentscheidung | Technische Umsetzung |
|---|---|---|
| 1. Welcher IdP? | Microsoft Entra ID (OIDC / SAML 2.0) | Einheitlicher Cloud-IdP für Authentifizierung, MFA und Conditional Access. Liefert im Token einen eindeutigen Identifikations-Key (UPN, Mail, ObjectGUID), aber keine aufgeblähten Gruppen. |
| 2. Ist SCIM in Ordnung? | Ja, als Kernstandard (differenziert nach Modus) | Entra -> Databricks: Volles SCIM (User & Groups) für Unity Catalog. Entra -> BL-03: User-Provisioning aktiv. Gruppen-Provisioning deaktiviert (im Hybrid-Fall) bzw. aktiv (im Pure-Cloud-Fall). |
| 3. Wo liegen die Benutzerattribute? | 3-Schichten-Modell (Separation of Concerns) | 1. Identität: Entra ID (Name, UPN, Mail). 2. App-Funktionsrechte: Lokales AD DS / LDAP (Hybrid) oder Entra App Roles (Cloud). 3. Datenzugriffsrechte: Databricks Unity Catalog (RLS-Mapping-Tabellen). |
| 4. Dauerhafte Einzelrechte verbieten? | Ja, striktes Verbot (Strict RBAC) | Keine Berechtigungsvergabe auf User-ID-Ebene. Berechtigungen basieren auf Rollen/Gruppen. RLS in Databricks nutzt dynamische Filter (current_user(), Mapping-Tabellen). |
| 5. Rezertifizierungs-Turnus? | Risikobasiert gestaffelt | Alle 90 Tage: Privilegierte Rollen, Admin-Rechte, sensitive RLS-Zuordnungen. Alle 365 Tage: Reguläre Fachrollen. Event-driven: Sofortiger Sperr-Trigger via SCIM bei Austritt. |

Datenfluss:
1. Authentifizierung (Entra ID): Login via OIDC/SAML; Token enthält nur Identitäts-Claims; kein Group-Overage-Problem.
2. Identitäts-Lifecycle (SCIM): Entra ID synchronisiert Benutzerkonten per SCIM nach BL-03 (Autheris) und Databricks.
3. Funktionsberechtigungen (lokales AD): Autheris liest über den Benutzer-Key via LDAPS die lokalen AD-Gruppen und mappt sie auf UI-/Fachfunktionen (z. B. "Darf Exportieren").
4. Datenberechtigungen & RLS (Databricks): Autheris reicht das Benutzer-Token via OAuth On-Behalf-Of an Databricks weiter. Databricks wertet current_user() über native Row-Level Security und Column Masking aus. Vollständiger Audit-Trail im Unity Catalog.

Modularer Role-Resolver (Product Readiness):
- Betriebsmodus "Konzern / Hybrid" (aktueller Fall): Identity Entra ID via SCIM (User-only); App-Rollen lokaler AD-/LDAP-Connector; Daten-RLS Databricks via Token-Delegation.
- Betriebsmodus "Pure Entra ID" (Zukunft / SaaS): Identity Entra ID via SCIM (User + Groups); App-Rollen Entra App Roles oder Entra-Sicherheitsgruppen aus SCIM-Payload bzw. Graph API; Daten-RLS identisch über Entra-Identität in Databricks.

Follow-up (owner, 2026-10-16): "auth ist aber sehr flexibel. wir haben auch oauth für endkunden das jedoch auch wieder irgendwie auf der graph-api basiert".

Correction to Q4 (owner, 2026-10-16): "einzelrechte müssen möglich sein - aber eben nur zeitlich beschränkt".

Delegation scope (owner, 2026-10-16): "rechte/obo primär bei databricks - jedoch auch richtung sql server denkbar".

Group transport (owner, 2026-10-16, rev. 4): "die lokalen gruppen hätte ich per externem dienst an autheris geschickt. dieser muss evtl. bei uns im lan unter windows laufen um zugriff zu erhalten. scim ist daher als standard eventuell gut".
