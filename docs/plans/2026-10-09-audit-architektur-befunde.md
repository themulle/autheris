# Befunde zur Audit-Architektur

**Stand:** 09.10.2026 · **Art der Prüfung:** Lesende Code-Review des Audit-Subsystems (Sqlite-, PostgreSql- und SqlServer-Repository, Anker, Health, Doku). Nichts wurde ausgeführt; die Befunde sind **CONFIRMED** (Code gelesen) oder **SUSPECTED** (Mechanismus belegt, Erreichbarkeit offen).
**Zugehörig:** [Feature „Lückenloses Zugriffs-Audit“](2026-10-09-feature-lueckenloses-zugriffs-audit.md) (Abdeckung) · [Umsetzungsplan](2026-10-09-umsetzungsplan-lueckenloses-zugriffs-audit.md) (Phase 8 behandelt diese Befunde)
Pfade relativ zu `src/Autheris.Infrastructure/Persistence/` (`P/`), PG = PostgreSql, MS = SqlServer, SL = Sqlite. Zeilenangaben können sich mit dem Code verschieben.

## Übersicht

| ID | Schwere | Status | Befund |
|---|---|---|---|
| AU-01 | Hoch | CONFIRMED | Standard-Anker bei PG/MS nur im Speicher, auch in Produktion |
| AU-02 | Hoch | CONFIRMED | HMAC-Schlüssel und Anker-Signatur in derselben Vertrauensdomäne |
| AU-03 | Hoch | CONFIRMED | Leerer Umgebungsname gilt als Entwicklung; bekannte Fallback-Schlüssel |
| AU-04 | Hoch | CONFIRMED | PG/MS schreiben das Audit-Ereignis vor dem Geschäfts-Commit auf eigener Verbindung (Phantom-Ereignisse) |
| AU-05 | Hoch | CONFIRMED | Tier-B-Stapel geht bei Dauerfehler verloren; „gestört“-Flag nie zurückgesetzt |
| AU-06 | Hoch | CONFIRMED | Rohes SQL mit Literalen (Personendaten) im Audit, keine Größenbegrenzung |
| AU-07 | Hoch | CONFIRMED (Mechanismus) / SUSPECTED (Erreichbarkeit) | **SQL Server:** Parameter kürzen still, Hash über Volltext ⇒ falscher „Kettenbruch“ |
| AU-08 | Mittel | CONFIRMED | Tote Konfiguration und tote Funktionen (Retention, Elasticsearch, WORM-Export, TierA/B) |
| AU-09 | Mittel | CONFIRMED | Health/Readiness erkennt Teilausfälle nicht; Erkennungslatenz bis 24 h |
| AU-10 | Mittel | CONFIRMED | Abweichendes Verhalten der drei Anbieter (Sortierung, Grenzen, Hash, Append-only) |
| AU-11 | Mittel | SUSPECTED | Mandantentrennung bei `tenantId = null`, stille Kappung bei 5000 |
| AU-12 | Mittel | CONFIRMED | Hintergrund-Task startet vor Schlüssel-Initialisierung |
| AU-13 | Mittel | SUSPECTED | Falscher Kettenbruch nach Datenbank-Failover oder Wiederherstellung |
| AU-14 | Niedrig | CONFIRMED | Anker-Aktualisierung nicht atomar; WORM-Dateien wachsen unbegrenzt |
| AU-15 | Niedrig | CONFIRMED | `occurred_at` ist nicht monoton zu `seq`; Textvergleich nur bei einheitlich UTC korrekt |
| AU-16 | Niedrig | CONFIRMED | SL verlässt sich auf `rowid`; `seq` im SL-Schema nachträglich und nullable |
| AU-17 | Niedrig | CONFIRMED | Optionaler Audit-Writer überspringt Audit stumm |
| AU-18 | Niedrig | CONFIRMED | Widersprüche zwischen Runbook, arc42 und Code |
| AU-19 | Niedrig | CONFIRMED | Leere Catch-Blöcke ohne Signal |

