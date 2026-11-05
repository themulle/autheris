# WP-SIMPL-46 Unified authorization model (one grant model, one decision point)
Status: Ready for implementation
Parent: [ADR-020](../../adr/ADR-020-unified-authorization-model.md) (Proposed; this spec starts only after the owner accepts it) and [plan](../2026-11-05-plan-project-simplification.md) | Issues: GHSA-78j7-3gh6-7cqx (see private advisory) | Owner decisions: OD-22, OD-26, OD-21, OD-27, OD-9, OD-19, OD-29; revises OD-7 | Security conditions: SEC-SIMPL-30, 31, 33; replaces SEC-SIMPL-85; proposed SEC-AZ-01..10 (section 10)

> **Owner decision OD-31 (2026-11-05):** customer DLL plugins stay. The access decision point consumes `EvaluateAuthorizationHooksAsync` as a narrowing input (deny or extra row filter, never grant). `EnrichRolesAsync` may only add control-plane roles; add a test `PluginRoleEnrichment_never_grants_data_access` (Category=Security) asserting that an enriched role neither satisfies a data grant nor bypasses write grants.

## 1. Goal and outcome
Every data-access decision (read, insert, update, delete, execute, discover) in Autheris is made by one component, `IAccessDecisionPoint`, from one grant model (consents and access-profile assignments for users and AD groups) with the precedence explicit deny > grant > default deny. Measurable: (a) zero `new TableAccessPolicy(` outside `Application/Policy/Pdp/`; (b) zero references to `IRebacEvaluator`, `IRebacStore`, `RebacTableGate`, `IPolicyEnforcementService` in `src`; (c) the cross-transport parity matrix (section 6.3) passes with identical outcomes on all transports; (d) `GET /api/v1/authz/explain` answers "why can X see Y" with a complete, ordered trace; (e) no role grants or bypasses data access.

