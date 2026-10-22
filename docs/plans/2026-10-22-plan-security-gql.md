# Plan: Security review fixes, area GQL (HIGH/MEDIUM)

Source: `docs/plans/security.txt` (findings `GQL-*`, severity HIGH or MEDIUM). Branch base verified: `fix/security-review-open-findings` @ 55dc7e27.
Scope: GQL-01, GQL-04, GQL-05, GQL-07, GQL-10, GQL-11, GQL-12, GQL-15, GQL-18. Out of scope (LOW): GQL-02, 03, 06, 08/09, 13, 14, 16, 17
(GQL-06(b), idempotency fingerprint without rowFilter/columnMasking, is still open and stays LOW).
No production yet: breaking API/option changes are allowed (no upgrade migrations).

## 1. Findings

### GQL-04 (HIGH) and GQL-05 (MEDIUM): four-eyes in atomic consent approval - ALREADY FIXED (verify only)
- Verified on HEAD: `MutationTypes.ApproveConsentRequestAsync` (`MutationTypes.cs:685-700`) derives `requiresFourEyes` from `Table.IsHighlySensitive`
  (flag OR high sensitivity, same rule as `ConsentApprovalPolicy.RequiresFourEyesApproval`) and rejects `rowFilter`/`columnMasking` on a non-final
  step with `BAD_REQUEST` (fixes GQL-05 fail-closed: restrictions are never silently dropped). `ExecuteAtomicApprovalWithConsentAsync` on all three
  engines (PostgreSql `:1393`, Sqlite `:1384`, SqlServer `:1427`) now locks the row (`FOR UPDATE` / `UPDLOCK,HOLDLOCK` / Sqlite write txn), runs
  `EnsureApproverIsNotRequesterAsync`, rejects a repeated approver (`SameApproverViolationMessage`) and derives the status from
  `StatusAfterApproval(..., Sensitivity)`, throwing `FourEyesNotSatisfiedMessage` if not final (fixes GQL-04; overlaps PERS-03).
- Residual: commit 5bbd74e3 says "Not built or tested yet". WP0 builds and runs `AtomicApprovalFourEyesTests`,
  `ApproveConsentRequestFourEyesMutationTests` and the `ConsentDecisionStatusGuardContractBase` integration subclasses (all engines). Add one missing
  Red test: `ApproveConsentRequest_HighSensitivityWithoutFlag_FirstCallWithColumnMasking_IsRejected` (sensitivity HIGH, `RequiresFourEyes=false`).
- Dependency: PERS-03 plan must not re-implement these checks; reference this verification.

### GQL-01 (MEDIUM): InvoiceItem DataLoader reads raw (ungoverned) values and misaligns rows after RLS - CONFIRMED
- Root cause: `BatchRelationLoader.cs:173-196` pairs `processedRows[i]` with `rawRows[i]` and falls back to raw `joinColumn`, `invoice_id` and `id`
  when the governed value is empty. `GovernedConnectorReader.Apply` drops Deny columns and (without RLS pushdown) drops rows, so the fallback is a
  deny bypass and the index pairing attaches items of RLS-hidden rows to visible invoices.
- Fix: delete every `rawRows` read after `Apply` (remove the `raw` variable). Join key and `id` are taken only from the governed `row`. A missing,
  null or masked join key means "relation not resolvable": the row is skipped and, once per batch, the loader records the existing
  `RELATION_KEY_MASKED` reason (same code as the catalog relation path) instead of guessing. A missing/denied `id` yields `id = null`
  on the item (never the raw value). If the join column is masked (`ColumnMaskDecision` not `None`) the whole relation is refused before execution.
