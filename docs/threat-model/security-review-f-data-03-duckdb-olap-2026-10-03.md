# Security Review & Threat Model: F-DATA-03 Embedded In-Memory OLAP via DuckDB.NET

**Reviewer:** Enterprise Security Architecture Team  
**Date:** 2026-10-03  
**Status:** Approved with Mandatory Security Constraints  
**Feature:** `F-DATA-03` Embedded In-Memory OLAP  

---

## 1. Executive Summary

Die Einbettung einer nativen C++-basierten In-Memory-SQL-Engine (`DuckDB.NET.Data.Full`) in den .NET-Gateway-Prozess bringt signifikante Geschwindigkeitsvorteile, birgt jedoch im Vergleich zu reinem C#-Code zusätzliche Angriffsvektoren (insbesondere Dateisystem-Zugriffe über native SQL-Funktionen wie `read_csv` oder `COPY TO`, unkontrollierte Speicherallokation im C++-Heap und Cross-Tenant-Kontamination).

Das Security-Team genehmigt den Implementierungsplan unter der Bedingung der strikten Einhaltung der folgenden **fünf verbindlichen Sicherheitskontrollen (SEC-OLAP-01 bis SEC-OLAP-05)**.

---

## 2. Threat Modeling & Risikomatrix (STRIDE)

| STRIDE-Kategorie | Bedrohung (Threat) | Auswirkung | Risikostufe | Verbindliche Mitigation (Security Control) |
| :--- | :--- | :--- | :---: | :--- |
| **Elevation of Privilege / Info Disclosure** | Client führt `SELECT * FROM read_csv('/etc/shadow')` oder `COPY ... TO '/var/log/...'` in DuckDB aus. | Lesen/Schreiben sensibler Host-Dateien; Sandbox-Escape. | **CRITICAL** | **SEC-OLAP-01:** Striktes Setzen von `SET enable_external_access = false;` unmittelbar nach Öffnen jeder DuckDB-Verbindung. |
| **Denial of Service (DoS)** | Client führt kartesischen Multi-Join oder Endlos-Rekursion aus; C++-Heap erschöpft Host-RAM. | Absturz des Gateway-Prozesses (OOM-Kill). | **HIGH** | **SEC-OLAP-02:** Verbindliches Setzen von `PRAGMA max_memory = '...'` und CPU-Thread-Begrenzung sowie striktes `CancellationToken` Query-Timeout. |
| **Information Disclosure** | Daten von Mandant A verbleiben nach Abfrage im gemeinsamen Speicher und werden von Mandant B abgefragt. | Cross-Tenant Data Leakage. | **CRITICAL** | **SEC-OLAP-03:** Strikt transiente, flüchtige In-Memory-Verbindungen (`DataSource=:memory:`) pro Request-Lebenszyklus (`using var conn`). Kein geteilter permanenter State. |
| **Tampering / Injection** | Bösartige Spalten- oder Tabellennamen in Connector-Metadaten manipulieren interne `CREATE TABLE`-DDLs. | SQL-Injection im internen DuckDB-Katalog. | **HIGH** | **SEC-OLAP-04:** Striktes Sanitizing und Quoting aller generierten Identifikatoren (`"column_name"`) nach Whitelist-Prüfung gegen den autorisierten `TableMetadata`-Katalog. |
| **Elevation of Privilege** | Nicht-autorisierter Benutzer greift via DuckDB-Query auf Tabellen zu, für die er keine ReBAC/ABAC-Rechte besitzt. | Unbefugter Datenzugriff. | **CRITICAL** | **SEC-OLAP-05:** Pre-Execution Governance Gate: ReBAC (`IRebacService.CheckAsync`) und Casbin-ABAC-Validierung für alle angeforderten Quelltabellen VOR dem Staging in DuckDB. |

---

## 3. Detail-Spezifikation der Sicherheitskontrollen

### SEC-OLAP-01: Sandboxing & Deaktivierung externer Zugriffe
Unmittelbar nach dem Öffnen der transienten DuckDB-Verbindung MUSS die Engine gegen Dateisystem- und Netzwerkoperationen gehärtet werden:
```sql
SET enable_external_access = false;
```
*Auswirkung:* Jeder Versuch, `read_csv`, `read_parquet`, `read_json`, `COPY ... TO` oder externe Erweiterungen aufzurufen, wird von DuckDB auf C++-Ebene deterministisch mit einer Permission-Exception abgewiesen.

### SEC-OLAP-02: DoS-Schutz & Speicherbegrenzung
Zur Verhinderung von Out-of-Memory-Zuständen MUSS jede DuckDB-Instanz konfigurierte Ressourcengrenzen erzwingen:
```sql
PRAGMA max_memory = '1GB'; -- Konfigurierbar über DuckDbOlapOptions
PRAGMA threads = 2;
```
Zusätzlich gilt eine konfigurierbare Obergrenze für die Anzahl gestagter Zeilen pro Tabelle (`MaxStagedRowsPerTable`, Default: 250.000). Überschreitungen brechen mit `InvalidOperationException` ab.

### SEC-OLAP-03: Zero Cross-Tenant Contamination
Die DuckDB-Verbindung darf niemals singleton-artig geteilt werden. Jeder Request erhält eine neue `DuckDBConnection("DataSource=:memory:")`, welche am Ende der Abfrage über `IDisposable` vollständig zerstört wird.

### SEC-OLAP-04: Metadaten-Sanitizing & Parameterized Execution
Tabellen- und Spaltennamen aus Katalogen werden vor der Generierung von `CREATE TEMP TABLE`-Statements durch eine Regex-Whitelist (`^[a-zA-Z0-9_]+$`) validiert und doppelt gequotet (`"..."`). Zeilendaten werden als typisierte Parameter oder über typsichere Batch-Einfügungen übergeben.

### SEC-OLAP-05: Pre-Flight Governance Gate
Vor dem Befüllen von DuckDB mit Datensätzen wird für jede Tabelle geprüft:
1. ReBAC-Prüfung: Darf das Subjekt `table:<domain>.<name>` abfragen (`can_query` / `can_read`)?
2. Dynamische Maskierung: Sämtliche PII-Spalten werden bereits im .NET-Governance-Layer maskiert, bevor sie DuckDB erreichen.

---

## 4. Geforderte Security Unit Tests

1. `DuckDb_ExternalAccess_Disabled_ThrowsException_On_FileAccess`: Verifiziert, dass `read_csv('/etc/passwd')` abgewiesen wird (`enable_external_access = false`).
2. `DuckDb_MaxMemory_Enforced_Prevents_Unbounded_Allocations`: Verifiziert, dass die konfigurierte Speicherobergrenze in der DuckDB-Session gesetzt wird.
3. `DuckDb_Session_Is_Completely_Isolated_Between_Requests`: Verifiziert, dass temporäre Tabellen aus Request A in Request B nicht mehr existieren.
4. `DuckDb_Preserves_PII_Masking`: Verifiziert, dass maskierte Daten aus dem Governance-Layer unverändert in DuckDB-Aggregationsabfragen verbleiben.
5. `DuckDb_Rebac_Enforces_Permissions_On_All_Tables`: Verifiziert, dass fehlende Rechte für eine Quelltabelle die gesamte Ausführung mit HTTP 403 / SecurityException abbricht.
6. `DuckDb_MaxStagedRows_Exceeded_Throws_InvalidOperationException`: Verifiziert Fail-Closed-Verhalten bei Datenmengen oberhalb des Limits.
