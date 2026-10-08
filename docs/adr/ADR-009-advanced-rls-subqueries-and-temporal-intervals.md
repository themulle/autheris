# ADR-009: Advanced RLS Subqueries, Multi-Hop Joins & Temporal Validity Predicates

## Status
Accepted

## Kontext
Reale Berechtigungs- und Compliance-Regeln (Row-Level Security, RLS) beschränken sich häufig nicht auf statische Spaltenvergleiche derselben Tabelle. Typische fachliche Szenarien erfordern:
1. **Relational abhängige Berechtigungen (Single-Source):** Berechtigung auf Zeilen einer Tabelle $T_A$ hängt vom Vorhandensein verknüpfter Zeilen in $T_B$ ab.
2. **Multi-Hop-Relationen:** Berechtigungsketten über mehrere Tabellen hinweg (z. B. `Invoices -> Customers -> AssetOwnership -> Assets`).
3. **Temporale Gültigkeitsprüfungen:** Ein Zugriff ist nur für Transaktionen gestattet, die innerhalb eines zeitlichen Gültigkeitsintervalls stattfanden (z. B. `InvoiceDate >= ValidFrom AND (ValidTo IS NULL OR InvoiceDate < ValidTo)`).
4. **Cross-Source / Federated Virtual Sets:** Wenn abhängige Datenquellen in heterogenen Datenbanken liegen, müssen ID-Mengen zweistufig geladen und parameter-budgetiert injiziert werden.
5. **Schutz des DB-Parameter-Budgets (NF-SEC-01 / NF-PERF-01):** RDBMS-Parameterlimits (SQLite: 999, SQL Server: 2.100) dürfen niemals überschritten werden.

## Entscheidung

### 1. Hybrid Federated RLS Architecture
Wir etablieren einen hybriden Ansatz:
- **Single-Source Correlated Subquery Pushdown:** Befinden sich Ziel- und Filtertabellen in derselben Datenquelle, generiert das Gateway eine korrelierte `EXISTS (SELECT 1 FROM ... WHERE ...)`-Klausel.
  - **Parameter-Overhead:** $P = 0$ für ID-Mengen; Ausführung erfolgt direkt über den RDBMS-Query-Optimizer mittels Index-Seeks.
  - **Dialekt-Sicherheit:** Automatische Bezeichner-Quotierung für SQL Server (`[...]`), PostgreSQL (`"..."`), SQLite (`"..."`) und Databricks (`` `...` ``).
- **Cross-Source Virtual Set Injection:** Bei verteilten Datenquellen werden IDs über den `IChunkedQueryExecutor` in partitionierte Chunks aufgeteilt und mit `OR` verknüpft, wodurch die Parameter-Limits strikt eingehalten werden.

### 2. Domänen- und Metadaten-Erweiterung
- Erweiterung von `ConsentRowFilter` um:
  - `FilterType`: `RowFilterType` (`SimpleColumnPredicate`, `SubqueryCorrelated`, `CrossSourceSetFilter`).
  - `DependentTable`, `DependentTableAlias`, `ForeignKeyColumn`, `PrimaryKeyColumn`.
  - `SubqueryFilterPredicateJson`: Strukturierte Prädikate für abhängige Tabellen.
  - `TargetTemporalColumn`, `DependentValidFromColumn`, `DependentValidToColumn`: Interval-Checks.
  - `AdditionalHops`: Liste von `SubqueryJoinHop` für $N$-stufige Relationen-Ketten.

### 3. SQL Injection Guardrails
- Sämtliche Tabellennamen, Schemanamen, Aliase und Spaltenbezeichner werden strikt gegen Regex `^[a-zA-Z_][a-zA-Z0-9_]*(\.[a-zA-Z_][a-zA-Z0-9_]*)*$` validiert. Jeder unzulässige Bezeichner führt zu einer sofortigen `InvalidOperationException`.

## Konsequenzen
- **Positiv:** Komplexe Governance-Anforderungen wie temporale Besitzwechsel (*"Kunde besaß Baukran zum Rechnungszeitpunkt"*) werden hocheffizient ohne Client-Einfluss erzwungen.
- **Positiv:** Zero Parameter Overhead im Standardfall (Single-Source Pushdown).
- **Positiv:** Vollständige Rückwärtskompatibilität zu bestehenden atomaren Zeilenfiltern.
