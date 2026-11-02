# Requirements: ARCH-MNT-18 Remove Static Configuration from Domain & Core Services (and ARCH-MNT-24 Housekeeping)

## 1. Executive Summary & Problem Statement

In the Autheris architecture, domain models and core data-path components should adhere strictly to clean architecture principles:
- **Clean Architecture & Inversion of Control**: Domain and Application layers should not rely on mutable global static state configured through side-effects during startup.
- **Test Independence & Parallelization**: Static classes with mutable state (such as `IndividualGrantLimits.Configure()`) create cross-test pollution and require disabling xUnit test parallelization (`[Collection(DisableParallelization = true)]`).
- **Telemetry Dependency Injection**: `DataSourceLoggers` and `SourceCommandTelemetry` held static `ILoggerFactory` and `IDataSourceTelemetryPolicyProvider` singletons that were neither properly injected nor lifecycle-managed.
- **Query Cost Multiplicity Registry**: `CatalogRelationRegistry` used global static concurrent dictionaries without an interface abstraction, preventing mocking or scoped isolation in GraphQL tests.
- **Repository Planning Hygiene (ARCH-MNT-24)**: Four merged plan files from earlier PRs remain unlinked from `00-master-plan-overview.md`.

## 2. Goals & Invariants

- **GOAL-01**: Define explicit interfaces (`IIndividualGrantLimits`, `IDataSourceLoggers`, `ISourceCommandTelemetry`, `ICatalogRelationRegistry`) registered in DI as singletons.
- **GOAL-02**: Provide options-backed implementations (`IndividualGrantLimitsService`, `DataSourceLoggersService`, `SourceCommandTelemetryService`, `DefaultCatalogRelationRegistry`) taking their dependencies via constructor injection.
- **GOAL-03**: Preserve backward-compatible, fail-closed static facades for entity property getters (`ConsentGrant.EffectiveValidTo`, `AccessProfile.EffectiveProfileValidTo`) and existing call-sites, ensuring default-deny limits (180 days regular, 30 days unmasked, 365 days service principal).
- **GOAL-04**: Enable unit test parallelization for grant limit tests by avoiding global static state mutations where possible.
- **GOAL-05**: Execute ARCH-MNT-24 housekeeping by removing the 4 merged plan files and recording them in the completed/merged tracks section of `00-master-plan-overview.md`.
- **INV-01 (Zero-Trust / Fail-Closed)**: If no custom limits are configured or resolved, the built-in fail-closed defaults are strictly enforced. Configured limits can only tighten (shorten) the built-in limits, never lengthen them beyond the built-in maxima.
- **INV-02 (Zero Performance Regression)**: Static facades in the hot path must introduce zero additional allocations and preserve `O(1)` performance.

## 3. Affected Components

1. `src/Autheris.Domain/Model/IndividualGrantLimits.cs` & `IIndividualGrantLimits.cs`
2. `src/Autheris.Application/Governance/Grants/IndividualGrantServiceCollectionExtensions.cs` & `IndividualGrantLimitsService.cs`
3. `src/Autheris.Application/Telemetry/DataSourceLoggers.cs` & `IDataSourceLoggers.cs`
4. `src/Autheris.Application/Telemetry/SourceCommandTelemetry.cs` & `ISourceCommandTelemetry.cs`
5. `src/Autheris.GraphQL/Catalog/CatalogRelationRegistry.cs` & `ICatalogRelationRegistry.cs`
6. `src/Autheris.GraphQL/Interceptors/QueryCostAnalyzerRule.cs` & `CatalogGraphQlTypeModule.cs`
7. `tests/Autheris.Tests.Unit/Governance/Grants/IndividualGrantClampTests.cs`
8. `docs/plans/` (housekeeping for 4 merged plans)

## 4. Acceptance Criteria

- [ ] `IIndividualGrantLimits` interface created and implemented with options binding.
- [ ] `IDataSourceLoggers` interface created, implemented with `ILoggerFactory` injection, and registered in DI.
- [ ] `ISourceCommandTelemetry` interface created, implemented with injected policy provider and loggers, and registered in DI.
- [ ] `ICatalogRelationRegistry` interface created, implemented, registered in DI, and consumed by `QueryCostAnalyzerRule`.
- [ ] Existing static facades remain functional and fail-closed.
- [ ] All unit, architecture, and parity tests pass with 0 warnings.
- [ ] 4 unlinked merged plan files deleted and archived in `00-master-plan-overview.md`.
