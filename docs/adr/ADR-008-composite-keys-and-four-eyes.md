# ADR-008: Composite Keys, Parameter-Budgeting und Vier-Augen-Freigabe-Lifecycle

## Status
Akzeptiert

## Kontext
1. **Zusammengesetzte Schlüssel (Composite Keys):** In relationalen Unternehmensdatenbanken besitzen viele Kernentitäten (z. B. Belegzeilen, Vertragskonditionen, internationale Bestände) keine singulären Primärschlüssel, sondern zusammengesetzte Schlüssel (`(domain, company_id, invoice_id)`). Beim Laden über GraphQL Batch DataLoader führt ein einfaches `IN (...)` zu Syntaxfehlern oder RDBMS-Inkompatibilitäten (SQL Server unterstützt keine ANSI Tuple-IN `(a, b) IN ((1, 2), (3, 4))`).
2. **Parameter-Budget-Erschöpfung:** Datenbank-Engines besitzen strikte Limits für die maximale Parameteranzahl (SQLite: 999, SQL Server: 2100, PostgreSQL: 65535). Wenn Vorab-Prüfungen (Tenant-Filter, Zeilenprädikate, Owner-Checks) bereits Parameter verbrauchen, sinkt die verfügbare Kapazität für ID-Listen drastisch.
3. **Vier-Augen-Prinzip & Audit-Kettenintegrität:** Hochsensible Tabellen (`Sensitivity = HIGH` oder `RequiresFourEyes = true`) erfordern zwingend zwei unabhängige Genehmiger (Funktionstrennung). Zudem muss die kryptografische SHA-256-Hashkette auch nach einem Prozess- oder Server-Neustart nahtlos weitergeführt werden können.

## Entscheidung
1. **Dialektspezifischer Composite-Key-Generator:**
   - PostgreSQL, SQLite, Databricks: Verwendung von nativem Tuple-IN (`("colA", "colB") IN (($1, $2), ($3, $4))`).
   - SQL Server: Verwendung von disjunktiven Klammerausdrücken (`(([colA] = @p1 AND [colB] = @p2) OR ...)`), ab größeren Mengen `INNER JOIN (VALUES ...)`.
2. **Dynamisches Parameter-Budgeting (`IChunkedQueryExecutor`):**
   - Die effektive Batch-Chunk-Größe wird dynamisch zur Laufzeit berechnet:
     $$\text{ChunkSize} = \max\left(1, \min\left(\text{DefaultChunkSize}, \frac{\text{MaxParams} - \text{ContextParams} - \text{SafetyBuffer}}{\text{KeyColumnCount}}\right)\right)$$
   - Dies garantiert deterministische Einhaltung aller DB-Parameterlimits selbst bei 8-fach zusammengesetzten Schlüsseln und aktiven RLS-Prädikaten.
3. **Striktes Vier-Augen-Prinzip:**
   - Bei Tabellen mit `RequiresFourEyes = true` versetzt die erste Genehmigung den Antrag in den Zustand `PENDING_SECOND_APPROVAL`.
   - Der zweite Genehmigende darf weder der Antragsteller noch der erste Genehmiger sein (`approver2 != approver1 && approver2 != requester`).
   - Erst nach erfolgreicher zweiter Genehmigung wird der Status auf `APPROVED` gesetzt und ein aktiver `Consent` generiert.
4. **Resiliente Audit-Hash-Kette:**
   - Beim Hochfahren liest das Repository den letzten `entry_hash` aus `AUDIT_LOG_ENTRIES` aus, sodass die kryptografische Verkettung über Reboots hinweg manipulationssicher gewahrt bleibt.

## Konsequenzen
### Positiv
- 100% Parameter-Compliance auf allen unterstützten Datenbank-Dialekten (keine `SqlException: Too many parameters`).
- Unterstützung heterogener Datenmodelle mit bis zu 8-stufigen Hierarchien und Composite Keys.
- Revisionssichere Einhaltung der Funktionstrennung und lückenlose Integrität der Audit-Historie.
