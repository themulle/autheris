# P4: Modern Lakehouse Connector (Apache Iceberg v2)\n\n**Status:** [Done] (100% GA – Core Foundation)  \n**Komponenten:** [`IcebergMetadataReader.cs`](file:///root/lis-git/gql/gql_extensions/src/Autheris.Extensions/Lakehouse/Services/IcebergMetadataReader.cs), [`IcebergPartitionPruner.cs`](file:///root/lis-git/gql/gql_extensions/src/Autheris.Extensions/Lakehouse/Services/IcebergPartitionPruner.cs), [`LakehouseDataSourceExecutor.cs`](file:///root/lis-git/gql/gql_extensions/src/Autheris.Extensions/Lakehouse/Services/LakehouseDataSourceExecutor.cs)\n\n---\n\n## 1. Übersicht & Problemstellung
Moderne Lakehouses setzen auf offene Tabellenformate wie Apache Iceberg, die direkte Abfragen mit Partition Pruning erfordern.

## 2. Architektur & Umsetzung
- Nativer Iceberg v2 Reader mit Metadaten-/Manifest-Cache.
- Vektorisiertes Partition- und Min/Max-Stats-Pruning.
- RLS-Auswertung vor Spaltenmaskierung (EX-04) und SigV4-Sicherheit.

## 3. Business Value
- Direkter, gesicherter Zugriff auf Lakehouse-Tabellen ohne kostspielige Zwischenschichten.\n