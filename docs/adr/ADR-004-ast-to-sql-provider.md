# ADR-004: Eigener AST-to-SQL Filter- und Sortier-Provider statt IQueryable

## Status
Akzeptiert

## Kontext
Standard Hot Chocolate Filterung (`UseFiltering`) stützt sich auf `IQueryable` und Entity Framework Core Expression Trees. Da die Tabellenmodelle dynamisch als Dictionary-Zeilen (`IReadOnlyDictionary<string, object?>`) geführt werden und relationale Abfragen direkt an ADO.NET / Dapper gehen, kann `UseFiltering` nicht direkt nach SQL übersetzt werden.

## Entscheidung
Wir entwickeln einen dedizierten `ISqlFilterProvider`:
1. Er traversiert den GraphQL Filter-AST (`where: { ... }`) rekursiv.
2. Dialekt-Abstraktion: Erzeugt native Parameter-Syntax für SQL Server (`@p1`), PostgreSQL (`$1`), SQLite (`@p1`) und Databricks (`?`).
3. Strikte Whitelist-Validierung: Alle Spalten- und Tabellennamen werden vor der Generierung gegen `TableMetadata` geprüft. Nicht katalogisierte Bezeichner führen zum sofortigen Abbruch (vollständiger SQL-Injection-Schutz).
4. Regel-5-Durchsetzung: Filter oder Sortierungen auf Spalten mit Status `Mask` oder `Deny` werden blockiert.

## Konsequenzen
### Positiv
- Volle Kontrolle über das erzeugte SQL und Vermeidung von N+1 Abfragen.
- Immunität gegen SQL-Injections durch parametrisierte Bindung und Katalog-Whitelisting.
- Multi-Dialekt-Unterstützung für heterogene Quell-Datenbanken.

### Negativ
- Entwicklungsaufwand für die Wartung des AST-Parsers für erweiterte Filteroperatoren.
