# Umsetzungsplan: Lückenloses Zugriffs-Audit

**Stand:** 09.10.2026 · **Feature-Beschreibung:** [Lückenloses Zugriffs-Audit](2026-10-09-feature-lueckenloses-zugriffs-audit.md)
**Befunde zur Audit-Architektur:** [AU-01 … AU-19](2026-10-09-audit-architektur-befunde.md) – Phase 0b/8 unten.
**Vorgehen:** kleine, einzeln auslieferbare Schritte; zuerst messen und absichern, dann umbauen. Jede Phase endet mit grünen Tests auf SQLite, PostgreSQL und SQL Server.

## Übersicht

| Phase | Inhalt | Löst | Aufwand |
|---|---|---|---|
| 0 | Bestandsaufnahme verifizieren, Abdeckungstest als Messlatte | L-4, L-9 (Messung) | S |
| 0b | **Audit-Kern härten** (vor Phase 2): AU-07, AU-04, AU-05, AU-12, gemeinsamer Kern | AU-04, AU-05, AU-07, AU-10, AU-12 | L |
| 1 | Ereigniskatalog und Datenminimierung | L-7 (Teil), 4.3, 4.6 | M |
| 2 | Zentrale Audit-Stelle und Endpunkt-Richtlinie | L-9, 4.1, 4.2 | L |
| 3 | Authentifizierung und Autorisierung | L-1, L-6 | M |
| 4 | Lücken in Datenkanälen schließen | L-3, L-4 | M |
| 5 | Katalog-/Metadatenzugriffe | L-2 | S–M |
| 6 | Ausfallverhalten und Konfiguration bereinigen | L-5, L-7, L-8 | M |
| 7 | Betrieb: Aufbewahrung, Alarme, Doku | Rest | M |
| 8 | Befunde zu Anker, Schlüsseln, Health, Doku | AU-01–03, AU-06, AU-08, AU-09, AU-11, AU-13–19 | M–L |

Aufwand: S ≈ 1–2 Tage, M ≈ 3–5 Tage, L ≈ 1–2 Wochen (eine Person, inklusive Tests).

## Phase 0 – Messlatte (vor jeder Änderung)

1. Alle registrierten Endpunkte automatisch auflisten (Endpoint-Datenquelle der Anwendung) und je Endpunkt festhalten, ob eine Anfrage einen Audit-Eintrag erzeugt. Ergebnis als Testbericht in `docs/` ablegen.
2. **Arrow Flight** gezielt verifizieren: Wird der SQL-Service durchlaufen, wird genau ein Eintrag geschrieben, bei Ablehnung ebenfalls?
3. Je Datenkanal einen Integrationstest „eine Anfrage ⇒ genau ein Abschlusseintrag“ schreiben, zunächst als **erwarteter Fehlschlag** (`Skip` mit Verweis auf die zuständige Phase), damit der Fortschritt messbar wird.
4. Basismessung: p95-Latenz und Durchsatz eines Standard-Abfragepfads mit aktuellem Audit (Referenz für Phase 6).

**Abnahme:** Befundliste L-1 bis L-9 ist bestätigt oder korrigiert; Abdeckungsbericht liegt vor.

## Phase 0b – Audit-Kern härten (vor Phase 2)

Die Review ([Befunde](2026-10-09-audit-architektur-befunde.md)) zeigt Mängel im Kern, auf dem alle neuen Ereignisse aufsetzen würden. Diese Schritte kommen **vor** der zentralen Middleware:

1. **AU-07 (SQL Server):** Werte vor dem Hashen validieren/kürzen, Test mit Überlänge je Feld. Vor jeder Freigabe des SQL-Server-Anbieters.
2. **AU-12:** Hintergrund-Task erst nach vollständiger Initialisierung starten (alle Anbieter).
3. **AU-05:** Tier-B-Stapel bei Fehler neu einreihen oder in Dead-Letter schreiben; „gestört“ per Probe-Schreibvorgang zurücknehmen; Task-Überwachung; Kennzahl für verworfene Einträge.
4. **AU-04:** Audit-Insert in derselben Transaktion wie der Geschäftsvorgang (oder Outbox mit garantierter Nachlieferung); in PG/MS und SL gleich.
5. **AU-10:** Gemeinsamen, anbieterneutralen Audit-Kern extrahieren (Kanonisierung, Hash, Kette, Anker, Stapel, Prüfung); je Anbieter nur Persistenz. Gemeinsame Vertragstests über alle drei Anbieter (Sortierung, Grenzen, Hash-Gleichheit, Append-only, Failover-Verhalten). Hash-Versionierung, damit bestehende Ketten prüfbar bleiben (Escape-Unterschied SL ↔ PG).

