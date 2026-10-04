# Implementation Plan: F-DATA-03 Embedded In-Memory OLAP via DuckDB.NET

**Author:** Solution & Data Architecture Team  
**Date:** 2026-10-03  
**Feature-ID:** `F-DATA-03`  
**Status:** In Review / Ready for Implementation  
**Target Delivery:** Autheris v1.6 GA  

---

## 1. Executive Summary & Business Context

In modernen Datenarchitekturen erfordern analytische Abfragen häufig Verknüpfungen (Joins), Fensterfunktionen (Window Functions) und Aggregationen über heterogene Quellsysteme (z. B. relationale PostgreSQL/MSSQL-Datenbanken kombiniert mit REST-APIs, S3-Parquet-Dateien oder internen Microservices). 

Bisherige Ansätze im Gateway führten solche Cross-Domain Joins entweder über den `CrossDomainJoinEngine` im .NET-Heap mittels verschachtelter Dictionary-Lookups durch oder erforderten die Bereitstellung externer, ressourcenintensiver Virtualisierungscluster (wie Apache Trino, Presto oder Denodo). Bei wachsenden Datenmengen führt der .NET-Heap-Ansatz zu Garbage Collection (GC) Pausen, LOH-Allokationen und hohem Speicherverbrauch.

**F-DATA-03** bettet die moderne, spaltenorientierte In-Memory-Vektorengine **DuckDB** (`DuckDB.NET.Data.Full`) direkt in den Autheris-Prozess ein:
1. **In-Process Virtualisierung:** Bereitstellung einer flüchtigen, isolierten In-Memory-Datenbank (`:memory:`) pro analytischer Abfrage-Session.
2. **SIMD-Vektorisierung:** Ausführung von komplexen Aggregationen, Sortierungen, Window Functions und Multi-Way-Joins mit nativer C++-Vektorperformance direkt auf den CPU-Registern.
3. **End-to-End Governance:** Sämtliche in DuckDB gestagten Teilresultate durchlaufen vorab den standardmäßigen Governance-Stack (Row-Level Security, Tenant-Filter, Casbin ABAC, ReBAC OpenFGA und dynamische PII-Maskierung).
4. **Zero-Copy & Arrow-Symbiose:** Nahtloses Zusammenspiel mit `F-DATA-04` (Apache Arrow) und `F-DATA-01` (Parquet) für ultra-performanten Daten-Ingress und -Egress.

---

## 2. Architektur & Komponenten-Design

```
+-----------------------------------------------------------------------------------+
|                        Client / BI-Tool / Analytics Request                       |
|               (POST /api/v1/olap/query  ODER  CrossDomain Join API)              |
+-----------------------------------------------------------------------------------+
                                          |
                                          v
+-----------------------------------------------------------------------------------+
| Autheris Security & Governance Guard                                            |
|   1. Authentication (JWT / API Key Bearer Token)                                  |
|   2. ReBAC Check (OpenFGA can_query / can_export für jede referenzierte Tabelle)  |
|   3. Casbin ABAC & Tenant Isolation Filter                                        |
+-----------------------------------------------------------------------------------+
                                          |
                                          v
+-----------------------------------------------------------------------------------+
| Connector Federation & Split Engine                                               |
|   - Parallel Fetch von Teilmengen aus Quell-Konnektoren (MSSQL, Postgres, REST...)  |
|   - Dynamische Spaltenmaskierung (PII-Schutz) vor Eintragen in DuckDB               |
|   - Bounded Resultset Enforcement (MaxStagedRowsPerTable)                         |
+-----------------------------------------------------------------------------------+
                                          | Governed In-Memory Row Stream / Appender
                                          v
+-----------------------------------------------------------------------------------+
| IDuckDbOlapEngine (DuckDbOlapEngine)                                              |
|   +-----------------------------------------------------------------------------+ |
|   | Flüchtig isolierte DuckDB In-Memory Session (DataSource=:memory:)           | |
|   |   - Erstellung temporärer Tabellen / Appender für beteiligte Domänen        | |
|   |   - SIMD-Vektorisierte SQL Execution (Window Functions, Aggs, Joins)        | |
|   |   - Memory Quota Limit (PRAGMA max_memory = '...')                          | |
|   |   - Execution Timeout Cancellation Token Binding                            | |
|   +-----------------------------------------------------------------------------+ |
+-----------------------------------------------------------------------------------+
                                          |
                                          v
+-----------------------------------------------------------------------------------+
| Result Serializer / Egress                                                        |
|   - JSON Result Streaming (Standard WebSQL / OLAP Endpoint)                       |
|   - ODER Arrow Flight / IPC Egress (F-DATA-04 application/vnd.apache.arrow.stream)|
+-----------------------------------------------------------------------------------+
```

---

## 3. Kern-Schnittstellen & Klassen

### 3.1 Domain-Optionen (`GatewayOptions.cs`)
```csharp
public sealed class DuckDbOlapOptions
{
    public bool Enabled { get; init; } = true;
    public string MaxMemory { get; init; } = "1GB";
    public int MaxStagedRowsPerTable { get; init; } = 250000;
    public int QueryTimeoutSeconds { get; init; } = 60;
    public bool EnableCrossDomainJoinOptimization { get; init; } = true;
}
```

### 3.2 Application Service (`IDuckDbOlapEngine`)
```csharp
public sealed record OlapTableSource(
    TableIdentifier Table,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> GovernedRows,
    TableMetadata Metadata);

public sealed record OlapQueryRequest(
    string Sql,
    IReadOnlyList<OlapTableSource> Sources,
    int? Limit = null);

public sealed record OlapQueryResult(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows,
    int TotalRowCount,
    TimeSpan ExecutionDuration);

public interface IDuckDbOlapEngine
{
    Task<OlapQueryResult> ExecuteOlapQueryAsync(
        OlapQueryRequest request,
        CancellationToken ct = default);
}
```

---

## 4. Phasenplan & Meilensteine

1. **Phase 1: Paketintegration & Domain Modeling**
   - Hinzufügen von `DuckDB.NET.Data.Full` 1.5.6 zu `Autheris.Application`.
   - Erweiterung von `GatewayOptions` um `DuckDbOlapOptions`.
2. **Phase 2: Security Review & Bedrohungsanalyse**
   - Härtung gegen DuckDB-spezifische Risiken (File-Access-Bypass via `read_csv`, `read_parquet`, Uncontrolled Memory Allocation, SQL-Injection in generierten DDLs).
3. **Phase 3: TDD Implementierung**
   - Erstellung isolierter `DuckDbOlapSecurityTests` (Table-Isolation, Quota-Limits, Null-Handling, Sanitizing).
   - Implementierung von `DuckDbOlapEngine` mit Appender / Parameterized Inserts und automatischer Session-Zerstörung.
   - Optimierung des `CrossDomainJoinEngine` zur optionalen Delegierung an DuckDB bei großen Batches.
4. **Phase 4: API Endpoint Exposition**
   - Bereitstellung von `POST /api/v1/olap/query` in `DuckDbOlapEndpoints.cs`.
5. **Phase 5: Verifikation, Review & Commit**
   - Ausführung der vollständigen Testsuite (1580+ Tests).
   - Sauberes Git-Commit.
