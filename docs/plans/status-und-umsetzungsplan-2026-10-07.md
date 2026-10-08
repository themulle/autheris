# Status und Umsetzungsplan (Stand 1133f90, 2026-10-08)

Branch `feat/ast-target-dialect-generator`. Dieses Dokument listet nur noch **offene** Punkte. Erledigte Befunde
stehen in der Git-History (Commit-Messages nennen die IDs). Die IDs stammen aus
[security-review-2026-10-07.md](security-review-2026-10-07.md) (SQL-, SQL2-, POL-, GQL-, MCP-, API-, EXT-, INF-, DEP-)
und [review-c4f2c32.md](review-c4f2c32.md) (R-…); F- und E- aus den Casbin-Nachreviews.

Regeln: Ein Thema pro Commit. TDD: Jeder Sicherheitstest muss ohne Fix rot sein. Vor dem Push grüner
`dotnet build` und `dotnet test` (Integrationstests unter podman mit `TESTCONTAINERS_HOST_OVERRIDE=host.docker.internal`).

## 1. Außerhalb dieses Repos

| ID | Punkt |
|---|---|
| DEP-1/2/3 | PoC-Repo (`talos/POC_Backstage_citizen_dev`), mit dem Owner abstimmen: Passwörter und HMAC-Schlüssel rotieren, Defaults aus `compose.yaml` entfernen (`${VAR:?}`), Ports nur an 127.0.0.1, Test-DB oder Read-only-Replikat statt LWETEM_PROD. |
| DEP-5-Rest | Altes ghcr-Paket (`…/gql`) manuell löschen; das Image heißt jetzt `ghcr.io/themulle/autheris`. |
| CI | PR nach `main` öffnen, damit die CI läuft (läuft nur auf `main`/PRs). |

## 2. Dokumentation

| ID | Punkt |
|---|---|
| F-6 | `configuration-guide.md` (Casbin, ca. Z. 750–790): Probe-Tabelle an `CasbinModelContract` angleichen (M1 Grund-Allow, M2 Mandanten-Trennung, M3 Subjekt, M4 Objekt, M5 Deny gewinnt, M6 `sub_rule`, M7 Rollen über `g`, M8 Rolle nicht für andere Nutzer; W1 Wildcard-Fähigkeit, W2–W6 nur wenn W1). `ModelPath` ist bei `Enabled=true` Pflicht und wird geprüft, sobald gesetzt. Semantikabschnitt: globale `*`-Regeln gelten für alle Mandanten, Deny gewinnt dateiübergreifend, Mandanten-Datei braucht `p`-Regeln (F-1). |
| E-6 | Dokumentieren: Eine leere Mandanten-Datei wird abgelehnt, solange `*`-Regeln aktiv sind. |
| R-API-1 | Migrationshinweis: `Itsm.LegacyGlobalWebhookSecret=true` und die DANGER-Schalter verhindern den Start außerhalb Development. |
| R-DEP-2 | Schlüsselformat dokumentieren: geprüft werden UTF-8-Bytes des Textes (32 Hex-Zeichen = 16 Byte Entropie); Empfehlung Base64 aus 32 Zufallsbytes. |
| R-SQL-10 / SQL2-1 | Test „maskierte Spalte, als `clear` deklariert, bleibt Mask“ und „ohne Browse-Quellinfo wird `clear`-Spalte entfernt“; ADR-018 ergänzen. |
| Arch 6 | Doku generell an die Optionsklassen angleichen (Casbin `Enabled`, 32-Byte-Schlüssel, D-4-Sensitivität ist in ADR-010). |

## 3. Tests nachziehen (Phase 2)

Jeder Test muss ohne Fix rot werden.

| Befund | Test |
|---|---|
| SQL-1 / R-SQL-9 | Die SQL-1-Tests sind tautologisch (`EnforceReadOnlyQueries` true). Neu: `EnforceReadOnlyQueries = false`, `EnforceWithCheckOption = true`; UPDATE und INSERT mit `TenantId` in beiden Engines; Multi-Table-Fall. |
| D-1 / R-SQL-3 | Integrationstest WebSQL-SELECT gegen PostgreSQL (Testcontainers): Reader vor Commit geschlossen, `set_config(…, true)` in derselben Transaktion. |
| POL-2 / D-4 | Repository-Tests SQLite und PG für Vier-Augen bei hoher Sensitivität. |
| POL-3 | `ActivateConsentAsync` legt Spaltenregeln an; spätere Spalte ist nicht Clear. |
| POL-4 | Lakehouse- und DeltaLake-Executor: verschiedene Pseudonyme je Mandant, keine Doppelmaskierung. |
| POL-7 | API ergibt 400 bei Wildcard; Userset `group:x#member` funktioniert weiter. |
| POL-15 | PG-Aktivierung für Gruppe und Service-Principal setzt `grantee_sid`. |
| EXT-2 / R-EXT-2 | Batch und Webhook behalten `SourceName`; dbt-Approve behält alle Eigenschaften. |
| API-4 / R-API-2 | Anonym wird nicht verbucht; ungültiger Mandanten-Claim wirft nicht. |
| R-SQL-5 | Drosselung ergibt 429 mit `Retry-After`. |
| R-SQL-6 | Prozeduraufruf setzt die Sitzungsvariablen über den Initializer. |
| R-SQL-7 | Rollback-Fehler verdeckt die Original-Exception nicht. |
| R-POL-4, R-POL-6 | Reload-Fehler wird geloggt, Event mit `"*"` wirft nicht; Default-Methoden werfen. |
| GraphQL E2E | Ende-zu-Ende mit SQLite über `WebApplicationFactory`; gleiche Zeilen wie OData und WebSQL; Abnahme `fms/air1` unter 2 s. |
| Sonstiges | `DbSessionContextInitializer_RollsBackTransaction_OnError` umbenennen oder ersetzen (irreführend). |
| Testinfrastruktur | Gelegentlich flakende Integrationstests (Health/401/403, im Rerun grün); Verdacht: geteilte In-Memory-SQLite (`Cache=Shared`) über Testklassen. |

