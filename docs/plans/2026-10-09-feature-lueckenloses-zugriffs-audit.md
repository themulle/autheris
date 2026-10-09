# Feature: Lückenloses Zugriffs-Audit („Audit by default“)

**Stand:** 09.10.2026 · **Status:** Entwurf zur Abstimmung · **Zugehöriger Plan:** [Umsetzungsplan](2026-10-09-umsetzungsplan-lueckenloses-zugriffs-audit.md)
**Anlass:** Frage „Werden in Autheris sämtliche Zugriffe geloggt?“ – Antwort der Bestandsaufnahme: **nein**.

## 1 Ausgangslage

Autheris schreibt Audit-Einträge in `AUDIT_LOG_ENTRIES` (HMAC-verkettet, Anker, optional WORM). Der Schreibpfad ist gut gebaut. Die **Abdeckung** ist es nicht: Es gibt keine zentrale Audit-Stelle, jeder Service und Endpoint ruft den Writer selbst auf. Was ein Entwickler vergisst, fehlt im Audit – ohne dass es jemandem auffällt.

Befunde (Grep-basiert, vor Umsetzung per Test zu bestätigen):

| # | Lücke | Risiko |
|---|---|---|
| L-1 | Fehlgeschlagene Authentifizierung (ungültiges/abgelaufenes Token, falsche Basic-Auth-Daten) wird nicht auditiert | Brute-Force und Token-Missbrauch bleiben unsichtbar |
| L-2 | Katalog-/Metadaten-Lesezugriffe (Backstage, DevPortal, SchemaRegistry, dbt) ohne Audit; nur die Iceberg-Federation auditiert | Metadaten können sensible Strukturen und Beschreibungen enthalten |
| L-3 | `WEBSQL_QUERY` wird nur bei ALLOW geschrieben; Ablehnungen vor der Ausführung sind nur bei DML belegt | Abgelehnte Angriffsversuche fehlen |
| L-4 | Arrow Flight: kein eigener Audit-Aufruf, Weiterleitung an den SQL-Service ungeprüft | Möglicher blinder Fleck bei einem Massendaten-Kanal |
| L-5 | `IAuditLogRepository?` ist in mehreren Services optional; fehlt es, wird still **nicht** auditiert | Fail-open bei Fehlkonfiguration |
| L-6 | Autorisierungsfehler (403) auf Endpunktebene, Rate-Limit-Treffer (429) und Token-Widerrufs-Treffer werden nicht einheitlich erfasst | Lücke in der Sicherheitsüberwachung |
| L-7 | `Audit:TierAEnabled` und `Audit:TierBAggregationWindowSeconds` sind definiert, aber wirkungslos | Konfiguration täuscht Steuerbarkeit vor |
| L-8 | Erfolgreiche Abfragen laufen über einen Puffer im Speicher (Standard 5000) und können bei hartem Absturz verloren gehen | Verlustfenster; im Runbook benannt, aber Standard ist „asynchron“ |
| L-9 | Es gibt keinen Nachweis, dass neue Endpunkte auditiert werden (rund 119 Endpunkte, wenige Aufrufstellen) | Die Lücken wachsen mit dem Code |

**Ergänzend:** Die Mängel des Audit-Kerns (AU-01 bis AU-19: Phantom-Ereignisse, verlorene Stapel, schwache Anker, Anbieter-Drift) wurden bereits vollständig behoben, gehärtet und getestet.

## 2 Ziel

> Jeder Zugriff auf Autheris – erlaubt, abgelehnt oder fehlgeschlagen – erzeugt genau einen nachvollziehbaren Audit-Eintrag, **oder** ist ausdrücklich und begründet davon ausgenommen. Eine Ausnahme ist ein bewusster Eintrag in einem Katalog, kein Versehen.

Nicht-Ziele: Anwendungs-Debuglogs ersetzen, Request-/Response-Bodies archivieren, SIEM-Auswertung bauen (nur Export-Schnittstelle).

## 3 Best Practices als Leitplanken

Quellen: OWASP Logging Cheat Sheet und ASVS V7, NIST SP 800-92, BSI-Grundschutz OPS.1.1.5, ISO/IEC 27001 A.8.15, DSGVO Art. 5, 30, 32.

