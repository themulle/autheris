# WP-SIMPL-25 Module host (AD-3)
Status: Ready for implementation
Parent: [plan](../2026-11-05-plan-project-simplification.md#wp-simpl-25-module-host-ad-3---phase-3-review-mandatory-before-merge-wf) | Issues: #515, #503 (plugin part, precondition) | Owner decisions: OD-17 (5: signing scheme specified and re-reviewed by Phase 3 before this WP), OD-21 (auth stays core), OD-24 (OData core; Lakehouse is the pilot module), OD-30 (single access decision point, ReBAC removed), OD-31 (customer DLL plugins are a separate trust domain), D3 | Security conditions: SEC-SIMPL-05, SEC-SIMPL-40..47, SEC-SIMPL-49

## 1. Goal and outcome
Introduce a runtime module host: `src/Autheris.Modules.Abstractions`, a fail-closed `ModuleLoader` with a signed manifest, a module ALC, host-owned endpoint registration, and module-aware config guarding. Measurable: the core image starts with zero modules; a test module signed by the test key loads; every negative case of section 6 stops startup with a non-zero exit and an error naming the module; customer DLL plugins (OD-31) and signed modules stay separate trust domains; a shrinking baseline of `Autheris.Api` -> `Autheris.Extensions` usages exists (the project reference itself is removed by WP-SIMPL-29, the last module WP).

## 2. Preconditions and dependencies
- Merged first: #503 plugin-loader collision fix (plan rule R5); Stage 2 exit (WP-SIMPL-17..24); WP-SIMPL-46 (single `IAccessDecisionPoint`; `RebacEndpointFilter` and all ReBAC types deleted, OD-30).
- Phase 3 must approve section 12 (signing scheme) BEFORE PR 2 is opened (OD-17 item 5). PR 1 may start in parallel with that review.
- Blocks WP-SIMPL-27, 28, 29 (module WPs, SEC-SIMPL-05). WP-SIMPL-26 is OData core hardening (no module, OD-24) and WP-SIMPL-30 is cancelled (OD-21); neither depends on this WP. No other WP may run in parallel on `Program.cs`/`GatewayApplicationBuilderExtensions.cs`.

## 3. Scope
### In scope
- New project `src/Autheris.Modules.Abstractions` (refs: ASP.NET Core abstractions, `Autheris.Domain` only).
- `src/Autheris.Api/Hosting/ModuleLoader.cs`, `ModuleAssemblyLoadContext.cs`, `ModuleEndpointRegistrar.cs`, `ModuleGraphQLRegistrar.cs`, `ModuleDirectoryPermissionCheck.cs`, `GovernanceConnectionFactory.cs` (host implementation of `IGovernanceConnectionFactory`).
- `src/Autheris.Infrastructure/Modules/Trust/` (`ModuleManifest`, `ManifestCanonicalizer`, `ManifestSignatureVerifier`, `FileHashVerifier`), SHA-256/manifest hashing and parsing helpers extracted from `PluginManager`/`PluginTrustList` (helpers only; trust decisions stay separate, step 2).
- Embedded trust anchor `src/Autheris.Api/Hosting/Keys/module-release-pub.pem` (EmbeddedResource).
- `tools/Autheris.ModuleSigner` (console, BCL only).
- `UnknownGatewayKeyGuard` module-section awareness.
- `[WF]` `Dockerfile*` `FLAVOR` arg; docker-publish.yml publishes `autheris` and `autheris-full`; release.yml signs modules.
- Shrinking baseline of `Autheris.Api` -> `Autheris.Extensions` usages (step 9). The project reference itself is removed by WP-SIMPL-29.
### Out of scope
- Moving any feature into a module (WP-SIMPL-27..29). OData stays core (OD-24, WP-SIMPL-26). Authentication mechanisms stay core (OD-21, WP-SIMPL-43).
- Removing the `Autheris.Api.csproj` -> `Autheris.Extensions` project reference (WP-SIMPL-29, after WP-SIMPL-26 moved OData out of Extensions and WP-SIMPL-27..29 moved Lakehouse, dbt, OpenMetadata, CDC and Lineage).
- Changes to customer DLL plugin behaviour (OD-31): plugins keep `Plugins:TrustedPluginHashes`; this WP only shares hashing/parsing helpers.
- Operator-supplied keys, third-party modules, collectible/unloadable ALCs, hot reload.
- Sigstore/cosign (rejected in section 12, no new dependency).

## 4. Current state (verified on 63cbb893)
- `src/Autheris.Api/Autheris.Api.csproj:8` references `Autheris.Extensions`.
- `AddGatewayExtensions` is called at `src/Autheris.Api/Extensions/DependencyInjection/GatewayExecutionServiceExtensions.cs:125`, defined in `src/Autheris.Extensions/ExtensionsServiceCollectionExtensions.cs`.
- Module-bound endpoint mappings: `GatewayApplicationBuilderExtensions.cs:221` (`MapDbtEndpoints`), `:269` (`MapIcebergRestCatalogEndpoints`). `:230` (`MapODataEndpoints`) is core (OD-24); WP-SIMPL-26 moves OData out of `Autheris.Extensions` into core.
- Plugin code: `src/Autheris.Infrastructure/Plugins/PluginManager.cs`, `PluginTrustList.cs`, `PluginAssemblyLoadContext.cs`, `Auth/AuthPluginHost.cs:187` (`IAuthPluginHost`).
- Endpoint wrappers in core: `src/Autheris.Api/Endpoints/EndpointSecurity.cs`, `WithAudit(...)` audit filter, `WithIngressTransport(...)` transport tag.
- Masking services in core: `IColumnMaskingProvider`, `IColumnMaskPolicy`, `IColumnMaskProvider`, `IMaskedColumnLookup`, `ISubgraphResultMasker`, `IFederatedMaskingCatalog` (re-check the set by grep before coding).
- Config guards: `src/Autheris.Api/Configuration/UnknownGatewayKeyGuard.cs`, `GatewayConfigurationSchema.cs`, `LegacySwitchGuard.cs`.
- No `modules/` directory; `tools/` has no signer.

## 5. Implementation steps (TDD order)
1. Abstractions (PR 1). Create `src/Autheris.Modules.Abstractions/` with:
   ```csharp
   public interface IAutherisModule {
       string Id { get; }
       IReadOnlyList<string> ConfigurationSections { get; }
       void ConfigureServices(IServiceCollection services, IConfiguration configuration, IModuleHostContext host);
       void MapEndpoints(IModuleEndpointRegistrar endpoints);
   }
   public interface IModuleEndpointRegistrar { RouteGroupBuilder MapGroup(string prefix); } // host-owned, see step 7
   public interface IModuleHostContext {
       TimeProvider Time { get; }
       string HostVersion { get; }
       IGovernanceConnectionFactory GovernanceConnections { get; } // governance store only, never a data source
       /* further governed service accessors only */
   }
   public interface IGovernanceConnectionFactory { ValueTask<DbConnection> OpenGovernanceConnectionAsync(CancellationToken ct); }
   public interface IAutherisGraphQLModule { void ConfigureGraphQL(IModuleGraphQLRegistrar graphql); } // optional, implemented next to IAutherisModule
   public interface IModuleGraphQLRegistrar { void AddTypeExtension(Type typeExtension); } // host-owned, see step 7
   ```
   Note: `MapEndpoints` takes `IModuleEndpointRegistrar`, not `IEndpointRouteBuilder` (SEC-SIMPL-43 supersedes AD-3.1). `IGovernanceConnectionFactory` opens connections to the governance store only (schema owned by the core forward-only migration runner); it never returns a data-source connection (data access goes through the governed pipeline and `IAccessDecisionPoint`). `IModuleGraphQLRegistrar` accepts only HotChocolate type extensions of existing root types; the host rejects (startup failure) a type that is not an `[ExtendObjectType]` extension or a field without authorization metadata. Add project to `Autheris.sln`.
2. Trust extraction, separate trust domains (PR 1, OD-31). Move SHA-256 file hashing and manifest JSON parsing helpers from `PluginManager`/`PluginTrustList` into `Infrastructure/Modules/Trust`; `PluginTrustList` calls the shared helpers. Only hashing/parsing helpers are shared: `Plugins:TrustedPluginHashes` never authorizes a module, the module release key never authorizes a plugin, and the plugin directory and the module root (`Gateway:Modules:Path`) must be distinct paths (startup fails when they are equal or nested). Existing plugin tests stay green unchanged.
3. Manifest and canonicalization (PR 2). `ModuleManifest` record (fields in section 12). Parsing is strict: unknown manifest fields fail, duplicate JSON members fail (SEC-SIMPL-41). `ManifestCanonicalizer.ToCanonicalBytes(manifest)` per section 12.
4. Verifier (PR 2). `ManifestSignatureVerifier.Verify(byte[] canonical, string signatureB64, ECDsa key)` using `VerifyData(..., HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)`. `FileHashVerifier` reads every file in the module folder (recursive) once into memory and compares its SHA-256 against `files[]`; unlisted file, missing file or mismatch = failure. Hash comparison with `CryptographicOperations.FixedTimeEquals`. The verified managed DLL buffers are kept as a `VerifiedModuleImage` (path -> bytes) and are the only source for loading (SEC-SIMPL-40: the bytes that load are the bytes that were verified; no re-read from disk).
5. Loader (PR 2). `ModuleLoader.LoadEnabled(WebApplicationBuilder)` called in `Program.cs` before `builder.Build()`:
   - read `Gateway:Modules:Enabled` (string array, the operator allowlist; directory contents never activate a module) and `Gateway:Modules:Path` (default `/app/modules`);
   - `ModuleDirectoryPermissionCheck`: refuse when the module root or any module folder is writable by the process user (SEC-SIMPL-42; skipped only under Development+flag);
   - per id: folder exists, manifest parses (strict, step 3), signature verifies, `id` equals folder name and enabled id, `hostVersion` equals host informational version, `abstractionsVersion` equals the loaded abstractions assembly version, file hashes over the in-memory buffers (step 4), then `new ModuleAssemblyLoadContext(verifiedImage)` and `Activator.CreateInstance(entryType)` cast to `IAutherisModule`; duplicate id fails; `ConfigureServices` exceptions are wrapped;
   - every failure throws `ModuleLoadException(moduleId, reason)`; `Program.cs` logs it and exits non-zero. No partial start.
   - post-check (SEC-SIMPL-44): host-owned security-critical services are registered before modules run; after `ConfigureServices`, assert that no descriptor for these types was added, replaced or removed by the module (snapshot `IServiceCollection` types before and after): `IAuditWriter`, `IAuthorizationHandler`, `IClaimsTransformation`, `IAuthorizationPolicyProvider`, `IAccessDecisionPoint` (ADR-020, SEC-AZ-01), the masking pipeline services listed in section 4, `IAuthPluginHost` and the plugin hook registrations (OD-31), tenant context. Any change fails startup naming the module and the type.
6. ALC (PR 2). `ModuleAssemblyLoadContext : AssemblyLoadContext(isCollectible: false)` constructed from the `VerifiedModuleImage`; every managed assembly is loaded with `LoadFromStream(new MemoryStream(verifiedBytes))`, never from a file path. `Load(name)`: names starting `Autheris.`, `Microsoft.`, `System.` or present in `AssemblyLoadContext.Default.Assemblies` return null (host); otherwise resolve only from the verified set (an assembly not in the verified set fails). If the verified set contains a DLL whose simple name exists in the default context, fail with "host assembly collision" (checked at load, before activation). `LoadUnmanagedDll` resolves `runtimes/<rid>/native/` inside the module folder only for files whose hash was verified in step 4.
7. Endpoint and GraphQL registrars (PR 3). `ModuleEndpointRegistrar.MapGroup(prefix)` returns `app.MapGroup(prefix).RequireAuthorization(<default policy name>)` plus the existing `EndpointSecurity` filters, the global rate limiter, the audit filter (`WithAudit`), the ingress transport tag (`WithIngressTransport`) and the module tag `module:<id>` (SEC-SIMPL-43). Data-access decisions inside module handlers go through `IAccessDecisionPoint` (ADR-020); no ReBAC filter exists (OD-30, WP-SIMPL-46). Modules never receive `IEndpointRouteBuilder`. After mapping, an `EndpointDataSource` scan asserts every endpoint tagged `module:*` has authorization metadata and no `AllowAnonymous` (fail startup otherwise). `ModuleGraphQLRegistrar` applies the checks of step 1 to GraphQL type extensions.
8. Config guard (PR 3). `UnknownGatewayKeyGuard` takes the set of loaded modules' `ConfigurationSections`; a section owned by a known-but-not-loaded first-party module id (static table `KnownModuleSections` in `GatewayConfigurationSchema.cs`) fails with `module <id> is not installed` (SEC-SIMPL-49).
9. Wiring and usage baseline (PR 4, last). `Program.cs`: call loader, then `ModuleEndpointRegistrar` mapping after core routes. Keep `AddGatewayExtensions`/`MapDbt`/`MapIcebergRestCatalog` calls until WP-SIMPL-27/28 move them. The `Autheris.Api.csproj:8` Extensions reference stays; its removal is owned by WP-SIMPL-29 (last module WP), which adds `PackageBoundaryArchitectureTests.Api_does_not_reference_Extensions`. Adding that test now as `[Fact(Skip=...)]` is FORBIDDEN; instead add a baseline file `tests/Autheris.Tests.Architecture/Baselines/api-extensions-usages.txt` that may only shrink.
10. Signer and workflows (PR 5, `[WF]`). `tools/Autheris.ModuleSigner`: `sign --module-dir <dir> --key-env AUTHERIS_MODULE_SIGNING_KEY` writes `module.json`; `verify --module-dir <dir> --pub <pem>`. release.yml: build modules, sign with secret `MODULE_SIGNING_KEY_P256` (environment `release`, protected), verify, publish. Dockerfile `ARG FLAVOR=core`; `full` copies `/app/modules` owned by root, mode 0555; image build runs `ModuleSigner verify` on every module and fails on unsigned (SEC-SIMPL-46). Helm values: modules volume `readOnly: true`.

## 6. Tests
Project `tests/Autheris.Tests.Unit/Hosting/` unless noted; fixtures build a tiny test module at test time from a checked-in test-only key pair under `tests/Autheris.Tests.Unit/Hosting/TestKeys/` (test key never equals release key). All `[Trait("Category","Security")]`.
- `ModuleLoaderTests`: `Unsigned_manifest_fails_startup`, `Signature_over_tampered_manifest_fails_startup`, `Hash_mismatch_fails_startup`, `Unlisted_file_fails_startup`, `Missing_listed_file_fails_startup`, `File_swapped_after_verification_is_not_loaded` (the file on disk is replaced after hashing; the loaded assembly is the verified buffer or startup fails, never the swapped file), `Unknown_manifest_field_fails_startup`, `Version_mismatch_fails_startup`, `Abstractions_version_mismatch_fails_startup`, `Id_differs_from_folder_fails_startup`, `Duplicate_id_fails_startup`, `Enabled_but_missing_fails_startup`, `Host_assembly_collision_fails_startup`, `Not_enabled_module_is_not_loaded`, `Unsigned_allowed_only_in_development_with_flag`, `Unsigned_rejected_in_production_even_with_flag`, `ConfigureServices_exception_fails_startup_naming_module`, `Module_replacing_audit_writer_fails_startup`, `Module_replacing_access_decision_point_fails_startup`, `Module_replacing_masking_service_fails_startup`, `Module_replacing_auth_plugin_host_fails_startup`, `Writable_module_directory_fails_startup`, `Valid_signed_module_loads_and_registers_services`.
- Trust domains (OD-31, plan rule R7): `ModuleLoaderTests.Plugin_trust_list_hash_does_not_authorize_module`, `.Plugin_directory_equal_to_module_root_fails_startup`, `PluginManagerTests.Signed_module_dll_is_not_loaded_as_plugin`. `PluginTrustTests.Untrusted_plugin_hash_is_not_loaded` and `NarrowingTests.AuthorizationHook_can_only_narrow` (owned by WP-SIMPL-46) and `PluginEnrichmentTests.PluginRoleEnrichment_never_grants_data_access` (owned by WP-SIMPL-43) stay green unchanged when the trust code moves; if one is missing when PR 1 opens, PR 1 adds it under that name.
- `ManifestCanonicalizerTests`: `Field_order_does_not_change_canonical_bytes`, `Signature_field_is_excluded`, `Non_ascii_and_whitespace_are_canonical`.
- `ModuleEndpointTests` (Integration, WebApplicationFactory): `Module_routes_require_authorization`, `Module_routes_carry_rate_limiter_audit_filter_and_ingress_tag`, `Module_route_with_allow_anonymous_fails_startup`, `Absent_module_routes_return_404`, `Module_graphql_field_without_authorization_fails_startup`.
- `ModuleConfigurationTests`: `Keys_of_missing_module_fail_fast`, `Keys_of_loaded_module_are_accepted`.
- Architecture (`tests/Autheris.Tests.Architecture`): `ModuleBoundaryArchitectureTests.Modules_reference_only_abstractions_domain_and_application`, `.Modules_open_no_data_source_connections` (Cecil scan for `DbConnection` subclasses/`NpgsqlConnection`/`SqlConnection`/`OracleConnection` ctor calls; governance connections come only from `IGovernanceConnectionFactory`), `.Governance_connection_factory_returns_governance_store_only` (host implementation reads only the governance connection string), `PackageBoundaryArchitectureTests.Api_extensions_usages_do_not_grow` (baseline). `.Api_does_not_reference_Extensions` is added by WP-SIMPL-29.
- `tools` test: `ModuleSignerTests.Sign_then_verify_roundtrip`, `.Verify_rejects_other_key`.
- `[WF]` workflow lint (PR 5): `ReleaseWorkflowTests.Image_build_rejects_unsigned_module` (docker-publish.yml/Dockerfile run `ModuleSigner verify` for every module of the `full` flavor and fail on an unsigned one).
- Phase 3 mandatory tests (exact names, sec plan section 4.3, SEC-SIMPL-40..47; all [Security]; each covers the scenario of the test named after "=", which may stay as an additional case): `ModuleLoaderSecurityTests.Unsigned_module_is_not_loaded` = `Unsigned_manifest_fails_startup`; `ModuleLoaderSecurityTests.Tampered_assembly_after_verification_is_not_loaded` = `File_swapped_after_verification_is_not_loaded` (buffer identity); `ModuleLoaderSecurityTests.Module_not_in_enabled_list_is_not_loaded` = `Not_enabled_module_is_not_loaded`; `ModuleLoaderSecurityTests.Writable_module_directory_refused_in_production` = `Writable_module_directory_fails_startup`; `ModuleLoaderSecurityTests.Incompatible_version_not_loaded` = `Version_mismatch_fails_startup`; `ModuleLoaderSecurityTests.Enabled_module_failure_stops_startup` = `Enabled_but_missing_fails_startup` (any failure of an enabled module stops startup; there is no `Required` flag and no operator trust root); `ModuleEndpointSecurityTests.Module_endpoint_without_authorization_unloads_module_and_fails_startup` = `Module_routes_require_authorization`; `ModuleEndpointSecurityTests.Anonymous_endpoint_requires_manifest_and_operator_approval` = `Module_route_with_allow_anonymous_fails_startup` (anonymous module endpoints are not supported at all, so the test asserts startup failure); `ModuleHostTests.Module_cannot_override_security_critical_services` = the four `Module_replacing_*` tests; `ModuleHostTests.Module_replacing_access_decision_point_fails_startup` as named; `ModuleHostTests.AllowUnsigned_fails_startup_outside_development` = `Unsigned_rejected_in_production_even_with_flag`; `ReleaseWorkflowTests.Image_build_rejects_unsigned_module` as named; `PluginTrustTests.Untrusted_plugin_hash_is_not_loaded` (R7, created by WP-SIMPL-46, kept green here when the trust code moves).
- Existing plugin tests unchanged and green (regression gate for step 2).

## 7. Verification commands
```
dotnet build Autheris.sln -c Release -warnaserror
dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release --filter "FullyQualifiedName~Hosting|FullyQualifiedName~Plugin"
dotnet test tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj -c Release
dotnet test tests/Autheris.Tests.Integration/Autheris.Tests.Integration.csproj -c Release --filter "FullyQualifiedName~ModuleEndpointTests"
git grep -n "IEndpointRouteBuilder" -- src/Autheris.Modules.Abstractions   # expect no hits
git grep -n "LoadFromAssemblyPath" -- src/Autheris.Api/Hosting           # expect no hits (load from verified buffers only)
docker build --build-arg FLAVOR=core . && docker build --build-arg FLAVOR=full .   # [WF] PR only
```

## 8. Acceptance criteria
- [ ] All tests of section 6 exist and pass; all Security-tagged.
- [ ] Core image starts with `Gateway:Modules:Enabled` empty and no `/app/modules`.
- [ ] Phase 3 sign-off on section 12 recorded in `docs/plans/2026-11-05-sec-project-simplification.md` before PR 2 merges.
- [ ] No new NuGet package (BCL `ECDsa` only); lock files unchanged except new project.
- [ ] Private key exists only as GitHub secret; `git grep -n "BEGIN EC PRIVATE KEY"` finds only test keys under `TestKeys/`.
- [ ] Customer DLL plugin behaviour unchanged (existing plugin tests green); plugin and module trust domains proven separate by the trust-domain tests of section 6 (OD-31).
- [ ] Module assemblies load only from the verified in-memory buffers (`File_swapped_after_verification_is_not_loaded` green).
- [ ] `api-extensions-usages.txt` baseline exists and may only shrink.
- [ ] Fallback AD-3.10 documented in the plan addendum (not implemented; the runtime-vs-compile-time decision is taken in WP-SIMPL-27).

## 9. PR slicing
1. Abstractions project + trust helper extraction + trust-domain tests (~300 lines).
2. Manifest, canonicalizer, verifier, ALC (buffer loading), loader + loader tests (~450).
3. Endpoint and GraphQL registrars, config guard + tests (~250).
4. Program.cs wiring, baseline architecture tests (~80).
5. `[WF]` signer tool, release.yml, docker-publish.yml, Dockerfile FLAVOR, Helm readOnly, `ReleaseWorkflowTests` (~270, workflow bundle approval).

## 10. Risks, rollback, must NOT
- Risk: HotChocolate/OpenAPI registration after ALC load fails; detected in WP-SIMPL-27 (Lakehouse pilot, OD-24), which takes the runtime-vs-fallback AD-3.10 decision.
- Rollback: modules are opt-in (`Enabled` empty); revert PR 4 restores old wiring.
- Must NOT: add a "skip verification" switch outside Development; let operators add keys; scan the directory to activate modules; re-read a module file from disk after hashing; let `Plugins:TrustedPluginHashes` authorize a module or the module key authorize a plugin; give modules `IEndpointRouteBuilder`, a data-source `DbConnection` (governance-store connections only via `IGovernanceConnectionFactory`), or `IServiceProvider` of the host before Build; let a module replace `IAccessDecisionPoint`, masking or plugin hook services; add a ReBAC or role-based endpoint filter; use collectible ALC; commit any non-test private key; log manifest bytes or key material.

## 11. Estimate
+1,000 src, +250 tools, +1,000 tests; 7.5 person-days.

## 12. Proposed module signing scheme (for Phase 3 review, OD-17 item 5)
- Algorithm: ECDSA P-256 with SHA-256 (BCL `ECDsa.Create(ECCurve.NamedCurves.nistP256)`), signature DER (RFC 3279), base64 in `signature`.
- Signed payload: canonical manifest = UTF-8 JSON, keys sorted ordinally at every level, no insignificant whitespace, `files` sorted by `path` (ordinal), paths relative with `/` separators, lowercase hex SHA-256, `signature` member removed, fixed `"schema":1` prefix field. Implemented once in `ManifestCanonicalizer` and shared by signer and loader.
- Manifest fields: `schema`, `id`, `version`, `hostVersion` (exact equality, SEC-SIMPL-45), `abstractionsVersion`, `entryAssembly`, `entryType`, `files[{path,sha256}]`, `keyId`, `signature`. Any other field fails (SEC-SIMPL-41).
- Coverage: every file under the module folder must be listed (including `.deps.json`, native libs); `module.json` itself is the only exclusion. The loader loads the verified in-memory bytes, never the files again (SEC-SIMPL-40).
- Trust anchor: one release public key (SPKI PEM) embedded in `Autheris.Api`; `keyId` = first 16 hex of SHA-256(SPKI). Max two embedded keys for rotation (current + next); a key is retired by a host release. Operators cannot add keys (Q3). This anchor is independent of the customer plugin trust list `Plugins:TrustedPluginHashes` (OD-31).
- Downgrade/replay: `hostVersion` exact equality binds a module to one host release, so an older signed module cannot load on a newer host.
- Private key: generated offline, stored only as GitHub secret `MODULE_SIGNING_KEY_P256` in the protected `release` environment (required reviewers); used only by release.yml signing step; never in PR workflows or forks.
- Compromise response: ship a host release with the next key, remove the compromised key; previously signed modules stop loading because of `hostVersion` binding.
- Development: unsigned accepted only when `ASPNETCORE_ENVIRONMENT=Development` AND `Gateway:Modules:AllowUnsignedInDevelopment=true`; logged as warning at startup; hashes are still verified when present.
- Rejected alternatives: Authenticode/strong-name (not an integrity control on Linux), Sigstore/cosign (new dependency and online transparency log; image signing via cosign may still be added for the container separately).

## Decisions taken in this spec
- `MapEndpoints` receives `IModuleEndpointRegistrar` instead of `IEndpointRouteBuilder` (SEC-SIMPL-43 is binding over AD-3.1); the registrar applies authorization, `EndpointSecurity` filters, the global rate limiter, the audit filter and the ingress tag; no ReBAC filter (OD-30).
- `hostVersion` exact equality implements SEC-SIMPL-45 (no min/max range).
- Module assemblies are loaded from the verified in-memory buffers only (SEC-SIMPL-40).
- `IGovernanceConnectionFactory` (governance store only) and `IAutherisGraphQLModule` are defined here because WP-SIMPL-29 needs them.
- Customer DLL plugins and signed modules share only hashing/parsing helpers; their trust anchors and directories stay separate (OD-31).
- Removal of the Api -> Extensions reference is owned by WP-SIMPL-29 (last module WP); this WP adds a shrinking baseline.
