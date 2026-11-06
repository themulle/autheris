# ADR-020: Unified Authorization Model (One Grant Model, One Decision Point)

## Status

Accepted by the owner on 2026-11-05 (OD-30). This ADR revises OD-7 (ReBAC enforcement; advisory GHSA-78j7-3gh6-7cqx, details in the private advisory) and replaces SEC-SIMPL-85; implementation in WP-SIMPL-46. Amended by OD-32..OD-35 (access-profile assignments materialize grant records; owner grant records; independent write grants; write enforcement in the shared pipeline) and by OD-38..OD-43 (named row policies at two levels, dynamic object coverage, per-cell masking, every contributing grant recorded, transparency; see "Amendment 2026-11-06" below; implementation in WP-SIMPL-48) and by OD-44 (virtual views with definer semantics, WP-SIMPL-49).

## Context

Autheris evaluates data access through six overlapping mechanisms. The owner reports that role-based and relationship-based authorization have conflicted in practice. Binding inputs: OD-22 (subject = user or AD group; objects = tables, views, procedures; controls = row-level security by IN list or subquery, PII masking with separately requested clear access, column invisibility; data-owner consent), OD-26 (explicit write grants operation x column set x row scope), OD-21 (authn/authz is core, both direct and upstream-header modes; Envoy ext_authz stays), OD-27 (effective groups = token groups plus SCIM-provisioned AD groups), OD-9 (global operations only for canonical `ClusterAdmin`).

### Inventory (verified on branch `docs/simplification-plans`, worktree `simpl-od19`, commit `db45aa80`)

| Mechanism | Where evaluated | Decides | Configured/used today |
|---|---|---|---|
| Consent grants (`Consent`, `ConsentColumnRule`, `ConsentRowFilter`; `Domain/Model/ConsentModels.cs:76`) | `ConsentResolutionService.ResolveAccess` (`Application/Services/ConsentResolutionService.cs:31-80`) via `TableAccessPolicy.DecideAsync` | table allow/deny, column level (Clear/Masked/Deny), row filters (literal, IN, subquery, `USER_ATTRIBUTE` values) | The data-owner workflow; 69 test files named, 493 mention it. Durable in the governance DB. |
| Access profiles incl. break-glass assignments (`Domain/Model/AccessProfile.cs`, `AccessProfileAssignment.cs:19,78`) | `TableAccessPolicy.cs:300-330` | masking mode and profile row filter for already-granted tables ("masking exemption only", SR-M-07); assignees user/group/role | 13 test files named. Durable. |
| Virtual filters (mandatory row filters) | `TableAccessPolicy.cs:336-378` (`IMandatoryRowFilterResolver`) | restrictive row predicate ANDed with the consent decision; can deny | 8,310 src lines, 80 test files named. |
| ABAC caller attributes (`CallerAttributeResolver`, `Application/Identity/Attributes/`) | bound into virtual filters (`TableAccessPolicy.cs:363-365`), consent row filters, `StreamRlsPolicyEnforcer`, Casbin subject attributes | values for row predicates; unresolved = deny | 3,049 src lines, 23 test files named. |
| ReBAC (`ZanzibarRebacEvaluator`, `RebacTableGate`, `RebacEndpoints`, `RebacEndpointFilter`) | `TableAccessPolicy.cs:283-297` (relation `can_query`, user SID only, `RebacTableGate.cs:18,70`); `CatalogVisibilityService.cs:329-375` (hides tables); `StreamRlsPolicyEnforcer.cs:258-264`; `ArrowExportEndpoints.cs:48,236-275` (relation `viewer`); `IcebergRestCatalogFederationService.cs:118-124,328-330` | table-level yes/no only; no columns, rows or masking | `RebacOptions.Enabled` defaults `true`, `EnforceOnQueryPaths`/`EnforceOnStreaming` default `false` (`Domain/Options/Security/RlsOptions.cs:19,25,38`). Tuples live in `InMemoryRebacStore` unless Redis is configured (`GatewaySecurityServiceExtensions.cs:87-101`). No shipped appsettings/Helm/YAML file outside tests sets a ReBAC key; only dev seed tuples (`RebacSeedHostedService.cs:56-62`). 2,334 src lines in ReBAC-named files; 16 test files named. |
| Casbin (`CasbinEnforcementService`, the only `IPolicyEnforcementService`) | `TableAccessPolicy.cs:396-401` (only when `HasPolicies(tenant)`), `CanWriteTableAsync` (`:476-500`), `EnvoyExtAuthzService` (sole decision source), `PolicySimulationService` | deny or restrict (columns, masks, row SQL) from policy files | `CasbinOptions.Enabled` defaults `false`; no shipped policy file. Planned for deletion (WP-SIMPL-14). 2,667 src lines. |
| Roles (`GatewayRole`: ClusterAdmin, TenantAdmin, GovernanceAdmin, SecurityAuditor, DataOwner, DataSteward, SchemaPublisher, Consumer, FilterAdmin, FilterSync, CallerAttributeApprover/Inspector; `FunctionalRight` 18 rights) | ASP.NET policies on admin/control-plane endpoints; **also** data access: `GranteeType.Role` consents and profile assignments (`GranteeMatcher.cs:28-29`), `ClusterAdmin` and `WebSql.DmlWriterRoles` write bypass (`TableAccessPolicy.cs:489-494`) | control plane; partly data plane | Control-plane use is pervasive (ClusterAdmin 71 files, GovernanceAdmin 74). |
| Envoy ext_authz (`Application/Mesh`, `EnvoyExtAuthzEndpoints.cs`) | `EnvoyExtAuthzService.cs` (469 lines) | whole-request allow/deny for a mesh route | Core per OD-21. Decides with Casbin only (`:186`) plus a virtual-filter restriction (`:201-228`); never consults consents. |
| Schema contracts, restricted scopes, auth plugin hooks | `TableAccessPolicy.cs:226-280`, `:432-461` | narrowing only (deny or extra row filter) | Rare; narrowing inputs. |