**Abnahme:** Vertragstests grün für alle drei Anbieter; Fehlerfall-Tests (Commit scheitert, Datenbank vorübergehend nicht erreichbar, Überlänge) belegen: kein Phantom-Ereignis, kein verlorener Stapel, kein falscher Kettenbruch.

## Phase 1 – Ereigniskatalog und Minimierung

1. `AuditEventTypes` (Konstanten) mit Metadaten: Beschreibung, Pflichtfelder, Stufe (A/B/C), Datenklasse. Alle bestehenden Freitext-Typen migrieren (Suche nach den bekannten Namen, z. B. `TABLE_QUERY`, `CONSENT_GRANTED`).
2. `AuditDetails`-Builder statt von Hand gebauter JSON-Zeichenketten: erlaubt nur katalogisierte Felder, begrenzt Längen, neutralisiert Steuerzeichen und Zeilenumbrüche, schließt Geheimnisse aus (Namensmuster wie `password`, `token`, `secret`, `authorization`).
3. Statement-Behandlung: normalisierter Hash plus Spaltenliste; Option `Audit:StoreStatementText` (Standard aus).
4. Unit-Tests: Log-Injection, Längenbegrenzung, Geheimnis-Ausschluss, Katalog-Vollständigkeit (jeder Typ hat Beschreibung und Stufe).
5. Doku: Katalog wird aus dem Code in eine Markdown-Tabelle erzeugt (kleines Werkzeug, Prüfung im CI, dass die Datei aktuell ist).

**Abnahme:** keine freien Typ-Zeichenketten mehr in `RecordAuditEventAsync`-Aufrufen (Analyzer oder Architekturtest); Hash-Kette unverändert prüfbar.

## Phase 2 – Zentrale Audit-Stelle

1. `AuditContext` (scoped): Trace-ID, Kanal, Quell-IP (hinter Reverse-Proxy korrekt aufgelöst), Principal, Beginn. Befüllung früh in der Pipeline, nach `SecurityContextResolution`/`TenantResolution` ergänzt.
2. `AccessAuditMiddleware`: schreibt im `finally` genau einen Abschlusseintrag (Ergebnis `ALLOW`/`DENY`/`ERROR` aus Antwortstatus und Kontext). Platzierung: nach Authentifizierung und Mandantenauflösung, aber **vor** dem Abfangen von Ausnahmen, damit Fehler sichtbar bleiben.
3. Attribut `Audit(...)` und `AuditExempt(...)` für Minimal-APIs (Endpunkt-Metadaten, `WithMetadata`). Standard bei fehlender Angabe: Architekturtest schlägt fehl (nicht still ausnehmen).
4. Fachdienste ergänzen den Kontext statt eigene Standardeinträge zu schreiben. Migration **kanalweise**; solange ein Kanal noch selbst schreibt, markiert er den Kontext als „bereits protokolliert“, damit kein Doppeleintrag entsteht.
5. Architekturtest: alle Endpunkte tragen Richtlinie oder Ausnahme; die Ausnahmeliste ist fest und klein (Health, Liveness, Metriken).

**Abnahme:** Abnahmekriterium 1 und 4 für REST, GraphQL, OData, MCP. Doppel-Eintrags-Test grün.

## Phase 3 – Authentifizierung und Autorisierung

1. Authentifizierung: Ereignisse `AUTH_FAILED` und `AUTH_TOKEN_REJECTED` aus den Authentifizierungs-Handlern (`OnAuthenticationFailed`, `OnChallenge`, Basic-Auth-Sitzung, Token-Widerruf-Middleware). Inhalt: Schema, Grundcode (abgelaufen, Signatur, Aussteller, Zielgruppe, widerrufen), Quell-IP, behaupteter Principal **nur wenn aus dem Token ohne Vertrauen lesbar und gekürzt**. Kein Tokeninhalt.
2. Verdichtung (Stufe C): `AuthFailureAggregator` je (Quelle, Grundcode) und Fenster; erstes Ereignis sofort, danach ein Zähler-Eintrag pro Fenster. Obergrenze pro Minute, Überschreitung erzeugt einen Alarm-Eintrag und eine Kennzahl.
3. Autorisierung auf Endpunktebene: `AUTHZ_ENDPOINT_DENIED` bei 403 aus Richtlinien (Rollen/Scopes), Rate-Limit (`RATE_LIMIT_EXCEEDED`) und read-only-Token-Sperren.
4. Erfolgreiche Anmeldung: `AUTH_SUCCEEDED` je Sitzung beziehungsweise Token-ID und Zeitfenster, nicht je Anfrage (sonst doppelt zum Abschlusseintrag).
5. Tests: je Fehlerart ein Eintrag; Flut-Test (Abnahme 7); kein Token im Eintrag; Zeitverhalten bei gestörtem Audit (Abschnitt Phase 6).

