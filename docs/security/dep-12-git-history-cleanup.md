# DEP-12: Bereinigung historischer Benchmark-Secrets aus der Git-Historie

## 1. Übersicht & Befund

- **Befund-ID**: DEP-12 (aus dem [Security-Review Gesamtprojekt 2026-10-07](../plans/security-review-2026-10-07.md))
- **Schweregrad**: Niedrig (Härtung / Secret-Hygiene)
- **Status im HEAD**: **Sauber** (HEAD enthält keinerlei Klartext-Secrets; Credentials werden über `.env` bzw. dynamische Generierung injected).
- **Problem**: In früheren Commits der Historie (u. a. `9fa510b`, `9292081`, `dae958c`, `ad13db6`) wurden Benchmark- und Test-Passwörter im Klartext committed, bevor Commits `2839be6` (S-1) und `30fb839` (S-2) die Umgebungsvariablen-Übersteuerung eingeführt haben.

---

## 2. Betroffene historische Secrets

Folgende Secrets wurden in der Commit-Historie identifiziert:

| Secret | Frühere Verwendung | Betroffene historische Dateien |
|---|---|---|
| `REDACTED_HISTORICAL_BENCHMARK_SECRET` | HMAC Secret Key / Master Key | `deploy/podman-compose.yaml`, `deploy/containers/gqlgateway-api/appsettings.Benchmark.json`, `deploy/scripts/test_extensions.py` |
| `REDACTED_HISTORICAL_BENCHMARK_SECRET` | SQL Server `sa` Passwort | `deploy/podman-compose.yaml`, `deploy/containers/gqlgateway-api/appsettings.Benchmark.json` |
| `REDACTED_HISTORICAL_BENCHMARK_SECRET` | PostgreSQL DB-Passwort | `deploy/podman-compose.yaml` |
| `REDACTED_HISTORICAL_BENCHMARK_SECRET` | PostgreSQL Load-Test-Passwort | `benchmarks/load/docker/docker-compose.gateway.yml`, `benchmarks/load/configs/gql-gateway/appsettings.bench.json` |
| `X-Hasura-Admin-Secret: REDACTED_HISTORICAL_BENCHMARK_SECRET` | Hasura Admin Header | `benchmarks/load/scripts/03_start_gateways.sh` |

> [!NOTE]
> Alle diese Zugangsdaten waren reine lokale Benchmark- und Container-Seed-Werte. Es handelte sich um keine Produktivdaten. Dennoch verlangt Secret-Hygiene nach Zero-Trust-Prinzipien das Bereinigen oder Ersetzen in der Git-Objektdatenbank.

---

## 3. Bereinigungswerkzeug & Automation

Für die automatisierte, wiederholbare Bereinigung steht das Skript [`scripts/dep12-clean-git-history.py`](../../scripts/dep12-clean-git-history.py) (sowie Wrapper `.sh` und `.ps1`) zur Verfügung.

Das Skript nutzt das offizielle und von Git empfohlene Werkzeug [`git-filter-repo`](https://github.com/newren/git-filter-repo):

```bash
# 1. Voraussetzungen installieren
pip install git-filter-repo

# 2. Historie auf Secrets prüfen (nur lesend)
python scripts/dep12-clean-git-history.py --scan

# 3. Bereinigung durchführen
python scripts/dep12-clean-git-history.py --execute
```

Das Skript ersetzt jedes Vorkommen durch den Platzhalter `REDACTED_HISTORICAL_BENCHMARK_SECRET`.

---

## 4. Durchführung & Koordination (Admin Runbook)

Da das Umschreiben der Git-Historie sämtliche Commit-Hashes ab dem Initial-Commit ändert, muss die Durchführung zentral mit allen Repository-Mitarbeitern koordiniert werden:

### Schritt 1: Lokale Änderungen stashen / committen
Stellen Sie sicher, dass keine ungesicherten lokalen Änderungen vorliegen:
```bash
git status
```

### Schritt 2: Frischen Mirror-Clone anlegen (Empfohlen)
```bash
git clone --mirror git@github.com:themulle/autheris.git autheris-mirror.git
cd autheris-mirror.git
```

### Schritt 3: Filter-Repo ausführen
```bash
python -m git_filter_repo --replace-text <(cat << 'EOF'
literal:REDACTED_HISTORICAL_BENCHMARK_SECRET==>REDACTED_HISTORICAL_BENCHMARK_SECRET
literal:REDACTED_HISTORICAL_BENCHMARK_SECRET==>REDACTED_HISTORICAL_BENCHMARK_SECRET
literal:REDACTED_HISTORICAL_BENCHMARK_SECRET==>REDACTED_HISTORICAL_BENCHMARK_SECRET
literal:REDACTED_HISTORICAL_BENCHMARK_SECRET==>REDACTED_HISTORICAL_BENCHMARK_SECRET
literal:X-Hasura-Admin-Secret: REDACTED_HISTORICAL_BENCHMARK_SECRET==>X-Hasura-Admin-Secret: REDACTED_HISTORICAL_BENCHMARK_SECRET
EOF
) --force
```

### Schritt 4: Verifikation
Prüfen, dass keine Treffer mehr existieren:
```bash
git log -S "REDACTED_HISTORICAL_BENCHMARK_SECRET" --oneline
git log -S "REDACTED_HISTORICAL_BENCHMARK_SECRET" --oneline
git log -S "REDACTED_HISTORICAL_BENCHMARK_SECRET" --oneline
git log -S "REDACTED_HISTORICAL_BENCHMARK_SECRET" --oneline
```
(Alle Befehle müssen leer zurückkehren.)

### Schritt 5: Force-Push an Remote
```bash
git push origin --force --all
git push origin --force --tags
```

### Schritt 6: Bereinigung der GitHub-Caches
Auf GitHub bleiben historische Commits über Pull-Request-Refs und Caches erreichbar, bis ein Garbage Collection Lauf stattgefunden hat. Bei öffentlichen Repositories oder hochsensiblen Leaks kann der GitHub Support kontaktiert werden, um den internen Git-Cache zu purgen.

### Schritt 7: Entwickler-Workspaces aktualisieren
Entwickler sollten nach dem zentralen Rewrite ihre lokalen Branches auf die neuen Upstream-Commits rebasen bzw. ein frisches `git clone` durchführen.

---

## 5. Automatisierter Regressionstest (HEAD Protection)

Um sicherzustellen, dass keine dieser Credentials jemals wieder in den HEAD gelangen, sichert der Unit-Test [`DEP_12_TrackedBenchmarkConfigurations_DoNotContainHardcodedHistoricalSecrets`](../../tests/Autheris.Tests.Unit/Security/SecurityReviewRemediationCoverageTests.cs) alle getrackten Konfigurations- und Benchmarkdateien bei jedem Testlauf ab.
