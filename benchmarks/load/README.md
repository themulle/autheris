# GraphQL Gateway Benchmark Suite auf Hetzner (gql_bench)

Dieses Verzeichnis enthält alle Skripte, Konfigurationen, Dockerfiles und Anleitungen, um **GqlGateway** (`lis-git/gql`) objektiv und reproduzierbar gegen führende GraphQL-Engines (**Hasura**, **Apollo Server 5**, **PostGraphile**) auf **Hetzner Cloud oder Dedicated Servern** zu benchmarken.

Als Referenzbasis dient der bewährte Standard-Benchmark [`hasura/graphql-bench`](https://github.com/hasura/graphql-bench) mit der relationalen **Chinook-Datenbank** (PostgreSQL) und dem Lasttest-Tool **k6**.

---

## 1. Übersicht & Zielsetzung

| Komponente | Details |
|---|---|
| **Gateways im Test** | • **GqlGateway** (.NET 10 / HotChocolate / FastSqlEngine)<br>• **Hasura GraphQL Engine v2** (Haskell / C++)<br>• **Apollo Server 5** (Node.js 20)<br>• **PostGraphile** (Node.js 20 / Graphile Engine) |
| **Datenbank** | PostgreSQL 16 mit **Chinook-Datensatz** (Artists, Albums, Tracks, Genres, Customers) |
| **Lastgenerator** | **k6** (High-Performance C++/Go Load Generator) & **graphql-bench** / **autocannon** |
| **Ziel-Infrastruktur** | Hetzner Cloud (z.B. CPX41 / CCX33) oder Hetzner Dedicated Server (AX42 / AX52) |
| **Messgrößen** | RPS (Durchsatz), Latenzen (P50, P90, P95, P99), Fehlerrate, CPU- & RAM-Effizienz |

---

## 2. Hetzner Server-Empfehlungen & Topologie

Für valide, unverfälschte Messergebnisse ohne "Noisy Neighbors" oder CPU-Throttling:

### Variante A: 2-Server Topologie (Empfohlen für exakte Messungen)
* **Server 1 (Load Generator):** Hetzner Cloud `CPX31` (4 vCPU, 8 GB RAM) – Führt k6 / graphql-bench aus.
* **Server 2 (SUT: Gateways & DB):** Hetzner Cloud `CCX33` (8 **Dedicated** AMD vCPU, 32 GB RAM) oder `CPX41` (8 vCPU, 16 GB RAM).
* **Netzwerk:** Privates Hetzner Cloud Netzwerk (10 Gbit/s, Latenz < 0.3 ms im selben RZ, z.B. Falkenstein `fsn1`).

### Variante B: All-in-One Server (Schnell & Kostengünstig)
* **Einzelsystem:** Hetzner Cloud `CPX41` (~0,045 €/Std. / ~27 €/Monat) oder Hetzner Dedicated `AX42` (AMD Ryzen 7, 64 GB RAM).
* Alle Komponenten laufen isoliert via Docker Compose mit festgelegten CPU- und RAM-Limits (`cpus: 4.0`, `mem_limit: 4G`).

---

## 3. Verzeichnisstruktur in `gql_bench`

```
gql_bench/
├── README.md                      # Diese Dokumentation
├── configs/
│   ├── chinook.sql                # Vollständiges Chinook DB-Schema & Daten
│   ├── apollo-server/             # Apollo Server 5 Implementierung mit PostgreSQL-Pool
│   │   ├── package.json
│   │   └── index.js
│   ├── postgraphile/              # PostGraphile Node.js Benchmark-Runner
│   │   ├── package.json
│   │   └── index.js
│   ├── hasura-metadata/           # Hasura Chinook Tabellen- & Beziehungs-Tracking
│   │   ├── psql_track_chinook_tables.json
│   │   └── psql_track_chinook_relationships.json
│   ├── gql-gateway/               # Benchmark-Konfiguration für GqlGateway
│   │   └── appsettings.bench.json # Tuning: Hohe Rate-Limits, Test-Auth, Server-GC
│   ├── graphql-bench/             # Original graphql-bench Query-Konfiguration
│   │   └── config.yaml
│   └── k6/                        # Modulare k6-Testskripte
│       └── load_test.js
├── docker/
│   ├── docker-compose.yml         # Startet Postgres, Hasura, Apollo, PostGraphile, GqlGateway
│   ├── Dockerfile.gql             # Multi-Stage Build für GqlGateway (.NET 10)
│   ├── Dockerfile.apollo          # Dockerfile für Apollo Server 5
│   └── Dockerfile.postgraphile    # Dockerfile für PostGraphile
├── hcloud/
│   ├── cloud-init.yaml            # Automatisches Hetzner Cloud-Init Provisioning
│   └── provision_hetzner_servers.sh # CLI-Skript zur Server-Erstellung mit hcloud
├── scripts/
│   ├── 01_hetzner_setup.sh        # Host-Provisioning, Docker, .NET, Node, Kernel-Tuning
│   ├── 02_init_database.sh        # Startet Postgres & verifiziert Chinook-Tabellen
│   ├── 03_start_gateways.sh       # Baut/startet Gateways, setzt Hasura-Metadaten, Warmup
│   ├── 04_run_benchmarks.sh       # Führt k6 Benchmark-Matrix über alle Gateways aus
│   └── 05_analyze_results.py      # Wertet JSON-Ergebnisse aus, erzeugt Markdown/CSV-Report
└── results/                       # Verzeichnis für JSON-Ergebnisse und Berichte
```

---

## 4. Ausführung: Single-Host vs. 3-Tier Multi-Host

In der Wurzel des Verzeichnisses stehen zwei komfortable Master-Skripte bereit:

### Variante A: Single-Host Schnellstart (`run_single_host.sh`)
Führt alle Schritte (Setup, DB, Gateways, k6-Lauf, Analyse) auf einem einzigen Server aus:
```bash
cd /root/lis-git/gql/gql_bench

# Standard-Benchmark (30s, 1.000 RPS)
./run_single_host.sh --full

# Schneller Funktionstest (10s, 500 RPS)
./run_single_host.sh --quick

# Stufenweiser Lasttest (Ramp-up von 100 bis 5.000 RPS)
./run_single_host.sh --ramp
```

---

### Variante B: Offizieller 3-Tier Multi-Host Benchmark (`run_multi_host.sh`)
Orchestriert automatisch die **vollständige 3-Server-Architektur** auf Hetzner Cloud (DB, Gateway, Client-Loadgenerator im 10G-Privatnetz):
```bash
cd /root/lis-git/gql/gql_bench
export HCLOUD_TOKEN="dein-hetzner-cloud-api-token"

# Startet Cluster, initialisiert DB, deployt Gateways, führt Lasttest aus und sammelt Ergebnisse
./run_multi_host.sh --full

# Nach Abschluss Server automatisch löschen (Kostenminimierung)
./run_multi_host.sh --full --auto-destroy
```

---

### Manuelle Einzelschritte (falls gewünscht)

---

### Schritt 1: Workspace auf den Hetzner Server kopieren

Vom lokalen Entwicklungsrechner:
```bash
# Workspace auf den Hetzner Server übertragen
rsync -avz --exclude 'bin' --exclude 'obj' --exclude 'node_modules' \
  /root/lis-git/ root@<HETZNER_SERVER_IP>:/root/lis-git/

# Per SSH einloggen
ssh root@<HETZNER_SERVER_IP>
cd /root/lis-git/gql/gql_bench
```

---

### Schritt 2: Host einrichten & Kernel tunen

Das Skript [`scripts/01_hetzner_setup.sh`](file:///root/lis-git/gql/gql_bench/scripts/01_hetzner_setup.sh) optimiert den Linux-Kernel für hohen Netzwerkdurchsatz (`somaxconn=65535`, `tcp_tw_reuse`, erweiterte Port-Range, `nofile=1048576`) und installiert Docker, k6, .NET 10 und Node.js:

```bash
./scripts/01_hetzner_setup.sh
```

---

### Schritt 3: Chinook PostgreSQL Datenbank initialisieren

Startet PostgreSQL 16 mit der Chinook-Datenbank und führt ein automatisches `VACUUM ANALYZE` durch:

```bash
./scripts/02_init_database.sh
```

---

### Schritt 4: Gateways starten & aufwärmen

Baut und startet alle vier GraphQL Gateways, konfiguriert die Relationen in Hasura und führt **Warmup-Requests** durch, um JIT-Kompilierungsartefakte vor der eigentlichen Messung zu eliminieren:

```bash
./scripts/03_start_gateways.sh
```

Endpoints nach dem Start:
* **Hasura GraphQL Engine:** `http://localhost:8085/v1/graphql`
* **Apollo Server 5:** `http://localhost:4000/`
* **PostGraphile:** `http://localhost:5001/graphql`
* **GqlGateway:** `http://localhost:5000/graphql`

---

### Schritt 5: Benchmark Matrix ausführen

Führt die Lasttests über alle konfigurierten Gateways und Abfragetypen aus:

```bash
# Standardlauf: 30s pro Test bei 1.000 RPS
./scripts/04_run_benchmarks.sh

# Benutzerdefinierte Parameter:
DURATION=60s RPS=2500 VUS=100 ./scripts/04_run_benchmarks.sh

# Stufenweiser Lastrampen-Test (Ramp-up bis 5.000 RPS):
SCENARIO=ramp DURATION=70s ./scripts/04_run_benchmarks.sh
```

---

### Schritt 6: Ergebnisse analysieren & Berichte einsehen

Nach dem Benchmark-Lauf generiert [`scripts/05_analyze_results.py`](file:///root/lis-git/gql/gql_bench/scripts/05_analyze_results.py) automatisch:
1. Eine sortierte Markdown-Tabelle im Terminal.
2. Einen Markdown-Bericht in `results/report_<timestamp>.md`.
3. Eine strukturierte CSV-Datei in `results/report_<timestamp>.csv`.

Manueller Aufruf zur Neuanalyse:
```bash
python3 ./scripts/05_analyze_results.py
```

---

## 5. Getestete Abfragetypen (Workloads)

1. **`pk` (Primary Key Lookup):**
   ```graphql
   query AlbumByPK {
     albums_by_pk(id: 1) { id title }
   }
   ```
   *Misst den minimalen Gateway-Overhead: HTTP-Parsing, GraphQL-AST, Auth/Validierung, Serialisierung.*

2. **`filter` (Gefilterte Suche):**
   ```graphql
   query SearchAlbums {
     albums(where: {title: {_like: "%Rock%"}}) { id title }
   }
   ```
   *Misst Filter-Klauseln und Parameter-Handling.*

3. **`join` (1-Level Relation):**
   ```graphql
   query SearchAlbumsWithArtist {
     albums(where: {title: {_like: "%Rock%"}}) {
       id
       title
       artist { id name }
     }
   }
   ```
   *Misst N+1 Vermeidung, DataLoader vs. SQL-Join Compilation.*

4. **`deep` (Mehrstufige Verschachtelung):**
   ```graphql
   query AlbumWithTracksAndGenre {
     albums_by_pk(id: 1) {
       id
       title
       tracks { id name genre { name } }
     }
   }
   ```
   *Misst komplexe Objektgraphen-Auflösung.*

---

## 6. Wichtige Grundsätze für faire Vergleiche

1. **Gleiche Ressourcen (cgroups):** Alle Gateway-Container im `docker-compose.yml` sind exakt auf `cpus: 4.0` und `mem_limit: 4G` begrenzt.
2. **JIT & Cache Warmup:** Vor jeder Messung werden mindestens 20 Warmup-Requests gesendet, damit .NET Tiered Compilation und V8 TurboFan aktiv sind.
3. **Logging minimiert:** Alle Gateways laufen mit `LogLevel: Warning`, da Festplatten-I/O bei tausenden RPS sonst zum künstlichen Flaschenhals wird.
4. **Verbindungspools:** Alle Datenbank-Pools sind identisch auf `100` Verbindungen konfiguriert.

## Security notes (benchmark-only environment)

The benchmark stack intentionally runs the gateway in Development mode with the test auth
handler and `danger_bypass_consent_checks`. It must never be reachable from the public Internet.

* Credentials are not committed. `scripts/lib_secrets.sh` generates a random Postgres password
  and Hasura admin secret per run (`openssl rand`) and stores them in the untracked
  `benchmarks/load/.bench-secrets.env`. Override via `BENCH_DB_PASSWORD` / `BENCH_HASURA_ADMIN_SECRET`.
* Published ports bind to `127.0.0.1` by default. The multi-host orchestrators set
  `DB_BIND_IP` / `GATEWAY_BIND_IP` to the private network addresses (10.0.1.x).
* `hcloud/provision_3tier_cluster.sh` creates an hcloud firewall (`bench-fw`) that allows only
  SSH (tcp/22) from your IP (auto-detected, or set `ALLOWED_SSH_CIDRS`). The private network is not
  filtered by hcloud firewalls, so inter-host traffic keeps working.
* SSH uses `StrictHostKeyChecking=accept-new` with a per-run `known_hosts_<timestamp>` file in `results/`.
* Host setup downloads the NodeSource and dotnet-install scripts to a file before executing them;
  neither publishes a stable checksum, so review them if you need stronger supply-chain guarantees.
