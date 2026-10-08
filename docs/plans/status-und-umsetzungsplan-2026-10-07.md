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

## 2. Tests nachziehen (Phase 2)

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

## 3. Niedrige Befunde (Gesamt-Review)

| ID | Befund | Status / Commit |
|---|---|---|
| POL-19 | `ChunkPiiRedactor` ist ohne Katalogspalten nicht fail-closed. | Offen |
| SQL-6 | Join-Guardrail arbeitet ohne Tabellenbezug. | Offen |
| DEP-9 | `--vulnerable`-Schritt in CI ist wirkungslos. | Offen |
| DEP-10 | Kein Locked-Mode, keine `NuGet.config`. | Offen |
| DEP-11 | `workflow_dispatch` überschreibt `latest`; Release ohne Tests und Signatur. | Offen |
| DEP-12 | Alte Benchmark-Secrets in der Git-History (HEAD sauber). | Offen |
| DEP-15 | Benchmark-Images mit `:latest` und `sa`. | Offen |
| F-8 (info) | Jede Casbin-Änderung baut alle Enforcer neu; viele `AddPolicy`-Aufrufe kosten O(n²). | Offen |
| API-13 | BasicAuth-Lockout nicht atomar und synchron. | Behoben in `ccb6ec5` |
| INF-2 | CDN-Purge und Shadowing nutzen einen ungehärteten HttpClient. | Behoben in `140df9c` |
| INF-3 | Secret-Referenzen landen im Klartext im Log. | Behoben in `fa02ffd` |
| POL-8 | Vorab terminierte DENY-Consents greifen bis zu 10 min verspätet. | Behoben in `49b6509` |
| POL-9 | Degraded-Mode bestimmt Sensitivität per Substring im Tabellennamen; L1 wird bei Reconnect nicht geleert. | Behoben in `593d704` |
| POL-12 | Audit der Freigabeschritte nach dem Commit, Fehler werden verschluckt. | Behoben in `120fa2d` |
| GQL-5 | `FORBIDDEN`-Meldung verrät Existenz von Tabellen und Ablehnungsgrund. | Behoben in `4819b4e` |
| GQL-6 | `tableConsumers` ohne Consent- und Tenant-Filter. | Behoben in `13bb9e1` |
| GQL-12 | dbt-Quarantäne-Status vor der Autorisierung. | Behoben in `4819b4e` |
| MCP-1 | `resources/list` und `resources/read` Spaltensichtbarkeit & Deny. | Behoben in `65d3329` |
| MCP-2/3 | Sync-over-async auf Redis & SSE Stream Leaks. | Behoben in `1b2c377` |
| MCP-4 | Synthetische Rollen (`AiAgent`, `Reader`) im Fast-Path. | Behoben in `2069074` |
| MCP-5 | Golden Queries ohne Consent-Filter. | Behoben in `13bb9e1` |
| MCP-6 | `@mcpTool` mit geratener `TargetTable` (latent). | Behoben in `71a742d` |
| MCP-7 | Fail-closed Argument-Parsing im Query-Executor. | Behoben in `ccf4a49` |
