# F-PERF-08: Hierarchical Resource Groups & Workload Queuing\n\n**Status:** [Done] (100% GA – Wave 2)  \n**Komponenten:** [`ResourceGroupManager.cs`](file:///root/lis-git/gql/gql/src/Autheris.Application/ResourceGroups/ResourceGroupManager.cs), [`ResourceGroupMiddleware.cs`](file:///root/lis-git/gql/gql/src/Autheris.GraphQL/Middleware/ResourceGroupMiddleware.cs)\n\n---\n\n## 1. Übersicht & Problemstellung
Unkoordinierte Hintergrund-Analysen oder schwere Agenten-Abfragen können interaktive UI-Anfragen verdrängen (Noisy-Neighbor-Effekt).

## 2. Architektur & Umsetzung
- Trino-inspiriertes Workload-Management (`Interactive`, `AutonomousAgents`, `BulkAnalytics`).
- FIFO Concurrency Slot Leasing mit Anti-Barging-Schutz (CQ-01).
- Schutz vor unberechtigter Prioritäts-Eskalation (SEC-02).
- Dynamisches Degraded Health Reporting (CQ-02).

## 3. Business Value
- Garantierte Latenzen für geschäftskritische interaktive Nutzer auch unter Spitzenlast.\n