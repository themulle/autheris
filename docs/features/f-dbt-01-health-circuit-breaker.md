# F-DBT-1: dbt Data Health Circuit Breaker\n\n**Status:** [Done] (100% GA – Wave 1)  \n**Komponenten:** [`DbtHealthCircuitBreaker.cs`](file:///root/lis-git/gql/gql/src/Autheris.Application/Dbt/Services/DbtHealthCircuitBreaker.cs), [`DbtHealthExecutionMiddleware.cs`](file:///root/lis-git/gql/gql/src/Autheris.GraphQL/Interceptors/DbtHealthExecutionMiddleware.cs), [`DbtArtifactStreamingParser.cs`](file:///root/lis-git/gql/gql_extensions/src/Autheris.Extensions/Dbt/DbtArtifactStreamingParser.cs)\n\n---\n\n## 1. Übersicht & Problemstellung
dbt-Transformations- und Datenqualitätspipelines (`dbt test`) laufen asynchron im Data Warehouse. Scheitern Tests (z. B. referenzielle Integrität, Not-Null, Einzigartigkeit), merken traditionelle API-Gateways dies nicht und liefern korrumpierte Daten an BI-Tools und Endanwender aus.

## 2. Architektur & Umsetzung
- **Streaming Parser:** Liest `run_results.json` speichereffizient ohne Full-DOM Allokation.
- **Circuit Breaker:** Markiert Modelle mit fehlgeschlagenen Tests sofort als `QUARANTINED`.
- **AST Middleware:** Erkennt Zugriffe auf unter Quarantäne stehende Entitäten zur Query-Kompilierungszeit und blockiert den Zugriff mit dem Fehlercode `TABLE_IN_QUARANTINE`.
- **Health Endpoints:** REST-Schnittstellen (`GET /run-results`, `/health`, `/health/reset`) zur Überwachung und manuellem Entsperren nach Fehlerbehebung.

## 3. Business Value
- Verhindert das Ausliefern fehlerhafter Kennzahlen an Konsumenten und Vorstands-Dashboards.
- Automatische Quarantäne schützt SLA und Compliance-Vorgaben.\n