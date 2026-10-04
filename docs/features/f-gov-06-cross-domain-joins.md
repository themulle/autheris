# F-GOV-06: Mehrstufige Pushdown-Kaskaden & Cross-Domain Joins\n\n**Status:** [Done] (100% GA – Wave 3)  \n**Komponenten:** [`CrossDomainJoinEngine.cs`](file:///root/lis-git/gql/gql/src/Autheris.Application/Pushdown/CrossDomainJoinEngine.cs), [`DefaultCrossDomainAccessResolver.cs`](file:///root/lis-git/gql/gql/src/Autheris.Application/Pushdown/DefaultCrossDomainAccessResolver.cs)\n\n---\n\n## 1. Übersicht & Problemstellung
Daten liegen in heterogenen Datenbanken und Domänen. Cross-Source Joins erfordern sonst aufwendige ETL-Pipelines in Data Warehouses.

## 2. Architektur & Umsetzung
- Dreistufiger Namensraum `Catalog.Schema.Table`.
- Föderierte Ausführung mit partiellem Pushdown von Where/Project/Order/Limit in die Quell-Connectoren.
- Zero-Trust Maskierung auf Primärentitäten und SQL-Identifier Injection Schutz (SEC-CDJ-01..06).

## 3. Business Value
- Föderierte Abfragen in Echtzeit über Datenbankgrenzen hinweg ohne ETL-Verzögerung.\n