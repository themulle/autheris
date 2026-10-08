# WebSQL und Trino-Endpunkt: Offene Befunde aus der Integration des PoC mit Autheris 1.1.0

**Stand:** Image `ghcr.io/themulle/autheris:latest` vom 08.10.2026, Quellcode `feat/ast-target-dialect-generator`.
Alle behobenen Punkte (2.1, 2.2, 2.3, 2.4, 4.1, 4.2, 4.3-ReBAC-Body, 4a.1, 4a.2, 4a.4, 4b.1, 4b.2, 4b.3, 4b.4-dataset-validation) wurden entfernt. Dieses Dokument enthält ausschließlich die verbleibenden offenen Punkte.

---

## Übersicht der offenen Punkte

| Nr. | Art | Befund | Wirkung | Abschnitt |
|---|---|---|---|---|
| 4.3 | Auffälligkeit | Parquet über OData liefert Datums- und Zeitspalten als `string` (über WebSQL als `timestamp`) | Typverlust in Parquet-Clients | 1.1 |
| 4.3 | Auffälligkeit | `Accept: text/csv`, `x-ndjson`, Arrow bei OData wird ignoriert (JSON mit 200); `?format=parquet` bei WebSQL ebenso | falsches Format ohne Fehler (strikte Content-Negotiation fehlt) | 1.2 |
| 4a.3 | Lücke | `$filter` gibt 501 NotImplemented (`$orderby`, `$count`, `/$count` sind umgesetzt) | Query Folding von Filtern in Power Query / Excel schlägt fehl | 2.1 |
| 4 | PoC-Konfiguration | Arrow-Export und OLAP: 403 mangels ReBAC-Beziehungen; Iceberg ohne Tabellen | im PoC nicht prüfbar, Einrichtung/Seed fehlt | 3.1 |
| 4b.4 | Konfiguration / Auth | Offene Restpunkte MCP (OAuth-Discovery 401 ohne Auth, CORS im Dev-Betrieb, JSON-RPC-Batches) | Härtung für Staging/Produktion | 3.2 |

---

## 1. Transportformate & Content Negotiation

### 1.1 Parquet über OData: Datums- und Zeitspalten als `string`
- **Beobachtung:** Zeit- und Datumsspalten (`created_at`, `date_of_delivery` in `md.crane`) werden beim OData-Parquet-Export (`Accept: application/vnd.apache.parquet`) als `string` serialisiert, während sie über WebSQL als typisierter `timestamp` ausgegeben werden.
- **Ursache:** Die OData-Formatierung konvertiert Datenzeilen vor der Parquet-Serialisierung in JSON-/String-Typen statt die CLR-Datentypen für Arrow-Felder beizubehalten.
- **Folge:** Typverlust in nachgelagerten Parquet-/Analytics-Clients.
- **Handlungsempfehlung:** Typ-Mapping in `ODataHandler`/`ParquetExportService` an die typisierte Arrow-Schema-Erzeugung von WebSQL angleichen.

### 1.2 Content Negotiation bei OData und WebSQL
- **Beobachtung:**
  - Bei OData-Endpunkten werden Header wie `Accept: text/csv`, `application/x-ndjson` oder Arrow ignoriert; der Endpunkt antwortet mit HTTP 200 und OData-JSON.
  - Bei WebSQL (`POST /api/v1/sql`) wird der Query-Parameter `?format=parquet` ignoriert und JSON ausgeliefert (nur der `Accept`-Header steuert das Format).
- **Folge:** Clients erhalten unerwartet JSON statt des angeforderten Formats, ohne dass ein Fehler gemeldet wird.
- **Handlungsempfehlung:**
  - OData: Strikte Content Negotiation umsetzen (nicht unterstützte `Accept`-Header mit HTTP 406 Not Acceptable ablehnen oder CSV/NDJSON unterstützen).
  - WebSQL: Query-Parameter `?format=parquet` auswerten oder konsistent auf den `Accept`-Header verweisen.

---

## 2. OData Minimal Conformance

### 2.1 OData `$filter` gibt HTTP 501
- **Status `$orderby`/`$count`:** Umgesetzt. `$orderby` nimmt `eigenschaft [asc|desc]` (nur Spalten mit Clear-Zugriff); `$count=true` und `/$count` liefern die Gesamtzahl unter demselben Zeilenfilter (`COUNT(*)` in derselben Transaktion), nie die Seitengröße. Quellen ohne SQL antworten 501. Tests: `ODataOrderByCountTests`, `ODataCountSegmentTests`.
- **Beobachtung:** `$filter` antwortet weiterhin mit HTTP 501 NotImplemented.
- **Folge:** Nach OData-Spezifikation ist 501 zwar formell zulässig, verhindert jedoch die OData „Minimal Conformance“ (erfordert `$top`, `$skip`, `$filter`, `$orderby`, `$select`, `$count`). Power Query schiebt im Editor gesetzte Filter per Query Folding als `$filter` an den Server und bricht mit 501 ab.
- **Handlungsempfehlung:**
  - Grundlegende `$filter`-Unterstützung (Gleichheit, Vergleiche, logische Operatoren) auf Tabellenabfragen abbilden; Filter nur auf Spalten mit Clear-Zugriff (wie `$orderby`).

---

## 3. PoC-Konfiguration & Sicherheits-Restpunkte

### 3.1 ReBAC-Beziehungen und Iceberg-Tabellen im PoC
- **Beobachtung:**
  - **Arrow-Export** (`POST /api/v1/export/arrow`) und **OLAP/DuckDB** (`POST /api/v1/olap/query`): Antworten mit HTTP 403 („ReBAC Access Denied: Not authorized by relationship graph“), da im Test-Setup keine ReBAC-Relationen (`viewer` auf Tabelle) für Nutzer wie `david` oder `admin` hinterlegt sind.
  - **Iceberg REST Catalog** (`/v1/{prefix}/namespaces…`): Liefert leere Listen (`namespaces: []`) bzw. 403 für Tabellen, da im Test-Container keine Iceberg-Tabellen registriert sind.
- **Handlungsempfehlung:** Standard-Beziehungen und Seed-Metadaten für Iceberg/Arrow im Test-/Container-Setup einrichten, falls diese Transportwege im PoC evaluiert werden sollen.

### 3.2 MCP-Restpunkte
- **JSON-RPC-Batches:** Arrays im HTTP-Body werden mit 400 abgewiesen (Protokoll 2025-03-26 erlaubte Batches, neuere Fassungen nicht mehr; für aktuelle SDK-Clients unkritisch).
- **OAuth-Discovery:** `/.well-known/oauth-protected-resource` und `/.well-known/oauth-authorization-server` antworten ohne Anmeldung mit 401. Standard-OAuth-Clients fragen diese Endpunkte anonym ab.
- **CORS / Dev-Flags:** Der PoC setzt `allow_all_cors_origins` (`X-Gateway-Insecure-Mode`). Für den Produktivbetrieb muss die CORS-Whitelist eingeschränkt werden.