Precedence today (`TableAccessPolicy.DecideAsync`, first terminal result wins): table inactive -> group resolution degraded (GOV-08) -> schema contract -> ReBAC `can_query` (if enforced on this path) -> access profiles -> consents (table-level Deny > no Allow = deny) -> virtual filters (AND, may deny) -> Casbin (deny wins, restriction merged) -> auth plugin hooks. Writes use a separate rule (ClusterAdmin or writer role or Casbin). Six call sites construct their own `TableAccessPolicy` (`UnifiedPolicyDecisionPoint.cs:113`, `GatewayExecutionService.cs:627`, `GovernedSqlRewriter.cs:866`, `FederatedDuckDbExecutionService.cs:124`, `GovernedProcedureExecutionService.cs:665`, `DefaultCrossDomainAccessResolver.cs:47`), with different `RebacEnforcement` modes; streaming, Iceberg, Arrow export, discovery and Envoy run their own logic.

### Contradictions found

1. **Approved consent, still denied.** With `EnforceOnQueryPaths=true` (or on any PDP path, where ReBAC applies whenever enabled), a data-owner-approved consent is denied because the consent workflow never writes a tuple. Only `AccessPlanningService.cs:346-349`, catalog sync and dbt ingestion write tuples.
2. **Applied plan, still denied.** The governance plan apply writes only `can_query` tuples and reports "Column-level access levels are not applied (no ReBAC consumer)" (`AccessPlanningService.cs:337-349` and the result text after it). The user then fails the consent zero-trust rule; `TableAccessPolicy.cs:383-391` even annotates "ReBAC: Allowed, Consent: Denied".
3. **Group grants break under ReBAC.** `RebacTableGate.IsAllowedAsync` checks the user SID only (`RebacTableGate.cs:70`). An AD-group consent (OD-22, OD-27) needs group-membership tuples that neither token groups nor SCIM populate.
4. **Same caller, different answer per transport.** PDP paths enforce ReBAC whenever enabled (default on), OData/REST/WebSQL/procedures only with `EnforceOnQueryPaths` (default off), streaming only with `EnforceOnStreaming`, Arrow export checks `viewer` instead of `can_query`, Envoy checks neither ReBAC nor consent.
5. **Revocation is asymmetric.** Revoking a consent does not remove a tuple; tuples carry only `ExpiresAt`.
6. **Durability.** Tuples are lost on restart without Redis; consents are durable, audited and signed (OD-29).
7. **Roles grant data.** `GranteeType.Role` consents and profile assignments, and the `ClusterAdmin`/`DmlWriterRoles` write bypass, contradict OD-22 (subjects are users and groups) and OD-26 (write grants are explicit).
8. **Discovery hides tables without tuples** (`CatalogVisibilityService.cs:320`), contrary to OD-19/OD-22 (list all non-hidden objects with their effective state).

