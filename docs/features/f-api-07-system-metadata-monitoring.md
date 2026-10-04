# F-API-07: Canonical System Metadaten & Monitoring Schema ($system)\n\n**Status:** [Done] (100% GA – Wave 2)  \n**Komponenten:** [`GatewaySystemMetricsService.cs`](file:///root/lis-git/gql/gql/src/Autheris.Application/Observability/GatewaySystemMetricsService.cs), `SystemEndpoints.cs` (`/api/governance/system/metrics`, `/health`, `/resource-groups`)\n\n---\n\n## 1. Übersicht & Problemstellung
SRE- und Governance-Teams benötigen einheitliche, echtzeitnahe Telemetriedaten über Cluster-Zustand, Concurrency-Sättigung und RLS-Epochen.

## 2. Architektur & Umsetzung
- Standardisierte System- und Health-Metriken (`GET /api/governance/system/metrics`).
- RBAC-gesichert (`GovernanceAdmin`, `ClusterAdmin`, `SecurityAdmin`).
- Überwachung aktiver Resource-Group-Slots, Queue-Tiefen und Cache-Statistiken.

## 3. Business Value
- Vollständige operative Transparenz für SRE- und Plattform-Teams.\n