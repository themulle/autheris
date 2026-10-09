# Requirements-Bericht: Anforderungen des PoC „Citizen Dev“ an Autheris, Prüfung gegen v1.1.2

**Stand:** 09.10.2026, Image `ghcr.io/themulle/autheris:v1.1.2` (Tag auf `main`, Merge von `feat/ast-target-dialect-generator`, #21).
**Quelle der Anforderungen:** PoC `POC_Backstage_citizen_dev` (Backstage, Talos-Datenadapter, Excel/Power Query, MCP-Agenten, dbt-Governance `dbt_sample`).
**Prüfweg:** `scripts/verify-autheris.sh` des PoC gegen den laufenden Container (Podman, `lwetem_prod` auf SQL Server, Beispielnutzer `david` mit Zeilenfilter), Ergebnis **alle Prüfungen bestanden**. Alles, was dieses Skript nicht abdeckt, ist als „nicht geprüft“ markiert.

Dieses Dokument ersetzt die Befundlisten nicht ([WebSQL/Trino-Befunde](2026-10-08-websql-befunde-v1-1-0.md), [Spike-Befund](2026-10-08-befund-autheris.md)). Es dreht die Blickrichtung um: Was braucht der PoC, was davon ist erfüllt, was fehlt.

## 1. Übersicht

| Status | Anzahl |
|---|---|
| erfüllt und geprüft (v1.1.2) | 17 |
| offen | 5 |
| nicht geprüft | 3 |

Priorität: **Muss** = ohne das ist der Anwendungsfall im PoC nicht nutzbar, **Soll** = Komfort oder Härtung.

## 2. Anforderungen und Status

### 2.1 Zugriff und Schnittstellen

| ID | Anforderung | Prio | Status v1.1.2 | Prüfung |
|---|---|---|---|---|
| R-01 | Basis-Betrieb: `health/live`, OData mit Header `OData-Version: 4.0` (Excel/Power Query) | Muss | erfüllt | Skript „Basis“ |
| R-02 | WebSQL und Trino akzeptieren `schema.tabelle` ohne Datenquelle (v1.1.0 lehnte mit 403 ab) | Muss | erfüllt | Skript „Stand: neu“ |
| R-03 | OData `$top`/`$skip`/`$select`, `@odata.nextLink` bei gekürztem Ergebnis | Muss | erfüllt | Skript OData |
| R-04 | OData `$orderby` und `$count` (auch `/$count`) | Muss | erfüllt | Skript OData |
| R-05 | OData `$filter` auf Spalten der Tabelle (für Query Folding in Power Query) | Soll | erfüllt (200); Spalten ohne Freigabe geben 400 | Skript, Stichprobe von Hand |
| R-06 | `$metadata` ohne Schlüssel auf fehlende Eigenschaften (Excel meldete „Metadatendokument ungültig“) | Muss | erfüllt | Skript OData |
| R-07 | Trino-Protokoll mit Zahlentypen und Spaltenname `_col0` bei `COUNT(*)` | Soll | erfüllt; Typ ist `integer`, nicht `bigint` | Skript Trino |
| R-08 | WebSQL: Parameter, `COUNT(*)`, `ORDER BY … LIMIT`, DML wird abgelehnt (403), Zeilenlimit mit `truncated` | Muss | erfüllt | Skript WebSQL |
| R-09 | Parquet als `Accept`-Format für OData und WebSQL, Zeilenlimit wird gemeldet | Soll | erfüllt | Skript Parquet |
| R-10 | Deklarierte Prozeduren (`feinplanung_latesttelemetrydata`, `_all`) mit 22 Spalten | Muss | erfüllt | Skript Prozeduren |
| R-11 | MCP über `POST /mcp`: `list_datasets` (mit Filter, blättert), `sample_rows`, `query_graphql` | Muss | erfüllt | Skript MCP |
| R-12 | OpenAPI/Swagger anonym lesbar (Open Schema) für Katalog und Entwickler | Soll | erfüllt | Skript OpenAPI |

### 2.2 Governance und Datenschutz

| ID | Anforderung | Prio | Status v1.1.2 | Prüfung |
|---|---|---|---|---|
| R-20 | Zeilenfilter je Nutzer lassen sich nicht umgehen (`md.crane`: ausgelieferte Krane unsichtbar) | Muss | erfüllt | Skript „Row Filter“ |
| R-21 | Keine Demo-Inhalte (`finance`, `hr`) im Katalog | Muss | erfüllt | Skript „Demo-Inhalte“ |
| R-22 | Filter auf nicht freigegebene Spalten antworten mit 403 und Meldung statt 500 oder 0 Treffer | Muss | laut Befundliste behoben (SEC-FILTER-01), im PoC nicht geprüft | nicht geprüft |
| R-23 | Katalog je Person eingeschränkt (kein Schema für unberechtigte Konten) | Muss | laut Befundliste behoben, im PoC nicht geprüft; Open-Schema-Betrieb des PoC zeigt bewusst alles | nicht geprüft |
| R-24 | **Import der dbt-Governance** (`dbt_sample/target/governance`: Schutzklasse, Maskierung, Aufbewahrung, Zugriffsprofile, virtuelle Filter) in die Governance-DB | Muss | **offen**: Die `governance.db` wird bisher aus `lwecatalog` per `build_governance_db.py` erzeugt. Ein Import der `history`-/`origin`-Angaben und der `classification_review`-Stände fehlt | – |
| R-25 | Maskenform je Regel einheitlich in WebSQL, GraphQL und Prozeduren | Soll | laut Befundliste behoben (Wunsch 11), im PoC nicht geprüft | nicht geprüft |

### 2.3 Beziehungen (GraphQL)

| ID | Anforderung | Prio | Status v1.1.2 | Prüfung |
|---|---|---|---|---|
| R-30 | GraphQL bildet die Beziehungen ab, z. B. `lwetem_prod_md_crane` → `client` → `tem_gps_position` | Muss | erfüllt für die Beziehungen in `TABLE_RELATIONS` | früher geprüft (Kette crane → client → gps_position) |
| R-31 | Beziehungen kommen aus dem Katalog (dbt: `relationships`-Tests und `constraints: foreign_key`), nicht aus einer festen Liste im Skript | Soll | **offen**: `catalog/relationships.yaml` entsteht aus dem fest verdrahteten `generate_relationships.py` (`TABLE_RELATIONS`) | – |
| R-32 | Verschachtelte Listen brauchen `first`, sonst überschreitet die Abfrage das Kostenbudget (5000); Fehlermeldung soll das nennen | Soll | **offen**: Verhalten unverändert, Hinweis nur in der PoC-README | – |

### 2.4 Betrieb und Robustheit

| ID | Anforderung | Prio | Status v1.1.2 | Prüfung |
|---|---|---|---|---|
| R-40 | Image ab Release-Tag aus ghcr; kein lokaler Build | Muss | erfüllt: `v1.1.2` veröffentlicht, im PoC festgeschrieben | Pull und Start |
| R-41 | CI grün: Test `WalkingSkeletonIntegrationTests…ReturnsParentDataWithNullChild` (erwartet `"items":null`, lieferte `[]`) war der Grund für den Abbruch von `v1.1.1` | Muss | erfüllt: `v1.1.2` wurde veröffentlicht | Release vorhanden, Testlauf nicht selbst wiederholt |
| R-42 | SQLite-Governance-DB bleibt auf einer Windows-Bind-Mount (Podman/WSL) intakt | Soll | **offen**: Eine über `./autheris/data` eingebundene `governance.db` (WAL) war beim ersten Start „database disk image is malformed“. Danach folgten bei jedem Aufruf `SqliteConnection does not support nested transactions` und fail-closed 500/403. Der unveränderte, eingecheckte Stand lief fehlerfrei |  Beobachtung 09.10.2026 |
| R-43 | Nach einem SQLite-Fehler im Audit wird die Transaktion beendet (Rollback), damit Folgeaufrufe nicht dauerhaft scheitern | Soll | **offen**: siehe R-42; die Folgefehler deuten auf eine nicht zurückgerollte Transaktion auf der gemeinsamen Verbindung hin (aus Logs, nicht im Quelltext nachgewiesen) | – |

### 2.5 Verbleibende Befunde aus der Liste vom 08.10.2026

Nicht gegen v1.1.2 geprüft, da das Skript sie nicht abdeckt:

| Befund | Inhalt | Prio |
|---|---|---|
| 1.1 | Parquet über OData: Datums-/Zeitspalten als `string` | Soll |
| 1.2 | Nicht unterstützte `Accept`-Werte (CSV, NDJSON, Arrow) bei OData geben 200 mit JSON; `?format=parquet` bei WebSQL wird ignoriert | Soll |
| 3.1 | Arrow-Export und OLAP: 403 mangels ReBAC-Beziehungen; Iceberg ohne Tabellen | Soll (nur, wenn diese Wege bewertet werden) |
| 3.2 | MCP: OAuth-Discovery 401 ohne Auth, CORS im Dev-Betrieb, JSON-RPC-Batches 400 | Soll (Staging/Produktion) |

## 3. Offene Punkte nach Priorität

1. **R-24** Import der dbt-Governance in die Governance-DB (Muss). Ohne ihn gelten die Entscheidungen aus `dbt_sample` (Schutzklassen, Maskierung, Zugriffsprofile) nur im Bericht, nicht im Gateway.
2. **R-42/R-43** Robustheit der SQLite-Governance-DB bei Bind-Mounts und nach Fehlern (Soll, aber blockiert den Betrieb komplett, wenn es auftritt).
3. **R-31** Beziehungen aus dem Katalog statt aus fester Liste (Soll).
4. **R-32** Kostenbudget bei verschachtelten GraphQL-Listen verständlich melden (Soll).
5. Befunde 1.1, 1.2, 3.1, 3.2 nachprüfen und Prüfungen ins Skript aufnehmen.

## 4. Nächste Schritte im PoC

- `scripts/verify-autheris.sh` um R-22, R-23, R-25 und die Befunde 1.1/1.2 erweitern, damit „nicht geprüft“ entfällt.
- `k8s/images.sh`: Digest des gespiegelten Images von v1.1.0 auf v1.1.2 umstellen (offen, bisher nicht angefasst).
- R-24 als eigene Aufgabe zuschneiden (Format der `target/governance/*.json` → Tabellen der Governance-DB).

## 5. Grenzen dieses Berichts

- Die Zahlen in Abschnitt 1 sind aus den Tabellen gezählt (17 erfüllt, 5 offen, 3 nicht geprüft; R-05 und R-07 zählen als erfüllt mit Hinweis).
- „Erfüllt“ heißt: das Skript bestand gegen den Container mit `david`/`admin`. Es ist kein Lasttest und prüft keine Entra-Anmeldung.
- Die Ursache von R-42 (WAL auf Bind-Mount) ist eine Vermutung; belegt ist nur, dass der eingecheckte Stand lief und der andere nicht.
