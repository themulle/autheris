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

## 2. Dokumentation (Completed / Erledigt)

| ID | Item / Punkt | Status |
|---|---|---|
| F-6 | Aligned Casbin probe table in `configuration-guide.md` to `CasbinModelContract` (M1–M8 mandatory, W1–W6 wildcard probes). Documented that `Casbin:ModelPath` is mandatory when `Casbin:Enabled=true` and validated as soon as set. Added Policy Semantics section covering global `*` rules, cross-file deny precedence, and tenant policy `p`-rule requirement (F-1). | Completed ✔ |
| E-6 | Documented that an empty tenant policy file is rejected whenever global `*` rules are active. | Completed ✔ |
| R-API-1 | Added migration notice in `configuration-guide.md` and `developer-guide.md`: `Itsm.LegacyGlobalWebhookSecret=true` and `DANGER:` bypass flags strictly block startup outside Development with `ValidationException`. | Completed ✔ |
| R-DEP-2 | Documented secret key format and requirements across guides and threat model: validation checks UTF-8 bytes (>= 32 bytes required); noted that 32 hex chars yield only 16 bytes of entropy; recommended Base64 from >= 32 random bytes. | Completed ✔ |
| R-SQL-10 / SQL2-1 | Updated `ADR-018-governed-stored-procedures.md`: documented that a masked column declared as `clear` remains masked, and without browse source info the clear column is removed fail-closed. | Completed ✔ |
| Arch 6 | Aligned documentation across files with options classes: `Casbin:Enabled` default `false`, >= 32-byte secret requirement, and D-4 sensitivity ranking (ADR-010). | Completed ✔ |

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

### Bereits umgesetzte Befunde (Completed ✔)

| ID | Befund | Status / Commit |
|---|---|---|
| API-8 | `TrustedNetworks` wird nicht validiert (`0.0.0.0/0` möglich). | Erledigt in `981fc7e` (`fix(config): validate ForwardAuth trusted networks and reject wildcard CIDRs (API-8)`) |
| API-9 | `DataProtectionOfficer` fehlt in der Header-Rollen-Denylist. | Erledigt in `7269212` (`fix(auth): forbid DataProtectionOfficer from proxy headers (API-9)`) |
| API-11 | Rohe Exception-Texte in WebSQL, Arrow-Flight und Iceberg. | Erledigt in `86a6d89` (`fix(api): sanitize error messages in WebSql, Arrow Flight and Iceberg endpoints (API-11)`) |
| API-16 | `warn_allow_all_cors_origins` ist in Production erlaubt. | Erledigt in `b8fbba6` (`fix(cors): reject warn_allow_all_cors_origins outside development without opt-in (API-16)`) |
| API-17 | WASM-Plugin `:latest` ungepinnt. | Erledigt in `dac0940` (`fix(mesh): pin default wasm plugin image tag in envoy export (API-17)`) |
| DEP-14 | Klartext-BasicAuth-Passwörter werden erst beim Login abgelehnt. | Erledigt in `15fe567` (`fix(auth): reject plaintext basic auth passwords during startup validation (DEP-14)`) |
| DEP-16 | Kein `AllowedHosts`, CORS-localhost-Fallback in Production. | Erledigt in `7cf19f6` (`fix(cors): remove localhost fallback in production cors policy (DEP-16)`) |
| MCP-7 | `catch (Exception)` beim Argument-Parsing ist fail-open. | Erledigt in `ccf4a49` (`fix(mcp): fail closed on argument parsing errors in query executor (MCP-7)`) |
| MCP-4 | Synthetische Rollen `AiAgent`/`Reader` im Fast-Path. | Erledigt in `2069074` (`fix(mcp): do not synthesize reader roles in fast path (MCP-4)`) |
| POL-11 | ReBAC-Objekt-ID enthält kein Schema. | Erledigt in `b9937cd` (`refactor(policy): one table access decision for all paths (Architecture 1, SQL2-6, POL-6, API-10, POL-11)`) |
| SQL2-10 | CrossDomainJoin baut den IN-Filter per String (toter Code). | Erledigt in `7ffa9fe` (`refactor: remove unwired prototype code (Architecture 3, SQL2-10)`) |
| SQL2-14 | DuckDB-Validator kennt keine Kommentare. | Erledigt in `fb71ad1` (`fix(olap): support comments in duckdb olap query validator (SQL2-14)`) |
| SQL2-15 | Maskentext ohne `EscapeSqlLiteral` und ohne `N`-Präfix. | Erledigt in `1ac38f8` (`fix(sql): use dialect-escaped masking literals and N prefix for sql server (SQL2-15)`) |
| SQL2-16 | Prozeduren ohne Audit-Repository liefern trotzdem aus. | Erledigt in `0afb7a5` (`fix(procedures): fail closed when audit repository is unavailable (SQL2-16)`) |
| SQL2-19 | Unqualifizierte Spalten in RLS-Subqueries. | Erledigt in `963a8ea` (`fix(rls): qualify unqualified column names in subquery filter predicates (SQL2-19)`) |
| POL-10 | Casbin-Cache-Key enthält `RequestedColumns` nicht. | Erledigt in `6c3ed38` (`fix(policy): include requested columns in Casbin cache key (POL-10)`) |
| POL-13 | Federation-Masking-Bypass über rohe Admin-Rollen-Claims. | Erledigt in `8f371b5` (`fix(federation): evaluate admin roles via role evaluator before bypassing mask (POL-13)`) |
| INF-3 | Secret-Referenzen landen im Klartext im Log. | Erledigt in `ca64652` (`fix(secrets): redact secret references in log statements (INF-3)`) |
| EXT-6 | OpenMetadata-User-Pfad ignoriert das Rollen-Mapping. | Erledigt in `aa47e96` (`fix(catalog): enforce role mapping for user policies in OpenMetadata sync (EXT-6)`) |
| EXT-7 | DataCatalog legt neue Tabellen sofort aktiv an. | Erledigt in `445fd0a` (`fix(catalog): do not activate newly discovered tables by default in data catalog (EXT-7)`) |
| WF-1 | `ExtendConsentExpiryAsync` ohne Prüfungen (ohne Aufrufer). | Erledigt in `fc0b9f0` (`fix(workflow): validate parameters and status in ExtendConsentExpiryAsync (WF-1)`) |