Data ownership and delegation already live in the governance store (`DataOwner`, `DataOwnerDelegation` in `Domain/Model/GovernanceModels.cs:10,20`, `IDataOwnershipRepository` in `IGovernanceRepository.cs:343`), and group nesting is resolved by SCIM (OD-27, WP-SIMPL-43). No current ReBAC tuple expresses a relation the grant model lacks.

## Decision Drivers

Correctness (one answer per subject x object x operation on every transport), explainability ("why can X see Y" answered in one place), performance and caching, migration effort, security risk (fail-closed, no bypass), and alignment with OD-22/OD-26/OD-29.

## Considered Options

- **(A) Keep all mechanisms behind one PDP with explicit precedence.** ReBAC, Casbin, consents, profiles, filters and roles stay; one PDP orders them.
- **(B) One grant model.** Consent and access-profile grants (subject user/group x object x operation x column set x row scope; OD-22/OD-26) are the only source of data-access decisions. Roles govern only the administrative/control plane. ABAC attributes are only inputs to row filters. ReBAC is removed; the relationships the grant model needs (ownership, delegation, group nesting) are already first-class (governance store, SCIM). One PDP, used by every transport, discovery and Envoy.
- **(C) ReBAC-first.** Model consents, column levels and row scopes as tuples and make the Zanzibar evaluator the engine.

## Assessment

| Criterion | A | B | C |
|---|---|---|---|
| Correctness | Conflicts 1-5 remain by design; every new transport needs the right precedence for six inputs. | One source; conflicts 1-8 disappear structurally. | Tuples cannot express row predicates, masking or column sets; a second store stays for them. |
| Explainability | Trace across six stores (two non-durable). | One trace over consents, deny records, profiles, filters. | Graph walk plus a side store. |
| Performance/caching | Two cache/invalidation channels (ReBAC generation keys + consent epoch). | One epoch-based cache (existing consent decision cache); ReBAC event-bus invalidation removed. | Graph traversal per object; discovery batch checks for every table. |
| Migration effort | Lowest code change, highest config/operations burden. | Medium: about -3,000 src lines, re-route six call sites, migrate role grantees and plan apply. No production data (not deployed). | Highest: rewrite of consent, masking and RLS. |
| Security risk | Highest: switches per path, Casbin-only Envoy. | Lowest: default deny, explicit deny wins, no role bypass, one parity-tested seam. | High during rewrite. |

## Decision

Adopt **option B**.