- Fail-closed: governance output is the only source after `Apply`; no raw fallback in any branch.
- Red tests (`tests/Autheris.Tests.Unit/GraphQL/BatchRelationLoaderGovernanceTests.cs`):
  `LoadItems_DeniedIdColumn_DoesNotEmitRawId`, `LoadItems_MaskedParentId_RelationNotResolved_ReportsRelationKeyMasked`,
  `LoadItems_RlsDropsRows_ItemsNotAttachedToWrongInvoice`, `LoadItems_InvoiceIdAliasOnlyInRaw_IsNotUsed`.
- Breaking: items whose join key is denied disappear instead of appearing with raw keys.

### GQL-07 (MEDIUM): syncDataCatalog admits non-canonical admins and runs cluster-wide - CONFIRMED
- Root cause: `MutationTypes.SyncDataCatalogAsync` (`:1132-1165`) checks raw role strings `GovernanceAdmin|ClusterAdmin|DeveloperAdmin`
  (ForwardAuth-asserted `ClusterAdmin` accepted), calls `IDataCatalogSyncService.SyncCatalogAsync(dryRun, ct)` without tenant scope
  (`DataCatalogSyncService.cs:57` iterates all provider tables) and writes no audit entry.
- Fix: mirror `SyncOpenMetadataAsync` (`:1080-1111`): admit only `ClusterAdminPolicy.IsCanonicalClusterAdmin(principal)` or role `GovernanceAdmin`;
  drop `DeveloperAdmin`. Non-cluster admins get `restrictToTenant = principal.GetTenantId()` (`SecurityException` -> `FORBIDDEN`).
  Change signature to `Task<CatalogSyncResult> SyncCatalogAsync(bool dryRun = false, TenantId? restrictToTenant = null, CancellationToken ct = default)`
  (`IDataCatalogInterfaces.cs:19`, `DataCatalogSyncService`, background service passes `null`). With a restriction, a table is processed only when
  `existing?.OwnerTenantId` equals the tenant; unknown (new) tables and tables of other tenants are skipped without a warning (no name disclosure).
  Write a required audit event `AuditEventTypes.DataCatalogSync` (new constant) with decision ALLOW/DENY, `dryRun`, scope and counts; audit failure
  on ALLOW aborts before the sync starts (write-ahead, like other required audits).
- Red tests (`tests/Autheris.Tests.Unit/Security/SyncDataCatalogAuthorizationTests.cs`, `.../DataCatalog/DataCatalogSyncTenantScopeTests.cs`):
  `SyncDataCatalog_ForwardAuthClusterAdmin_IsForbidden`, `SyncDataCatalog_DeveloperAdmin_IsForbidden`,
  `SyncDataCatalog_GovernanceAdmin_PassesOwnTenantRestriction`, `SyncCatalog_Restricted_SkipsForeignAndUnknownTables_NoForeignNamesInWarnings`,
  `SyncDataCatalog_WritesRequiredAuditEvent`, `SyncDataCatalog_AuditFailure_FailsClosedBeforeSync`.
- Breaking: `DeveloperAdmin` loses access; interface signature change.

### GQL-10 (MEDIUM): cluster CDC subscription counter per tenant compared with per-subject limit, leaks on crash - CONFIRMED
- Root cause: `CdcSubscriptionGovernor.cs:140-171` uses key `cdc:subs:{tenant}` against `MaxSubscriptionsPerSubject`; INCR/DECR counter with a
  24 h TTL refreshed on every increment, so crashed pods leak counts indefinitely while the tenant stays active.
