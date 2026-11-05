# Autheris Benchmarking & Metrics Measurement Protocol

This document defines the standardized measurement environment, protocols, calculation methods, schemas, and verification commands used to establish and track the Stage 0 baselines and subsequent simplification milestones (per [PLAN-SIMPL](../../docs/plans/2026-11-05-plan-project-simplification.md) section 3 and [WP-SIMPL-10](../../docs/plans/simplification/WP-SIMPL-10-measurement-baselines.md)).

---

## 1. Reference Host & Environment Specification

Measurements must record host hardware and environment parameters to ensure reproducibility across runs:

| Parameter | Reference Value / Standard Configuration |
|---|---|
| **CPU Model** | AMD Ryzen 7 8840U w/ Radeon 780M Graphics (or standard host CPU) |
| **Cores** | 16 logical threads (8 cores) |
| **RAM** | 11–16 GiB System RAM |
| **Operating System** | Linux (Ubuntu 24.04 LTS / WSL2 Kernel 6.18+) |
| **Architecture** | `x86_64` / `linux-x64` |
| **.NET SDK** | .NET 10.0 (per `global.json`: `10.0.401`, roll-forward `latestPatch`) |
| **Container Engine** | Docker 29.1+ / Podman 5.0+ |

---

## 2. General Measurement Rules

1. **Append-Only Results**: Results in `benchmarks/results/` are append-only. Existing result files are never overwritten; every measurement produces a new timestamped file:
   ```
   benchmarks/results/<metric>-<yyyy-mm-dd>-<sha7>.json
   ```
2. **Clean Tree Enforcement**: Measurement scripts fail if uncommitted changes exist (`git diff --quiet`), ensuring every measured metric maps unambiguously to a specific git commit SHA. (An explicit `--allow-dirty` flag is provided only for local testing).
3. **No Embedded Credentials**: Scripts must never echo, log, or commit secret tokens (PATs, bearer tokens). Tokens are consumed strictly from environment variables (`GITHUB_TOKEN`).

---

## 3. Statistical Methods

### Percentile Calculation (Nearest-Rank Method)
Percentiles ($P_{50}$, $P_{90}$, $P_{95}$, $P_{99}$) are calculated using the nearest-rank method on ordinally sorted samples:
$$\text{rank} = \left\lceil \frac{P}{100} \times N \right\rceil$$
where $N$ is the sample size and $P \in (0, 100]$. The sample at index $(\text{rank} - 1)$ is taken.

---

## 4. Measurement Categories & Scripts

### 4.1 Repository Metrics (`benchmarks/scripts/repo-metrics.sh`)
Measures codebase size, structural distribution, dependency injection registrations, and test hygiene metrics.

