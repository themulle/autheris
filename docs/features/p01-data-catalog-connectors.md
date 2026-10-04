# P1: Enterprise Data Catalog Connectors (Purview, Collibra, OpenMetadata)\n\n**Status:** [Done] (100% GA – Core Foundation)  \n**Komponenten:** [`PurviewDataCatalogClient.cs`](file:///root/lis-git/gql/gql/src/Autheris.Infrastructure/DataCatalog/PurviewDataCatalogClient.cs), [`CollibraDataCatalogClient.cs`](file:///root/lis-git/gql/gql/src/Autheris.Infrastructure/DataCatalog/CollibraDataCatalogClient.cs), [`OpenMetadataDataCatalogClient.cs`](file:///root/lis-git/gql/gql/src/Autheris.Infrastructure/DataCatalog/OpenMetadataDataCatalogClient.cs), [`DataCatalogSyncService.cs`](file:///root/lis-git/gql/gql/src/Autheris.Application/DataCatalog/Services/DataCatalogSyncService.cs)\n\n---\n\n## 1. Übersicht & Problemstellung
Manuelle Doppelpflege von Klassifizierungen und Maskierungsregeln führt zu Fehlern und Sicherheitslücken.

## 2. Architektur & Umsetzung
- Native REST-Clients mit Polly 8 Resilienz und Entra ID OAuth.
- Automatischer Import von PII-Tags und DSGVO-Art.-9-Klassifizierungen.
- Dynamische Übersetzung in Gateway-Maskierungsregeln mit Epochen-Invalidierung.

## 3. Business Value
- 100% konsistente Data Governance synchron zum führenden Unternehmenskatalog.\n