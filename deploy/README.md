# GqlGateway Simulations- und Benchmark-Umgebung

Vollständige, reproduzierbare Last- und Latenz-Benchmark-Umgebung für das Projekt **GqlGateway** (.NET 8/10 GraphQL-Gateway mit Consent-, RLS- und Column-Masking-Governance-Schicht) auf Basis von **Podman Compose**.

---

## 1. Architektur-Übersicht

```
                           [ k6 Load Generator ]
                      (60% Simple | 20% Complex | 10% Mutations | 10% Invalid)
                                     │
                             HTTP (Port 8080)
                                     ▼
                        [ Nginx Reverse Proxy ]
               (ForwardAuth Ingress, Header Injection, Shared Secret)
                                     │
                             HTTP (Port 5050)
                                     ▼
                        [ GqlGateway.Api (.NET 10) ]
                   ┌─────────────────┼─────────────────┐
                   ▼                 ▼                 ▼
          [ PostgreSQL DB ]     [ Redis 7 ]    [ SQLite Governance DB ]
          (200k+ Invoices &     (Rate Limiter,  (Policies, Data Owners,
           Line Items)           Consent Cache,  Consents & Row Filters)
                                 Idempotency)
```

### Enthaltene Services in der `podman-compose.yaml`
1. **`gqlgateway-api`**: Multi-Stage Build (`Containerfile`) mit .NET 10 Runtime auf Port 5050. Startet in gehärteter Production-Konfiguration: ForwardAuth aktiv, TestAuthHandler deaktiviert, Token-Bucket Rate Limiter aktiv, Postgres-, SQLite-, SQL Server- und Redis-Anbindung sowie dynamisch aktivierte Extensions.
2. **`postgres`**: Enterprise-RDBMS für Invoices & Line Items (Domain `finance`). Seeding mit konfigurierbaren Zeilenmengen (Standard: 200.000 Zeilen) inkl. sensibler Daten (`email`, `iban`, `salary`).
3. **`sqlserver`**: Azure SQL Edge Engine (`mcr.microsoft.com/azure-sql-edge:latest`) für CRM Sales Orders (Domain `crm`). Leichtgewichtige MS SQL Server-Engine (~500 MB RAM) zur Validierung von T-SQL-Dialekten (`ORDER BY ... OFFSET ... ROWS FETCH NEXT ... ROWS ONLY`) und sensiblen E-Mail-Maskierungen.
4. **`governance-seed`**: Init-Container (`Containerfile`), der die SQLite-Governance-DB (`/data/governance.db`), die SQLite-HR-Datenbank (`/data/hr.db`, 5.000 Mitarbeiter) und den Lakehouse Table-Katalog initialisiert, inkl. Rollen, Gruppen, Data-Ownern, Masking-Regeln und RLS-Row-Filtern.
5. **`redis`**: Cache-Store für `ConsentCacheService` (Epoch Invalidation Pub/Sub), `RedisRateLimiterService` (Token-Bucket Lua-Scripts), `RedisIdempotencyStore` und EventBus.
6. **`minio` & `lakehouse-seed`**: MinIO S3 Object Storage (`minio/minio`) und Ingestion-Container zur Bereitstellung von **Apache Iceberg v2** Metadaten (`v2.metadata.json`), Manifest-Listen und Parquet-Dateien für die Lakehouse-Extension.
7. **`azurite`**: Offizieller Microsoft Azure Storage Emulator (`mcr.microsoft.com/azure-storage/azurite`) zur Bereitstellung von Azure Blob Storage / ADLS Gen2 für den Lakehouse-Konnektor.
8. **`mock-extensions`**: Schlanker Mock-Container (< 20 MB RAM) für alle externen Enterprise-Systeme aus `gql_extensions`:
   - **Multi-Catalog Governance**: **Microsoft Purview** (Apache Atlas REST), **Collibra Data Intelligence** (REST Core v2), **Alation** (Integration API v2), **OpenMetadata** (REST v1 & Realtime-Webhooks) sowie automatisierte DSGVO Art. 9 Tag-Erkennung.
   - **ITSM Two-Phase Approvals**: **ServiceNow** Table API (`/api/now/table/...`) & **Jira Service Management** REST API (`/rest/api/2/issue`) inkl. HMAC-SHA256 Webhook Verification.
   - **dbt Integration**: Streaming-Auslieferung von `manifest.json`, Model Contract Enforcement & Breaking-Change CI Gate (`/api/extensions/dbt/validate-contract`), Governance Proposal Lifecycle und Exposures Publishing (`/api/extensions/dbt/exposures`).
