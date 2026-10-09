# Virtuelle Filter: Messungen (Phase 9)

**Bezug:** [Umsetzungsplan](2026-10-08-umsetzungsplan-virtuelle-filter.md), Phase 9.

## 1. Auflösung im Gateway (gemessen)

**Aufbau:** `benchmarks/Autheris.Benchmarks`, Aufruf mit `dotnet run -c Release --project benchmarks/Autheris.Benchmarks -- vf`.
- 200 Bindungen in 5 Profilen: Benutzer, Gruppe und drei Rollen, davon zwei passend.
- 40 strukturierte Filter, 150 Tabellen in 5 Schemas.
- Muster mit regulären Ausdrücken.
- Echter `StructuredFilterSqlBuilder`.

**Gemessen am 08.10.2026** in WSL2 (8 GB RAM), während parallel andere Builds und Tests liefen. Die Werte sind daher eher zu hoch.

| Fall | P50 | P99 | Ziel |
| :--- | :--- | :--- | :--- |
| Treffer im Gedächtnis (Normalbetrieb, gleiche Generation) | 1,3 µs | 9,2 µs | unter 50 µs: erfüllt |
| Ohne Treffer (erster Zugriff nach einer Änderung) | 97 µs | 396 µs | – |

**Folgerung:** Die Auflösung fällt neben der Datenbankabfrage nicht ins Gewicht. Teuer ist nur der erste Zugriff je Objekt nach einer Änderung. Das betrifft eine Abfrage pro Objekt und Aufrufer und Generation, nicht jede Abfrage.

## 2. Abfrage in SQL Server (offen, braucht den PoC)

Hier nicht messbar: Es gibt keine SQL-Server-Instanz mit den PoC-Daten. Vorgehen für den Talos-PoC (`fms.air1`, 46 700 Krane):

1. **Filter:** David-Filter als Profil anlegen, einmal strukturiert und einmal als SQL-Definition mit Zeitfenster aus Entwurf 3.7.
2. **Strategien:** `Gateway:RowFilters:SubqueryStrategy` nacheinander auf `Exists`, `InCorrelated` und `In` setzen. Nur einspaltige strukturierte Filter ohne Zeitfenster nutzen `IN`; alles andere ist `EXISTS`.
3. **Index:** jede Variante mit und ohne Index `(client_id, ts)` auf `fms.air1` messen.
4. **Messweise:**
   - Die Anweisung aus `effective-filters` (Feld `sql`) zusammen mit dem WebSQL-SELECT unter `SET STATISTICS IO, TIME ON` ausführen.
   - Den tatsächlichen Plan sichern.
   - Messgrößen: logische Lesevorgänge, CPU-Zeit, Dauer, Plan (Semi-Join oder Nested Loops).
5. **Ergebnis:** in diesen Abschnitt eintragen, mit der Entscheidung über die Standardstrategie. Heute ist im PoC `InCorrelated` gesetzt; ein Wechsel nur mit Zahlen.
