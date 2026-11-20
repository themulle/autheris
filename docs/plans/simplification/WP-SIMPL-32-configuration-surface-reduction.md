# WP-SIMPL-32 Configuration surface reduction (<= 600 properties)
Status: Ready for implementation
Parent: [plan](../2026-11-05-plan-project-simplification.md#wp-simpl-32-configuration-surface-reduction-req-simpl-064) | Issues: #514, #515 | Owner decisions: OD-30 (ReBAC keys removed by WP-SIMPL-46, SEC-AZ-08; nothing ReBAC-related is left here), OD-8, OD-3 and OD-26 (opt-in write switches stay), OD-18 (procedures opt-in stays), OD-24 (OData core) | Security conditions: SEC-SIMPL-31, 32, 61, 62

> **Rule (plan resolution R13):** opt-in switches of kept features stay (e.g. `SqlEndpoints:Procedures:Enabled`, `WebSql:Enabled`, write switches). This WP deletes only flags of deleted features and default-on flags of always-on core features.

## 1. Goal and outcome
After deletions and module moves, reduce the core configuration surface to: `*Options` classes <= 90 (PRD baseline 173), settable properties <= 600 (~1,173), `bool *Enabled` <= 40 (96), undocumented property names 0 (335). Module options live in modules. Every removed key fails fast at startup.

## 2. Preconditions and dependencies
- Merged: WP-SIMPL-06 (reference generator, `ConfigReferenceTests`), WP-SIMPL-07 (ratchets), WP-SIMPL-46 (ReBAC keys already removed), WP-SIMPL-32 PR 1 (#841: inventory script and budget ratchets).
- Module moves: see "Audit 2026-11-19" (the module PRs WP-SIMPL-28 PR 4 and WP-SIMPL-29 PR 3 must be merged before PR 3 and PR 5 of this WP; a precise order is given there). WP-SIMPL-26 is OData core hardening, WP-SIMPL-30 is cancelled.
- Must precede WP-SIMPL-33. Not parallel with module WPs touching options.

## 3. Scope
### In scope
- Merge small option classes per subsystem (one `<Subsystem>Options` with nested records).
- Remove `*Enabled` flags of deleted features and default-on `*Enabled` flags of always-on KEEP features; the feature becomes unconditional. Opt-in switches (default off) of KEEP features stay (step 3).
- Move remaining module option classes into modules; lower ratchet baselines to targets.
### Out of scope
- Renaming sections that are not merged; changing defaults of security controls (SEC-SIMPL-62).

## 4. Current state (verified on 63cbb893, before module waves)
- `src/Autheris.Domain/Options/**`: 40 files; ~1,137 `{ get; set|init; }` properties there; 74 `bool *Enabled` properties there. PRD count over all `src/`: 173 classes / ~1,173 props / 96 `*Enabled`.
- Guard: `Api/Configuration/UnknownGatewayKeyGuard.ThrowIfUnknownKeys` rejects keys outside `GatewayConfigurationSchema`.
- Baselines (as created on main, see Audit 2026-11-19): `options-class-count.txt`, `enabled-flag-count.txt`, `options-consumed-indirectly.txt`, `removed-options.txt`, `secure-defaults.txt`; the budget ceilings are constants in `OptionsBudgetTests`.

## 5. Implementation steps
1. Script `tools/options-inventory/options_inventory.py` (new, no build): lists every `*Options` class, property, consumer count (git grep), `Enabled` flags and security-name match (`Enforce|Require|Allowed|Deny|Mask|Pii|Tls|Auth`). Output CSV into `artifacts/` (not committed); summary numbers in the PR.
2. Red: add `OptionsBudgetTests` (section 6) with targets; fails today.
3. For each `*Enabled` (or `Allow*`) flag, in this order:
   - security-named or guarding a fail-closed default -> keep or make unconditional-secure (SEC-SIMPL-32);
   - opt-in switch of a KEEP feature (default off) -> keep. These include `SqlEndpoints:Procedures:Enabled` (OD-18, WP-SIMPL-39 kill switch), `WebSql:Enabled` (`WebSqlOptions.Enabled`, default false; WP-SIMPL-38 relies on it), `WebSqlOptions.AllowDml` and `WebSql:Dml:AllowDeleteGrants` (OD-3, OD-26: DELETE disabled by default), `ForwardAuthOptions.Enabled` (default false), and any switch named by an OD or by a WP "Decisions taken" section;
   - default-on flag of a KEEP feature -> delete flag, feature always on (removed key fails fast, step 4);
   - flag of a deleted feature -> delete with the feature's keys (removed key fails fast);
   - flag of a MODULE feature -> module presence replaces the flag.
   The inventory CSV of step 1 carries the chosen branch per flag; the PR lists every kept opt-in switch.
4. Merge classes per subsystem; keep configuration paths stable where possible; changed paths are listed in the migration guide and added to `UnknownGatewayKeyGuard` removed-key list with replacement message (SEC-SIMPL-31; fails also for `false`/empty).
5. Regenerate configuration reference (WP-SIMPL-06 generator); document every remaining property (undocumented = 0).
6. Lower ratchet baselines to targets; update `appsettings.json` and Helm values.

## 6. Tests
- `tests/Autheris.Tests.Architecture/OptionsBudgetTests` (exists, PR 1): `Options_classes_ratchet`, `Settable_properties_ratchet`, `Enabled_flags_ratchet`, `Undocumented_properties_ratchet`. They are exact-match ratchets (a stale ceiling fails), so every PR lowers the ceiling constants to the measured value; the final PR sets them to the targets or to the measured floor.
- `OptionsAuditTests.No_enforcement_option_deleted_while_wired` [Security] (SEC-SIMPL-32).
- `OptionsAuditTests.Opt_in_switches_retained` [Security] (R13): the opt-in switches listed in step 3 exist, default to off and still gate their feature (Theory, one row per switch).
- `SecureDefaultsSnapshotTests.Security_defaults_unchanged` [Security] (SEC-SIMPL-62).
- `ConfigReferenceTests.Secret_properties_have_no_default` [Security] (SEC-SIMPL-61).
- `RemovedConfigKeysTests.Removed_key_fails_fast(string key)` [Theory, Security] - one row per removed key including `Enabled=false`.

## 7. Verification commands
- `dotnet build Autheris.sln -c Release -warnaserror`
- `dotnet test tests/Autheris.Tests.Architecture -c Release --no-build`
- `dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release --no-build --filter "Category=Security"`
- `python3 tools/options-inventory/options_inventory.py --summary`

## 8. Acceptance criteria
- [ ] Options <= 90, props <= 600, `*Enabled` <= 40, undocumented 0 (OptionsBudgetTests green).
- [ ] Every removed key in `RemovedConfigKeysTests` and failing fast.
- [ ] Security defaults snapshot unchanged; no wired enforcement option removed.
- [ ] Opt-in switches of KEEP features retained with default off (`Opt_in_switches_retained` green).

## 9. PR slicing
(Superseded by the Audit 2026-11-19 slicing below.) One PR per subsystem group (Security/Identity, Query/Sql, Governance/Audit, Runtime/State, Catalog), each <= 400 added src lines; final PR lowers baselines and enables `OptionsBudgetTests`.

## 10. Risks, rollback, must NOT
- Risk: removing an `Enabled` flag that guarded a fail-closed default -> SEC-SIMPL-32 audit per PR.
- Must NOT: make a security control configurable off; delete an opt-in switch of a kept feature (turning an off-by-default feature on); give secret-like properties defaults; silently ignore old keys.

## 11. Estimate
+~500 / -~2,500 src. 4 person-days.

## Decisions taken in this spec
- Budget counted over core `src/` only (module options excluded), consistent with PRD targets for the core.
- Opt-in switches of kept features count against the `*Enabled` budget and are never removed to meet it (R13).

## Audit 2026-11-19
Verified against `origin/main` (f8edc9f3f) with `tools/options-inventory/options_inventory.py` run on a `git archive` of main (no build). This section supersedes sections 2, 6 (test names) and 9 where they differ.

### A1. Counts on main against the targets
| Metric | Target | Main now | Gap | After WP-29 PR 3 (local) |
|---|---|---|---|---|
| `*Options` classes | <= 90 | 165 | -75 | 164 |
| Settable properties | <= 600 | 948 | -348 | 942 |
| `bool *Enabled` | <= 40 | 70 | -30 | 69 |
| Undocumented | 0 | 462 | -462 | 462 |
The plan's old numbers (173 / 1,173 / 96 / 335) are obsolete. The ceilings in `OptionsBudgetTests` equal the main counts (165 / 948 / 70 / 462). `options-class-count.txt` and `enabled-flag-count.txt` (checked by `ProgramRatchetTests`) are a second copy of two of them: each PR lowers the constants and the two baseline files together.

### A2. Names that PR 1 created (the plan used other names)
- Tests in `tests/Autheris.Tests.Architecture`: `OptionsBudgetTests.{Options_classes,Settable_properties,Enabled_flags,Undocumented_properties}_ratchet`, `OptionsAuditTests.{No_enforcement_option_deleted_while_wired,Opt_in_switches_retained}`, `SecureDefaultsSnapshotTests.Security_defaults_unchanged`, `ConfigReferenceTests.Secret_properties_have_no_default`. `RemovedConfigKeysTests.Every_removed_key_fails_startup` lives in `tests/Autheris.Tests.Unit/Configuration` (not Architecture). All of them exist; only the work below adds rows.
- Baselines: `options-class-count.txt`, `enabled-flag-count.txt`, `options-consumed-indirectly.txt`, `removed-options.txt` (77 lines; `OptionsAuditTests.RemovedOptionsCount = 76` is pinned and must be raised with every added row), `secure-defaults.txt` (never regenerated in CI). `unconsumed-options.txt` does not exist.
- The script writes `options-inventory.csv`, `options-enabled-flags.csv` (column `branch`: candidate-default-on 38, keep-opt-in 16, keep-security 7, review 9) and `options-classes.csv` into `artifacts/`. The `branch` column is a heuristic seed only; the reviewed decision per flag is part of PR 0 below.

### A3. Corrections to the plan
1. **Module options do not leave core.** After WP-27..29 the Lakehouse, CDC, dbt and OpenMetadata option classes still sit in `src/Autheris.Domain/Options` (`DistributedCacheOptions.cs` Lakehouse*, `StreamingCdcOptions.cs`, `Integrations/DataCatalogOptions.cs` `DbtOptions`, `Integrations/OpenMetadataOptions.cs`), because `GatewayOptions` has typed properties and core webhook ports read `GatewayOptions.Dbt/OpenMetadata`. WP-28 PR 3b and the PR 4 branch only edit a doc comment in `DataCatalogOptions.cs`. Only `CdcWebhooksOptions` moves (WP-29 PR 3: -1 class, -6 props, -1 flag). So "module options live in modules" is not a given; it is PR 5 of this WP (A6). Potential: about 13 classes, 93 props, 4 to 7 flags (Lakehouse 4 classes/20 props, CDC 6/47, dbt 1/6, OpenMetadata 1/14, CdcWebhooks 1/6).
2. **WP-29 PR 2a** moved `Gateway:Events:DeliveryMode` and `SubscriberQueueCapacity` to `Gateway:EventBus` and added `GatewayConfigurationSchema.EnforcedModuleSections` (a module section whose keys fail startup while the module is absent). The removal is a `RemovedEventBusKeys` list in `UnknownGatewayKeyGuard` with its own early check that runs before module-section checks. Rebase PR 4/5 of this WP onto it; `SideBound` still lists `Gateway:Events` -> `EventBusOptions` on main.
3. **The classes budget is gameable.** The scanner counts only `class XxxOptions` and only `{ get; set|init; }` properties. Turning classes into `record`s with positional parameters, or nesting types with other names, lowers the numbers without lowering the surface. Rule: merged and nested types stay `class` with settable properties (what the scanner counts), or PR 0 extends the scanner first to count record parameters. Enforce it with a test in `OptionsBudgetTests` (`Scanner_counts_record_parameters` or "no positional records in `Domain/Options`").
4. **Merging classes changes paths.** The schema is derived by reflection from `GatewayOptions` plus `GatewayConfigurationSchema.SideBound` (29 side-bound paths). A renamed or merged section needs a `SideBound` edit and a replacement message; a deleted property is automatically an unknown key (fails with any value, including `false` and empty, with the message "Unknown configuration keys under 'Gateway'"). `RemovedFlagKeys` in the guard currently holds one row (`CatalogSearch:Enabled`) and only adds a hint message; `RemovedSimpl12Prefixes`, `RemovedAuthzPrefixes`, Casbin, FinOps and Audit:Worm are prefix lists. Rule for this WP: for a deleted flag add a row to `RemovedFlagKeys` (key plus replacement text) and rows `key=true`, `key=false`, `key=` (empty) to `RemovedConfigKeysTests.Every_removed_key_fails_startup`. A section whose only property was the flag disappears entirely, so also add the section prefix. Keys of a loaded module's claimed section skip the check (`IsClaimedByLoadedModule`), so module-owned keys need the module's own validator.
5. **Opt-in list.** The five switches of step 3 match `Opt_in_switches_retained` (5 rows: `SqlEndpoints:Procedures:Enabled`, `WebSql:Enabled`, `WebSql:AllowDml`, `Authentication:ForwardAuth:Enabled`, `WebSql:Dml:AllowDeleteGrants`). The inventory shows 16 more default-off flags of kept features with no row: Federation, DataCatalog, CustomQueryPoller, GraphQL Subscriptions, GraphQL SlowListReport, Mcp, WebSql CrossSource, CoLocation, ProcedureEndpoints (already row 1 by path, `ProcedureEndpointsOptions`), MssqlChangeTracking, PostgreSqlCdc, Lakehouse, Redis, TableCache, SchemaContracts, plus the auth switches BasicAuth, BasicAuthSession, EntraId, Adfs. PR 0 adds a row per switch (module sections Lakehouse and CDC excepted: module presence replaces them, A5). Tests, deploy and Helm set `WebSql:Enabled` (24 test references), `Mcp:Enabled` (16), `WebSql:CrossSource:Enabled`, `DuckDbOlap:Enabled`, `Arrow:Enabled`; these stay valid.
6. **Feasibility of the targets under R13 and SEC-SIMPL-32.** Of the 38 default-on flags: 9 are security-named and stay (`AuditRetention`, `Audit`, `ScimMassChangeProtection`, `HitLStepUp`, `ReverseProxy`, `RestrictedScope`, `EpochValidation`, `TableCacheEncryption`, `TenantRegistry`), 3 belong to modules (`OpenMetadataPolicy`, `DbtOptions`, `CdcWebhooks`), and about 6 are operational kill switches or test seams (`StartupWarmup` plus step, `SystemMetrics`, `SqlEndpoints`, `RestDataEndpoint` with the PLAN-SEC-115 read-path switches, `ColdPathTiming`). That leaves about 20 deletable flags: 70 - 20 - module moves (3 to 7) = about 43 to 47, which is above 40. The 600-property target needs -348 properties; module moves give at most -93, flag deletion about -20, so about -235 tuning knobs must be hard-coded or deleted (the inventory marks 226 properties with <= 2 references outside their file, 25 unconsumed, but those counts are a name-match heuristic). Class merging alone reduces no properties. See owner questions.

### A4. Per-group numbers on main (real counts from the inventory)
Group membership is by file (list per PR below). Classes are `*Options` classes.
| Group | Classes | Props | Undocumented | `*Enabled` (default-on / keep-opt-in / security / review) |
|---|---|---|---|---|
| 1 Security/Identity | 39 | 217 | 106 | 15 (6 / 1 / 7 / 1) |
| 2 Query/Sql | 38 | 234 | 136 | 25 (14 / 7 / 0 / 4) |
| 3 Governance/Audit | 20 | 104 | 29 | 5 (5 / 0 / 0 / 0) |
| 4 Runtime/State | 56 | 321 | 151 | 20 (10 / 7 / 0 / 3) |
| 5 Catalog | 12 | 72 | 40 | 5 (3 / 1 / 0 / 1) |
| Total | 165 | 948 | 462 | 70 |

### A5. Order and dependencies
- WP-28 PR 3 (dbt, branches `refactor/simpl-28-pr3a/3b/3c`) and WP-29 PR 2a, 2b and 3 are implemented locally but unmerged; WP-28 PR 4 (OpenMetadata) is planned (Phase 3 and 4a in progress). They move code and routes but no option class except `CdcWebhooksOptions`.
- Order: PR 0 and PR 1 (Governance/Audit) do not touch module sections and may start once PR 1 (#841) is on main. PR 2 (Catalog) waits for WP-28 PR 4. PR 3 (Security) and PR 4 (Query) have no module dependency. PR 5 (Runtime/State) waits for WP-29 PR 3 and the CDC and Lakehouse module state; PR 6 (module option move) waits for WP-28 PR 4 and WP-29 PR 3. PR 7 (final) last.
- Not parallel with each other: every PR edits the same four ceiling constants, two baseline files, `removed-options.txt`, `RemovedOptionsCount`, `secure-defaults.txt` and `docs/reference/configuration.md`. Serialize PRs; the gain from parallel lanes is lost in rebase conflicts.
- Where a flag decision is "module", module presence replaces the flag: the key moves to `KnownModuleSections`/`EnforcedModuleSections` and the module claims the section.

### A6. Slicing and Hydrogen-ready work packages
"One PR per group plus a final PR" is not Hydrogen-ready (no file lists, no per-flag decisions, groups 2 and 4 exceed the 400-line cap with the 136 and 151 documentation lines alone). Replace with these PRs. Size = changed src lines (deleted lines are cheap, documentation lines count as added); limit 400 added per PR.

**Common for every PR (Hydrogen brief)**
- Inputs: `artifacts/options-enabled-flags.csv` and `flag-decisions.csv` (PR 0) for the group's rows.
- Per deleted flag or property: delete from the class, from consumers, from `appsettings*.json`, `deploy/helm/autheris/values*.yaml`, `deploy/.env.example`, `deploy/podman-compose*.yaml`, `deploy/containers/gqlgateway-api/appsettings.Benchmark.json`; append a row to `removed-options.txt` and raise `RemovedOptionsCount`; add `RemovedFlagKeys` plus `RemovedConfigKeysTests` rows (true, false, empty); lower the ceiling constants, `options-class-count.txt`, `enabled-flag-count.txt`; regenerate `docs/reference/configuration.md` via the WP-SIMPL-06 generator. Document every remaining property of the group in the same PR (XML summary), so the group's undocumented count reaches 0; do not document properties the PR deletes.
- Integration impact: grep Integration, Parity and Unit tests for each removed key (`Gateway:<Section>:<Prop>` and `Gateway__...`) and rewrite those that set it. Known test users of default-on flags, by group below. Compile-check `Autheris.Tests.Integration` locally; execution on GitHub Actions.
- Local run: `OptionsBudgetTests`, `OptionsAuditTests`, `SecureDefaultsSnapshotTests`, `ConfigReferenceTests`, `ProgramRatchetTests` (Architecture) and `RemovedConfigKeysTests`, `UnknownGatewayKeyGuardTests` (Unit), plus tests of touched classes.
- Phase 3: SEC-SIMPL-31 (removed key fails with any value), SEC-SIMPL-32 (no wired enforcement option removed: `OptionsAuditTests.No_enforcement_option_deleted_while_wired`), SEC-SIMPL-61 (secret-like properties have no default), SEC-SIMPL-62 (`secure-defaults.txt` diff reviewed: only deletions of paths whose flag became unconditional-secure, never a changed value to a weaker default, never regenerated in CI).

**PR 0 - Decisions and test scaffolding (S, about 150 lines, no behavior change)**
- Add `tools/options-inventory/flag-decisions.csv` (committed): one row per `*Enabled` and `Allow*` flag with the branch (keep opt-in / unconditional / delete / module), reason and owning PR. Seed from `options-enabled-flags.csv`; the 9 security-named, 6 kill-switch and 3 module flags above are the open rows.
- New tests: `OptionsAuditTests.Every_enabled_flag_has_a_decision` (inventory row count equals CSV rows; a new flag fails until decided), extra `Opt_in_switches_retained` rows for the 16 switches in A3.5, `OptionsBudgetTests` anti-gaming rule (A3.3).
- Phase 3: SEC-SIMPL-32 review of the CSV (the only place security flags are kept by name).

**PR 1 - Governance/Audit (M, about 250 lines)**: files `Domain/Options/{Governance,GovernanceText,GdprReport,ConsentEvents,GrantValidity,IndividualGrant,AccessPlan,Classification,UsageAccounting,ExtensibilityPipeline,Webhooks}Options.cs`, `Security/AuditLoggingOptions.cs`, `Application/Audit/AuditGroupCommitSink.cs`, `Domain/Model/SchemaSunsettingModels.cs`. 20 classes, 104 props, 29 undocumented, 5 flags (all default-on: `GovernanceQueryAnalysis`, `Extensibility`, `Webhooks`, `AuditRetention`, `Audit`). Decisions: `AuditRetention` and `Audit` stay (security, audit chain); the other three are delete candidates (unconditional) after checking `Extensibility` and `Webhooks` for kill-switch use. Tests touched: `AuditRetentionSecurityTests`, audit integration configs. Phase 3: SEC-SIMPL-32 and 62 (audit paths match the snapshot regex). Pilot PR: it fixes the process before the larger groups.

**PR 2 - Catalog (S-M, about 200 lines; after WP-28 PR 4)**: files `Domain/Options/{CatalogSearch,CatalogLocalization,Analytics}Options.cs`, `Application/Catalog/Services/CatalogDiscoveryOptions.cs`, `Integrations/{CatalogCache,DataCatalog,OpenMetadata}Options.cs`, `Application/Catalog/OpenMetadata/OpenMetadataModels.cs` (`OpenMetadataPolicy`). 12 classes, 72 props, 40 undocumented, 5 flags (`OpenMetadataPolicy`, `DbtOptions` = module; `AggregationOptions` = delete candidate; `DataCatalog` keep opt-in; `OpenMetadataOptions` review/module). Decision on `Dbt`/`OpenMetadata` options belongs to PR 6; PR 2 only documents them. Impact: `deploy/podman-compose.openmetadata.yaml`, `Gateway__OpenMetadata__Enabled`, `Gateway__Catalog__Enabled` in deploy files; `Gateway:Catalog:Enabled=true` without the OpenMetadata module fails startup after WP-28 PR 4, so docs and compose files must change together.

**PR 3 - Security/Identity (L, split 3a and 3b, about 300 lines each)**: 3a `Security/{Jwt,IdentityRealms,ApiKey}Options.cs`, `Plugins/AuthPluginOptions.cs`, `Domain/Model/EnvoyAuthModels.cs`, `Security/EnvoyExtAuthzOptions.cs`; 3b `Security/{Rls,OutboundEgress,RestrictedScope,DataMasking}Options.cs`, `Tenancy/*`, `RevocationWatchOptions.cs`, `Resilience/RateLimitingOptions.cs`. Together 39 classes, 217 props (93 security-named), 106 undocumented, 15 flags (7 keep-security: AuthPlugin, ForwardAuth, BasicAuth, BasicAuthSession, EntraId x2, Adfs; 6 default-on of which `ScimMassChangeProtection`, `HitLStepUp`, `ReverseProxy`, `RestrictedScope`, `TenantRegistry` stay; `AsyncJob` is a delete candidate; 1 review `CallerAttribute`). Almost no flag is deletable here, so this PR is mostly merges and documentation; per-property deletion needs SEC-SIMPL-32 review per row. Tests using the flags: `Gateway:Authentication:{BasicAuth,EntraId,Adfs,ForwardAuth}:Enabled` (9 test refs), `ReverseProxy:Enabled` (2), `CallerAttributes:Enabled` (1), `SecurityFindingsRemediationTests`. Phase 3: all four SEC-SIMPL ids, most review effort of the WP.

**PR 4 - Query/Sql (L, split 4a and 4b, about 350 lines each)**: 4a `Protocols/RestApiOptions.cs` (12 classes, 74 props, 10 flags), `Protocols/GraphQlOptions.cs` (7 classes, 27 props, 6 flags), `Domain/Model/OpenApiModels.cs`, `Application/Sql/*`; 4b `Protocols/{OData,Mcp,AppScopedTrustedDocuments}Options.cs`, `Resilience/QueryGovernorOptions.cs`, `QueryCostOptions.cs`, `SqlParserCacheOptions.cs`, `DocsOptions.cs`, `SemanticOptions.cs`, `Application/Mcp/*` (`ReadPathSwitches` has 3 flags, PLAN-SEC-115 kill switches: keep), `Application/Governance/Analysis/QueryAnalysisModels.cs`, `src/TrinoSqlEngine/**` option classes (5 files, 52 props). Together 38 classes, 234 props, 136 undocumented, 25 flags (14 default-on, 7 keep-opt-in, 4 review). Do not touch `WebSql:Enabled`, `AllowDml`, `AllowDeleteGrants`, `Procedures:Enabled`, `CrossSource`, `Mcp:Enabled`. Delete candidates: GraphQL Metrics/Aggregation/IncrementalDelivery/GoldenQuery, `WebSqlPaging`, `WebSqlCost`, `DynamicKeySet`, `PredicatePushdown`, `ParquetEgress`, `ArrowExport`, `ResourceGroups`. Keep-or-owner (seams): `SqlEndpoints:Enabled`, `RestApi:DataEndpoint:Enabled`, `OData:Enabled`. Tests setting these: `ReadPathSwitchIntegrationTests` (DataEndpoint=false, 4 places), `ReadOnlyTokenGetRouteTests` and `RemovedAuthzRoutesTests` (`SqlEndpoints:Enabled=false`), `AggregationOptionsTests` (`GraphQL:Aggregation:Enabled=false`), OData (7 refs), Arrow (4). Conflicts with WP-SIMPL-38 (Trino) which edits `TrinoSqlEngine` and the Trino options: do 4b's Trino rows after WP-38 PR 5.

**PR 5 - Runtime/State (L, split 5a and 5b, about 350 lines each; after WP-29 PR 3)**: 5a `Storage/{Database,DistributedCache,GovernancePersistence,Redis}Options.cs`, `TableCache/*`, `StateOptions.cs`, `EventBusOptions.cs`; 5b `Observability/TelemetryOptions.cs`, `Api/Hosting/Warmup/*`, `Resilience/DegradedModeOptions.cs`, `DataSources/*`, `Protocols/{StreamingCdc,CustomQueryPoller}Options.cs`, `FederationOptions.cs`, `DevOptions.cs`, `DemoDataSwitch.cs`, `DatabricksOptions.cs`, `HttpDataSourceOptions.cs`, `VirtualFilterFederationOptions.cs`, `GatewayOptions.cs`, `Application/DataSources/*`. Together 56 classes, 321 props, 151 undocumented, 20 flags (10 default-on, 7 keep-opt-in, 3 review). Delete candidates: `Telemetry` and `Propagation`, `SystemMetrics`, `SoftDelete`, `VirtualFilterFederation`, `EpochValidation` stays (security). Seams: `Startup:Warmup:Enabled` and `Startup:ColdPathTiming:Enabled` (integration tests `HealthReadyEndpointTests`, `StartupWarmupReadinessTests`, `StartupWarmupFoundationTests`, units `StartupWarmupTests`, `ColdPathTimingTests` set them) and `SystemMetrics:Enabled` (`ColumnlessCatalogTableStartupTests`). `TableCacheEncryption` stays. `Gateway:Events` to `Gateway:EventBus` removal rows already exist from WP-29 PR 2a. Phase 3: SEC-SIMPL-62 for encryption and revocation paths.

**PR 6 - Module-owned options (M-L, about 300 lines, owner decision first)**: move `LakehouseOptions` family, CDC options (`CdcStream*`, `MssqlChangeTracking`, `PostgreSqlCdc`, `CdcTrackedTable`), `DbtOptions` and `OpenMetadataOptions` from `src/Autheris.Domain/Options` into their modules, bound from the module's claimed section, with the core webhook ports (dbt, OpenMetadata) reading a port instead of `GatewayOptions.Dbt/OpenMetadata`. Impact: 13 classes, about 93 props, 4 to 7 flags leave the core count. Costs: `modules/` is outside `RepositoryScan` (the ratchets will not see module options, so add a `modules/` budget or accept it; the core target stays "core src only"), CODEOWNERS entries, DI snapshots and `ModuleHttpClientGuard` unchanged. Phase 3: SEC-28-xx and SEC-SIMPL-31 (section without module fails; `EnforcedModuleSections`), webhook secrets stay core-validated. If the owner declines, PR 6 is dropped and the targets change (owner question 2).

**PR 7 - Final baseline (S, about 60 lines)**: set the four ceiling constants and the two baseline files to the targets (or the agreed floor), rename nothing, update `docs/reference/configuration.md`, and add the migration-guide section of removed keys for WP-SIMPL-34 (list from `removed-options.txt` and `RemovedFlagKeys`). The acceptance criteria in section 8 are checked here, not earlier.

### A7. Owner questions (with defaults)
1. Keep the target `*Enabled` <= 40 and properties <= 600? Estimated floor under R13 and SEC-SIMPL-32 is 43 to 47 flags and about 700 to 750 properties unless tuning knobs are hard-coded. Default: keep classes <= 90 and undocumented 0, set flags to <= 48 and properties to the measured floor after PR 4 (mid-point review), never weaker than 650.
2. Move module options into the modules (PR 6)? Default: yes for CDC, dbt, OpenMetadata and Lakehouse; core keeps only ports.
3. Are `Startup:Warmup:Enabled`, `Startup:ColdPathTiming:Enabled`, `SystemMetrics:Enabled`, `SqlEndpoints:Enabled`, `RestApi:DataEndpoint:Enabled` (SEC-115 kill switch), `OData:Enabled` operational kill switches that stay? Default: keep all six (integration tests and ops use them), counted against the budget.
4. Is a documentation-only sweep allowed to start before deletions (it would conflict by file)? Default: no; document per group inside the group PR.
5. Replace `ForwardAuth`-style auth switches that are default-off by module presence? Default: no (R13).

## Changelog
- 2026-11-19 audit: counts, names and baselines refreshed; module-option assumption corrected; dependency order and per-PR work packages (PR 0 to 7) added; owner questions listed.