- Fix: replace the counter with expiring slot sets. Add to `IDistributedClusterStateProvider`:
  `ValueTask<bool?> TryAcquireSlotAsync(string setKey, string member, int limit, TimeSpan ttl, CancellationToken ct)`,
  `ValueTask<bool> RenewSlotAsync(string setKey, string member, TimeSpan ttl, CancellationToken ct)`,
  `ValueTask ReleaseSlotAsync(string setKey, string member, CancellationToken ct)`. Redis: one Lua script on a ZSET (score = expiry ms;
  `ZREMRANGEBYSCORE -inf now`, `ZCARD < limit` -> `ZADD`, `PEXPIRE` key). In-memory provider: same semantics with a lock. The governor acquires two
  slots per lease, member = lease GUID: `cdc:subs:{tenant}:{sid}` (limit `MaxSubscriptionsPerSubject`) and `cdc:subs:{tenant}` (limit new
  `GraphQlOptions.MaxSubscriptionsPerTenant`, `[Range(1,100000)]`, default 500). Slot TTL 2 min; the `Lease` renews both every 40 s via a
  `PeriodicTimer`; a failed renewal (false or exception after 2 retries) completes the subscription stream (fail closed). If the second acquire
  fails the first slot is released. Remove `ClusterSubscriptionCounterTtl` and the INCR path.
- Fail-closed: store unavailable (null/exception) -> `SERVICE_UNAVAILABLE` as today; leaked slots expire after at most 2 min.
- Red tests (`tests/Autheris.Tests.Unit/GraphQL/CdcSubscriptionGovernorClusterTests.cs`, Redis integration in `Autheris.Tests.Integration`):
  `Cluster_OneSubjectAtLimit_OtherSubjectOfSameTenantAdmitted`, `Cluster_TenantCapExceeded_Denied`, `Cluster_SecondSlotFails_FirstReleased`,
  `Cluster_ExpiredSlotOfCrashedInstance_NoLongerCounted`, `Lease_RenewalFails_StreamCompleted`, `Redis_TryAcquireSlot_ConcurrentAcquires_RespectLimit`.
- Dependency: shares `IDistributedClusterStateProvider` with the CLUSTER/CDC area plans; coordinate the interface change (one owner).

### GQL-11 (MEDIUM): WebSocket token skips EnterpriseClaimsTransformation; Legacy tenant acts as wildcard - CONFIRMED
- Root cause: `JwtSocketTokenValidator.cs:90-107` only runs `ClaimsNormalizer.Normalize`, so a token-supplied `autheris:realm` survives and realm
  binding/resolver roles are not applied; `WebSocketAuthInterceptor.cs:133-135` skips the tenant cross-check when either side is `LegacySingleTenant`.
- Fix: inject `IClaimsTransformation` into `JwtSocketTokenValidator` and, after `Normalize` and `EntraTokenPolicy.Apply`, return
  `await _claimsTransformation.TransformAsync(normalizedPrincipal)` (same instance type as HTTP, registered at
  `GatewayServiceCollectionExtensions.cs:239`). Any exception from the transformation rejects the connection. In the interceptor, replace the
  condition with strict equality: when the upgrade request is authenticated, `tokenTenant != upgradeTenant` rejects (Legacy is a distinct tenant).
  When the upgrade is anonymous and the token resolves to `LegacySingleTenant` while the gateway is configured multi-tenant (any realm with a
  `TenantBinding`), reject with "Token tenant unresolved".
- Red tests (`tests/Autheris.Tests.Unit/Security/WebSocketClaimsTransformationTests.cs`):
  `SocketToken_WithClientRealmClaim_RealmStrippedAndIssuerRealmApplied`, `SocketToken_ResolverRealm_GetsRealmRoles`,
  `ConnectionInit_TokenLegacyTenant_UpgradeTenantAcme_Rejected`, `ConnectionInit_AnonymousUpgrade_TokenWithoutTenantInMultiTenant_Rejected`,
  `SocketToken_TransformationThrows_Rejected`.
- Dependency: AUTH/API area (EnterpriseClaimsTransformation idempotency marker `ClaimsNormalizer.LegacyMarkerIssuer` must stay valid for re-entry).