9. **`reverse-proxy`**: Nginx ForwardAuth Gateway auf Host-Port 8082. Injiziert `X-Forwarded-Secret` und leitet Benutzeridentitäten (`X-Forwarded-User`, `X-Forwarded-Roles`, `X-Forwarded-Groups`) unter echten Netzwerkbedingungen an das Gateway weiter.
10. **`load-generator`**: k6-Container mit parametrisierbarem Lastprofil (Ramp-up → Steady State → Ramp-down) und dynamischem Query-Mix (PostgreSQL + SQLite + SQL Server + Apache Iceberg Lakehouse).
11. **`prometheus` & `grafana`**: Automatisches Scraping von Gateway-Metriken (`/metrics`) und vorkonfiguriertes Grafana-Dashboard (`http://localhost:3000`).

---

## 2. Speicher-Szenarien (Memory-Optimierung)

Um Ressourcen auf Entwicklungsrechnern und CI/CD-Runnern zu schonen, stehen **6 dedizierte Szenarien** zur Verfügung:

| Szenario | Flag (`-Scenario` / `--scenario`) | Enthaltene Container / Extensions | Geschätzter RAM-Bedarf | Primärer Testfokus |
| :--- | :--- | :--- | :---: | :--- |
| **`minimal`** | `-Scenario minimal` | Postgres, Redis, Governance-Seed, Gateway, Reverse-Proxy | **~1.5 GB** | Kern-RLS, Consent-Cache, Token Bucket Rate Limiter, Invoices & HR, OData v4 |
| **`lakehouse`**| `-Scenario lakehouse`| Minimal + MinIO + Lakehouse-Seed (Iceberg S3) | **~1.8 GB** | Apache Iceberg v2 Scans, Vectorized Partition Pruning, S3 SigV4 Streaming |
| **`azure`** | `-Scenario azure` | Minimal + Azurite + Mock-Extensions (Azure Blob Storage) | **~1.7 GB** | Apache Iceberg v2 auf Azure Blob Storage / ADLS Gen2 |
| **`enterprise`**| `-Scenario enterprise`| Minimal + Mock-Extensions (Purview, Collibra, Alation, OpenMetadata, ITSM, dbt) | **~1.8 GB** | Multi-Catalog Sync & Webhooks, ServiceNow/Jira Two-Phase Approvals, dbt Contracts |
| **`relational`**| `-Scenario relational`| Minimal + Azure SQL Edge (`sqlserver`) | **~2.0 GB** | 3-Way Cross-Database Queries (PostgreSQL + SQLite + MS SQL Server) |
| **`openmetadata-real`**| `-Scenario openmetadata-real`| Minimal + Echter OpenMetadata Server + OpenSearch Cluster | **~4.5 GB** | Echter OpenMetadata 1.5 Server Stack inkl. Web-UI (`http://localhost:8585`) |
| **`full`** *(Default)* | `-Scenario full` | **Alle Container**: Minimal + SQL Server + MinIO + Mock-Extensions + Monitoring | **~3.5 GB** | Vollständiger E2E-Benchmark aller relationalen Quellen, Extensions und Dashboards |

---

## 3. Systemvoraussetzungen

- **Betriebssystem**: Windows 10/11 (mit Podman Desktop / WSL2) oder Linux (RHEL, Fedora, Ubuntu, Debian) oder macOS.
- **Podman**: Version 4.4+ oder 5.x / 6.x (`podman compose` oder `podman-compose`).
- **Hardware-Empfehlung**:
  - Für `minimal` / `lakehouse` / `enterprise`: 4 CPU-Kerne, 4–8 GB RAM.
  - Für `full` (alle Extensions & k6 Lasttest): 8 CPU-Kerne, 12–16 GB RAM.

