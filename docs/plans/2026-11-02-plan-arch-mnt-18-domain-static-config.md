# Implementation Plan: ARCH-MNT-18 Remove Static Config from Domain & Services (and ARCH-MNT-24)

Input PRD: [`2026-11-02-req-arch-mnt-18-domain-static-config.md`](2026-11-02-req-arch-mnt-18-domain-static-config.md)  
Origin: Master Plan Track 11 row `ARCH-MNT-18` & `ARCH-MNT-24`

## 1. Architectural Design

### 1.1 Individual Grant Limits (`IIndividualGrantLimits`)
- Create interface `Autheris.Domain.Model.IIndividualGrantLimits`:
  ```csharp
  public interface IIndividualGrantLimits
  {
      TimeSpan RegularMax { get; }
      TimeSpan UnmaskedMax { get; }
      TimeSpan ServicePrincipalMax { get; }
      DateTimeOffset EffectiveConsentValidTo(GranteeType granteeType, DateTimeOffset validFrom, DateTimeOffset validTo, ConsentEffect effect = ConsentEffect.Allow);
      DateTimeOffset? EffectiveProfileValidTo(MaskingPolicyMode mode, DateTimeOffset createdAt, DateTimeOffset? validTo);
  }
  ```
- Implement `IndividualGrantLimitsService : IIndividualGrantLimits` in `Autheris.Application.Governance.Grants` injecting `IOptions<IndividualGrantOptions>`.
- In `Autheris.Domain.Model.IndividualGrantLimits`:
  - Provide a default immutable instance `BuiltInDefault` (180 days regular, 30 days unmasked, 365 days service principal).
  - Delegate static methods `EffectiveConsentValidTo` and `EffectiveProfileValidTo` through `Current` (volatile / atomic reference initialized to `BuiltInDefault`).
  - Wire `IndividualGrantLimitsService` to update `IndividualGrantLimits.Current` upon DI activation or options binding while preserving direct instantiability for isolated tests.

### 1.2 Data Source Loggers (`IDataSourceLoggers`)
- Create interface `Autheris.Application.Telemetry.IDataSourceLoggers`:
  ```csharp
  public interface IDataSourceLoggers
  {
      ILogger Get(string? dataSourceName);
  }
  ```
- Implement `DataSourceLoggersService : IDataSourceLoggers` with injected `ILoggerFactory`.
- Register in `GatewayGovernanceServiceExtensions` as a singleton.
- Update `DataSourceLoggers` static class to delegate to `Current` singleton.

### 1.3 Source Command Telemetry (`ISourceCommandTelemetry`)
- Create interface `Autheris.Application.Telemetry.ISourceCommandTelemetry`:
  ```csharp
  public interface ISourceCommandTelemetry
  {
      SourceCommandScope Start(in SourceCommandDescriptor descriptor, IDataSourceTelemetryPolicyProvider? policyProvider = null);
  }
  ```
- Implement `SourceCommandTelemetryService : ISourceCommandTelemetry` injecting `IDataSourceTelemetryPolicyProvider` and `IDataSourceLoggers`.
- Register in DI as a singleton.
- Update `SourceCommandTelemetry` static class to delegate to `Current` singleton.

### 1.4 Catalog Relation Registry (`ICatalogRelationRegistry`)
- Create interface `Autheris.GraphQL.Catalog.ICatalogRelationRegistry`:
  ```csharp
  public interface ICatalogRelationRegistry
  {
      void Register(string typeName, string fieldName, RelationCardinality cardinality);
      bool TryGetCardinality(string? typeName, string? fieldName, out RelationCardinality cardinality);
      void Clear();
  }
  ```
- Implement `DefaultCatalogRelationRegistry : ICatalogRelationRegistry`.
- Register in `GatewayServiceCollectionExtensions.GraphQL.cs` as a singleton.
- Pass `ICatalogRelationRegistry` into `QueryCostAnalyzerRule` constructor with fallback to `CatalogRelationRegistry.Current`.

### 1.5 ARCH-MNT-24 Housekeeping
- Delete 4 merged plan files:
  - `docs/plans/2026-10-24-plan-issue-341-graphql-error-mapping.md`
  - `docs/plans/2026-10-24-plan-pers-04-sec-281-6-residuals.md`
  - `docs/plans/2026-10-24-prd-pers-04-sec-281-6-residuals.md`
  - `docs/plans/2026-10-24-plan-security-low-quickwins.md`
- Record entries under "Earlier merged tracks" in `docs/plans/00-master-plan-overview.md`.

## 2. Phase 3 Security & Zero-Trust Review

- **SEC-MNT18-01 (Fail-Closed Default Clamping)**: When no DI service or custom option is provided, `IndividualGrantLimits.Current` defaults strictly to `BuiltInDefault` (180d User, 30d Unmasked, 365d SP).
- **SEC-MNT18-02 (Anti-Escalation Ceiling)**: Custom options passed to `IndividualGrantLimitsService` can only shorten, never extend, the built-in durations (`Math.Min`). Values `<= 0` fail closed to 1 day.
- **SEC-MNT18-03 (Telemetry Sanitization)**: Parameterized statement text scrubbing and query hashing remain invariant in `SourceCommandScope`.
- **SEC-MNT18-04 (Cost Analyzer Integrity)**: Unregistered relation fields continue to fail closed to `RelationCardinality.OneToMany` (worst-case multiplier).

## 3. Work Breakdown & Execution Steps

1. **WP1**: Housekeeping (delete 4 obsolete plans, update `00-master-plan-overview.md`).
2. **WP2**: Create `IIndividualGrantLimits` and `IndividualGrantLimitsService`; wire into `IndividualGrantLimits` and DI; update `IndividualGrantClampTests` to test both isolated service and static facade.
3. **WP3**: Create `IDataSourceLoggers` and `DataSourceLoggersService`; wire DI.
4. **WP4**: Create `ISourceCommandTelemetry` and `SourceCommandTelemetryService`; wire DI.
5. **WP5**: Create `ICatalogRelationRegistry` and `DefaultCatalogRelationRegistry`; inject into `QueryCostAnalyzerRule`; wire DI.
6. **WP6**: Verification: Run unit, architecture, and parity tests; build solution.