## 4. Niedrige Befunde (Gesamt-Review)

| ID | Befund |
|---|---|
| POL-8 | Vorab terminierte DENY-Consents greifen bis zu 10 min verspätet. |
| POL-9 | Degraded-Mode bestimmt Sensitivität per Substring im Tabellennamen; L1 wird bei Reconnect nicht geleert. |
| POL-10 | Casbin-Cache-Key enthält `RequestedColumns` nicht. |
| POL-11 | ReBAC-Objekt-ID enthält kein Schema. |
| POL-12 | Audit der Freigabeschritte nach dem Commit, Fehler werden verschluckt. |
| POL-13 | Federation-Masking-Bypass über rohe Admin-Rollen-Claims. |
| POL-19 | `ChunkPiiRedactor` ist ohne Katalogspalten nicht fail-closed. |
| GQL-5 | `FORBIDDEN`-Meldung verrät Existenz von Tabellen und Ablehnungsgrund. |
| GQL-6 | `tableConsumers` ohne Consent- und Tenant-Filter. |
| GQL-12 | dbt-Quarantäne-Status vor der Autorisierung. |
| MCP-4 | Synthetische Rollen `AiAgent`/`Reader` im Fast-Path. |
| MCP-5 | Golden Queries ohne Consent-Filter. |
| MCP-6 | `@mcpTool` mit geratener `TargetTable` (latent). |
| MCP-7 | `catch (Exception)` beim Argument-Parsing ist fail-open. |
| API-8 | `TrustedNetworks` wird nicht validiert (`0.0.0.0/0` möglich). |
| API-9 | `DataProtectionOfficer` fehlt in der Header-Rollen-Denylist. |
| API-11 | Rohe Exception-Texte in WebSQL, Arrow-Flight und Iceberg. |
| API-13 | BasicAuth-Lockout nicht atomar und synchron. |
| API-16 | `warn_allow_all_cors_origins` ist in Production erlaubt. |
| API-17 | WASM-Plugin `:latest` ungepinnt. |
| EXT-6 | OpenMetadata-User-Pfad ignoriert das Rollen-Mapping. |
| EXT-7 | DataCatalog legt neue Tabellen sofort aktiv an. |
| INF-2 | CDN-Purge und Shadowing nutzen einen ungehärteten HttpClient. |
| INF-3 | Secret-Referenzen landen im Klartext im Log. |
| SQL-6 | Join-Guardrail arbeitet ohne Tabellenbezug. |
| SQL2-10 | CrossDomainJoin baut den IN-Filter per String (toter Code). |
| SQL2-14 | DuckDB-Validator kennt keine Kommentare. |
| SQL2-15 | Maskentext ohne `EscapeSqlLiteral` und ohne `N`-Präfix. |
| SQL2-16 | Prozeduren ohne Audit-Repository liefern trotzdem aus. |
| SQL2-19 | Unqualifizierte Spalten in RLS-Subqueries. |
| WF-1 | `ExtendConsentExpiryAsync` ohne Prüfungen (ohne Aufrufer). |
| DEP-9 | `--vulnerable`-Schritt in CI ist wirkungslos. |
| DEP-10 | Kein Locked-Mode, keine `NuGet.config`. |
| DEP-11 | `workflow_dispatch` überschreibt `latest`; Release ohne Tests und Signatur. |
| DEP-12 | Alte Benchmark-Secrets in der Git-History (HEAD sauber). |
| DEP-14 | Klartext-BasicAuth-Passwörter werden erst beim Login abgelehnt. |
| DEP-15 | Benchmark-Images mit `:latest` und `sa`. |
| DEP-16 | Kein `AllowedHosts`, CORS-localhost-Fallback in Production. |
| F-8 (info) | Jede Casbin-Änderung baut alle Enforcer neu; viele `AddPolicy`-Aufrufe kosten O(n²). |

## 5. Architektur

| Nr. | Punkt |
|---|---|
| 1 | Eine zentrale Zugriffsentscheidung (`ITableAccessResolver`); löst POL-6 (heute `RebacTableGate`, Default aus), SQL2-6 und API-10 strukturell. |
| 2 | Gemeinsamer Lesepfad für Konnektoren (`GovernedConnectorReader`). |
| 3 | Restlichen toten Code entfernen (u. a. `CompositeKeySqlGenerator`, `PushdownPlanner`). |
| 5 | Provider-Namen und Session-Init an einer Stelle (Provider-Namen stehen noch an mehreren Stellen). |
