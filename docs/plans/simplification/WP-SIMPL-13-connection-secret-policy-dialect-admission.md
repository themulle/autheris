# WP-SIMPL-13 Connection-secret policy and data-source dialect admission
Status: Ready for implementation
Parent: [plan](../2026-11-05-plan-project-simplification.md#wp-simpl-13-connection-secret-policy-and-data-source-dialect-admission-adr-019-od-12-od-14) | Issues: #507 (plaintext-password bundle), #483 (TCPS check preserved; remaining Oracle part after WP-SIMPL-21) | Owner decisions: OD-12, OD-14, OD-15, OD-36 (Databricks tier flip owned by WP-SIMPL-37) | Security conditions: SEC-SIMPL-38, SEC-SIMPL-90, SEC-SIMPL-91, SEC-SIMPL-92 (gate: OD-12 tests green before any dialect change merges)

## 1. Goal and outcome
- Outside Development, a literal `Password` in the connection string of a PostgreSQL, SQL Server or Oracle data source fails startup and connection creation (today only Oracle does). One provider-neutral policy class replaces the Oracle-only check.
- `SnowflakeDialectGenerator`, `TargetSqlDialect.Snowflake` and all Snowflake capability/marker entries are deleted. `git grep -i snowflake -- src` only matches the OpenMetadata service-type comment.
- Startup accepts exactly PostgreSql, SqlServer, Oracle, Databricks and Sqlite (Sqlite until WP-SIMPL-31) as data-source providers. Ansi, DuckDb, Snowflake and unknown names fail with a typed configuration error.
- Oracle, Databricks, Ansi (generator only) and DuckDb (federation staging) are not removed.

## 2. Preconditions and dependencies
- None (engine lane). Start from current `main`.
- Can run in parallel with WP-SIMPL-11/12/14..18. Must merge before WP-SIMPL-21, which depends on PR 2 and PR 3 (steps 5-6: Snowflake deletion and dialect startup check).
- SEC-SIMPL-90/91 tests must be green before PR 2 (Snowflake deletion) merges. PR 1 adds the SEC-SIMPL-91 test.

## 3. Scope
### In scope
- New `ConnectionSecretPolicy` (Infrastructure). Wire it into `GatewayStartupValidator.ValidateOracleRuntime` (rename it) and `SqlConnectionFactory`. Key Vault password resolution for PG/SS.
- Delete Snowflake from the generator, the enum, the factory, the emitter marker table, base-generator switch cases, the README and tests.
- A startup data-source dialect check that returns a typed error.
### Out of scope
- Any Oracle or Databricks deletion (OD-12). No WebSQL tier flip: Databricks stays `Experimental` in `DialectCapabilityTable` until X1-DBX, and `DialectAdmissionTests.Admission_ExactSet` keeps PG, SS, Sqlite, Oracle. The X1-DBX tier flip (Databricks enabled on WebSQL, Trino and MCP with parity tests, `Admission_ExactSet` changed in the same commit) is owned by WP-SIMPL-37 (OD-36).
- SQLite removal (WP-SIMPL-31), the Spark CI job move to nightly (WP-SIMPL-21) and the OBO token cache rework (SEC-SIMPL-90 is covered by existing tests; only verify they exist).

## 4. Current state (verified at 63cbb893)
- `src/Autheris.Infrastructure/Persistence/OracleConnectionSupport.cs:11` contains `public static class OracleConnectionStringPolicy`. `HasPlaintextPassword(string)` at `:62` uses `OracleConnectionStringBuilder` and throws `SecurityException` for an invalid string.
- Callers:
  - `GatewayStartupValidator.cs:297` `ValidateOracleRuntime(GatewayOptions, IHostEnvironment, ILogger?)`, at `:312` and `:321`. It skips non-Oracle providers at `:306`, logs a warning in Development, and outside Development throws `ValidationException` on a plaintext password or a missing `PasswordKeyVaultRef`. It is called from `StartupChecks/GatewayStartupChecks.cs:66`. The file has 768 lines.
  - `SqlConnectionFactory.cs:283` `ResolveOraclePassword`, called at `:99` only for Oracle. It throws `SecurityException` on plaintext when `_requireOracleTcps`, then resolves `PasswordKeyVaultRef` via `SecretReferenceResolver.Resolve` (`:299`).
- `tests/Autheris.Tests.Unit/Security/OracleStartupValidationTests.cs:80` `OtherProviders_AreNotAffected` asserts that a PG plaintext password is NOT rejected. This test inverts in this WP.
- `TargetSqlDialect` (`src/TrinoSqlEngine/TargetSqlDialect.cs:4-14`): Ansi, PostgreSql, SqlServer, Sqlite, DuckDb, Snowflake, Oracle, Databricks. `DatabaseDialect` (`src/Autheris.Domain/Common/DatabaseDialect.cs:5`) has no Snowflake, Ansi or DuckDb.
- Snowflake in `src` (11 files):
  - `AnalyticalDialectGenerators.cs:359-~455` (class at `:361`)
  - `SqlDialectGeneratorFactory.cs:21`
  - `SqlEmitterContext.cs:328,334`
  - `SqlDialectGeneratorBase.Expressions.cs:308`
  - comments in `SqlDialectGeneratorBase.Functions.cs:21,50,78,436`, `SqlDialectGeneratorBase.cs:26`, `CompiledSqlBinders.cs:29`, `SqlDialectMapper.cs:54`, `TrinoSqlEngine/README.md:28,204`
  - `OpenMetadataSyncService.cs:883`: comment about the OpenMetadata service type; keep it.
- Snowflake in tests:
  - 4 Unit files: `DialectAdmissionTests.cs:22,30`, `Dialects/SqlDialectRegistryTests.cs:98`, `GovernedSqlExecutorX1Tests.cs:413` (provider string), `Policy/RowFilterKeySetStageTests.cs:263`
  - 17 files in `tests/TrinoSqlEngine.Tests`: AnalyticalDialectTests, DateFunctionTranslationTests, CastTypeNameTests, ExtractEmissionTests, FunctionCallEmissionTests, PaginationWithTiesTests, BinderMutationTests, CheckedDmlExecutorTransactionTests, CompileApiTests, DialectCapabilitiesTests, DialectRegistryCompletenessTests (`:49` `SnowflakeAndAnsi_HaveNoCapabilityEntry`, `:95` InlineData), ParameterAccountingTests, ConformanceModel, GateHarness, TimeZoneEmissionTests, TimeZoneRegistryTests.
- Provider parsing: `DataSourceProvider.TryResolveDialect` (`DatabaseDialect.cs:211`) maps an empty provider to `sqlite`.

## 5. Implementation steps
1. **Red (PR 1).** Write `ConnectionSecretPolicyTests` and `ConnectionSecurityTests` (section 6). Invert `OtherProviders_AreNotAffected` into `PostgreSqlPlaintextPassword_FailsStartupOutsideDevelopment`.
2. **Green.** Create `src/Autheris.Infrastructure/Persistence/ConnectionSecretPolicy.cs`:
   ```csharp
   public static class ConnectionSecretPolicy
   {
       // Throws SecurityException on an unparsable string (same as today's Oracle behaviour).
       public static bool HasPlaintextPassword(DatabaseDialect dialect, string connectionString);
       public static string WithPassword(DatabaseDialect dialect, string connectionString, string password);
       public static bool AppliesTo(DatabaseDialect d) => d is DatabaseDialect.PostgreSql or DatabaseDialect.SqlServer or DatabaseDialect.Oracle;
   }
   ```
   - Builders: `NpgsqlConnectionStringBuilder.Password`, `Microsoft.Data.SqlClient.SqlConnectionStringBuilder.Password`, `OracleConnectionStringBuilder.Password`. An empty password counts as "no password".
   - Delete `OracleConnectionStringPolicy.HasPlaintextPassword` and move its three assertions (`OracleStartupValidationTests.cs:124-126`) into `ConnectionSecretPolicyTests`.
3. **Startup.** Rename `ValidateOracleRuntime` to `ValidateDataSourceSecrets` (same signature, update `GatewayStartupChecks.cs:66`). For every connection where `AppliesTo(dialect)`:
   - Development: log the existing warning with the provider name. Never log the connection string.
   - Otherwise: throw `ValidationException` on a plaintext password (same message, with the provider name).
   - Oracle only: keep `Validate(requireTcps: true)` and the mandatory `PasswordKeyVaultRef`.
   - PG/SS: a missing `PasswordKeyVaultRef` stays allowed (integrated/Kerberos/managed identity). Only a literal password is rejected.
4. **Factory.** Generalise `ResolveOraclePassword` to `ResolvePassword(DatabaseDialect, options, connectionString)` and call it for PG, SS and Oracle (`SqlConnectionFactory.cs:95-101`).
   - Plaintext outside Development: `SecurityException`, before any network traffic.
   - When `PasswordKeyVaultRef` is set: resolve via `SecretReferenceResolver` (fail closed on null) and inject with `WithPassword`.
   - Rename the field `_requireOracleTcps` only if trivial. Otherwise add a `_productionSecrets` bool from the same environment check.
5. **Snowflake deletion (PR 2).**
   - Delete the class block `AnalyticalDialectGenerators.cs:359` to the end of `SnowflakeDialectGenerator`, the factory entry `SqlDialectGeneratorFactory.cs:21`, the marker `SqlEmitterContext.cs:334` and the case at `SqlDialectGeneratorBase.Expressions.cs:308` (keep the other labels of that switch section). Then delete the enum member `TargetSqlDialect.Snowflake`.
   - Fix comments that name Snowflake (files listed in section 4, except OpenMetadata).
   - Tests: delete Snowflake-only `[InlineData]` rows and methods in the 17 TrinoSqlEngine.Tests files. Rename `SnowflakeAndAnsi_HaveNoCapabilityEntry` to `Ansi_HasNoCapabilityEntry`. `DialectAdmissionTests:30` and `SqlDialectRegistryTests:98` use `Ansi`. `RowFilterKeySetStageTests:263` uses `Ansi`, if the assertion is about a dialect without capability; otherwise delete the case.
   - Keep `GovernedSqlExecutorX1Tests:413 [InlineData("Snowflake")]`: it is a provider string and still proves the fail-closed path. Add `[InlineData("Ansi")]`.
6. **Dialect startup check (PR 3).** Create `src/Autheris.Api/Extensions/DependencyInjection/DataSourceDialectValidator.cs` (do not grow `GatewayStartupValidator.cs`). Call it from `GatewayStartupChecks` before `ValidateDataSourceSecrets`.
   - `Validate(GatewayOptions)`: for each connection, `DataSourceProvider.TryResolveDialect(provider, out d) && d is PostgreSql or SqlServer or Oracle or Databricks or Sqlite`.
   - Otherwise throw `UnsupportedDataSourceDialectException : ValidationException` (new, same file) with `DataSourceName` and `Provider` properties. The message lists the admitted set from one static array; no secret appears in it.
   - The compile-time `SqlDialectMapper` admission stays unchanged as the second line.
   - Add `DialectGateTests` (SEC-SIMPL-38).

## 6. Tests
- `tests/Autheris.Tests.Unit/Security/ConnectionSecretPolicyTests.cs`, all `[Trait("Category","Security")]`:
  - `PostgreSql_plaintext_password_is_rejected`: `HasPlaintextPassword(PostgreSql,"Host=h;Username=u;Password=p")` is true; the validator throws outside Development.
  - `SqlServer_plaintext_password_is_rejected`: same with `"Server=s;User Id=u;Password=p"`.
  - `Oracle_plaintext_password_is_rejected`: holds the three moved assertions.
  - `Secret_reference_is_accepted`: no password plus `PasswordKeyVaultRef` passes for all three providers.
  - `Integrated_security_without_reference_is_accepted_for_postgres_and_sqlserver`.
  - `Plaintext_password_is_warned_not_rejected_in_development`: the logger never receives the connection string.
  - `Unparsable_connection_string_fails_closed`.
- `tests/Autheris.Tests.Unit/Security/ConnectionSecurityTests.cs` `[Security]`: `Oracle_requires_tcps_and_plaintext_password_rejected_for_all_data_sources_outside_development` (SEC-SIMPL-91, exact name).
- Factory tests, `[Security]`, in the class that holds `Factory_RefusesAPlaintextPassword_OutsideDevelopment_BeforeConnecting`:
  - `Factory_RefusesPostgresAndSqlServerPlaintextPassword_OutsideDevelopment_BeforeConnecting`
  - `Factory_InjectsKeyVaultPassword_ForPostgresAndSqlServer`
- `tests/TrinoSqlEngine.Tests/TargetSqlDialectTests.cs`: `No_snowflake_value`, which asserts that `Enum.GetNames<TargetSqlDialect>()` does not contain `"Snowflake"` and that the set equals {Ansi, PostgreSql, SqlServer, Sqlite, DuckDb, Oracle, Databricks}.
- `tests/Autheris.Tests.Unit/Configuration/DialectConfigurationTests.cs`:
  - `Unsupported_dialect_fails_validation` (Theory: "Ansi", "DuckDb", "Snowflake", "nonsense") throws `UnsupportedDataSourceDialectException`. `[Security]`.
  - `Oracle_and_Databricks_are_accepted` (also PostgreSql, SqlServer, Sqlite).
- `tests/Autheris.Tests.Unit/Sql/DialectGateTests.cs` `[Security]`: `Unsupported_dialect_fails_closed_without_generic_fallback`. A provider string "Snowflake" or "Ansi" gives a typed error, and no generator or command is created (SEC-SIMPL-38).
- Existing tests that stay green and unchanged: `DialectAdmissionTests.Admission_ExactSet`, `OracleDriverArchitectureTests`, `OracleIntegrationTestArchitectureTests`, and the Databricks OBO tests (SEC-SIMPL-90: confirm by `git grep -n DatabricksOboTokenProvider tests`). If no per-subject cache-key test exists, add `DatabricksOboTokenProviderTests.Cache_key_is_tenant_subject_audience_and_failed_exchange_fails_closed` `[Security]`.

## 7. Verification commands
```
dotnet build Autheris.sln -c Release -warnaserror
dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release --no-build --filter "FullyQualifiedName~ConnectionSecretPolicy|FullyQualifiedName~ConnectionSecurity|FullyQualifiedName~OracleStartupValidation|FullyQualifiedName~DialectConfiguration|FullyQualifiedName~DialectGate|FullyQualifiedName~DialectAdmission|FullyQualifiedName~GovernedSqlExecutorX1"
dotnet test tests/TrinoSqlEngine.Tests -c Release --no-build
dotnet test tests/Autheris.Tests.Architecture -c Release --no-build
git grep -n -i snowflake -- src            # only OpenMetadataSyncService.cs comment
git grep -n 'OracleConnectionStringPolicy.HasPlaintextPassword' -- src tests   # empty
git grep -n 'ValidateOracleRuntime' -- src tests                              # empty
```

## 8. Acceptance criteria
- [ ] PG, SS and Oracle plaintext passwords are rejected outside Development at startup and in the factory; Development only warns. All `ConnectionSecretPolicyTests` and the SEC-SIMPL-91 test are green.
- [ ] `TargetSqlDialect` has 7 members, without Snowflake. `SnowflakeDialectGenerator` is gone. The Snowflake grep shows one line.
- [ ] A startup provider outside {PostgreSql, SqlServer, Oracle, Databricks, Sqlite} gives `UnsupportedDataSourceDialectException`.
- [ ] `Admission_ExactSet` is unchanged and green. The Ansi and DuckDb generators still exist.
- [ ] `Oracle.ManagedDataAccess.Core` is referenced only by `Autheris.Infrastructure.csproj` (`git grep -l Oracle.ManagedDataAccess -- '*.csproj'`).
- [ ] Release build with `-warnaserror` passes. Unit, TrinoSqlEngine and Architecture tests are green. `source-size-ceilings.txt` does not grow.

## 9. PR slicing
1. PR 1 `fix(security): provider-neutral connection secret policy` (Steps 1-4, about +150 src, +200 tests). Labels: security. Closes the #507 bundle row.
2. PR 2 `refactor(sql): delete Snowflake generator` (Step 5, about -110 src, about -150 tests). Merges after the SEC-SIMPL-90/91 tests are green.
3. PR 3 `feat(config): fail closed on unsupported data-source dialect` (Step 6, about +80 src, +80 tests).

## 10. Risks, rollback, and things the developer must NOT do
- Risk: existing PG/SS deployments with a literal password stop starting outside Development. This is intended (no production yet). Rollback means reverting PR 1.
- Risk: a Snowflake `InlineData` row may be the only row of a Theory. In that case delete the method; do not replace it with `Ansi` when Ansi is already covered.
- Do NOT delete or move Oracle or Databricks code, `DatabricksOboTokenProvider`, Testcontainers.Oracle, or the Spark CI job.
- Do NOT flip any `DialectSupportTier`, and do NOT change `Admission_ExactSet` (the Databricks flip belongs to WP-SIMPL-37, OD-36).
- Do NOT log connection strings or passwords. Error messages name the data source and provider only.
- Do NOT add a generic-dialect fallback anywhere.

## 11. Estimate
- src: about +230 / -130 (net about +100).
- tests: about +300 / -450.
- Effort: 2 person-days.

## Decisions taken in this spec
1. Policy outcome is "reject outside Development, warn in Development", identical to today's Oracle behaviour (the plan's Phase 3 confirmation question; SEC-SIMPL-91 says "fails startup").
2. For PG/SS, `PasswordKeyVaultRef` is not mandatory, because passwordless integrated or managed-identity auth must keep working. Only a literal password is rejected. Oracle keeps the mandatory reference.
3. Step 2 "admission" means startup provider validation, not the WebSQL capability tier. Databricks stays `Experimental` for WebSQL until X1-DBX (consistent with `SqlDialectMapper.cs:54` and `Admission_ExactSet`).
4. `GovernedSqlExecutorX1Tests:413 "Snowflake"` stays as an unknown-provider string; `"Ansi"` is added as a row instead of replacing it.
5. The new validator lives in its own file, so the 768-line `GatewayStartupValidator.cs` does not grow toward its size ceiling.
6. Owner of the Databricks tier flip (X1-DBX): WP-SIMPL-37 (OD-36), after the Databricks contract/nightly lane of WP-SIMPL-21 is green, with a Phase 3 review and the `Admission_ExactSet` change in the same commit. This WP never flips a tier.