**Was immer auditiert werden muss**

| Kategorie | Beispiele in Autheris | Entscheidung |
|---|---|---|
| Authentifizierung | Erfolg, Fehlschlag, Token-Widerruf, Sitzungsbeginn/-ende, Dev-Persona | Fehlschläge **neu**, aggregiert (siehe 4.4) |
| Autorisierung | Deny je Tabelle/Spalte/Relation/Endpunkt, Break-Glass, Berechtigungsänderungen | Endpunkt-Deny **neu** |
| Zugriff auf geschützte Daten | Abfragen aller Kanäle (REST, SQL, GraphQL, MCP, OData, Flight, Parquet, Iceberg, DuckDB, CDC) | Vollständigkeit per Test erzwingen |
| Zugriff auf Metadaten | Katalog, Schema, Beschreibungen, Lineage | **Neu**, Stufe B (verdichtet) |
| Änderungen an Konfiguration und Richtlinien | Consent, Approval, Delegation, Maskierung, virtuelle Filter, Schema-Reload, dbt-Import | Bestand prüfen, Lücken schließen |
| Administrative Aktionen | Admin-Endpunkte, Imports, Syncs | Vollständig |
| Sicherheitsrelevante Ereignisse | Rate-Limit, Widerruf-Treffer, Ablehnung durch Guardrails, Validierungsfehler mit Angriffsbezug | **Neu** |
| System- und Audit-Ereignisse | Start/Stop, Audit-Pipeline gestört, Kettenbruch, Anker-Export, Ändern der Audit-Konfiguration | Teilweise neu |
| Fehler | Ausführungsfehler nach erteilter Erlaubnis (`ALLOW` ohne Ergebnis) | Ergebnis immer festhalten |

**Wie ein Eintrag aussehen muss („wer, was, wann, wo, womit, Ergebnis“)**

- *Wer:* `ActorSid`, Mandant, Authentifizierungsart (Entra, Basic, Dienstprinzipal, Token-ID), bei Fehlschlag der **behauptete** Principal, falls bekannt.
- *Was:* standardisierter Ereignistyp aus einem Katalog, Zielobjekt, Operation, bei Abfragen Hash des Statements und Namen der berührten Spalten, **keine Werte**.
- *Wann:* UTC, monotone Folgenummer (vorhanden).
- *Wo:* Kanal, Quell-IP (nach Reverse-Proxy-Auflösung), User-Agent gekürzt, Replikat.
- *Womit/Warum:* Trace-ID, Rechtfertigung/Ticket (Break-Glass, Vier-Augen), Policy-Epoche.
- *Ergebnis:* `ALLOW`, `DENY`, `ERROR`, plus Grundcode statt Freitext.

**Was nie in den Audit darf:** Passwörter, Tokens, Schlüssel, Parameter- und Zeilenwerte, vollständige SQL-Literale, Personendaten über das Notwendige hinaus (Datenminimierung). Eingaben werden gegen Log-Injection bereinigt (Zeilenumbrüche, Steuerzeichen) und in der Länge begrenzt.

**Integrität und Betrieb**

- Manipulationsschutz: bestehende HMAC-Kette, externer Anker, WORM beibehalten.
- Trennung der Pflichten: Schreibrecht nur für die Anwendung, Lese-/Exportrecht getrennt, kein Löschrecht (Aufbewahrung nur über kontrollierte Archivierung).
- Zeit: einheitliche UTC-Zeit, Hinweis auf NTP in der Betriebsdoku.
- Aufbewahrung: `AuditLogRetentionDays` (Standard 3650) ist umzusetzen und zu prüfen; Löschen nur durch den Archivierungsjob mit eigenem Audit-Eintrag.
- Überwachung des Audits: Ausfall der Pipeline, Kettenbruch und Anstieg von Fehlschlägen lösen Alarme aus.
- **Fail-closed für Daten- und Änderungszugriffe**, **kontrolliert fail-open mit Zähler und Alarm** nur dort, wo ein Ausfall des Audits sonst Anmeldung verhindern würde (siehe 4.5).

