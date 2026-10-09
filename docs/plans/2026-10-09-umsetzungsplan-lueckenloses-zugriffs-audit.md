# Umsetzungsplan: Lückenloses Zugriffs-Audit

**Stand:** 09.10.2026 · **Feature-Beschreibung:** [Lückenloses Zugriffs-Audit](2026-10-09-feature-lueckenloses-zugriffs-audit.md)  
**Status Audit-Kern-Härtung (AU-01 bis AU-19):** Vollständig implementiert und getestet (Phasen 0b und 8 abgeschlossen und aus den offenen Aufgaben entfernt).  
**Vorgehen:** kleine, einzeln auslieferbare Schritte; zuerst messen und absichern, dann umbauen. Jede Phase endet mit grünen Tests auf SQLite, PostgreSQL und SQL Server.

---

## Übersicht der offenen Phasen

| Phase | Inhalt | Löst | Aufwand | Status |
|---|---|---|---|---|
| **0** | Bestandsaufnahme verifizieren, Abdeckungstest als Messlatte | L-4, L-9 (Messung) | S | Offen |
| **1** | Ereigniskatalog und Datenminimierung | L-7 (Teil), 4.3, 4.6 | M | Offen |
| **2** | Zentrale Audit-Stelle und Endpunkt-Richtlinie | L-9, 4.1, 4.2 | L | Offen |
| **3** | Authentifizierung und Autorisierung | L-1, L-6 | M | Offen |
| **4** | Lücken in Datenkanälen schließen | L-3, L-4 | M | Offen |
| **5** | Katalog-/Metadatenzugriffe | L-2 | S–M | Offen |
| **6** | Ausfallverhalten und Konfiguration bereinigen | L-5, L-7, L-8 | M | Offen |
| **7** | Betrieb: Aufbewahrung, Alarme, Doku | Rest | M | Offen |

Aufwand: S ≈ 1–2 Tage, M ≈ 3–5 Tage, L ≈ 1–2 Wochen (eine Person, inklusive Tests).

---

## Phase 0 – Messlatte (vor jeder Änderung)

1. Alle registrierten Endpunkte automatisch auflisten (Endpoint-Datenquelle der Anwendung) und je Endpunkt festhalten, ob eine Anfrage einen Audit-Eintrag erzeugt. Ergebnis als Testbericht in `docs/` ablegen.
2. **Arrow Flight** gezielt verifizieren: Wird der SQL-Service durchlaufen, wird genau ein Eintrag geschrieben, bei Ablehnung ebenfalls?
3. Je Datenkanal einen Integrationstest „eine Anfrage ⇒ genau ein Abschlusseintrag“ schreiben, zunächst als **erwarteter Fehlschlag** (`Skip` mit Verweis auf die zuständige Phase), damit der Fortschritt messbar wird.
4. Basismessung: p95-Latenz und Durchsatz eines Standard-Abfragepfads mit aktuellem Audit (Referenz für Phase 6).

**Abnahme:** Befundliste L-1 bis L-9 ist bestätigt oder korrigiert; Abdeckungsbericht liegt vor.

---

## Phase 1 – Ereigniskatalog und Minimierung

1. `AuditEventTypes` (Konstanten) mit Metadaten: Beschreibung, Pflichtfelder, Stufe (A/B/C), Datenklasse. Alle bestehenden Freitext-Typen migrieren (Suche nach den bekannten Namen, z. B. `TABLE_QUERY`, `CONSENT_GRANTED`).
2. `AuditDetails`-Builder statt von Hand gebauter JSON-Zeichenketten: erlaubt nur katalogisierte Felder, begrenzt Längen, neutralisiert Steuerzeichen und Zeilenumbrüche, schließt Geheimnisse aus (Namensmuster wie `password`, `token`, `secret`, `authorization`).
3. Statement-Behandlung: normalisierter Hash plus Spaltenliste; Option `Audit:StoreStatementText` (Standard aus).
4. Unit-Tests: Log-Injection, Längenbegrenzung, Geheimnis-Ausschluss, Katalog-Vollständigkeit (jeder Typ hat Beschreibung und Stufe).
5. Doku: Katalog wird aus dem Code in eine Markdown-Tabelle erzeugt (kleines Werkzeug, Prüfung im CI, dass die Datei aktuell ist).

