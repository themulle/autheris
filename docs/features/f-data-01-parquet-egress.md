# F-DATA-01: Hierarchischer Parquet Egress & Nested Query Serialization\n\n**Status:** [Done] (100% GA – Wave 3)  \n**Komponenten:** [`ParquetExportService.cs`](file:///root/lis-git/gql/gql/src/Autheris.Application/Serialization/ParquetExportService.cs), `POST /api/v1/export/parquet`, `Accept: application/vnd.apache.parquet`\n\n---\n\n## 1. Übersicht & Problemstellung
JSON ist für große Datenmengen in Analytics- und Data-Science-Szenarien hochgradig ineffizient (hohe CPU- und Bandbreitenlast).

## 2. Architektur & Umsetzung
- Nativer binärer Apache Parquet Export direkt aus MSSQL, Postgres, SQLite und Iceberg.
- Unterstützt verschachtelte 1:N-Relationen via Dremel `LIST<STRUCT>`-Serialisierung und tabellarisches Flattening.
- Strikte Einhaltung von RLS-Pushdown, dynamischer PII-Maskierung und Schutz vor Parquet-Bombs.

## 3. Business Value
- Faktor 5-10x schnellere Ladezeiten für Python/Polars/DuckDB/Pandas bei bis zu 85% geringerem Netzwerkvolumen.\n