1. **Single grant model.** Data access (read, insert, update, delete, execute) is decided only by grant records: consents (data-owner approved, time-limited, OD-29) and the grant records that approved access-profile assignments materialize (OD-32), granted to a user (incl. service principals as non-human users) or an AD group (effective groups per OD-27). Grants carry operation, object or object pattern (the pattern fixes the data source and is evaluated at decision time, OD-40), column set (with Clear/Masked per PII column; PII columns not named as Clear are Masked, OD-40), row scope (references to named row policies with parameter values, or simple column filters, optionally bound to caller attributes; OD-38) and validity. Write grants (WP-SIMPL-45 `WriteRule`s on the same records) are independent of read grants: no read grant is needed for INSERT or UPDATE, and `own rows` UPDATE applies the caller's RLS predicate (OD-33). A data owner reads own data only through an automatically created, auditable owner grant record (OD-34); there is no implicit owner access. Grant records are the only data-access source; an access profile never grants by itself. Assigning a profile runs the consent approval path and on approval materializes one time-limited grant record per profile item (object or pattern); patterns also cover objects created later (OD-32 as revised by OD-40). Write enforcement sits in the shared pipeline; only WebSQL is wired, other transports reject writes fail-closed until a separately approved WP wires them (OD-35).
2. **One PDP.** `IAccessDecisionPoint` (WP-SIMPL-46) is the only component allowed to decide data access. GraphQL, REST, OData, WebSQL, Trino protocol, MCP, procedures, analytics, federation, export, streaming, Lakehouse, Envoy ext_authz and discovery (WP-SIMPL-41) call it.
3. **Deterministic precedence:** fail-closed preconditions (unresolved identity, degraded groups, inactive object, transport not allowed) -> **explicit deny** (deny consents, column deny rules, hidden columns, virtual-filter deny, narrowing inputs) -> **grant** (union of matching allow grants; row scopes ORed across grants, then ANDed with the subject's hard row policies (OD-39); column state per cell when grants differ in columns or masking (OD-41); masking from the column rules of the grant records) -> **default deny**. Explicit deny wins over every grant, including profile-materialized grants (break-glass assignments no longer exist, OD-5).
4. **Roles are control plane only.** `GatewayRole` and `FunctionalRight` guard admin, governance, catalog and approval endpoints. No role, including `ClusterAdmin`, grants or bypasses data access. `GranteeType.Role` grants are migrated and no longer match.
5. **ABAC is an input, not a decider.** Caller attributes bind values into row predicates; an unresolved attribute denies.
6. **ReBAC is removed** (evaluator, stores, tuple API, endpoint filter, options, `danger_bypass_rebac`). Old keys fail fast (SEC-SIMPL-31), removed routes return 404 (SEC-SIMPL-30).
7. **Casbin is removed without a native replacement on the data path** (WP-SIMPL-14 re-scoped). Its policy simulation is replaced by the PDP explain API.
8. **Explainability.** One explain API answers "why can X see Y" and one owner view answers "who can see Y", both from the PDP trace and the grant store.

## Amendment 2026-11-06 (OD-38..OD-44)

- **Row policies (OD-38).** Row restrictions that read other tables are named, approved row policies (the existing virtual-filter definition model), structured or SQL; SQL policies need the approval of the data owner of every table they read. Key columns may be composite and are compared pair-wise. Evaluation is `Live` (correlated semi-join, same data source) or `KeySet` (cached keys bound as parameters; mandatory across data sources); failure, timeout or overflow deny. Grants reference policies with typed parameter values; free SQL in grants is removed.
- **Two levels (OD-39).** Grant row scopes are ORed across the subject's grants (a filtered group grant and an unfiltered group grant of the same user yield all rows, by intent). Hard row policies are assigned to a user or AD group, are subject-bound, are ANDed after the grant union and cannot be bypassed by any grant. Objects without the key columns are `Deny` by default, `Skip` only explicitly.
- **Dynamic coverage (OD-40).** Grant object patterns are evaluated at decision time; newly created objects are covered; PII columns not explicitly approved as Clear are Masked. The decision cache key includes the catalog epoch.
- **Per-cell masking (OD-41).** Grants are grouped by column signature; one signature compiles to a plain `WHERE`; several signatures compute each scope once as a flag and decide each cell with `CASE`. No `UNION ALL` rewrite.
- **Attribution (OD-42).** The decision record lists every contributing grant; audit and usage count the access for each of them. `AuthorizingGrant` is not used.
- **Transparency (OD-43).** Non-blocking approval-time hint (redundant grant, empty intersection) and an effective-access report with provenance (group, grant, row policy), without predicate or parameter values of other subjects.
- **Virtual views (OD-44).** A view defined in Autheris and approved by the owners of every base object is an object of its own (reserved data source `views`). The decision point decides it with the caller's grants on the view only (definer semantics); an explicit deny of the caller on any base object denies the view; PII columns propagate by lineage and stay Masked unless the base owners approved Clear and the view grant has Clear. Implementation after the feature freeze (WP-SIMPL-49).

Precedence is unchanged: precondition > explicit deny (including hard-policy denies and base-object denies of a view) > grant (row-scope union, then hard policies, then narrowing inputs) > default deny.

## Consequences

### Positive
- One answer per request on every transport, proven by a cross-transport parity matrix.
- Grants are durable, audited, signed and time-limited in one store; revocation is immediate through the policy epoch.
- About 3,000 fewer src lines (ReBAC) plus the Casbin engine; one cache invalidation channel.
- The class of per-path enforcement switches disappears structurally once WP-SIMPL-46 PR 5 merges.

### Negative / Risks
- Hierarchical grants (all tables of a schema or domain) are no longer expressed by tuples. They stay available through access-profile table patterns and bulk consent requests; per-object consent rows grow.
- OD-7 and SEC-SIMPL-85 are revised by OD-30; WP-SIMPL-36 sub-package 6b is replaced by WP-SIMPL-46 PR 5.
- Customers that used role-based consents must re-grant to groups (migration report provided).

## Revisit Triggers
- A requirement for relationship-derived access that neither ownership, delegation, group membership nor profile patterns can express (e.g. per-row ownership graphs). Then add a narrowly scoped relation source inside the PDP, never a parallel gate.
- PDP p95 latency above 5 ms per object on the parity benchmark after caching.

## References
- PRD `docs/plans/2026-11-05-req-project-simplification.md` OD-7, OD-9, OD-19, OD-21, OD-22, OD-26, OD-27, OD-29, OD-32..OD-44
- Phase 3 `docs/plans/2026-11-05-sec-project-simplification.md` SEC-SIMPL-30, 31, 33, 85
- WP-SIMPL-14, 36, 41, 42, 43, 45, 46, 47, 48, 49
