# WP-SIMPL-06 Delete unconsumed options; generated configuration reference
Status: Ready for implementation
Parent: [plan](../2026-11-05-plan-project-simplification.md#wp-simpl-06-delete-unconsumed-options-generated-configuration-reference-514) | Issues: #514, #477 (unused-options part only) | Owner decisions: OD-17 (accepted defaults) | Security conditions: SEC-SIMPL-04, SEC-SIMPL-31, SEC-SIMPL-32, SEC-SIMPL-61, SEC-SIMPL-62

## 1. Goal and outcome
Every option property that no code reads is deleted and fails startup if still configured; a generated, checked-in configuration reference documents every remaining bound option. Measurable: `Baselines/unconsumed-options.txt` deleted (45 entries -> 0), `docs/reference/configuration.md` generated and test-guarded, undocumented option properties 0, secret-like properties render as `<secret>`.

## 2. Preconditions and dependencies
- None blocking. Runs parallel to WP-SIMPL-04/05 (WP-SIMPL-05's link gate must accept `docs/reference/configuration.md`; it only contains backticked source paths that exist).
- WP-SIMPL-14 (Casbin), 12 (catalog vendors), 15 (Garnet), 16 (FinOps) depend on this WP and delete whole classes later.
- #477 pre-filled security allowlists are NOT here (security lane WP-SIMPL-36).

## 3. Scope
### In scope
- Delete the 45 properties listed in `tests/Autheris.Tests.Architecture/Baselines/unconsumed-options.txt` and the file; remove the baseline escape in `OptionsConsumptionInventoryTests`.
- Add each removed key to `UnknownGatewayKeyGuard` handling (SEC-SIMPL-31) and to shipped config samples cleanup (`appsettings*.json`, Helm values, compose) - `ShippedConfigurationKeysTests` must stay green.
- `GenerateDocumentationFile` + `CS1591` suppression on options-bearing projects; XML summaries for every bound option property.
- `ConfigurationReferenceTests` generator/comparer and `docs/reference/configuration.md`.
### Out of scope
- `CasbinOptions.EnforceInQueryPipeline`, `McpOptions.RequirePiiMasking`: already removed (asserted by `tests/Autheris.Tests.Unit/Configuration/DeadSecurityOptionsRemovedTests.cs:28,57`; guarded in `UnknownGatewayKeyGuardTests.cs:220-222`). Nothing to do.
- `ScimOptions.AllowedIpRanges`: WIRED and enforced (`src/Autheris.Api/Security/ScimAuthenticationHandler.cs:57`, `src/Autheris.Application/Identity/ScimIpAllowlist.cs`, validator `IdentityRealmsScimOptionsValidator.cs:33`; property `src/Autheris.Domain/Options/Security/IdentityRealmsOptions.cs:46`). Per SEC-SIMPL-32 it STAYS (plan text superseded).
- Merging option classes, `*Enabled` flag removal (WP-SIMPL-32). Pre-filled security allowlists (WP-SIMPL-36). ci.yml artifact upload (optional `[WF]`, skipped).

## 4. Current state (verified on 63cbb893)
- Baseline `tests/Autheris.Tests.Architecture/Baselines/unconsumed-options.txt`: 45 entries, consumed by `OptionsConsumptionInventoryTests.cs:35` (`RepositoryScan.ReadBaseline("unconsumed-options.txt")`). The plan's name `GatewayOptionsConsumptionRatchetTests` is a different test (size/ratchet of `GatewayOptions`, 112 lines) and is unaffected.
- Entries by class: WebhooksOptions 9 (AutoDisableAfterConsecutiveFailures, CircuitCooldownSeconds, DeliveryBatchSize, DeliveryWorkers, MaxPayloadBytes, MaxRetryAfterSeconds, PollIntervalSeconds, RetryBaseSeconds, RetryMaxDelaySeconds); AuditOptions 4 (CatalogReadMode, CatalogSummaryWindowSeconds, ElasticsearchSinkUrl, TierBAggregationWindowSeconds); QueryCostOptions 3; MetricsOptions 3 (Data/Default/TenantCardinalityLimit); RebacOptions 2 (OpenFgaApiUrl, OpenFgaStoreId at `RlsOptions.cs:47-48`); GatewayOptions 2 (CustomQueryPoller, OpenSchema); single entries: AuditGroupCommitOptions.MaxBatchDelayMs, TenantRegistryGateOptions.CacheTtl, EnvoyFilterExportOptions.PathPrefixes, OpenApiDocumentOptions.IncludeGovernanceMetadata, AlationOptions.CustomFieldIdPii, AuthenticationOptions.GroupCacheTtlMinutes, CatalogSearchOptions.FieldWeights, CdnOptions.PurgeMode, CollibraOptions.CommunityId, DataCatalogOptions.SyncMode, DataMaskingOptions.MaskingCacheTtlHours, DatabricksOboOptions.AllowLongRunning, FederationOptions.GatewayPackagePath, FinOpsOptions.ScopeMonthlyBudgets, GarnetOptions.DisableConsole, GovernanceDbOptions.EnableOutboxProcessor, L1MemoryCacheOptions.DefaultTtlMinutes, LakehouseOptions.MaxConcurrentFileScans, RedactionOptions.AttributeValueLengthLimit, IdentityRealmsOptions.DefaultRealm, TableCacheTargetOptions.CronExpression, WorkflowOptions.ActiveProfile.
- `options-consumed-indirectly.txt`: 11 entries (stay; not in scope).
- `src/Autheris.Api/Configuration/GatewayConfigurationSchema.cs:23` maps 32 `(section, typeof(...))` pairs; `UnknownGatewayKeyGuard.cs` fails startup on unknown keys; `src/Autheris.Api/Configuration/InertOptionWarnings.cs` warns on inert options (remove warnings for deleted properties).
- No project sets `GenerateDocumentationFile` today.

## 5. Implementation steps (TDD order)
1. RED (removal guard): in `tests/Autheris.Tests.Unit/Configuration/UnknownGatewayKeyGuardTests.cs` add `[Theory] Removed_unconsumed_option_keys_fail_startup(string key)` with one key per affected class (e.g. `Gateway:Webhooks:DeliveryWorkers`, `Gateway:QueryCost:BaseQueryCost`, `Gateway:Rebac:OpenFgaApiUrl` - take exact section paths from `GatewayConfigurationSchema`), values `"false"` and `""` included (SEC-SIMPL-31). Also add `DatabricksOboOptions.AllowLongRunning` and `AuthenticationOptions.GroupCacheTtlMinutes` keys.
2. RED (SEC-SIMPL-32): `tests/Autheris.Tests.Architecture/OptionsAuditTests.No_enforcement_option_deleted_while_wired` - for every name in a checked-in list `Baselines/removed-options.txt` (the 45 names, created in this step) containing Enforce|Require|Allowed|Deny|Mask|Pii|Tls|Auth|Redaction, assert the property no longer exists AND no `src/**/*.cs` file references `.<PropertyName>` on that type (text grep). Flagged entries in this WP: `AlationOptions.CustomFieldIdPii`, `AuthenticationOptions.GroupCacheTtlMinutes`, `DataMaskingOptions.MaskingCacheTtlHours`, `RedactionOptions.AttributeValueLengthLimit`; the PR description states for each "unwired: grep shows no consumer" (paste grep command).
3. GREEN: per entry, `git grep -n '<PropertyName>' -- src` to confirm no reader (bind-only/validator-only use counts as unconsumed; delete validator branches too). Delete the property, its validator rules, `InertOptionWarnings` lines, sample config keys, and its tests. Delete `unconsumed-options.txt`; in `OptionsConsumptionInventoryTests` replace baseline comparison with "unconsumed set must be empty". If any entry turns out to be read indirectly, move it to `options-consumed-indirectly.txt` with a comment and record it in the PR.
4. RED (reference): add `tests/Autheris.Tests.Architecture/ConfigurationReferenceTests.cs`. Generator `ConfigurationReferenceGenerator` (internal static, same project): iterate `GatewayConfigurationSchema` entries (expose a public read-only accessor if it is private), reflect public settable/init properties recursively (nested option classes, collections as `list<T>`, dictionaries as `map<string,T>`), default = value from `new T()` (`Activator.CreateInstance`), description = XML `<summary>` from `<Assembly>.xml` next to the dll. Output: markdown table per section, columns `Key | Type | Default | Description | Source file` (source file = `src/...` path found by type-name grep, backticked). Never reads `IConfiguration`, files or environment (SEC-SIMPL-61).
5. Secret handling: property name contains Password|Secret|Key (as suffix or whole word, not `KeySet`/`Keys` collections of non-secrets - use `(?:^|[a-z])(Password|Secret|ApiKey|Key|Token|ConnectionString|Credential|PrivateKey)$`) -> Type shown, Default `<secret>`; generator throws when such a property has a non-null, non-empty default (SEC-SIMPL-04/61).
6. GREEN: in `src/Directory.Build.props` (or each options-bearing csproj: Domain, Application, Api, Infrastructure, Extensions) set `<GenerateDocumentationFile>true</GenerateDocumentationFile>` and `<NoWarn>$(NoWarn);CS1591</NoWarn>`; add missing `/// <summary>` to options properties until `Every_option_has_a_description` passes. Generate with `AUTHERIS_UPDATE_CONFIG_REFERENCE=1 dotnet test ... --filter Reference_is_current` and commit `docs/reference/configuration.md` (header: "Generated by ConfigurationReferenceTests; do not edit"). Link it from `docs/configuration-guide.md`.

## 6. Tests
- Unit `UnknownGatewayKeyGuardTests.Removed_unconsumed_option_keys_fail_startup` [Trait("Category","Security")] - startup fails, error names key, never value.
- Architecture `OptionsConsumptionInventoryTests` (existing) - now asserts unconsumed set is empty, no baseline file.
- Architecture `OptionsAuditTests.No_enforcement_option_deleted_while_wired` [Trait("Category","Security")] - SEC-SIMPL-32/62.
- Architecture `ConfigurationReferenceTests.Reference_is_current` - generated text == checked-in file (normalize LF); with env var set, rewrites file and passes.
- `ConfigurationReferenceTests.Every_option_has_a_description` - no empty description for any bound property.
- `ConfigurationReferenceTests.Secret_like_options_never_render_values_or_defaults` [Trait("Category","Security")] - SEC-SIMPL-61 test name per sec doc is `ConfigReferenceTests.Secret_like_options_never_render_values_or_defaults`; implement under that exact class name `ConfigReferenceTests` (separate small class) to match the binding condition; asserts `<secret>` rendering and that a fake options type with a defaulted secret makes the generator throw.
- `ConfigurationReferenceTests.Generator_does_not_read_configuration_or_environment` [Trait("Category","Security")] - set env var `Gateway__Webhooks__Url=leak-canary` and assert output lacks `leak-canary`.
- Delete unit tests that only asserted defaults/binding of removed properties (grep test names for each property).

## 7. Verification commands
- `dotnet build Autheris.sln -c Release -warnaserror` (check CS1591 is suppressed, no new warnings)
- `dotnet test tests/Autheris.Tests.Architecture -c Release --no-build --filter "FullyQualifiedName~ConfigurationReferenceTests|FullyQualifiedName~ConfigReferenceTests|FullyQualifiedName~OptionsConsumptionInventoryTests|FullyQualifiedName~OptionsAuditTests|FullyQualifiedName~ShippedConfigurationKeysTests"`
- `dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release --no-build --filter "FullyQualifiedName~Configuration"`
- `git ls-files | grep unconsumed-options.txt` -> empty. One build at a time; clean bin/obj after push.

## 8. Acceptance criteria
- [ ] 45 properties deleted; baseline file deleted; inventory test has no baseline escape.
- [ ] Each removed key fails startup (sample per class tested), including `false`/empty values.
- [ ] `ScimOptions.AllowedIpRanges` unchanged and still enforced.
- [ ] `docs/reference/configuration.md` committed, current, every row has a description, secrets render `<secret>`.
- [ ] All tests in section 6 green; Release `-warnaserror` build green.

## 9. PR slicing
- PR 1 (-~500 src): steps 1-3 (deletions, guard tests, OptionsAuditTests). Title `refactor(config): delete unconsumed options (#514, #477)`.
- PR 2 (+~350 src incl. XML docs, + generated md): steps 4-6. XML summary lines may exceed 400 net added src lines; if so split per project (Domain options first) or request `size-exception` (doc comments only).

## 10. Risks, rollback, must NOT do
- Risk: a property read via reflection/string key is misclassified; mitigate with the per-entry grep and the indirect baseline. Rollback: revert PR 1; the guard keys make reintroduction explicit.
- Must NOT delete `ScimOptions.AllowedIpRanges` or any wired Enforce/Require/Allowed/Mask/Pii/Tls/Auth option. Must NOT render real defaults of secret-like properties or read `IConfiguration` in the generator. Must NOT add a default secret anywhere (SEC-SIMPL-04). Must NOT touch whole classes scheduled for WP-SIMPL-12/14/15/16 beyond the listed properties.

## 11. Estimate
PR 1: -~500 / +~120 test lines. PR 2: +~350 src (XML docs, generator in tests), +~250 test lines, + generated md. 3 person-days.

## Decisions taken in this spec
- `ScimOptions.AllowedIpRanges` stays: verified wired; SEC-SIMPL-32 overrides the plan's deletion line.
- Casbin/MCP flags already removed by ARCH-MNT-06; no work.
- The inventory test is `OptionsConsumptionInventoryTests` (plan named the wrong class).
- SEC test kept under binding class name `ConfigReferenceTests`; other reference tests under `ConfigurationReferenceTests` (plan name).
- CI artifact upload skipped (optional `[WF]`); checked-in file is the source of truth.