**Abnahme:** keine freien Typ-Zeichenketten mehr in `RecordAuditEventAsync`-Aufrufen (Analyzer oder Architekturtest); Hash-Kette unverändert prüfbar.

---

## Phase 2 – Zentrale Audit-Stelle

1. `AuditContext` (scoped): Trace-ID, Kanal, Quell-IP (hinter Reverse-Proxy korrekt aufgelöst), Principal, Beginn. Befüllung früh in der Pipeline, nach `SecurityContextResolution`/`TenantResolution` ergänzt.
2. `AccessAuditMiddleware`: schreibt im `finally` genau einen Abschlusseintrag (Ergebnis `ALLOW`/`DENY`/`ERROR` aus Antwortstatus und Kontext). Platzierung: nach Authentifizierung und Mandantenauflösung, aber **vor** dem Abfangen von Ausnahmen, damit Fehler sichtbar bleiben.
3. Attribut `Audit(...)` und `AuditExempt(...)` für Minimal-APIs (Endpunkt-Metadaten, `WithMetadata`). Standard bei fehlender Angabe: Architekturtest schlägt fehl (nicht still ausnehmen).
4. Fachdienste ergänzen den Kontext statt eigene Standardeinträge zu schreiben. Migration **kanalweise**; solange ein Kanal noch selbst schreibt, markiert er den Kontext als „bereits protokolliert“, damit kein Doppeleintrag entsteht.
5. Architekturtest: alle Endpunkte tragen Richtlinie oder Ausnahme; die Ausnahmeliste ist fest und klein (Health, Liveness, Metriken).

**Abnahme:** Abnahmekriterium 1 und 4 für REST, GraphQL, OData, MCP. Doppel-Eintrags-Test grün.

---

## Phase 3 – Authentifizierung und Autorisierung

1. Authentifizierung: Ereignisse `AUTH_FAILED` und `AUTH_TOKEN_REJECTED` aus den Authentifizierungs-Handlern (`OnAuthenticationFailed`, `OnChallenge`, Basic-Auth-Sitzung, Token-Widerruf-Middleware). Inhalt: Schema, Grundcode (abgelaufen, Signatur, Aussteller, Zielgruppe, widerrufen), Quell-IP, behaupteter Principal **nur wenn aus dem Token ohne Vertrauen lesbar und gekürzt**. Kein Tokeninhalt.
2. Verdichtung (Stufe C): `AuthFailureAggregator` je (Quelle, Grundcode) und Fenster; erstes Ereignis sofort, danach ein Zähler-Eintrag pro Fenster. Obergrenze pro Minute, Überschreitung erzeugt einen Alarm-Eintrag und eine Kennzahl.
3. Autorisierung auf Endpunktebene: `AUTHZ_ENDPOINT_DENIED` bei 403 aus Richtlinien (Rollen/Scopes), Rate-Limit (`RATE_LIMIT_EXCEEDED`) und read-only-Token-Sperren.
4. Erfolgreiche Anmeldung: `AUTH_SUCCEEDED` je Sitzung beziehungsweise Token-ID und Zeitfenster, nicht je Anfrage (sonst doppelt zum Abschlusseintrag).
5. Tests: je Fehlerart ein Eintrag; Flut-Test; kein Token im Eintrag; Zeitverhalten bei gestörtem Audit.

**Abnahme:** Kriterien 2, 3 (Endpunkt-Teil) und 7.

---

## Phase 4 – Datenkanäle schließen

