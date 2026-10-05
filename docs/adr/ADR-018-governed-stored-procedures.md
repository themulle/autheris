# ADR-018: Stored Procedures als Governed Endpoints mit datenbankseitiger RLS über SESSION_CONTEXT

**Status:** Vorgeschlagen (Implementierung Phase 1, lesend)
**Datum:** 2026-10-05
**Bezug:** F-SQL-02, ADR-001 (Trusted Subsystem)

## Kontext

Das Gateway kann den Rumpf einer Stored Procedure nicht umschreiben. Die RLS- und Masking-Injektion in den SQL-AST (F-DATA-02 / F-SQL-01) wirkt deshalb nicht. ADR-001 sieht ein reines Lese-Dienstkonto (SELECT) vor.

## Entscheidung

1. **Zweites, getrenntes Dienstkonto** (`SqlEndpoints.Procedures.ConnectionName`) mit ausschließlich `EXECUTE` auf den freigegebenen Schemas (`AllowedSchemas`). Es hat kein SELECT und kein DML auf Tabellen. Zugriff auf Tabellen entsteht nur über Ownership Chaining innerhalb der Prozedur.
2. **Zeilensicherheit in der Datenbank:** Das Gateway schreibt vor jedem Aufruf Tenant, Nutzer-SID und Zweck in den schreibgeschützten `SESSION_CONTEXT` (`autheris.tenant_id`, `autheris.user_sid`, `autheris.purpose`, `@read_only = 1`). Eine `SECURITY POLICY` mit FILTER-Predicate pro Tabelle wertet diese Werte aus.
3. **Registrierung ist fail-closed.** Eine Prozedur wird erst aktiv, wenn der `StoredProcedureCatalogValidator` bestätigt hat:
   - Schema erlaubt, kein `sys.*`, `sp_*`, `xp_*`, kein dreiteiliger Name,
   - EXECUTE vorhanden, kein direktes SELECT auf referenzierte Tabellen,
   - Signatur stimmt mit der Deklaration überein (Name, Typ, Länge), keine OUTPUT-Parameter,
   - Ergebnisstruktur bestimmbar,
   - jede referenzierte Tabelle ist im Katalog und hat eine aktive Security Policy mit FILTER-Predicate,
   - kein `EXECUTE AS`, kein dynamisches SQL (Ausnahme: `@allow-dynamic-sql` nach DBA-Review),
   - als `read` deklarierte Prozeduren ändern keine Tabellen,
   - nur direkte Referenzen auf Tabellen (verschachtelte Prozeduren/Views werden abgelehnt).
   Die Validierung läuft beim Start, nach Hot-Reload und zyklisch. Bei Drift wird der Endpoint deaktiviert (503) und `PROCEDURE_DISABLED` auditiert.
4. **Spalten-Governance im Gateway** auf der Ergebnismenge: Consent-Deny entfernt die Spalte, Mask maskiert, unbekannte Spalten werden entfernt (außer `@result-column x clear`).
5. **Consents mit Zeilenfilter werden abgelehnt.** Sie lassen sich nicht in eine Prozedur schieben, und eine In-Memory-Filterung weicht von der SQL-Semantik ab (Review E-6). Phase 1 ist damit unabhängig von E-5/E-6.
6. **Audit synchron und fail-closed** (`PROCEDURE_EXECUTE`, `PROCEDURE_DENIED`, `PROCEDURE_REJECTED`, `PROCEDURE_FAILED`). Parameter werden nur als gekürzter SHA-256-Hash protokolliert.
7. **Aufruf ausschließlich** über `CommandType.StoredProcedure` mit typisierten Parametern. Kontextparameter (`@context`) kann der Client nicht setzen; sie werden mit 400 abgelehnt wie jeder unbekannte Parameter.

## Konsequenzen

- (+) Zeilen-, Aggregat- und Join-Zugriffe innerhalb der Prozedur sind durch die Datenbank gefiltert.
- (+) Das Login kann kein Ad-hoc-SQL ausführen.
- (+) Connection Pooling bleibt möglich (Kontext wird pro Aufruf gesetzt, `sp_reset_connection` löscht ihn).
- (−) DBAs müssen Security Policies pflegen.
- (−) Feingranulare Consent-Zeilenfilter sind für Prozeduren nicht abbildbar (Phase 1).
- (−) Prozeduren mit verschachtelten Aufrufen oder `EXECUTE AS` brauchen eine eigene Bewertung.

## Phase 2 (noch nicht umgesetzt)

Schreibende Prozeduren: Writer-Rolle, Consent-Aktion `execute`, `Idempotency-Key`, Vier-Augen/HitL. Voraussetzung sind die Fixes M-3/R2-6, R2-1 und E-9 aus den Security-Reviews. `@mode write` und `@approval` werden bis dahin abgelehnt.