### GQL-12 (HIGH): cost limit bypass via nested fragments - CONFIRMED
- Root cause: `QueryCostAnalyzerRule.CalculateSelectionSetCost` returns 0 when `currentDepth > _maxExecutionDepth` (`:336-338`), and inline
  fragments / spreads recurse with `currentDepth + 1` (`:523`, `:545`; masking cost `:814-841` identical). `fragmentCostCache` is keyed by name only
  (`:533-547`), so a truncated deep evaluation (0) is reused at the root. The validation rule and `CostAndQuotaMiddleware.cs:122` use default depth 25.
- Fix: (a) inline fragments and spreads pass `currentDepth` unchanged in both `CalculateSelectionSetCost` and `CalculateMaskingCost`; (b) depth
  overflow returns `SafeAdd(_maxAllowedCost, 1000)` (same fail-closed sentinel as spread overflow), never 0; (c) cache key becomes
  `(string Fragment, string? TypeName, int Depth)` (`record struct FragmentCostKey`) for both caches; (d) all three call sites pass
  `GraphQlOptions.MaxAllowedExecutionDepth` (one helper `ResolveMaxExecutionDepth(options)`), so validation, middleware and pre-flight agree.
- Red tests (`tests/Autheris.Tests.Unit/GraphQL/QueryCostFragmentDepthTests.cs`):
  `Cost_25NestedInlineFragments_EqualsUnwrappedCost`, `Cost_NestedFragmentSpreads_EqualsUnwrappedCost`, `Cost_FieldDepthExceeded_ExceedsBudget`,
  `Cost_FragmentFirstSpreadDeep_ReusedAtRoot_NotZero`, `MaskingCost_NestedFragments_NotZero`, `CostAndQuotaMiddleware_UsesConfiguredDepth`.
- Breaking: queries over the depth limit are now rejected by cost instead of passing as free.

### GQL-15 (MEDIUM): federated result masking is a no-op on Hot Chocolate 16.6.8 - CONFIRMED
- Root cause: `SubgraphResultMaskingMiddleware.cs:40-73` passes `OperationResult.Data.Value` (an HC16 `ResultDocument`) to
  `SubgraphResultMasker.MaskRecursive`, which only walks dictionaries/enumerables (`SubgraphResultMasker.cs:185-240`), and rebuilds the result with
  `originalData.Formatter`, which serializes the original document. `MaskingApplied=true` is set regardless.
- Fix: move masking to field level. New `SubgraphFieldMaskingTypeInterceptor : TypeInterceptor` (registered when `Federation.EnableResultMasking`)
  adds a field middleware to every leaf (scalar/enum, incl. lists of them) output field of subgraph object types; after `next`, it calls new
  `SubgraphResultMasker.MaskLeaf(string responseName, string fieldName, string? parentType, object? value, MaskContext ctx)` (extracted from the
  existing leaf logic, same rule precedence config > catalog > `MaskUnknownScalars`). `MaskContext` is built once per request (scoped, lazily, via
  `context.ContextData`) using the existing catalog/config loading; catalog unavailable -> `MaskUnknownScalars=true` (fail closed). Set
  `MaskingApplied` only when at least one value was replaced. Delete `SubgraphResultMaskingMiddleware` and its registration (no result rewriting).
- Red tests: executor-level, real `IRequestExecutor` from `ServiceCollection().AddGraphQL()` (`tests/Autheris.Tests.Unit/Federation/SubgraphFieldMaskingExecutorTests.cs`):
  `Executor_EmailField_NonAdmin_ReturnsMasked`, `Executor_AliasedSensitiveField_Masked`, `Executor_CatalogUnavailable_UnknownScalarMasked`,
  `Executor_AdminBypass_ReturnsClear` (only if the current masker allows a bypass), `Executor_NoMaskedField_MaskingAppliedNotSet`.
- Spike (first task in WP4, timebox 0.5 d): confirm field middleware order relative to `UseProjection`/federation `_entities` resolvers on HC 16.6.8.
  If entity resolvers bypass field middleware, fall back to a `IExecutionDiagnosticEventListener`-free alternative: rebuild the result by serializing
  the `ResultDocument` to `JsonNode`, masking, and returning `OperationResultBuilder.New().SetData(dictionary)` with the default formatter; record the
  decision in this plan.