1. **WebSQL:** `WEBSQL_QUERY_DENIED` für Ablehnungen vor der Ausführung (Syntax-/Richtlinien-/Guardrail-Ablehnung), `QUERY_EXECUTION_ERROR` für Fehler nach Erlaubnis.
2. **Arrow Flight:** Weiterleitung über den SQL-Dienst belegen oder Audit nachrüsten; Test mit Erlaubnis, Ablehnung, Abbruch während des Streams (Eintrag mit Ergebnis `ERROR` und übertragenen Zeilen).
3. **Parquet, DuckDB-OLAP, Iceberg, CDC, Prozeduren, Hausinterne Exporte:** Je Kanal prüfen, dass der Abschlusseintrag Zeilen- und Byteanzahl enthält (Anomalie-Erkennung, z. B. Massenabzug).
4. **Streaming-Antworten:** Eintrag erst nach Ende oder Abbruch des Streams, mit Zählern; Abbruch durch den Client erzeugt ebenfalls einen Eintrag.
5. Tests je Kanal.

**Abnahme:** Kriterium 3 (SQL-Teil) und 4 vollständig.

---

## Phase 5 – Katalog- und Metadatenzugriffe

1. Endpunkte für Backstage, DevPortal, SchemaRegistry, dbt-Lesen, OpenAPI/Schema-Export mit `Audit(Summarized, "CATALOG_READ")` versehen.
2. Verdichtung: je (Principal, Objektklasse) und Fenster ein Eintrag mit Zähler; Einzelabruf sensibler Objekte (Klasse hoch) bleibt Einzeleintrag.
3. Konfiguration `Audit:CatalogReadMode = Summarized | Full`.

**Abnahme:** Kriterium 1 für alle Katalog-Endpunkte.

---

## Phase 6 – Ausfallverhalten und Konfiguration

1. `IAuditLogRepository` überall verpflichtend (Konstruktorparameter ohne Nullable); Start schlägt fehl, wenn nicht registriert (L-5).
2. Notfallpfad für Authentifizierungs-Fehlschläge bei gestörtem Audit: strukturiertes Anwendungslog plus Zähler `autheris_audit_fallback_total`, Alarm in der Betriebsdoku.
3. Wirkungslose Schalter bereinigen: `TierAEnabled` entfernen (Verhalten ist fest), `TierBAggregationWindowSeconds` an die Verdichtung binden. Startvalidierung weist alte Schlüssel mit Hinweis ab oder ignoriert sie mit Warnung.
4. Verlustfenster: Standard für `SynchronousQueryAudit`; Batch-Schreiben (mehrere Einträge pro Transaktion) für die synchrone Variante, damit der Durchsatz hält.
5. Lasttest gegen die Basismessung aus Phase 0: Zusatzlatenz p95 unter dem vereinbarten Grenzwert.

**Abnahme:** Kriterien 5 und 9; Lastbericht liegt vor.

---

## Phase 7 – Betrieb, Aufbewahrung, Dokumentation

1. `AuditLogRetentionDays` tatsächlich anwenden: Archivierungsjob exportiert vor dem Löschen (WORM/Objektspeicher), schreibt `AUDIT_RETENTION_PURGE` mit Bereich und Hash der archivierten Reihe. Löschen nur diesem Job vorbehalten; Kette bleibt über Ankerpunkte prüfbar.
2. Alarme: Pipeline gestört, Kettenbruch, Fehlschlag-Spitzen, Fallback-Zähler > 0, Abdeckungs-Kennzahl < 100 %.
3. Zugriff auf das Audit auditieren (`AUDIT_READ`, `AUDIT_EXPORT`); Rollen für Lesen/Export trennen.
4. Dokumentation aktualisieren: README-Aussagen präzisieren, `arc42`, Betriebshandbuch (Verlustfenster, Notfallpfad, Archivierung), Konfigurationsleitfaden, Ereigniskatalog, Verzeichnis der Verarbeitungstätigkeiten.
5. Abschlussprüfung: alle Abnahmekriterien nachweisen und im Bericht festhalten.