---

## 4. Schnellstart (One-Click)

### Unter Windows (PowerShell)
Startet die gesamte Umgebung (Szenario `full`):
```powershell
.\run-benchmark.ps1
```

Speicheroptimierter Start einzelner Szenarien:
```powershell
# Nur Lakehouse Extension (Iceberg auf MinIO S3, ~1.8 GB RAM)
.\run-benchmark.ps1 -Scenario lakehouse

# Nur Enterprise Extensions (OpenMetadata, ServiceNow, Jira, dbt, ~1.8 GB RAM)
.\run-benchmark.ps1 -Scenario enterprise

# Minimales Kern-Gateway ohne schwere Zusatzcontainer (~1.5 GB RAM)
.\run-benchmark.ps1 -Scenario minimal -Duration "1m"
```

Mit individuellen Parametern:
```powershell
.\run-benchmark.ps1 -Scenario full -VUs 100 -Duration "5m" -SeedRows 500000 -Chaos $true
```

Um die Container nach dem Test für manuelle Analysen laufen zu lassen:
```powershell
.\run-benchmark.ps1 -KeepRunning $true
```

### Unter Linux / WSL / macOS (Bash oder Make)
```bash
# Bestimmtes Szenario ausführen
./run-benchmark.sh --scenario lakehouse
./run-benchmark.sh --scenario enterprise
./run-benchmark.sh --scenario minimal

# Oder mit Makefile
make bench-minimal
make bench-lakehouse
make bench-enterprise
make bench-full

# Direkte Ausführung der Extension-Tests
make test-ext
```

---

## 4. Parameter-Anpassung

Alle Parameter lassen sich über CLI-Argumente oder Umgebungsvariablen anpassen:

| Parameter | PowerShell Flag | Bash Flag | Standardwert | Beschreibung |
| :--- | :--- | :--- | :---: | :--- |
| **Gleichzeitige Nutzer** | `-VUs 50` | `--vus 50` | `50` | Anzahl virtueller Nutzer in k6 |
| **Steady State Dauer** | `-Duration "3m"` | `--duration 3m` | `3m` | Dauer der Maximallast (z. B. `1m`, `5m`, `10m`) |
| **Seed-Zeilenanzahl** | `-SeedRows 200000` | `--seed-rows 200000`| `200000` | Zeilenanzahl in der PostgreSQL `invoices`-Tabelle |
| **Redis Chaos Test** | `-Chaos $true` | `--no-chaos` | `$true` | Führt Redis-Ausfalltest zur Validierung von Fail-Open/Fail-Closed durch |
| **Container behalten** | `-KeepRunning $true`| `--keep-running` | `$false` | Verhindert automatischen Teardown am Testende |

---

## 5. Podman-spezifische Besonderheiten

Die Umgebung wurde speziell für **Podman** entwickelt und beachtet alle Eigenheiten:
1. **Rootless Betrieb**: Alle Container laufen ohne Root-Rechte mit dedizierten Benutzer-IDs (`appuser:10001` in `gqlgateway-api`).
2. **SELinux-Volume-Labels**:
   - `:Z` für private Volumes (`postgres-data`, `prometheus-data`, read-only SQL scripts).
   - `:z` für geteilte Volumes zwischen Containern (`governance-data` zwischen `governance-seed` und `gqlgateway-api`).
3. **Keine `privileged: true`-Direktiven**: Maximale Sicherheit im Zero-Trust-Modus.
4. **Healthcheck-Ketten**:
   - `gqlgateway-api` wartet auf gesunde `postgres`-, `redis`- und `governance-seed`-Container.
   - `reverse-proxy` wartet auf gesunden `gqlgateway-api`-Dienst.
   - `load-generator` startet erst, wenn der Reverse-Proxy vollständig betriebsbereit ist.

---

