# ADR-015: Apache Iceberg & Modern Lakehouse Connector mit Zero-Trust Pushdown

## Status
Akzeptiert

## Kontext
Unternehmen speichern analytische und operative Rohdatenbestände zunehmend in offenen Tabellenformaten wie **Apache Iceberg** auf Object Storage (AWS S3, Azure ADLS Gen2, MinIO).
- **Problem**: Konkurrenzprodukte (Apollo GraphQL, Hasura DDN) verlangen den Betrieb externer Cloud Data Warehouses (Snowflake, Databricks SQL, Trino). Dies verursacht hohe Cluster-Kosten, addiert 1.500 bis 3.000 ms Latenz und entkoppelt die Autorisierungslogik vom Gateway.
- **Marktlücke**: Kein marktführendes GraphQL-Gateway bietet eine native, ressourcenschonende In-Process-Abfrage von Iceberg-Metadaten und Parquet-Dateien mit integriertem Partition Pruning und Spalten-Maskierung.

## Entscheidung
Wir etablieren in `Autheris.Extensions` einen nativen **Apache Iceberg Lakehouse Connector** (Feature `F-LAKE-01`):
1. **Direktes Metadaten- & Manifest-Parsing**:
   - `IIcebergMetadataReader` lädt und parst Iceberg `v2.metadata.json`, Snapshots und Manifest-Listen ohne Abhängigkeit von schweren Fremd-Engines.
2. **Vektorisiertes Partition & Min/Max Stats Pruning**:
   - `IIcebergPartitionPruner` wertet GraphQL-Filterprädikate gegen Iceberg-Partitionsgrenzen und Spalten-Min/Max-Werte aus.
   - Überspringt bis zu 95% irrelevanter Parquet-Dateien vor dem Netzwerk-I/O.
3. **Integrierte Zero-Trust Governance & Maskierung**:
   - `LakehouseDataSourceExecutor` erzwingt Mandantenisolation (`tenantId`) direkt im Scan-Filter.
   - PII- (E-Mail, IBAN) und DSGVO-Art.-9-Daten werden in-memory beim Dekodieren der Datensätze maskiert (`IColumnMaskingProvider`).
4. **Sicherheits-Insecure-Modi**:
   - `warn_allow_unsigned_s3_requests`: Erlaubt unsignierten Zugriff auf lokale MinIO-Entwicklungsinstanzen.
   - `danger_bypass_lakehouse_auth`: Verhindert Start in Produktion bei Umgehung von Authentifizierung.

## Konsequenzen
### Positiv
- Direkte Abfrage riesiger analytischer Datenmengen in 15–50 ms statt Sekunden.
- Massiv reduzierte Total Cost of Ownership (TCO), da keine teuren SQL-Warehouse-Cluster vorgehalten werden müssen.
- Wiederverwendung derselben ABAC- und Maskierungsregeln über GraphQL, OData v4 und MCP Tools.

### Negativ / Risiken
- Große historische Snapshots können Metadaten-Ladezeiten erhöhen. Gegenmaßnahme: L1-Memory-Caching der geparsten Tabellen-Snapshots (`MetadataCacheTtlMinutes`).