## 4 Funktionsbeschreibung

### 4.1 Zentrale Audit-Stelle statt verteilter Aufrufe

Eine Middleware (`AccessAuditMiddleware`) umschließt jede Anfrage. Sie legt einen `AuditContext` an (Trace-ID, Kanal, Quelle, Zeitpunkt) und schreibt **nach** der Verarbeitung genau einen Abschlusseintrag. Fachdienste ergänzen den Kontext (`audit.Describe(...)`: Zieltabelle, Spalten, Entscheidung, Grundcode), schreiben aber nicht mehr selbst den Standardeintrag. Fachliche Sondereinträge (Consent, Break-Glass, Maskierungsänderungen) bleiben eigene Events mit Verweis auf dieselbe Trace-ID.

### 4.2 Endpunkt-Audit-Richtlinie (deklarativ)

Jeder Endpunkt trägt ein Attribut oder eine Metadatenangabe:

- `Audit(AuditLevel.Full, "TABLE_QUERY")` – jeder Zugriff, Stufe A (synchron)
- `Audit(AuditLevel.Summarized, "CATALOG_READ")` – Stufe B (verdichtet, siehe 4.4)
- `AuditExempt("Begründung")` – nur für Health/Liveness/Metriken, Eintrag im Ausnahmekatalog

Ein **Architekturtest** durchläuft alle registrierten Endpunkte und schlägt fehl, wenn einer weder Richtlinie noch Ausnahme besitzt. Damit ist L-9 dauerhaft geschlossen.

### 4.3 Ereigniskatalog

Alle Ereignistypen stehen als Konstanten in `AuditEventTypes` (mit Beschreibung, Pflichtfeldern, Stufe). Freie Zeichenketten an den Aufrufstellen entfallen. Der Katalog wird in `docs/` automatisch erzeugt; Neues ohne Katalogeintrag lässt sich nicht kompilieren.

Neu: `AUTH_FAILED`, `AUTH_TOKEN_REJECTED`, `AUTHZ_ENDPOINT_DENIED`, `RATE_LIMIT_EXCEEDED`, `TOKEN_REVOKED_HIT`, `CATALOG_READ`, `METADATA_EXPORT`, `WEBSQL_QUERY_DENIED`, `QUERY_EXECUTION_ERROR`, `AUDIT_CONFIG_CHANGED`, `AUDIT_PIPELINE_FAULT`, `AUDIT_RETENTION_PURGE`, `SERVICE_STARTED`, `SERVICE_STOPPED`.

### 4.4 Stufen und Verdichtung

| Stufe | Inhalt | Schreibweise |
|---|---|---|
| A | Änderungen, Admin, Deny, Auth-Erfolg, Break-Glass, sensible Datenzugriffe | synchron, fail-closed |
| B | erlaubte Abfragen, Katalog-Lesezugriffe | gepuffert, **Kapazität und Verlustfenster dokumentiert**; Option `SynchronousQueryAudit` für regulierte Mandanten |
| C | Authentifizierungs-Fehlschläge, Rate-Limit-Treffer | je Quelle und Zeitfenster verdichtet (erstes Ereignis sofort, danach Zähler pro Minute), damit ein Angreifer das Audit nicht fluten kann |

`TierAEnabled` entfällt oder wird zu einem echten, **nicht abschaltbaren** Verhalten; `TierBAggregationWindowSeconds` steuert künftig die Verdichtung (Stufe B/C). Tote Schalter werden entfernt oder implementiert, nie belassen.

### 4.5 Ausfallverhalten

- Daten- und Änderungszugriffe: Audit nicht schreibbar ⇒ Anfrage scheitert (bestehendes Verhalten, jetzt für alle Kanäle).
- Authentifizierungs-Fehlschläge: Audit nicht schreibbar ⇒ Anmeldung bleibt abgelehnt (sie war es ohnehin), Ereignis wird zusätzlich im Notfallpfad (strukturiertes Anwendungslog + Zähler `autheris_audit_fallback_total`) festgehalten und alarmiert.
- Fehlt `IAuditLogRepository`, **startet die Anwendung nicht** (kein optionaler Writer mehr; L-5).