## 6. Query-Mix im Lasttest (k6)

Der Lastgenerator bildet ein realistisches Anwendungsszenario ab:

- **60 % Einfache paginierte Abfragen**:
  - GraphQL-Query: `table(domain: "finance", name: "invoices", schema: "public", first: 10..50, after: 0..500)`.
  - Testet RLS-Pushdown (`WHERE department = 'Finance'`) und dynamisches Column-Masking (`email`, `iban`, `salary`).
- **20 % Komplexe Abfragen & Relationen**:
  - GraphQL-Query: `finance.invoicesWithItems(first: 5..15) { id, amount, vendor, email, items { productName, price, sensitiveNote } }`.
  - Nutzt GreenDonut `BatchDataLoader` zur Vermeidung des N+1 Problems bei 1:N-Relationen.
  - Dynamische Katalogabfragen (`catalog { domain, schema, tableName, columns }`) mit Rollen-Filterung.
- **10 % Mutationen & Idempotenz**:
  - Consent-Antragsflow (`requestTableAccess(...)`).
  - Sendet Duplikate mit identischem `Idempotency-Key` und prüft, ob die Antwort sofort aus dem Redis-Idempotency-Store bedient wird.
- **10 % Ungültige / Übergroße Anfragen**:
  - Paginierung über das Response-Cap (`first: 99999`) -> `RESPONSE_TOO_LARGE`.
  - Anfragen blockierter Benutzer (`X-Benchmark-Role: Blocked`) -> `FORBIDDEN`.
  - Anfragen auf nicht-existente Tabellen -> `FORBIDDEN` (Anti-Enumeration).
  - Burst-Anfragen zur Prüfung der IP- und SID-Token-Bucket-Limits -> `RATE_LIMIT_EXCEEDED`.

---

## 7. Chaos Engineering: Redis-Ausfall-Test

Das Skript `scripts/chaos_test.py` simuliert einen temporären Ausfall des Redis-Containers während des Betriebs:
- **Rate Limiter (Fail-Open)**: Bei Nichterreichbarkeit von Redis fällt `RedisRateLimiterService` laut Architektur auf den lokalen In-Memory-Rate-Limiter zurück. Reguläre Anfragen werden nicht blockiert (Verfügbarkeit bleibt erhalten).
- **Governance & Sicherheit (Fail-Closed)**: Unautorisierte Zugriffe (z. B. blockierte Rollen oder fehlende ALLOW-Consents) werden weiterhin strikt mit `FORBIDDEN` abgewiesen.
- **Healthcheck**: `/health/ready` meldet während des Ausfalls korrekt `HTTP 503 Service Unavailable` für die Komponente Redis.
- **Recovery**: Nach Neustart des Redis-Containers verbindet sich das Gateway automatisch neu und `/health/ready` liefert wieder `HTTP 200 Ready`.

---

## 8. Monitoring & Dashboards

- **Grafana**: `http://localhost:3000` (Login: `admin` / `admin`)
  - Vorkonfiguriertes Dashboard **"GqlGateway Benchmark Dashboard"**:
    - Request Throughput (RPS)
    - Latenz-Perzentile (p50, p95, p99)
    - ConsentCache Hits vs. Misses
    - Rate Limit Rejections (`pre_auth_ip` vs. `post_auth_sid`)
    - GC Memory und CPU-Auslastung des .NET-Gateways
- **Prometheus**: `http://localhost:9090`

---

## 9. Ergebnis-Artefakte

Nach jedem Durchlauf werden strukturierte Reports im Ordner `results/` abgelegt:
1. `results/benchmark-report.md`: Ausführlicher Markdown-Ergebnisbericht mit KPI-Tabellen, Latenzkurven und Sicherheitsbewertung.
2. `results/benchmark-summary.json`: Alle k6-Metriken im JSON-Format.
3. `results/benchmark-summary.csv`: Tabellarische Metriken für automatisierte CI/CD-Auswertungen.
4. `results/chaos-test-result.json`: Validierungsergebnis des Redis-Chaos-Tests.