**Widerlegt:** Ein Fork der Kette bei mehreren Replikas (PG/MS). Der Tail wird unter `pg_advisory_xact_lock` bzw. `sp_getapplock` aus der Datenbank gelesen; dazu kommen ein eindeutiger Index auf `seq` und ein Retry. Der Zwischenspeicher dient nur der Manipulationserkennung. SL ist Ein-Prozess und wird bei mehr als einer Replika abgelehnt.

## Hoch

### AU-01 Standard-Anker nur im Speicher
- **Beleg:** `P/PostgreSqlGovernanceRepository.cs:197-204` (MS analog): ohne `Audit:ChainAnchorPath` wird `InMemoryAuditChainAnchorStore` genutzt, nur eine Warnung (`PostgreSqlGovernanceRepository.Audit.cs:766-769`). SL nutzt als Standard `<db>.audit-anchor.json` (`SqliteGovernanceRepository.Audit.cs:958`), also auf demselben Host und Volume wie die Datenbank.
- **Szenario:** Ein Datenbank-Owner schreibt die ganze Kette neu. Beim Neustart gilt „kein Anker, Speicher-Store“, der Anker wird aus dem Datenbank-Tail neu gesetzt (Trust on first use, `…Audit.cs:792-808`). Kürzung und Neuschreiben bleiben unentdeckt. Jede Replika hat einen eigenen Anker.
- **Behebung:** Außerhalb von Entwicklung `ChainAnchorPath`, `ChainAnchorWormDirectory` oder einen Signer **verpflichtend** machen, sonst Startfehler.

### AU-02 Schlüssel und Anker in derselben Vertrauensdomäne
- **Beleg:** `P/SqliteGovernanceRepository.cs:192`, `P/PostgreSqlGovernanceRepository.cs:187`: `_auditAnchorKey` wird per HKDF aus `_auditHmacKey` abgeleitet. Ohne `ChainAnchorSignerKeyVaultRef` (Standard leer, `GatewayOptions.cs:860`) ist der Anker nur ein HMAC mit demselben Hauptschlüssel.
- **Szenario:** Wer den Audit-Schlüssel besitzt (Anwendungs-Host oder Key Vault), signiert Kette und Anker konsistent neu.
- **Behebung:** In Produktion asymmetrischen Signer oder entferntes KMS erzwingen; Schlüsselrotation und Prüfschlüssel getrennt halten.

### AU-03 Leerer Umgebungsname gilt als Entwicklung
- **Beleg:** `P/SqliteGovernanceRepository.cs:112-113`, `P/PostgreSqlGovernanceRepository.cs:131-132`: `isDevOrTest = IsNullOrEmpty(envName) || "Development"`. ASP.NET Core nimmt bei fehlender Variable „Production“ an, der Audit-Code „Entwicklung“. Fallback-Schlüssel: SL mit festem Literal (`…cs:184`), PG/MS mit SHA-256 einer Konstante (`…cs:182`).
- **Szenario:** Ein Container ohne `ASPNETCORE_ENVIRONMENT` erlaubt In-Memory-SQLite, einen öffentlich bekannten HMAC-Schlüssel und einen TOFU-Anker (`SqliteGovernanceRepository.Audit.cs:901-908`). Die Kette ist fälschbar.
- **Behebung:** Nur ausdrücklich „Development“ (und „Test“) als Entwicklung werten; leerer Wert ist Produktion. Fallback-Schlüssel nie in Nicht-Entwicklung, Quelltext-Literale entfernen und pro Prozess zufällig erzeugen.