## 2. Preconditions and dependencies
- ADR-020 accepted by the owner (status Accepted). Until then nothing of this WP merges.
- Must be merged first: WP-SIMPL-43 PR 2-5 (`IGroupSidResolver`, effective groups token plus SCIM, `GroupResolutionStatus` on the request security context), WP-SIMPL-45 PR 1-3 (`WriteRule`, `IWriteGrantEvaluator`). WP-SIMPL-36 sub-package 0 (`fix/security-batch-5`) merged.
- Coordinated (edits in section 12): WP-SIMPL-14 (no native policy engine on the data path), WP-SIMPL-36 sub-package 6b (replaced by PR 5 here), WP-SIMPL-41 (discovery calls `DecideManyAsync`), WP-SIMPL-43 step 11 (Envoy calls the PDP), WP-SIMPL-47 (grant records are the PDP's grant source).
- Parallel-safe: WP-SIMPL-38/39/40/42/44. Not parallel with WP-SIMPL-14 PR-B or WP-SIMPL-45 PR 4 (same files: `TableAccessPolicy.cs`, `GovernedSqlRewriter.cs`).

## 3. Scope
### In scope
- `IAccessDecisionPoint` with `DecideAsync`, `DecideManyAsync`, `ExplainAsync`; structured `DecisionReason` trace.
- Routing of all transports, discovery, Envoy ext_authz, Arrow export, streaming, Iceberg and cross-domain resolution through the PDP.
- Explain API and owner "who has access" API.
- Migration: role-grantee consents and profile assignments, governance plan apply (tuples -> consents), migration report.
- Removal of ReBAC (code, options, endpoints, endpoint filter, stores, seeding, danger flag) and of `IPolicyEnforcementService` from the data path.
- Security tests, parity matrix, architecture tests.
### Out of scope
- New grant semantics beyond OD-22/OD-26 (no hierarchical tuples, no per-row ownership graphs).
- The approval workflow itself (WP-SIMPL-47), write-grant semantics (WP-SIMPL-45), authentication and group resolution (WP-SIMPL-43), discovery payload (WP-SIMPL-41).
- Control-plane role model changes beyond removing data-plane role effects (OD-9 stays as specified in WP-SIMPL-36).

## 4. Current state (verified on `db45aa80`, worktree `/work/wt/simpl-od19`)
- Pipeline: `TableAccessPolicy.DecideAsync` (`src/Autheris.Application/Policy/TableAccessPolicy.cs:170`): inactive :216, group resolution degraded :220-223, schema contract :226-280, ReBAC :283-297, access profiles :300-330, consent resolution (`src/Autheris.Application/Services/ConsentResolutionService.cs:44-80`: table-level Deny then zero-trust), virtual filters :336-378, ReBAC/consent diagnostic note :383-391, Casbin :396-401, auth plugin hooks :432-461. Writes: `CanWriteTableAsync` :476-500 (ClusterAdmin, `DmlWriterRoles`, Casbin).
- `RebacEnforcement { QueryPaths, WhenEnabled }` (:21-28); `TableAccessQuery.Rebac` default `QueryPaths` (:58).
- Construction sites of `TableAccessPolicy`: `Policy/UnifiedPolicyDecisionPoint.cs:113`, `Services/GatewayExecutionService.cs:627` (OData via `Extensions/.../ODataHandler`, GraphQL `Query.Catalog`, MCP `McpDatasetCatalog`), `Sql/Services/GovernedSqlRewriter.cs:866` (WebSQL, Trino protocol `WebSqlEndpoints.cs:63`), `Sql/Services/FederatedDuckDbExecutionService.cs:124`, `Procedures/Services/GovernedProcedureExecutionService.cs:665`, `Connectors/CrossDomain/DefaultCrossDomainAccessResolver.cs:47`.
- `IUnifiedPolicyDecisionPoint` (`Policy/IUnifiedPolicyDecisionPoint.cs`) consumers: `TableStreamEndpoints.cs:85`, `GovernedAggregationService`, `SemanticMetricQueryService`, `GovernedChangeDetectionService`, `CrossSourceRelationResolver`, `GovernedDataQueryService` (REST `DatasetDataEndpoints`, MCP `McpToolExecutionHandler`). Registered scoped at `Api/Extensions/DependencyInjection/GatewayExecutionServiceExtensions.cs:100`.
- Own logic outside the pipeline: `Catalog/Services/CatalogVisibilityService.cs:320,329-375` (ReBAC filter), `Streaming/Services/StreamRlsPolicyEnforcer.cs:258-264`, `Api/Endpoints/ArrowExportEndpoints.cs:48` (`RequireRebac("viewer", ...)`) and `:236-275`, `Extensions/Lakehouse/Services/IcebergRestCatalogFederationService.cs:118-124,328-330`, `Mesh/Services/EnvoyExtAuthzService.cs:186` (Casbin only, no consent).
- ReBAC files: `Application/Security/Rebac/**` (evaluator, `InMemoryRebacStore`, `RebacBatchDataLoader`, `NullRebacEvaluator`, interfaces), `Infrastructure/Rebac/RedisRebacStore.cs`, `Api/Endpoints/RebacEndpoints.cs` (`/api/v1/rebac` tuples, check, batch-check; mapped at `GatewayApplicationBuilderExtensions.cs:246`), `Api/Security/RebacEndpointFilter.cs` (one use), `Api/Extensions/DependencyInjection/RebacSeedHostedService.cs`, `Application/Policy/RebacTableGate.cs`, DI `GatewaySecurityServiceExtensions.cs:85-105`, `RebacOptions` (`Domain/Options/Security/RlsOptions.cs:19-54`), `danger_bypass_rebac` (`Domain/Options/Security/ApiKeyOptions.cs:66`, `GatewayOptions.cs:119,209`), `GrantKind.RebacTuple` (`Governance/Grants/GrantModels.cs`). 2,334 src lines in ReBAC-named files; tuple writers: `RebacEndpoints.cs:157`, `RebacSeedHostedService.cs:75`, `Governance/Services/AccessPlanningService.cs:337-349`, `Extensions/DataCatalog/DataCatalogSyncService.cs:278-282`, `Extensions/Dbt/DbtMetadataIngestionService.cs:1826-1830`.
- Role grants: `GranteeType.Role = 3` (`Domain/Model/TableModels.cs`), matched by `Policy/GranteeMatcher.cs:28-29`; `GrantPolicy.cs:43` treats Role as standing.
- Tests: 16 test files named `*Rebac*` (125 mention ReBAC), 18 named `*Casbin*`. Test projects: `Autheris.Tests.Unit`, `Autheris.Tests.Integration`, `Autheris.Tests.Architecture`.

## 5. Implementation steps (TDD order; write the section-6 tests of each step first)
1. **Decision contracts** (`src/Autheris.Application/Policy/Pdp/AccessDecisionContracts.cs`, new):
   ```csharp
   public enum AccessOperation { Read, Insert, Update, Delete, Execute, Discover }
   public enum AccessTransport { GraphQl, Rest, OData, WebSql, Trino, Mcp, Procedure, Analytics, Federation, ArrowExport, Streaming, Lakehouse, EnvoyExtAuthz, Discovery, Internal }
   public enum DecisionStep { Precondition, ExplicitDeny, Grant, Narrowing, DefaultDeny }
   public sealed record DecisionReason(DecisionStep Step, string Code, string? SourceKind, string? SourceId, string? Column = null); // SourceKind: Consent|AccessProfile|VirtualFilter|SchemaContract|RestrictedScope|AuthHook|Hidden|Transport; never predicate values
   public sealed record AccessRequest(SecurityPrincipalContext Subject, TableMetadata Object, AccessOperation Operation,
       AccessTransport Transport, IReadOnlyList<string>? Columns = null, IReadOnlyList<Consent>? SupplementalConsents = null,
       WriteGrantQuery? Write = null, bool Trace = false);
   public interface IAccessDecisionPoint {
       ValueTask<TableAccessDecision> DecideAsync(AccessRequest request, CancellationToken ct);
       ValueTask<IReadOnlyList<TableAccessDecision>> DecideManyAsync(SecurityPrincipalContext subject, IReadOnlyList<TableMetadata> objects, AccessOperation operation, AccessTransport transport, CancellationToken ct);
       ValueTask<AccessExplanation> ExplainAsync(AccessRequest request, CancellationToken ct);
   }
   ```
   Extend `TableAccessDecision` (`src/Autheris.Domain/Interfaces/TableAccessDecision.cs`) with `IReadOnlyList<DecisionReason> Reasons { get; init; } = []` and `long PolicyEpoch { get; init; }`; keep `DeniedReasons` (derived text) for existing consumers. Move `DecisionReason`/`DecisionStep` to `Autheris.Domain.Interfaces` if the Domain record needs them.
2. **PDP implementation** (`src/Autheris.Application/Policy/Pdp/AccessDecisionPoint.cs`, new; replaces `UnifiedPolicyDecisionPoint.cs` and `IUnifiedPolicyDecisionPoint.cs`, both deleted; register scoped at `GatewayExecutionServiceExtensions.cs:100`). Holds the only `new TableAccessPolicy(...)`. Fixed order, first terminal result wins:
   - `Precondition`: identity incomplete or tenant mismatch, `IsRoleResolutionDegraded` (keep GOV-08), object inactive, transport not allowed for the source (existing transport gate) -> deny.
   - `ExplicitDeny`: table-level Deny consent for user or any effective group; virtual-filter `IsDenied`; schema contract exclusion; restricted-scope denial; auth hook deny -> deny. Column-level: Deny column rule or hidden column -> `ColumnAccessLevel.Deny` for that column (column invisible, OD-22).
   - `Grant`: union of active, non-revoked, non-expired Allow consents (user SID or effective group SID; `GranteeType.ServicePrincipal` matches as user) plus supplemental consents. Column level per column = most permissive matching Allow rule, but PII columns are `Masked` unless a `Clear` rule exists; access profiles apply only as masking modifiers on granted tables (unchanged SR-M-07) and never create access. Row predicate = OR over the granting consents' row filters (an unfiltered grant contributes TRUE), then AND every mandatory virtual-filter predicate. Caller attributes are bound only here; unresolved -> deny with `ATTR_UNRESOLVED`.
   - Writes (`Insert/Update/Delete`): read decision must allow, then `IWriteGrantEvaluator.EvaluateAsync(request.Write!)` (WP-SIMPL-45); no role path.
   - `Execute`: procedure grant via the existing procedure consent lookup in `GovernedProcedureExecutionService` moved behind the PDP.
   - `Discover` (`DecideManyAsync`): never denies listing; returns per object the read decision so WP-SIMPL-41 maps columns to `Clear/Masked/NoAccess`; hidden objects/columns are excluded by the caller (WP-SIMPL-41), not by the PDP.
   - `DefaultDeny`: no grant -> deny with `NO_GRANT`.
   Every step appends a `DecisionReason` (only ids and codes). Cache: reuse the consent decision cache keyed by (tenant, user SID, sorted effective group SIDs hash, object, operation, policy epoch, caller-attribute epoch); cache only the decision, never across tenants.
3. **Remove ReBAC and Casbin from `TableAccessPolicy`**: delete :283-297 and :383-391, the `rebacEvaluator` and `policyEnforcementService` ctor parameters, `RebacEnforcement`, `TableAccessQuery.Rebac`, `BuildEvaluationContext` Casbin use (:396-401) and `CanWriteTableAsync` (replaced by step 2 writes; coordinate with WP-SIMPL-45 which deletes it). Make the `TableAccessPolicy` constructor `internal` and add `InternalsVisibleTo` only for the test projects.
4. **Re-route call sites** to `IAccessDecisionPoint.DecideAsync` with the right `AccessTransport`: `GatewayExecutionService.AccessPolicy()` :627, `GovernedSqlRewriter.AccessPolicy()` :866 and its query at :529, `FederatedDuckDbExecutionService.AccessPolicy()` :124 and :479, `GovernedProcedureExecutionService` :658-670, `DefaultCrossDomainAccessResolver` :47-90, and all `IUnifiedPolicyDecisionPoint` consumers listed in section 4. Delete the `AccessPolicy()` factory methods.
5. **Bypass paths onto the PDP**: `CatalogVisibilityService.ApplyRebacAsync` deleted (discovery lists all non-hidden objects; WP-SIMPL-41 uses `DecideManyAsync`); `StreamRlsPolicyEnforcer.cs:258-264` -> `DecideAsync(Streaming)` per event table (decision cached per subscription and epoch); `ArrowExportEndpoints.cs:48` remove `RequireRebac`, `IsSqlPermittedByRebacAsync` (:236-275) -> PDP read decision per referenced table; `IcebergRestCatalogFederationService.cs:118-124,328-330` -> PDP; `EnvoyExtAuthzService.cs:162-198` -> `DecideAsync(EnvoyExtAuthz, Read or Insert/Update by method)`; keep the existing "restricted decision denies" rule (:201-208 `IsRestricted`).
6. **Explain API** (`src/Autheris.Api/Endpoints/AuthorizationExplainEndpoints.cs`, new; service `src/Autheris.Application/Policy/Pdp/AccessExplanationService.cs`):
   - `POST /api/v1/authz/explain` body `{ subject: { sid | upn }, object: "domain.schema.table", operation, transport?, columns? }` -> `AccessExplanation(bool Allowed, IReadOnlyList<DecisionReason> Trace, IReadOnlyList<ColumnStateExplanation> Columns, EffectiveGroupsExplanation Groups, long PolicyEpoch)`. `EffectiveGroupsExplanation` lists group SIDs with source `Token|Scim` and the resolution status.
   - `GET /api/v1/authz/objects/{domain}.{schema}.{table}/grants` -> active grants (subject, kind, operations, columns with Clear/Masked, row-filter ids, validity, approver) from the grant store.
   - Authorization: caller is `GovernanceAdmin` or `SecurityAuditor` of the object's tenant, or an active data owner/delegate of that object (`IDataOwnershipRepository.GetDataOwnersForTableAsync`, `DataOwnerDelegation`); any authenticated user may explain with `subject` = self only. Other subjects' row-filter predicate values are never returned (ids only). Cross-tenant -> uniform 404. Audit `AUTHZ_EXPLAIN` / `AUTHZ_GRANTS_LIST` with caller, subject, object.
   - `PolicySimulationService` and `/api/governance/policy-simulation/replay` (`GovernanceEndpoints.Audit.cs:233`) are deleted (WP-SIMPL-14 consumer) and replaced by explain.
7. **Migration (forward-only governance migration step, newest DB version)**:
   - Consents and access-profile assignments with `GranteeType.Role`: revoked with reason `ADR020_ROLE_GRANTEE_UNSUPPORTED` (consents) / set inactive (assignments), audited. `Consent.Validate` and access-profile validation reject `Role` for new records; `GranteeMatcher` returns false for `Role` (fail closed). Keep the enum value for persisted data.
   - `AccessPlanningService.ApplyAccessAsync` (:303-412) creates Allow consents (grantee, table, column rules from `grant.Columns`, `ValidTo = grant.ValidUntil`, `GrantKind.Consent`, approvals as recorded) through the existing consent repository and grant policy instead of tuples; result text no longer mentions ReBAC.
   - `DataCatalogSyncService.cs:278-282` and `DbtMetadataIngestionService.cs:1826-1830`: delete the hierarchy tuple writes.
   - Migration report `GET /api/v1/admin/authz/migration-report` (ClusterAdmin only, removed one release later): revoked role grants (id, role name, table), and, when a Redis tuple store is still reachable at upgrade, a one-shot export of `can_query`/`viewer` tuples as "candidate consent requests". Tuples are never converted to consents automatically (only the data owner grants, OD-22).
8. **ReBAC removal**: delete the files in section 4 (ReBAC list), `GrantKind.RebacTuple`, `IsRebacBypassed`, `danger_bypass_rebac`, `RebacOptions`, `GatewayOptions.Rebac`, `ArrowExport` filter use. Add to `UnknownGatewayKeyGuard`: `Gateway:Rebac:*` ("removed by ADR-020; data access is granted by consents"), `Gateway:Insecure:danger_bypass_rebac`. `/api/v1/rebac/*` returns 404 (SEC-SIMPL-30).
9. **Roles out of the data plane**: confirm no data-path reference to `IsInRole`, `ClusterAdminPolicy`, `TenantRoles`/`ClusterRoles` remains in `Application/Policy/**` except `GranteeMatcher` (Role branch false). `SecurityPrincipalContext` keeps roles for control-plane endpoints.

## 6. Tests
### 6.1 Unit (`tests/Autheris.Tests.Unit/Policy/Pdp/`), all `[Trait("Category","Security")]`
- `AccessDecisionPrecedenceTests`: `ExplicitTableDeny_OnGroup_WinsOverUserAllow`, `ColumnDenyRule_WinsOverClearRule`, `HiddenColumn_IsDeny_EvenWithClearGrant`, `VirtualFilterDeny_WinsOverGrant`, `BreakGlassProfile_DoesNotOverrideExplicitDeny`, `NoGrant_DefaultDeny_ReasonNoGrant`, `ExpiredGrant_IsDefaultDeny`, `RevokedGrant_DeniedAfterEpochBump`, `GroupGrant_FromScimGroup_Allows`, `DegradedGroups_PreconditionDeny`, `RowFilters_OrAcrossGrants_AndWithMandatoryFilters`, `PiiColumn_MaskedWithoutClearRule`, `AccessProfile_WithoutConsent_GrantsNothing`, `UnresolvedCallerAttribute_Denies`, `TransportNotAllowed_PreconditionDeny`.
- `RoleIndependenceTests`: `ClusterAdmin_WithoutGrant_IsDenied_Read`, `ClusterAdmin_WithoutWriteGrant_IsDenied_Write`, `RoleGranteeConsent_NeverMatches`, `TenantRoleClaim_GrantsNothing`.
- `AccessDecisionTraceTests`: `Trace_IsOrdered_PreconditionDenyGrantDefault`, `Trace_ContainsIdsNotPredicateValues`, `Decision_IsDeterministic_ForSameInputs` (1,000 shuffled consent orders, identical result).
- `AccessExplanationServiceTests`: `Explain_Self_Allowed_ForAnyUser`, `Explain_OtherSubject_RequiresOwnerOrAdmin`, `Explain_CrossTenant_Uniform404`, `Explain_MatchesDecideAsync_ForAllParityScenarios`, `Grants_List_OmitsPredicateValues`.
- `AuthzMigrationTests`: `RoleGranteeConsents_RevokedWithReason`, `RoleGranteeAssignments_Deactivated`, `PlanApply_CreatesConsents_NotTuples`, `MigrationReport_ClusterAdminOnly`.
- `RemovedAuthzKeysTests` (`tests/Autheris.Tests.Unit/Configuration/`): `RebacSection_FailsStartup`, `DangerBypassRebac_FailsStartup`, `RebacRoutes_Return404`.
- `EnvoyExtAuthzSecurityTests` (extend): `NoConsent_Denied`, `ConsentWithRowFilter_DeniedAsRestricted`, `ConsentUnrestricted_Allowed`.
- Delete: the 16 `*Rebac*` test files (`git ls-files tests | grep -i rebac`) after porting any fail-closed assertion that still applies to the PDP (tenant isolation, cancellation, timeout -> deny) into `AccessDecisionPrecedenceTests`; `PolicySimulationService` tests. `RebacEnforcementTests.All_paths_enforced_when_tuples_exist_and_exemption_fails_startup` (SEC-SIMPL-85) is replaced by the parity matrix.
### 6.2 Architecture (`tests/Autheris.Tests.Architecture/AuthorizationArchitectureTests.cs`, `[Trait("Category","Security")]`)
- `Only_Pdp_Constructs_TableAccessPolicy`, `No_Rebac_Types_In_Src`, `No_PolicyEnforcementService_In_DataPath`, `No_Role_Checks_In_Policy_Namespace` (except `GranteeMatcher`), `Every_Transport_Entry_Depends_On_IAccessDecisionPoint` (types: `GatewayExecutionService`, `GovernedSqlRewriter`, `FederatedDuckDbExecutionService`, `GovernedProcedureExecutionService`, `GovernedDataQueryService`, `GovernedAggregationService`, `SemanticMetricQueryService`, `StreamRlsPolicyEnforcer`, `EnvoyExtAuthzService`, `IcebergRestCatalogFederationService`, `DefaultCrossDomainAccessResolver`, WP-SIMPL-41 `DiscoveryService`).
### 6.3 Cross-transport parity matrix (`tests/Autheris.Tests.Integration/Security/CrossTransportAuthorizationParityTests.cs`, PostgreSQL Testcontainer governance store and data source, `[Trait("Category","Security")]`)
Scenarios (rows) x transports (columns: GraphQL, REST datasets, OData, WebSQL, Trino `/v1/statement`, MCP `query_data`, procedure, analytics aggregate, Arrow export, Envoy check, discovery). Each cell asserts the same `allowed`, per-column state and row-set fingerprint (sorted primary keys) as the PDP `ExplainAsync` result for that scenario. Scenarios: S1 user grant; S2 token-group grant; S3 SCIM-group grant; S4 no grant; S5 group table deny + user allow; S6 column deny rule; S7 PII default masked; S8 PII clear rule; S9 hidden column; S10 IN-list row filter; S11 subquery row filter; S12 caller-attribute filter unresolved; S13 break-glass profile on a masked column; S14 ClusterAdmin without grant; S15 expired grant; S16 revoked grant after epoch bump. Envoy cells assert allow/deny only (restricted = deny); discovery cells assert column states only and never row data. Theory name: `Same_decision_on_every_transport(scenario, transport)`.
### 6.4 Benchmark (not a gate): `benchmarks/Autheris.Benchmarks/AccessDecisionPointBenchmark.cs` replaces `CasbinDecisionBenchmark.cs`; cached p95 < 5 ms per object.

## 7. Verification commands (one build at a time)
```
dotnet build Autheris.sln -c Release -warnaserror
dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release --no-build --filter "Category=Security&(FullyQualifiedName~Policy.Pdp|FullyQualifiedName~RemovedAuthzKeys|FullyQualifiedName~Envoy)"
dotnet test tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj -c Release --no-build --filter "FullyQualifiedName~AuthorizationArchitectureTests"
dotnet test tests/Autheris.Tests.Integration/Autheris.Tests.Integration.csproj -c Release --no-build --filter "FullyQualifiedName~CrossTransportAuthorizationParityTests"
git grep -n -E "IRebacEvaluator|IRebacStore|RebacTableGate|RebacOptions|danger_bypass_rebac|RequireRebac" -- src   # only UnknownGatewayKeyGuard entries
git grep -n "new TableAccessPolicy(" -- src                                                                       # only Policy/Pdp/AccessDecisionPoint.cs
git grep -n "IPolicyEnforcementService" -- src                                                                    # 0
git grep -n -E "IsInRole|ClusterAdminPolicy" -- src/Autheris.Application/Policy                                    # 0
```

## 8. Acceptance criteria
- [ ] ADR-020 status Accepted; OD-7 revised in the PRD; SEC-SIMPL-85 replaced by the SEC-AZ conditions.
- [ ] One `IAccessDecisionPoint`; `IUnifiedPolicyDecisionPoint` and `UnifiedPolicyDecisionPoint` deleted; all greps of section 7 as stated.
- [ ] Parity matrix 16 x 11 green; explain result equals `DecideAsync` for every scenario.
- [ ] Explain and grants endpoints authorized as in step 6, audited, uniform 404 cross-tenant, no predicate values of other subjects.
- [ ] Role-grantee records revoked/deactivated by migration; new Role grantees rejected; `ClusterAdmin` without grant denied on read and write.
- [ ] `/api/v1/rebac/*` 404; `Gateway:Rebac:*` and `danger_bypass_rebac` fail startup with the replacement message.
- [ ] Governance plan apply creates consents; no tuple writer remains.
- [ ] Discovery lists tables without grants (WP-SIMPL-41 tests green with `DecideManyAsync`).

## 9. PR slicing (<= 400 net added src lines each)
1. PR 1 "PDP contracts and implementation": steps 1-3, unit tests 6.1 precedence/role/trace (~380 src).
2. PR 2 "Re-route transports": step 4, architecture tests (net negative src).
3. PR 3 "Bypass paths": step 5 (discovery filter removal, streaming, Arrow export, Iceberg, Envoy) + Envoy tests (~250 src).
4. PR 4 "Explain API": step 6 + tests (~350 src).
5. PR 5 "Migration and ReBAC removal": steps 7-9, `RemovedAuthzKeysTests`, `AuthzMigrationTests` (+250 / about -3,000 src).
6. PR 6 "Parity matrix": section 6.3 and benchmark (tests only).

## 10. Risks, rollback, and things the developer must NOT do
- Risk: a transport keeps a private decision path. Mitigation: architecture tests in PR 2 before deletions.
- Risk: masking semantics drift while moving logic. Mitigation: existing consent/masking/virtual-filter tests stay green unchanged in PR 1-3; parity matrix in PR 6.
- Rollback: PR 1-4 are additive or re-routing and revert cleanly; PR 5 reverts as one commit (no production data; not deployed).
- Must NOT: convert tuples, roles or Casbin rules into consents automatically; let any role, `ClusterAdmin` included, bypass a grant; let an access profile create access without a consent; cache decisions across tenants or without the policy epoch; return other subjects' predicate values from explain; treat `Degraded` groups as empty groups; keep `Rebac` options as no-ops (they must fail fast).
- Proposed Phase 3 conditions (to be confirmed and numbered by `csharp-security-expert`): **SEC-AZ-01** single PDP (architecture test); **SEC-AZ-02** precedence explicit deny > grant > default deny, deterministic; **SEC-AZ-03** roles never grant or bypass data access; **SEC-AZ-04** access profiles and break-glass never override explicit deny and never grant without consent; **SEC-AZ-05** cross-transport parity matrix is a required check; **SEC-AZ-06** explain/grants API authorization, audit, uniform 404, no foreign predicate values; **SEC-AZ-07** decision cache keyed by tenant, subject, effective groups, object, operation, policy and attribute epoch; revocation effective at the next request after the epoch bump; **SEC-AZ-08** removed ReBAC keys fail fast and routes 404 (SEC-SIMPL-30/31); **SEC-AZ-09** degraded group resolution and unresolved attributes deny; **SEC-AZ-10** Envoy ext_authz decides through the PDP and denies restricted decisions.

## 11. Estimate
+1,300 / -3,400 src (ReBAC about -2,300 plus `RebacTableGate`, DI, filters, `PolicySimulationService`, `UnifiedPolicyDecisionPoint`); tests +1,600 / -1,800. 9 person-days (PDP 2, re-route 2, bypass paths 1.5, explain 1.5, migration/removal 1, parity matrix 1).

## 12. Required edits to other work packages (applied when ADR-020 is accepted)
- **WP-SIMPL-14:** drop step 3 (`NativePolicyEnforcementService`) and decision D-14a; Casbin and `IPolicyEnforcementService` are deleted without a data-path replacement; Casbin column/mask/row rules have no successor (consents, column rules and virtual filters cover OD-22); `PolicySimulationService` is deleted by WP-SIMPL-46 step 6. SEC-SIMPL-33 text "existing RBAC/ReBAC filters" becomes "the PDP".
- **WP-SIMPL-36:** sub-package 6b (ReBAC switch) is replaced by WP-SIMPL-46 PR 5; GHSA-78j7-3gh6-7cqx part 2 resolved by removal.
- **WP-SIMPL-41:** discovery calls `IAccessDecisionPoint.DecideManyAsync(..., Discover, Discovery)`; the ReBAC filter in `CatalogVisibilityService` is removed (step 5).
- **WP-SIMPL-43:** step 11 Envoy calls `IAccessDecisionPoint`; section 4.3 drops `RebacEndpointFilter`.
- **WP-SIMPL-45:** `IWriteGrantEvaluator` is invoked by the PDP for write operations; its consumers call the PDP.
- **WP-SIMPL-47:** grant records written by the workflow are the PDP's only grant source.

## Decisions taken in this spec
- D-46a: Service principals are matched as users (non-human users); OD-22 "user" includes them.
- D-46b: Access profiles stay masking/row modifiers on granted objects (SR-M-07) and carry WP-SIMPL-45 write rules; they never grant read access alone. Break-glass never overrides explicit deny.
- D-46c: Row predicates are ORed across grants and ANDed with mandatory virtual filters (current consent semantics kept).
- D-46d: Schema contracts, restricted scopes and auth hooks stay as narrowing inputs inside the PDP (deny or extra filter only).
- D-46e: `TableAccessDecision` is extended instead of introducing a new decision record, to limit consumer churn.
- D-46f: ReBAC tuples are not migrated into consents; a one-shot report lists them as candidates for owner-approved requests.