- Dependency: GQL-16 (LOW) shares the ResultDocument root cause; not addressed here.

### GQL-18 (MEDIUM): lineage/GDPR resolvers trust raw `ClusterAdmin` role - CONFIRMED
- Root cause: `QueryTypes.GetCallerSecurityContext` (`:697-725`) sets `IsClusterAdmin = roles.Contains("ClusterAdmin")`; `canAccessForeignReports`
  (`:660-664`) also accepts raw `ClusterAdmin`; consumed by `LineageImpactAnalyzerService.cs:66-70, 86, 194` (tenant check lifted).
- Fix: `isClusterAdmin = ClusterAdminPolicy.IsCanonicalClusterAdmin(principal)`; `canAccessForeignReports = isClusterAdmin || roles` in
  {`PrivacyAdmin`, `DataProtectionOfficer`, `GovernanceAdmin`} (raw `ClusterAdmin` removed from the list). Non-cluster privileged callers keep the
  tenant restriction in the analyzer (no change there, add tests). Architecture test: no `Contains("ClusterAdmin"` / `Equals("ClusterAdmin"` in
  `src/Autheris.GraphQL` outside `ClusterAdminPolicy` (extend the existing allowlist-based architecture test).
- Red tests (`tests/Autheris.Tests.Unit/GraphQL/LineageCallerContextTests.cs`):
  `ForwardAuthClusterAdmin_RevocationImpactForeignTenantConsent_Denied`, `ForwardAuthClusterAdmin_GdprReportForeignSubject_Denied`,
  `CanonicalClusterAdmin_ForeignTenant_Allowed`, `ForwardAuthClusterAdmin_TableConsumers_OwnerEmailsHidden`,
  `Architecture_GraphQL_NoRawClusterAdminRoleChecks`.

## 2. Work packages (order, rough size)
| WP | Findings | Size | Notes |
|----|----------|------|-------|
| WP0 | GQL-04/05 verification | S (0.5 d) | Build + run existing tests, add the HIGH-sensitivity Red test. |
| WP1 | GQL-12 | S-M (1 d) | HIGH, isolated in `QueryCostAnalyzerRule`. |
| WP2 | GQL-18, GQL-07 | M (1.5 d) | Same pattern (canonical cluster admin + tenant scope + audit). |
| WP3 | GQL-01, GQL-11 | M (1.5 d) | Independent files; can run in parallel lanes. |
| WP4 | GQL-15 | M-L (2 d) | Spike first; executor-level tests mandatory. |
| WP5 | GQL-10 | M (2 d) | Interface change in `IDistributedClusterStateProvider`; coordinate with CLUSTER/CDC plans. |
WP1-WP3 have no mutual dependencies; WP5 last because of the shared interface.

## 3. Security conditions for Phase 3
1. No code path in `BatchRelationLoader` reads `rawRows` after `GovernedConnectorReader.Apply` (grep check in review).
2. Every cluster-wide privilege check in `src/Autheris.GraphQL` uses `ClusterAdminPolicy.IsCanonicalClusterAdmin`; enforced by architecture test.
3. Cost analysis never returns 0 for a non-empty selection set that was truncated; depth overflow and spread overflow exceed the budget.
4. The WebSocket principal equals what the HTTP pipeline would produce for the same token (realm stripped/re-derived); tenant equality is strict.
5. Federation masking is proven by executor-level tests on HC 16.6.8; `MaskingApplied` is never reported when nothing was masked.
6. CDC slot leases expire without graceful release within 2 min; store errors deny admission and terminate streams whose renewal fails.
7. `syncDataCatalog` writes a required audit entry before acting and never processes or names tables outside the caller's tenant.
8. GQL-04/05 tests pass on all three engines before any other WP is merged.
