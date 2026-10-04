# F-DBT-3: Live-Telemetry Exposure Publisher\n\n**Status:** [Done] (100% GA – Wave 1)  \n**Komponenten:** [`DbtExposurePublisher.cs`](file:///root/lis-git/gql/gql_extensions/src/Autheris.Extensions/Dbt/DbtExposurePublisher.cs), [`InMemoryTelemetryMetricsProvider.cs`](file:///root/lis-git/gql/gql/src/Autheris.Application/Dbt/Services/InMemoryTelemetryMetricsProvider.cs)\n\n---\n\n## 1. Übersicht & Problemstellung
dbt Exposures beschreiben Downstream-Konsumenten von Modellen, werden in der Praxis jedoch selten gepflegt und veralten rasch.

## 2. Architektur & Umsetzung
- Reale Nutzungsmessung im Gateway: Welche GraphQL-Operationen und Konsumenten rufen welche Modelle ab.
- Automatischer Export von `exposures.yaml` angereichert mit P99-Latenzen, Aufruffrequenzen (30 Tage) und Konsumenten-Metadaten.
- Integration in das Data Engineering Git-Repository oder Upload zu dbt Cloud.

## 3. Business Value
- Sichtbarkeit der realen Geschäftsrelevanz von dbt-Modellen für Data Engineering.
- Gezielte Performance- und Index-Optimierung basierend auf tatsächlichem Traffic.\n