### AU-04 Audit-Ereignis vor dem Geschäfts-Commit (PG/MS)
- **Beleg:** `P/PostgreSqlGovernanceRepository.Consent.cs:482, 1228, 1320`, `P/SqlServerGovernanceRepository.Consent.cs:499, 1242`: `RecordAuditEventAsync` läuft auf eigener Verbindung und wird sofort committed; danach folgen `IncrementTableEpochInternalAsync` und der Commit der Geschäftstransaktion. SL macht es richtig über `existingTx` mit Pending-Zustand (`SqliteGovernanceRepository.Audit.cs:208, 400ff`).
- **Szenario:** Der Commit schlägt fehl oder das Epoch-Update wirft ⇒ `CONSENT_GRANTED`/`APPROVED` steht im unveränderlichen Audit, der Consent existiert nicht (Phantom-Ereignis). Umgekehrt (Audit nach Commit) wäre eine Änderung ohne Eintrag möglich.
- **Behebung:** Audit-Insert in **derselben Transaktion** (Sperre dort nehmen) oder Outbox-Muster mit garantierter Nachlieferung; Entscheidung dokumentieren und in allen drei Anbietern gleich umsetzen.
- **Hinweis:** Der SQL-Server-Anbieter hat dieses Verhalten aus dem PostgreSQL-Vorbild geerbt (1:1-Portierung). Die Korrektur gehört in beide.

### AU-05 Verlorene Stapel und nicht rücksetzbares „gestört“
- **Beleg:** `SqliteGovernanceRepository.Audit.cs:139-164`, `PostgreSqlGovernanceRepository.Audit.cs:121-150`: Nach drei Versuchen wird `_isAuditPipelineFaulted = true` gesetzt (nie zurückgesetzt) und der Stapel (bis 250 Einträge, deren Anfrage bereits beantwortet wurde) verworfen.
- **Folgen:** Alle Tier-B-Einträge (`TABLE_QUERY`/`WEBSQL_QUERY` ALLOW) scheitern bis zum Neustart (Verweigerung aller erlaubten Abfragen). Eine Ausnahme außerhalb des try/catch in `ProcessAuditChannelAsync` beendet den Task unbeobachtet, der Kanal wird nicht mehr geleert, Schreiber laufen nach 2 s in „gesättigt“.
- **Behebung:** Stapel bei Fehler neu einreihen oder in eine Dead-Letter-Tabelle schreiben; „gestört“ per Probe-Schreibvorgang zurücknehmen; Task-Fehler überwachen und neu starten; Kennzahl für verworfene Einträge.

### AU-06 Rohes SQL mit Literalen, keine Größenbegrenzung
- **Beleg:** `GovernedSqlExecutionService.cs:919-933` übergibt `originalSql` und `securedSql`. DML wird nur gehasht (ab Zeile 1219), SELECT nicht. Keine Kürzung in irgendeinem Repository.
- **Szenario:** `WHERE email='max@x'` landet unverändert im unveränderlichen Audit (Datenschutz, Löschbarkeit). Langes SQL bläht die Zeile unbegrenzt auf.
- **Behebung:** Hash plus normalisierte Form ohne Literale; `DetailsJson` auf Höchstlänge begrenzen (mit Hash des Originals); Klartext nur per Option (Phase 1 des Plans).

### AU-07 SQL Server: stille Kürzung bricht die Hash-Prüfung
- **Beleg:** `P/SqlServerGovernanceRepository.Audit.cs:282-294`: Parameter mit fester Größe (`@trace` 128, `@event` 128, `@actor` 256, `@target` 512, `@col` 256); `ComputeAuditEntryHash` rechnet über den ungekürzten Text.
- **Szenario:** Ein zu langer `TargetTable`/`TraceId` (z. B. MCP-Toolname) wird vom Treiber still abgeschnitten, die Prüfung berechnet einen anderen Hash ⇒ falscher „Kettenbruch“ (dauerhaft, Readiness 503).
- **Herkunft:** Fehler der neuen SQL-Server-Portierung (das PostgreSQL-Vorbild nutzt unbegrenzten Text). **Dringend vor Freigabe des SQL-Server-Anbieters beheben.**
- **Behebung:** Werte **vor** dem Hashen validieren oder kürzen (und den gekürzten Wert hashen), oder `NVARCHAR(MAX)`-Spalten nutzen; Test mit Überlänge je Feld.

## Mittel

