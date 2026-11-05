# WP-SIMPL-25 Module host (AD-3)
Status: Ready for implementation
Parent: [plan](../2026-11-05-plan-project-simplification.md#wp-simpl-25-module-host-ad-3---phase-3-review-mandatory-before-merge-wf) | Issues: #515, #503 (plugin part, precondition) | Owner decisions: OD-17 (5: signing scheme specified and re-reviewed by Phase 3 before this WP), D3 | Security conditions: SEC-SIMPL-05, SEC-SIMPL-40..47, SEC-SIMPL-49

## 1. Goal and outcome
Introduce a runtime module host: `src/Autheris.Modules.Abstractions`, a fail-closed `ModuleLoader` with a signed manifest, a module ALC, host-owned endpoint registration, and module-aware config guarding. Measurable: the core image starts with zero modules; a test module signed by the test key loads; every negative case of section 6 stops startup with a non-zero exit and an error naming the module; `Autheris.Api.csproj` no longer references `Autheris.Extensions`.

## 2. Preconditions and dependencies
- Merged first: #503 plugin-loader collision fix (plan rule R5); Stage 2 exit (WP-SIMPL-17..24).
- Phase 3 must approve section 12 (signing scheme) BEFORE PR 2 is opened (OD-17 item 5). PR 1 may start in parallel with that review.
- Blocks WP-SIMPL-26..30 (SEC-SIMPL-05). No other WP may run in parallel on `Program.cs`/`GatewayApplicationBuilderExtensions.cs`.

## 3. Scope
### In scope
- New project `src/Autheris.Modules.Abstractions` (refs: ASP.NET Core abstractions, `Autheris.Domain` only).
- `src/Autheris.Api/Hosting/ModuleLoader.cs`, `ModuleAssemblyLoadContext.cs`, `ModuleEndpointRegistrar.cs`, `ModuleDirectoryPermissionCheck.cs`.
- `src/Autheris.Infrastructure/Modules/Trust/` (`ModuleManifest`, `ManifestCanonicalizer`, `ManifestSignatureVerifier`, `FileHashVerifier`), SHA-256/manifest code extracted from `PluginManager`/`PluginTrustList`.
- Embedded trust anchor `src/Autheris.Api/Hosting/Keys/module-release-pub.pem` (EmbeddedResource).
- `tools/Autheris.ModuleSigner` (console, BCL only).
- `UnknownGatewayKeyGuard` module-section awareness.
- `[WF]` `Dockerfile*` `FLAVOR` arg; docker-publish.yml publishes `autheris` and `autheris-full`; release.yml signs modules.
- Remove `Autheris.Api.csproj:8` Extensions reference only once no module-bound code remains in Api (see step 9; last PR).
### Out of scope
- Moving any feature into a module (WP-SIMPL-26..30).
- Operator-supplied keys, third-party modules, collectible/unloadable ALCs, hot reload.
- Sigstore/cosign (rejected in section 12, no new dependency).

## 4. Current state (verified on 63cbb893)
- `src/Autheris.Api/Autheris.Api.csproj:8` references `Autheris.Extensions`.
- `AddGatewayExtensions` is called at `src/Autheris.Api/Extensions/DependencyInjection/GatewayExecutionServiceExtensions.cs:125`, defined in `src/Autheris.Extensions/ExtensionsServiceCollectionExtensions.cs`.
- Module-bound endpoint mappings: `GatewayApplicationBuilderExtensions.cs:221` (`MapDbtEndpoints`), `:230` (`MapODataEndpoints`), `:269` (`MapIcebergRestCatalogEndpoints`).
- Plugin code: `src/Autheris.Infrastructure/Plugins/PluginManager.cs`, `PluginTrustList.cs`, `PluginAssemblyLoadContext.cs`, `Auth/AuthPluginHost.cs:187`.
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
   public interface IModuleEndpointRegistrar { RouteGroupBuilder MapGroup(string prefix); } // host-owned, see step 6
   public interface IModuleHostContext { TimeProvider Time { get; } string HostVersion { get; } /* governed service accessors only */ }
   ```
   Note: `MapEndpoints` takes `IModuleEndpointRegistrar`, not `IEndpointRouteBuilder` (SEC-SIMPL-43 supersedes AD-3.1). Add project to `Autheris.sln`.
2. Trust extraction (PR 1). Move SHA-256 file hashing and manifest JSON parsing from `PluginManager`/`PluginTrustList` into `Infrastructure/Modules/Trust`; `PluginTrustList` calls the shared code. Existing plugin tests stay green unchanged.
3. Manifest and canonicalization (PR 2). `ModuleManifest` record (fields in section 12). `ManifestCanonicalizer.ToCanonicalBytes(manifest)` per section 12.
4. Verifier (PR 2). `ManifestSignatureVerifier.Verify(byte[] canonical, string signatureB64, ECDsa key)` using `VerifyData(..., HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)`. `FileHashVerifier` compares every file in the module folder (recursive) against `files[]`; unlisted file, missing file or mismatch = failure. Hash comparison with `CryptographicOperations.FixedTimeEquals`.
5. Loader (PR 2). `ModuleLoader.LoadEnabled(WebApplicationBuilder)` called in `Program.cs` before `builder.Build()`:
   - read `Gateway:Modules:Enabled` (string array) and `Gateway:Modules:Path` (default `/app/modules`);
   - `ModuleDirectoryPermissionCheck`: refuse when the module root or any module folder is writable by the process user (SEC-SIMPL-42; skipped only under Development+flag);
   - per id: folder exists, manifest parses, signature verifies, `id` equals folder name and enabled id, `hostVersion` equals host informational version, `abstractionsVersion` equals the loaded abstractions assembly version, file hashes, then `new ModuleAssemblyLoadContext(folder)` and `Activator.CreateInstance(entryType)` cast to `IAutherisModule`; duplicate id fails; `ConfigureServices` exceptions are wrapped;
   - every failure throws `ModuleLoadException(moduleId, reason)`; `Program.cs` logs it and exits non-zero. No partial start.
   - post-check: after `ConfigureServices`, assert no descriptor for host-owned services (`IAuditWriter`, `IAuthorizationHandler`, `IClaimsTransformation`, `IAuthorizationPolicyProvider`, tenant context) was added or replaced by the module (snapshot `IServiceCollection` count/types before and after; SEC-SIMPL-44).
6. ALC (PR 2). `ModuleAssemblyLoadContext : AssemblyLoadContext(isCollectible: false)`; `Load(name)`: names starting `Autheris.`, `Microsoft.`, `System.` or present in `AssemblyLoadContext.Default.Assemblies` return null (host); otherwise resolve from module folder. If the module folder contains a DLL whose simple name exists in the default context, fail with "host assembly collision" (checked at load, before activation). `LoadUnmanagedDll` resolves `runtimes/<rid>/native/` inside the module folder.
7. Endpoint registrar (PR 3). `ModuleEndpointRegistrar.MapGroup(prefix)` returns `app.MapGroup(prefix).RequireAuthorization(<default policy name>)` plus the existing `EndpointSecurity` filters and `RebacEndpointFilter` and the module tag `module:<id>`. Modules never receive `IEndpointRouteBuilder`. After mapping, an `EndpointDataSource` scan asserts every endpoint tagged `module:*` has authorization metadata and no `AllowAnonymous` (fail startup otherwise).
8. Config guard (PR 3). `UnknownGatewayKeyGuard` takes the set of loaded modules' `ConfigurationSections`; a section owned by a known-but-not-loaded first-party module id (static table `KnownModuleSections` in `GatewayConfigurationSchema.cs`) fails with `module <id> is not installed` (SEC-SIMPL-49).
9. Wiring and project cut (PR 4, last). `Program.cs`: call loader, then `ModuleEndpointRegistrar` mapping after core routes. Keep `AddGatewayExtensions`/`MapDbt/OData/Iceberg` calls until their WPs move them; the Extensions reference removal from `Autheris.Api.csproj:8` happens in the WP that moves the last Api user (WP-SIMPL-30). Add the architecture test now as `[Fact(Skip=...)]` is FORBIDDEN; instead add a baseline file `tests/Autheris.Tests.Architecture/Baselines/api-extensions-usages.txt` that may only shrink.
10. Signer and workflows (PR 5, `[WF]`). `tools/Autheris.ModuleSigner`: `sign --module-dir <dir> --key-env AUTHERIS_MODULE_SIGNING_KEY` writes `module.json`; `verify --module-dir <dir> --pub <pem>`. release.yml: build modules, sign with secret `MODULE_SIGNING_KEY_P256` (environment `release`, protected), verify, publish. Dockerfile `ARG FLAVOR=core`; `full` copies `/app/modules` owned by root, mode 0555; image build runs `ModuleSigner verify` on every module and fails on unsigned (SEC-SIMPL-46). Helm values: modules volume `readOnly: true`.

## 6. Tests
Project `tests/Autheris.Tests.Unit/Hosting/` unless noted; fixtures build a tiny test module at test time from a checked-in test-only key pair under `tests/Autheris.Tests.Unit/Hosting/TestKeys/` (test key never equals release key). All `[Trait("Category","Security")]`.
- `ModuleLoaderTests`: `Unsigned_manifest_fails_startup`, `Signature_over_tampered_manifest_fails_startup`, `Hash_mismatch_fails_startup`, `Unlisted_file_fails_startup`, `Missing_listed_file_fails_startup`, `Version_mismatch_fails_startup`, `Abstractions_version_mismatch_fails_startup`, `Id_differs_from_folder_fails_startup`, `Duplicate_id_fails_startup`, `Enabled_but_missing_fails_startup`, `Host_assembly_collision_fails_startup`, `Not_enabled_module_is_not_loaded`, `Unsigned_allowed_only_in_development_with_flag`, `Unsigned_rejected_in_production_even_with_flag`, `ConfigureServices_exception_fails_startup_naming_module`, `Module_replacing_audit_writer_fails_startup`, `Writable_module_directory_fails_startup`, `Valid_signed_module_loads_and_registers_services`.
- `ManifestCanonicalizerTests`: `Field_order_does_not_change_canonical_bytes`, `Signature_field_is_excluded`, `Non_ascii_and_whitespace_are_canonical`.
- `ModuleEndpointTests` (Integration, WebApplicationFactory): `Module_routes_require_authorization`, `Module_route_with_allow_anonymous_fails_startup`, `Absent_module_routes_return_404`.
- `ModuleConfigurationTests`: `Keys_of_missing_module_fail_fast`, `Keys_of_loaded_module_are_accepted`.
- Architecture (`tests/Autheris.Tests.Architecture`): `ModuleBoundaryArchitectureTests.Modules_reference_only_abstractions_domain_and_application`, `.Modules_open_no_data_source_connections` (Cecil scan for `DbConnection` subclasses/`NpgsqlConnection`/`SqlConnection`/`OracleConnection` ctor calls), `PackageBoundaryArchitectureTests.Api_extensions_usages_do_not_grow` (baseline), `.Api_does_not_reference_Extensions` added by WP-SIMPL-30.
- `tools` test: `ModuleSignerTests.Sign_then_verify_roundtrip`, `.Verify_rejects_other_key`.
- Existing plugin tests unchanged and green (regression gate for step 2).

## 7. Verification commands
```
dotnet build Autheris.sln -c Release -warnaserror
dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release --filter "FullyQualifiedName~Hosting|FullyQualifiedName~Plugin"
dotnet test tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj -c Release
dotnet test tests/Autheris.Tests.Integration/Autheris.Tests.Integration.csproj -c Release --filter "FullyQualifiedName~ModuleEndpointTests"
git grep -n "IEndpointRouteBuilder" -- src/Autheris.Modules.Abstractions   # expect no hits
docker build --build-arg FLAVOR=core . && docker build --build-arg FLAVOR=full .   # [WF] PR only
```

## 8. Acceptance criteria
- [ ] All tests of section 6 exist and pass; all Security-tagged.
- [ ] Core image starts with `Gateway:Modules:Enabled` empty and no `/app/modules`.
- [ ] Phase 3 sign-off on section 12 recorded in `docs/plans/2026-11-05-sec-project-simplification.md` before PR 2 merges.
- [ ] No new NuGet package (BCL `ECDsa` only); lock files unchanged except new project.
- [ ] Private key exists only as GitHub secret; `git grep -n "BEGIN EC PRIVATE KEY"` finds only test keys under `TestKeys/`.
- [ ] Plugin feature behaviour unchanged.
- [ ] Fallback AD-3.10 documented in the plan addendum (not implemented).

## 9. PR slicing
1. Abstractions project + trust extraction (~250 lines).
2. Manifest, canonicalizer, verifier, ALC, loader + loader tests (~400).
3. Endpoint registrar, config guard + tests (~200).
4. Program.cs wiring, baseline architecture tests (~80).
5. `[WF]` signer tool, release.yml, docker-publish.yml, Dockerfile FLAVOR, Helm readOnly (~250, workflow bundle approval).

## 10. Risks, rollback, must NOT
- Risk: HotChocolate/OpenAPI registration after ALC load fails; detected in WP-SIMPL-26; fallback AD-3.10.
- Rollback: modules are opt-in (`Enabled` empty); revert PR 4 restores old wiring.
- Must NOT: add a "skip verification" switch outside Development; let operators add keys; scan the directory to activate modules; give modules `IEndpointRouteBuilder`, `DbConnection`, or `IServiceProvider` of the host before Build; use collectible ALC; commit any non-test private key; log manifest bytes or key material.

## 11. Estimate
+900 src, +250 tools, +900 tests; 7 person-days.

## 12. Proposed module signing scheme (for Phase 3 review, OD-17 item 5)
- Algorithm: ECDSA P-256 with SHA-256 (BCL `ECDsa.Create(ECCurve.NamedCurves.nistP256)`), signature DER (RFC 3279), base64 in `signature`.
- Signed payload: canonical manifest = UTF-8 JSON, keys sorted ordinally at every level, no insignificant whitespace, `files` sorted by `path` (ordinal), paths relative with `/` separators, lowercase hex SHA-256, `signature` member removed, fixed `"schema":1` prefix field. Implemented once in `ManifestCanonicalizer` and shared by signer and loader.
- Manifest fields: `schema`, `id`, `version`, `hostVersion` (exact equality, SEC-SIMPL-45), `abstractionsVersion`, `entryAssembly`, `entryType`, `files[{path,sha256}]`, `keyId`, `signature`.
- Coverage: every file under the module folder must be listed (including `.deps.json`, native libs); `module.json` itself is the only exclusion.
- Trust anchor: one release public key (SPKI PEM) embedded in `Autheris.Api`; `keyId` = first 16 hex of SHA-256(SPKI). Max two embedded keys for rotation (current + next); a key is retired by a host release. Operators cannot add keys (Q3).
- Downgrade/replay: `hostVersion` exact equality binds a module to one host release, so an older signed module cannot load on a newer host.
- Private key: generated offline, stored only as GitHub secret `MODULE_SIGNING_KEY_P256` in the protected `release` environment (required reviewers); used only by release.yml signing step; never in PR workflows or forks.
- Compromise response: ship a host release with the next key, remove the compromised key; previously signed modules stop loading because of `hostVersion` binding.
- Development: unsigned accepted only when `ASPNETCORE_ENVIRONMENT=Development` AND `Gateway:Modules:AllowUnsignedInDevelopment=true`; logged as warning at startup; hashes are still verified when present.
- Rejected alternatives: Authenticode/strong-name (not an integrity control on Linux), Sigstore/cosign (new dependency and online transparency log; image signing via cosign may still be added for the container separately).

## Decisions taken in this spec
- `MapEndpoints` receives `IModuleEndpointRegistrar` instead of `IEndpointRouteBuilder` (SEC-SIMPL-43 is binding over AD-3.1).
- `hostVersion` exact equality implements SEC-SIMPL-45 (no min/max range).
- Removal of the Api -> Extensions reference completes in WP-SIMPL-30; this WP adds a shrinking baseline.