#### Measurement Rules & Regular Expressions
* **`src_loc`**: `git ls-files -z 'src/*.cs' | wc -l --files0-from=- | tail -n 1` (Counts all lines in C# production sources).
* **`modules_loc`**: `git ls-files -z 'modules/*.cs' | wc -l --files0-from=- | tail -n 1` (0 when `modules/` does not exist).
* **`test_loc`**: `git ls-files -z 'tests/*.cs' | wc -l --files0-from=- | tail -n 1` (Counts all lines in C# test sources).
* **`map_endpoints`**: Matches endpoint mapping calls in `src/` using the single-sourced ratchet regex:
  `\.Map(Get|Post|Put|Delete|Patch|Methods|Group|Hub|GraphQL)\s*\(` (excluding comment lines).
* **`application_folders`**: Count of distinct top-level directories in `src/Autheris.Application` excluding `bin`, `obj`, and `Properties`.
* **`options_classes`**: `class\s+[A-Za-z0-9_]+Options\b` in `src/`.
* **`enabled_flags`**: `public\s+bool\s+[A-Za-z0-9_]*Enabled\b` in `src/`.
* **`hosted_services`**: `AddHostedService<` in `src/`.
* **`di_registrations`**: `\.(AddSingleton|AddScoped|AddTransient|TryAdd[A-Za-z0-9_]*)\s*(<|\()` in `src/`.
* **`substitute_for`**: `Substitute\.For\b` in `tests/`.
* **`received`**: `\.Received\(` in `tests/`.
* **`sleeps`**: `(Thread\.Sleep|Task\.Delay)\s*\(` in `tests/`.
* **`container_test_files`**: Test files containing `Testcontainers` or `IClassFixture<.*(Postgres|SqlServer|Oracle)`.
* **`container_share_pct`**: `container_test_files / total_test_files * 100`.

#### Reconciled Deltas against Initial Plan Estimates
* **Map* Endpoints**: The initial plan table noted 275 based on a heuristic scanner; the exact ratchet regex finds 306 active mapping call sites.
* **DI Registrations**: Broad method-call pattern finds 636 registrations in `src/` (compared to 549 estimate).
* **Substitute.For**: 4,112 calls in tests.

---

### 4.2 Binary & Runtime Footprint (`benchmarks/scripts/footprint.sh`)
Measures uncompressed and compressed image footprint, cold-start latency, warm latency, and lazy-loading assertions.

#### Protocol
1. **Container Image Build**:
   ```bash
   dotnet publish src/Autheris.Api/Autheris.Api.csproj -c Release -p:AutherisIncludeTestAuth=false -o ./dist/publish
   $CONTAINER_ENGINE build -f Dockerfile -t autheris:measure .
   ```
2. **Uncompressed Size**:
   ```bash
   $CONTAINER_ENGINE image inspect --format '{{.Size}}' autheris:measure
   ```
3. **Compressed Transfer Size**:
   ```bash
   $CONTAINER_ENGINE save autheris:measure | gzip -c | wc -c
   ```
4. **Cold-Start Latency**:
   * 20 fresh container invocations (`podman run -d --rm` / `docker run -d --rm`).
   * Polling `/health/ready` every 50 ms (60 s timeout).
   * Elapsed time measured from container run launch to first HTTP 200 OK.
   * Reports $P_{50}$ (median) and $P_{95}$.
5. **Warm Query Latency**:
   * 200 sequential warm-up requests.
   * 2,000 sequential measurement requests of the standard read benchmark query.
   * Reports $P_{50}$ and $P_{99}$.
6. **Lazy-Loading Verification (OD-12 / OD-15)**:
   * In a cold-started container with no federated query and no configured Oracle data source, native assemblies must not be loaded into memory.
   * Verified by inspecting `/proc/1/maps` in the container:
     ```bash
     $CONTAINER_ENGINE exec <id> cat /proc/1/maps | grep -c -i duckdb # must be 0
     $CONTAINER_ENGINE exec <id> cat /proc/1/maps | grep -c -i oracle # must be 0
     ```
   * Recorded as boolean values in `values.lazy_duckdb` and `values.lazy_oracle`.

---

### 4.3 CI Duration (`benchmarks/scripts/ci-duration.sh`)
Measures wall-clock CI execution duration from push to completion of the last required check (including runner queue time, per OD-6 and Issue #523).

#### Protocol
* Evaluates successful workflow runs of `ci.yml` via GitHub Actions API.
* Only counts runs created after the fix for CI hangs (#516/#517).
* Computes:
  $$\text{duration} = \text{last\_job.completed\_at} - \text{run.created\_at}$$
* Reports median overall duration across 10 runs for `main` branch pushes as well as `pull_request` events.

---

## 5. Result File JSON Schema

All measurement outputs conform to the following schema:

```json
{
  "metric": "repo-metrics | footprint | ci-duration",
  "date": "YYYY-MM-DD",
  "sha": "7-char-sha",
  "host": {
    "hostname": "string",
    "os": "string",
    "kernel": "string",
    "arch": "string",
    "cpu": "string"
  },
  "values": {
    /* Metric-specific numeric and boolean values */
  },
  "method": "Command line and arguments used"
}
```