### 4.6 Datenschutz und Datenminimierung

- Statements werden als normalisierter Hash plus Spaltenliste gespeichert; Klartext-SQL nur, wenn `Audit:StoreStatementText=true` (Standard aus).
- IP-Adressen und User-Agent: Aufbewahrung und Zweck im Verzeichnis der Verarbeitungstätigkeiten (Art. 30) festhalten; Option zur gekürzten IP.
- Lesezugriff auf das Audit ist selbst ein Ereignis (`AUDIT_READ`), Export nur mit eigener Rolle.

### 4.7 Auswertung

Bestehende Endpunkte für Filter, Export und Kettenprüfung bleiben. Ergänzt werden Filter nach Kanal, Ergebnis und Grundcode, plus eine Vollständigkeits-Kennzahl (siehe Abnahme).

## 5 Abnahmekriterien

1. **Abdeckung:** Der Architekturtest findet für alle Endpunkte Richtlinie oder begründete Ausnahme; die Ausnahmeliste enthält nur Betriebsendpunkte.
2. **Auth:** Ungültiges Token, abgelaufenes Token, falsche Basic-Auth-Daten und widerrufenes Token erzeugen je einen Eintrag (verdichtet bei Wiederholung), ohne Token-Inhalt.
3. **Deny:** Jede Ablehnung (Endpunkt, Tabelle, Spalte, Relation, SQL vor Ausführung) erzeugt einen Eintrag.
4. **Kanäle:** Je ein Test pro Kanal (REST, SQL, GraphQL, MCP, OData, Flight, Parquet, Iceberg, DuckDB, CDC) belegt genau einen Abschlusseintrag je Anfrage.
5. **Fail-closed:** Bei gestörtem Audit scheitern Daten-/Änderungszugriffe aller Kanäle; die Anwendung startet nicht ohne Audit-Writer.
6. **Minimierung:** Tests belegen, dass weder Tokens, Passwörter noch Parameterwerte im Eintrag landen; Zeilenumbrüche in Eingaben werden neutralisiert.
7. **Flut:** 10 000 Fehlanmeldungen pro Minute erzeugen eine begrenzte Zahl Einträge, die Zählerwerte stimmen.
8. **Integrität:** Hash-Kette bleibt über alle neuen Ereignistypen prüfbar (alle drei Datenbank-Anbieter).
9. **Konfiguration:** Keine wirkungslosen Audit-Schalter mehr; Doku und `appsettings` stimmen mit dem Verhalten überein.
10. **Dokumentation:** Ereigniskatalog, Verlustfenster, Ausnahmeliste, Aufbewahrung und Datenschutzhinweise in `docs/` aktualisiert; README-Aussagen („alle DML-Ereignisse“, „Incomplete logs“) präzisiert.

## 6 Risiken und offene Fragen

| Risiko | Gegenmaßnahme |
|---|---|
| Last durch Audit aller Lesezugriffe | Stufe B/C verdichten, Batch-Schreiben, Messung vor Freigabe (Kennzahl: p95-Zusatzlatenz) |
| Speicherwachstum | Aufbewahrung umsetzen, Partitionierung/Archivierung prüfen, Größenabschätzung im Plan |
| Eintrags-Flut als Angriff (DoS auf Audit) | Verdichtung je Quelle, Obergrenze pro Minute, Alarm |
| Umbau der verteilten Aufrufe bricht Bestandstests | schrittweise Migration, Doppel-Eintrags-Test (genau ein Eintrag je Anfrage) |
| Personenbezug von IP/User-Agent | Zweckbindung, gekürzte IP als Option, Aufbewahrung festlegen |

Offen für die Abstimmung: (a) Dürfen Katalog-Lesezugriffe verdichtet werden oder verlangt die Fachseite je Zugriff einen Eintrag? (b) Soll `SynchronousQueryAudit` für alle Mandanten Standard werden? (c) Aufbewahrungsdauer pro Ereignisklasse (Auth-Fehlschläge kürzer als Datenzugriffe?). (d) Gibt es regulatorische Vorgaben (z. B. BAIT/DORA, DSGVO-Auskunft), die einzelne Ereignisse verlangen?