**Abnahme:** Kriterien 2, 3 (Endpunkt-Teil) und 7.

## Phase 4 – Datenkanäle schließen

1. **WebSQL:** `WEBSQL_QUERY_DENIED` für Ablehnungen vor der Ausführung (Syntax-/Richtlinien-/Guardrail-Ablehnung), `QUERY_EXECUTION_ERROR` für Fehler nach Erlaubnis.
2. **Arrow Flight:** Weiterleitung über den SQL-Dienst belegen oder Audit nachrüsten; Test mit Erlaubnis, Ablehnung, Abbruch während des Streams (Eintrag mit Ergebnis `ERROR` und übertragenen Zeilen).
3. **Parquet, DuckDB-OLAP, Iceberg, CDC, Prozeduren, Hausinterne Exporte:** Je Kanal prüfen, dass der Abschlusseintrag Zeilen- und Byteanzahl enthält (Anomalie-Erkennung, z. B. Massenabzug).
4. **Streaming-Antworten:** Eintrag erst nach Ende oder Abbruch des Streams, mit Zählern; Abbruch durch den Client erzeugt ebenfalls einen Eintrag.
5. Tests je Kanal (Phase 0 – erwartete Fehlschläge werden grün).

**Abnahme:** Kriterium 3 (SQL-Teil) und 4 vollständig.

## Phase 5 – Katalog- und Metadatenzugriffe

1. Endpunkte für Backstage, DevPortal, SchemaRegistry, dbt-Lesen, OpenAPI/Schema-Export mit `Audit(Summarized, "CATALOG_READ")` versehen.
2. Verdichtung: je (Principal, Objektklasse) und Fenster ein Eintrag mit Zähler; Einzelabruf sensibler Objekte (Klasse hoch) bleibt Einzeleintrag.
3. Entscheidung aus den offenen Fragen (a) umsetzen; Konfiguration `Audit:CatalogReadMode = Summarized | Full`.

**Abnahme:** Kriterium 1 für alle Katalog-Endpunkte.

## Phase 6 – Ausfallverhalten und Konfiguration

1. `IAuditLogRepository` überall verpflichtend (Konstruktorparameter ohne Nullable); Start schlägt fehl, wenn nicht registriert (L-5).
2. Notfallpfad für Authentifizierungs-Fehlschläge bei gestörtem Audit: strukturiertes Anwendungslog plus Zähler `autheris_audit_fallback_total`, Alarm in der Betriebsdoku.
3. Wirkungslose Schalter bereinigen: `TierAEnabled` entfernen (Verhalten ist fest), `TierBAggregationWindowSeconds` an die Verdichtung binden. Startvalidierung weist alte Schlüssel mit Hinweis ab oder ignoriert sie mit Warnung (Übergangsfrist eine Version).
4. Verlustfenster: Standard für `SynchronousQueryAudit` nach Entscheidung (b); Batch-Schreiben (mehrere Einträge pro Transaktion) für die synchrone Variante, damit der Durchsatz hält.
5. Lasttest gegen die Basismessung aus Phase 0: Zusatzlatenz p95 unter dem vereinbarten Grenzwert (Vorschlag: +10 %), sonst Stufen anpassen.

**Abnahme:** Kriterien 5 und 9; Lastbericht liegt vor.

## Phase 7 – Betrieb, Aufbewahrung, Dokumentation