### AU-08 Tote Konfiguration und tote Funktionen
- `TierAEnabled`, `TierBAggregationWindowSeconds`, `AuditLogRetentionDays`, `ElasticsearchSinkUrl` kommen außerhalb von `GatewayOptions.cs:827-837` und den `appsettings` im Code nicht vor. Kein Löschlauf, keine Elasticsearch-Senke (ADR-005:15 verspricht sie).
- `IAuditWormExportService.ExportAuditSnapshotAsync` hat außer der DI-Registrierung (`GatewayServiceCollectionExtensions.cs:347`) keinen Aufrufer; `Worm.Enabled` hat weder Zeitplan noch Endpunkt. README:135-140 („Automated export to WORM storage“) und `docs/configuration-guide.md:408-413` versprechen mehr.
- **Behebung:** Umsetzen oder entfernen; Doku angleichen (Plan, Phase 6 und 7).

### AU-09 Health und Readiness
- `GatewayHealthCheckService.cs:112`: `chainHealthy = !IsViolated`. Ein fehlgeschlagener Prüflauf (Zustand 3) und „noch nie geprüft“ gelten als gesund. Erster Lauf nach 2 min, danach alle 24 h (`AuditChainIntegrityMonitor.cs:16`, `GatewayOptions.cs:830`): Erkennungslatenz bis 24 h.
- Der „gestört“-Test greift nur für die drei bekannten Repository-Typen (Zeilen 46-85); bei einem Decorator greift der Fallback (SUSPECTED). SL setzt bei Kettenbruch kein „gestört“, PG/MS schon.
- **Behebung:** Zustand 3 und „nie geprüft nach Frist“ als ungesund; Schnittstelle `IAuditHealth` statt Typprüfung; gleiches Verhalten bei allen Anbietern.

### AU-10 Abweichungen der Anbieter
| Thema | SL | PG / MS |
|---|---|---|
| `GetAuditLogEntriesAsync` | `ORDER BY rowid ASC` | `rowid DESC` |
| `QueryAuditLogsAsync` | sortiert nach `occurred_at DESC` (Text), Limit auf 5000, `target_table` groß-/kleinschreibungsabhängig | sortiert nach `rowid`, kein Limit, PG vergleicht mit `LOWER()` |
| `GetAuditChainRange` | `<= @to`, UTC-konvertiert | `< @to`, nicht konvertiert (Überlappung/Versatz zwischen Export-Fenstern) |
| `EscapeField` | escapet `\n`/`\r` nicht | escapet ⇒ **Hash nicht portabel** (Runbook:131 räumt es ein) |
| HKDF-Info Anker | `Autheris:AuditChainAnchor:v1` | `Autheris:AuditAnchor:v1` |
| Append-only | keine Trigger | PG: Zeilen- und TRUNCATE-Trigger; MS: INSTEAD-OF-Trigger plus FK-Wächtertabelle; beide per `DROP TRIGGER` durch den Owner umgehbar |
| Verifikation | Vollscan unter `_lock`, blockiert alle Schreiber | Vollscan ohne Sperre |
- **Ursache:** Dreifach duplizierter Code ohne gemeinsame Basis. **Behebung:** Gemeinsamen, anbieterneutralen Audit-Kern (Hashing, Kanonisierung, Kette, Anker, Stapel) extrahieren; nur Persistenz bleibt anbieterspezifisch; gemeinsame Vertragstests über alle Anbieter.

### AU-11 Mandantentrennung und stille Kappung (SUSPECTED)
- `LineageImpactAnalyzerService.cs:247-251, 401-407` übergeben `tenantId: callerContext?.TenantId`; bei `null` liefert `QueryAuditLogsAsync` **alle Mandanten** (`PostgreSqlGovernanceRepository.Audit.cs:402`). Mit `limit: 5000` werden Art.-15-Berichte (365 Tage) still gekappt.
- **Behebung:** Mandant im Repository erzwingen (nicht nullable, Systemkontext explizit); bei Kappung ein Abschneide-Kennzeichen zurückgeben.

### AU-12 Hintergrund-Task vor Schlüssel-Initialisierung
- `Task.Run(ProcessAuditChannelAsync)` läuft vor Schlüssel und Anker (`SqliteGovernanceRepository.cs:86` vs. 175-178, `PostgreSqlGovernanceRepository.cs:105` vs. 178). Wirft der Konstruktor, bleibt der Task zurück; er kann `_auditHmacKey` lesen, bevor er gesetzt ist.
- **Behebung:** Task erst nach vollständiger Initialisierung starten.

