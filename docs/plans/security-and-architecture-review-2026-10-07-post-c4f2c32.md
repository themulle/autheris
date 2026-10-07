# Security- und Architektur-Review (Post-Commit c4f2c32) – 2026-10-07

## Zusammenfassung & Status

Evaluation des Commits `c4f2c32` (`feat(security & architecture)`).
Die grundlegende Architektur der GraphQL-Tree-Execution über den geprüften Abfragedienst ist solid. SQL-1, SQL2-1, POL-2, POL-3, POL-4, POL-7, EXT-2, API-1 und API-4 wurden architektonisch korrekt angelegt.

Folgende 6 Muss-Kriterien müssen vor einem Merge nach `main` behoben und durch Unit-Tests abgesichert werden:

## Muss vor dem Merge behoben werden

1. **Bestehende Tests brechen (R-SQL-1):** Seit SQL2-3 warf `SqlDataSourceExecutor` bei `_environment == null` eine `NotSupportedException`. Unit-Tests instanziieren den Executor ohne Host-Environment und erwarten synthetische Daten für Dev/Unit-Tests. Zudem fiel `GatewayExecutionService` nicht mehr kontrolliert auf den Standard-SQL-Executor zurück.
2. **Globale Casbin-Verbote greifen nicht überall (R-POL-2):** Hat ein Mandant eigene Regeln, wurden die globalen `*`-Regeln komplett übersprungen, inklusive der Verbote (`CasbinEnforcementService.cs:283`). Deny muss absolute Priorität besitzen und mandantenübergreifend gelten.
3. **Widerruf per Policy-Datei wirkt nicht (R-POL-3):** Beim Neuladen wurden nur die Mandanten ersetzt, die in der neuen Datei stehen. Aus der Policy entfernte Mandanten behielten ihre Berechtigungen und Zeilenfilter bis zum Neustart.
4. **Globale Casbin-Erlaubnisse wirken nie (R-POL-1):** Leere Mandantenfelder wurden zu `*`. Der Casbin-Matcher verlangte jedoch exakt denselben Mandanten (`r.tenant == p.tenant`), wodurch `*` nie für spezifische Mandanten matchte.
5. **Maskierte Zahlen- und Wahrheitswerte in GraphQL (R-GQL-1):** Maskierte Spalten wurden im SQL-Tree als `'***'` gerendert. Bei Zahlenspalten bricht GraphQL mit Coercion-Fehler ab; bei Boolean-Spalten wird fälschlicherweise `false` geliefert. Bei Nicht-String-Typen muss Maskierung `null` liefern.
6. **Namenskollisionen legen GraphQL lahm (R-GQL-2):** Tabellen wie `orders` und `orders_filter` oder Spalten mit Namen `and`/`or`/`not` erzeugen doppelte GraphQL-Typen und Input-Felder.

## Weitere Befunde zur Nachverfolgung

- **R-GQL-3:** WebSocket-AuthInterceptor cacht Autorisierungsentscheidungen für die gesamte Verbindung; Token-Wechsel und Re-Evaluierung härten.
- **R-GQL-4 / SQL2-7:** Obergrenze für Pagination (`offset` und aggregiertes Zeilenlimit über Baum-Ebenen).
- **R-GQL-7:** N² Relation-Abfragen beim Schema-Aufbau auf O(N) Batch-Lookup optimieren.
- **R-SQL-3:** Transaktionsbezogener Sitzungskontext in PostgreSQL (`LOCAL`) gegen Verbindungspooling-Leaks hinter PgBouncer.
- **R-SQL-4:** `SET TRANSACTION READ ONLY` für Prozedur-Row-Scope wiederherstellen.
- **R-POL-5 / R-POL-6:** Fail-closed Validierung beim Start von Casbin-Policy-Dateien und explizite Implementierung der Interface-Methoden.
- **R-ERR-1:** Fehlermeldung `INVALID_QUERY` in Produktion auf generische Fehlertexte beschränken.