1. `AuditLogRetentionDays` tatsächlich anwenden: Archivierungsjob exportiert vor dem Löschen (WORM/Objektspeicher), schreibt `AUDIT_RETENTION_PURGE` mit Bereich und Hash der archivierten Reihe. Löschen nur diesem Job vorbehalten (eigene Datenbankrolle); Kette bleibt über Ankerpunkte prüfbar.
2. Alarme: Pipeline gestört, Kettenbruch (besteht), Fehlschlag-Spitzen, Fallback-Zähler > 0, Abdeckungs-Kennzahl < 100 %.
3. Zugriff auf das Audit auditieren (`AUDIT_READ`, `AUDIT_EXPORT`); Rollen für Lesen/Export trennen.
4. Dokumentation aktualisieren: README-Aussagen präzisieren, `arc42`, Betriebshandbuch (Verlustfenster, Notfallpfad, Archivierung), Konfigurationsleitfaden, Ereigniskatalog, Verzeichnis der Verarbeitungstätigkeiten (IP, User-Agent).
5. Abschlussprüfung: alle zehn Abnahmekriterien nachweisen und im Bericht festhalten.

## Phase 8 – Befunde zu Anker, Schlüsseln, Health, Doku

Reihenfolge nach Schwere (Details in den [Befunden](2026-10-09-audit-architektur-befunde.md)):

1. **AU-03:** Nur ausdrücklich `Development`/`Test` gilt als Entwicklung; leerer Umgebungsname ist Produktion; Fallback-Schlüssel nur in Entwicklung und zufällig pro Prozess.
2. **AU-01 / AU-02:** Außerhalb von Entwicklung Pflicht: externer Anker (`ChainAnchorPath`/`WormDirectory`) und asymmetrischer Signer; sonst Startfehler. Schlüsselrotation und Prüfschlüssel dokumentieren.
3. **AU-06:** SQL-Literale nicht im Klartext ins Audit (Hash/Normalisierung), `DetailsJson` begrenzen – gemeinsam mit Phase 1.
4. **AU-09 / AU-19:** Health: Prüflauf-Fehler und „nie geprüft“ als ungesund, Schnittstelle `IAuditHealth`, Anker-Rückstand als Warnung, keine leeren Catch-Blöcke ohne Signal; Prüfintervall für Produktion verkürzen oder inkrementell prüfen.
5. **AU-11:** Mandant im Repository erzwingen, Abschneide-Kennzeichen bei Kappung.
6. **AU-13:** Failover/Wiederherstellung als eigener Zustand mit Wiederherstellungsverfahren (Vier-Augen, eigener Audit-Eintrag).
7. **AU-08 / AU-18:** Tote Funktionen umsetzen oder entfernen (Retention in Phase 7), Doku korrigieren (README, arc42, Runbook, ADR-005).
8. **AU-14 – AU-17:** Anker atomar und begrenzt, `occurred_at`/`seq`-Semantik festlegen, SL-Schema angleichen, optionalen Writer verpflichtend machen (Phase 6).

**Abnahme:** Jeder Befund AU-01 … AU-19 hat Status *behoben*, *bewusst akzeptiert (begründet)* oder *entfällt*; der Befundbericht wird entsprechend fortgeschrieben.

## Querschnitt

- **Reihenfolge der Auslieferung:** Phase 0, 0b und 1 zuerst und einzeln (0b vor 2); Phase 3 hat den höchsten Sicherheitsnutzen und kann parallel zu Phase 2 beginnen (eigene Hooks), muss aber den `AuditContext` danach nutzen.
- **Datenbank-Anbieter:** Neue Ereignistypen brauchen keine Schemaänderung (Typ ist Text). Falls Spalten für Kanal, Quell-IP oder Grundcode hinzukommen: Migration in allen drei Repositories (SQLite, PostgreSQL, SQL Server) inklusive Idempotenz, und Hash-Berechnung versionieren, damit alte Einträge prüfbar bleiben.
- **Rückwärtskompatibilität:** Hash-Format der Kette unverändert lassen; neue Felder nur über `DetailsJson`, solange kein Versionssprung nötig ist.
- **Testmatrix:** Unit (Builder, Aggregator, Katalog), Architektur (Endpunkt-Abdeckung, kein freier Typ), Integration (je Kanal, drei Datenbanken), Last (Phase 6), Sicherheit (Injection, Flut, Geheimnis-Ausschluss).
- **Nachvollziehbarkeit:** Jede Phase endet mit einem kurzen Bericht in `docs/plans/` (Ergebnis gegen Abnahmekriterien).

## Reihenfolge der Entscheidungen (vor Phase 5/6 nötig)

1. Katalog-Lesezugriffe verdichtet oder je Zugriff?
2. Synchrones Query-Audit als Standard?
3. Aufbewahrung je Ereignisklasse?
4. Regulatorische Pflichtereignisse (BAIT/DORA, DSGVO-Auskunft)?
5. Grenzwert für Zusatzlatenz.