### AU-13 Falscher Kettenbruch nach Failover oder Wiederherstellung (SUSPECTED)
- `PostgreSqlGovernanceRepository.Audit.cs:248-252`: `dbTailSeq < knownSeq` ⇒ dauerhaftes `FlagAuditChainViolation` und `_isAuditPipelineFaulted`. Failover mit Replikationsverzug oder Point-in-Time-Restore sind legitim; die Instanz bleibt bis zum Neustart unbrauchbar.
- **Behebung:** Zustand unterscheiden („Datenbank hinter dem Anker“ ≠ „manipuliert“), Wiederherstellungsverfahren mit Anker-Rücksetzung unter Vier-Augen-Prinzip und eigenem Audit-Eintrag.

## Niedrig
- **AU-14** Anker-Update ist Laden-dann-Speichern (`PostgreSqlGovernanceRepository.Audit.cs:330-333`); bei Races kann der Anker zurückgehen (harmlos, hinter dem Tail), WORM-Dateien wachsen unbegrenzt (`Take(8)` beim Laden).
- **AU-15** Tier-B-Einträge tragen `OccurredAt` des Anfragezeitpunkts, aber spätere `seq`; Bereichsexporte per `rowid` enthalten Einträge außerhalb des Fensters. Textvergleich auf `occurred_at` stimmt nur bei einheitlich UTC.
- **AU-16** SL verlässt sich auf `rowid`; nach `VACUUM` kann sich die Nummerierung bei Tabellen ohne `INTEGER`-Primärschlüssel ändern (`id` ist Text) und Export-Cursor brechen. `seq` fehlt im SL-`CREATE` und kommt per `ALTER` (nullable), in PG/MS ist es `NOT NULL`.
- **AU-17** `IAuditLogRepository?` überspringt Audit stumm: `GovernedSqlExecutionService.cs:916, 1213`, `AiDataGuardrailService.cs:604`, `CdcSubscriptionGovernor.cs:158`.
- **AU-18** Doku-Widersprüche: Runbook:136 („nicht automatisch geplant“) vs. Runbook:182 und Code (`AuditChainIntegrityMonitor` ist geplant); Runbook:136 „Verstoß setzt Pipeline auf gestört“ gilt nur für PG/MS; arc42:13/86 nennt „SHA-256 chaining“, der Code nutzt HMAC-SHA256.
- **AU-19** Leere Catch-Blöcke: `Dispose` schluckt Drain-Fehler (`SqliteGovernanceRepository.cs:229-232`); Anker-Speicherfehler werden nur geloggt (`PostgreSqlGovernanceRepository.Audit.cs:336-339`), ein zurückbleibender Anker löst keine Health-Warnung aus.

## Empfohlene Reihenfolge
1. **AU-07** (Fehler in der neuen SQL-Server-Portierung, vor Freigabe).
2. **AU-01, AU-02, AU-03** (Anker, Schlüssel, Umgebungsstandard): Startvalidierung außerhalb von Entwicklung verschärfen.
3. **AU-04** (Atomarität zwischen Geschäftsvorgang und Audit), **AU-05** (Wiederherstellung der Tier-B-Pipeline).
4. **AU-06** (Datenschutz im Audit) gemeinsam mit Phase 1 des Umsetzungsplans.
5. **AU-08, AU-18** (tote Funktionen und Doku), **AU-10** (gemeinsamer Audit-Kern, Vertragstests).
6. Übrige Mittel- und Niedrig-Befunde.

## Bewertung der Abdeckungs-Pläne
Der Umsetzungsplan zur Abdeckung (zentrale Middleware, Ereigniskatalog, Verdichtung) baut auf dem Audit-Kern auf. **AU-04, AU-05 und AU-10 sollten vor Phase 2 behoben werden**, sonst wächst die Zahl der Ereignisse auf einem Kern, der sie nicht atomar, nicht ausfallsicher und je Anbieter unterschiedlich schreibt.