### Noch offene niedrige Befunde

| ID | Befund |
|---|---|
| POL-8 | Vorab terminierte DENY-Consents greifen bis zu 10 min verspätet. |
| POL-9 | Degraded-Mode bestimmt Sensitivität per Substring im Tabellennamen; L1 wird bei Reconnect nicht geleert. |
| POL-12 | Audit der Freigabeschritte nach dem Commit, Fehler werden verschluckt. |
| POL-19 | `ChunkPiiRedactor` ist ohne Katalogspalten nicht fail-closed. |
| GQL-5 | `FORBIDDEN`-Meldung verrät Existenz von Tabellen und Ablehnungsgrund. |
| GQL-6 | `tableConsumers` ohne Consent- und Tenant-Filter. |
| GQL-12 | dbt-Quarantäne-Status vor der Autorisierung. |
| MCP-5 | Golden Queries ohne Consent-Filter. |
| MCP-6 | `@mcpTool` mit geratener `TargetTable` (latent). |
| API-13 | BasicAuth-Lockout nicht atomar und synchron. |
| INF-2 | CDN-Purge und Shadowing nutzen einen ungehärteten HttpClient. |
| SQL-6 | Join-Guardrail arbeitet ohne Tabellenbezug. |
| DEP-9 | `--vulnerable`-Schritt in CI ist wirkungslos. |
| DEP-10 | Kein Locked-Mode, keine `NuGet.config`. |
| DEP-11 | `workflow_dispatch` überschreibt `latest`; Release ohne Tests und Signatur. |
| DEP-12 | Alte Benchmark-Secrets in der Git-History (HEAD sauber). |
| DEP-15 | Benchmark-Images mit `:latest` und `sa`. |
| F-8 (info) | Jede Casbin-Änderung baut alle Enforcer neu; viele `AddPolicy`-Aufrufe kosten O(n²). |

## 5. Architektur (Completed / Erledigt)

Alle architektonischen Kernrefactorings aus Abschnitt 5 wurden umgesetzt:

| Nr. | Punkt | Status / Commit |
|---|---|---|
| 1 | Eine zentrale Zugriffsentscheidung (`ITableAccessResolver` / `TableAccessPolicy`); löst POL-6 (`RebacTableGate`), SQL2-6 und API-10 strukturell. | Erledigt in `b9937cd` (`refactor(policy): one table access decision for all paths (Architecture 1, SQL2-6, POL-6, API-10, POL-11)`) |
| 2 | Gemeinsamer Lesepfad für Konnektoren (`GovernedConnectorReader`); löst SQL2-5. | Erledigt in `3957c24` (`refactor(connectors): one governed read path for connector rows (Architecture 2, SQL2-5)`) |
| 3 | Restlichen toten Code entfernen (`CompositeKeySqlGenerator`, `PushdownPlanner`, unverdrahtete Typen). | Erledigt in `7ffa9fe` & `17f8c08` (`refactor: remove unwired prototype code` / `refactor: remove dead code that only unit tests still used`) |
| 5 | Provider-Namen und Session-Init an einer Stelle unifiziert. | Erledigt in `7359c5e` (`refactor(sql): unify data source provider resolution (Architecture 5, SQL2-22)`) |
