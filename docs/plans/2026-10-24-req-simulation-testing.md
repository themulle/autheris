# REQ-SIMTEST: Simulation test programme ("test rig for years of production use")

Phase 1 PRD. Status: draft. Origin: master plan row `REQ-SIMTEST`. Owner order 2026-10-24. Stage 1 (P1-P3) is implemented in the `simtest` lane; Stages 2-4 are planned only.

## 1. Problem

Hand-written and pairwise suites (Parity, SqlCompilerGate, Robin) find what we already thought of. Production use over years reaches inputs, orderings and timings nobody wrote down (#280 class: valid feature, 500). A car test rig simulates hundreds of thousands of km on a few metres of track; we need the same for Autheris: machine-generated volume, independent oracles, accelerated time, injected faults, all deterministic and reproducible.

## 2. Goals / non-goals

Goals
1. Find 5xx, wrong answers and policy leaks that curated tests miss, at volume, with reproducible seeds.
2. Reuse third-party corpora and tools where licence-clean; build only the policy-aware part ourselves (Stage 2).
3. Every finding becomes a KnownGap (strict xfail) plus a GitHub issue; 5xx and security findings are never allowed gaps.

Non-goals
1. No product fixes in this programme (findings are filed, fixed by their own lanes).
2. Nothing runs in normal CI; nightly/weekly only.
3. No SQLsmith code (GPL), no Jepsen-style tests (no distributed-consistency claim in Autheris), no SQLancer code (ideas only).
4. No performance benchmarking / SLO claims (soak checks correctness and leaks, not throughput numbers).

## 3. Licence context and blockers

Repo licence: Business Source License 1.1 (`LICENSE`, change date 2029-10-01 to Apache-2.0; internal production use granted). It is NOT Apache/MIT today. Consequences:
- Committing third-party content requires a licence that allows redistribution inside a BSL repo and keeps its own notice. Permissive (MIT/BSD/Apache/CC0/public domain) is fine; EULA-with-conditions content is not committed.
- External tools run in CI as separate processes (no linking, no redistribution) so copyleft (AGPL/LGPL) tools are usable as test tools; their code is never copied into the repo.
- Blockers found: (B1) TPC-H/TPC-DS query text and generators: TPC EULA, see 4. (B2) IMDB data for JOB: personal/non-commercial licence, see 4. (B3) NBomber: organisational use needs a paid subscription, see 4. No programme-level blocker; each has a fallback below.

## 4. Tool / licence verdict table (verified 2026-10-24)

| Item | Licence (verified) | Maintenance | Verdict | Source |
|---|---|---|---|---|
| SQLite sqllogictest (runner + test files) | Runner: choice of GPL/BSD/MIT/CC0 (COPYRIGHT file); SQLite core/TCL tests public domain. Test files carry the same author terms; the exact grant of the generated corpus must be re-confirmed on import (O1) | SQLite project, active | USE (P1) with MIT/CC0 option; commit only files whose terms we confirmed, record provenance + licence in a NOTICE file | sqlite.org/sqllogictest/doc/tip/COPYRIGHT.md, sqlite.org/copyright.html |
| TPC-H / TPC-DS queries and dbgen/qgen | TPC EULA: you may copy/modify/distribute only under the EULA, with full licence copy and the legend "THE TPC SOFTWARE IS AVAILABLE WITHOUT CHARGE FROM TPC." DuckDB's tpch extension dbgen carries the same EULA, so its queries are NOT a permissive derivative (DuckDB itself is MIT, the embedded dbgen is not) | TPC | BLOCKER for committing. Fallback: do not commit; generate at CI time (download dbgen via DuckDB `tpch`/`tpcds` extension in the job) or write our own original queries of the same shapes (no TPC text) | raw.githubusercontent.com/duckdb/duckdb/main/extension/tpch/dbgen/LICENSE (clauses 4, 9) |
| Join Order Benchmark | Queries: research repo (CWI/gregrahn mirror, not maintained by authors; no explicit permissive licence seen). Data: IMDB non-commercial datasets, personal/non-commercial use only | Dormant | BLOCKER for data in a commercial-product repo; queries unclear. Move to Stage 4 optional; use synthetic skewed data instead (P9) | github.com/gregrahn/join-order-benchmark, developer.imdb.com/non-commercial-datasets |
| Schemathesis | MIT | Very active (push 2026-10-23); REST/OpenAPI and GraphQL, stateful | USE (P2), pip/uvx or container, external process | github.com/schemathesis/schemathesis |
| EvoMaster | LGPL-3.0 | Very active; JVM, white/black-box REST+GraphQL | Allowed as external tool (no linking); defer, Schemathesis covers REST+GraphQL first; revisit if coverage-guided search needed | github.com/WebFuzzing/EvoMaster |
| GraphQL alternatives | CATS (Apache-2.0, REST only); RESTler (MIT, REST, last push 2026-06) | active | Not needed for Stage 1; Schemathesis GraphQL mode chosen | github API metadata |
| SharpFuzz | MIT | Maintained (last push 2026-06, stable tool) | USE (P3). AFL/AFL++ on Linux (or libfuzzer-dotnet); explicit timeout (docs: 5000 ms, never auto), out-of-process mode so hangs/stack overflow do not kill the campaign | github.com/Metalnem/sharpfuzz, docs/legacy-usage-instructions.md |
| CsCheck | Apache-2.0 | Active (push 2026-10-21) | Preferred for P4 (C#-native, built-in shrinking by seed, parallel/metamorphic helpers); confirm in Stage 2 spike | github.com/AnthonyLloyd/CsCheck |
| FsCheck | BSD-3-Clause | Active (push 2026-10-21) | Acceptable alternative; F#-flavoured API, extra dependency | github.com/fscheck/FsCheck |
| NBomber | Proprietary; v4.0 licence (effective 2026-09-01): free for personal use only, any organisational use needs a paid Commercial Subscription | Active | BLOCKER for P7 unless owner buys a subscription; default: do not use | nbomber.com/assets/NBOMBER-LICENSE-AGREEMENT-4.0.pdf |
| k6 | AGPL-3.0 | Very active | OK as an unmodified external binary/container in a test job (we do not distribute or link it, no network service offered to third parties). Default for P7. Scripts are our own (not derivative) | github.com/grafana/k6 |
| Toxiproxy | MIT | Active (push 2026-10-20) | USE (P8), container | github.com/Shopify/toxiproxy |
| SQLancer | MIT (Java) | Active | Ideas only (TLP, NoREC, PQS); no code, no JVM dependency | github.com/sqlancer/sqlancer |
| SQLsmith | GPL | n/a | Rejected: idea only | brief |

## 5. Anti-patterns rejected

1. Committing TPC or IMDB content without verified licence (B1, B2).
2. Fuzzing with random bytes against the whole host and calling 4xx "findings": signal is 5xx, hangs, leaks, wrong answers only.
3. Oracles without a self-test (an oracle that cannot detect an injected bug gives false assurance).
4. Time-based flakiness: wall-clock sleeps instead of an injectable clock.
5. Unseeded randomness, unbounded runs, findings without a reproducer (seed + input).
6. Allowing 5xx or security findings as KnownGap; letting a nightly go red on already-filed gaps.
7. Reference results taken from Autheris itself (circular); expectations come from an independent reference DB.
8. Running any of this in normal CI.

## 6. Cross-cutting requirements

- REQ-SIM-01 Env-flag guard `AUTHERIS_SIM_RUN=1` (like `AUTHERIS_PARITY_RUN`); without it every simulation test is skipped/not discovered.
- REQ-SIM-02 Architecture guard test (like `ParityIsolationArchitectureTests`): simulation projects are referenced by no production project and by no normal CI test target; their workflows are only `schedule`/`workflow_dispatch`.
- REQ-SIM-03 Deterministic seeds: seed from `AUTHERIS_SIM_SEED` or date-derived nightly default, printed in every report; reuse `ParitySeed` conventions.
- REQ-SIM-04 Findings: each unique finding = KnownGap (strict xfail, so a fix turns the build red until the entry is removed) + draft GitHub issue text file; dedupe key = error class (status code + error code/exception type + normalised message), not input.
- REQ-SIM-05 Hard rule: a 5xx, hang, or policy/masking leak fails the job and cannot be registered as KnownGap; it is filed as a bug and the job stays red (or the finding is quarantined by the owner explicitly).
- REQ-SIM-06 Nightly job time budget under 60 min (`timeout-minutes: 60`); container, soak and dialect jobs weekly.
- REQ-SIM-07 Third-party content provenance: `THIRD-PARTY-NOTICES` entry per imported corpus (source, licence, commit/version, import date).
- REQ-SIM-08 Reports in the `parity-report.py` style (markdown summary + JSON baseline); no secrets or masked values in reports/artifacts.

## 7. Stage 1 requirements and acceptance (implemented in this lane)

### P1 Third-party SQL corpus into SqlCompilerGate / parity nightly
- REQ-SIM-10 Import sqllogictest statements (SELECT-only: select1..5, evidence/index-style groups) plus TPC-H-shaped and TPC-DS-shaped queries; expected results from a reference DB (SQLite via the existing gate databases; DuckDB/PostgreSQL where already available), never from Autheris.
- REQ-SIM-11 TPC content is not committed (B1): either generated at job time with dbgen/qgen fetched from the DuckDB extension into the runner temp dir, or original look-alike queries written by us. Default: original look-alike queries for committed corpus + optional job-time TPC generation behind `AUTHERIS_SIM_TPC=1`.
- REQ-SIM-12 Sampling: deterministic by seed (`sample = f(seed, statement id)`), size configurable, nightly budget respected.
- Acceptance
  1. At least 5000 new corpus statements available and wired into the SqlCompilerGate nightly (committed pool may be larger than a night's sample).
  2. Same seed gives same sample and same verdicts; seed printed.
  3. Each statement classified: equal, accepted difference, unsupported-by-design (documented), or finding.
  4. Findings registered as KnownGap with draft issue text (error class, minimal SQL, expected vs actual); no product code changed.
  5. No 5xx or policy finding is registered as a KnownGap (REQ-SIM-05).
  6. THIRD-PARTY-NOTICES updated; licence of every committed file recorded.
  7. Corpus load/sampling covered by normal-CI-safe unit tests of the loader only (no DB execution).

### P2 Off-the-shelf API fuzzers
- REQ-SIM-20 Dedicated workflow `sim-api-fuzz-nightly.yml`: container with Schemathesis (pinned version) against the test host, OpenAPI (REST/OData) and GraphQL schema; test-host auth token injected from the fixture; fixed seed (`--seed`), bounded examples/time.
- REQ-SIM-21 Checks enabled: no 5xx, schema conformance, response-not-leaking (assertion that responses never contain masked values/forbidden-tenant markers planted in the fixture data), no unhandled exception text/stack traces.
- REQ-SIM-22 Report deduplicated by (status code, error code); uploaded as artifact; summary in the job log.
- Acceptance
  1. Job runs nightly and on dispatch, finishes under 60 min, fails on any 5xx or leak.
  2. Re-running with the same seed reproduces the same cases (documented command per finding).
  3. Zero-5xx and no-leak assertions exist and have a self-test (planted bad response detected by the checker script).
  4. 4xx classes are listed but not failing; gaps via KnownGap list with issue text.
  5. Tool runs as an external container; nothing from Schemathesis is committed.
  6. Destructive/write endpoints excluded or run against a disposable fixture DB (open question O3).

### P3 Parser fuzzing (SharpFuzz on TrinoSqlEngine)
- REQ-SIM-30 Harness project `tests/Autheris.Tests.Fuzz` (console app, not a test target in normal CI) feeding input to the TrinoSqlEngine parse (and compile where side-effect-free) entry point; instrumented with SharpFuzz; AFL++ on Linux, out-of-process mode.
- REQ-SIM-31 Per-input time limit (default 2 s inside the harness, AFL `-t 5000`); crash = any unhandled exception other than the documented parse-error type, stack overflow, or OOM; hang = exceeds limit.
- REQ-SIM-32 Seed corpus from the SqlCompilerGate statements (P1) and Parity dialect cases; folder `tests/Autheris.Tests.Fuzz/corpus` (seeds) and `findings/` (crash/hang inputs, minimised); every committed finding is a regression test in the harness replay mode.
- Acceptance
  1. Harness builds on Linux; documented local run command.
  2. Nightly smoke run (about 5 min, fixed seed, deterministic corpus order) fails on any crash or hang; weekly longer run optional.
  3. Crash/hang corpus folder exists; replaying all inputs there is a fast check usable locally.
  4. Parser exceptions of the documented parse-error type are accepted; everything else is a finding (issue draft, never an allowed gap).
  5. Instrumentation applies only to the TrinoSqlEngine assemblies; no production build is instrumented.

## 8. Stage 2-4 requirements (planned, own PRD addenda before implementation)

Stage 2 (own generator)
- REQ-SIM-40 (P4) Schema/policy-aware generator in C# deriving one logical query model rendered to WebSQL, GraphQL and REST; seed-reproducible, shrinking to a minimal query. Library: CsCheck unless the spike shows a blocker (open question O4).
- REQ-SIM-41 (P5) Oracles: channel equality; TLP (ternary logic partitioning); NoREC; security invariants (no masked value or out-of-policy row in result, error text or metrics; unknown-vs-forbidden answer equality as in #320). Each oracle has a self-test that detects an injected bug (mutant) and fails if it does not.
- REQ-SIM-42 (P6) Policy variants (roles, tenants, consent, masking): stricter caller's result is a subset/masking of the broader caller's.
- Acceptance: same-seed reproducibility, shrink to under 10 tokens for injected bugs, oracle self-tests green, no allowed-gap path for security invariants.

Stage 3 (operation simulation, weekly)
- REQ-SIM-50 (P7) Soak with accelerated time: injectable clock (`TimeProvider`) in the test host for token, cache and consent expiry; mixed callers, schema reloads, consent switches; load via k6 (external, default) not NBomber (B3). Checks: memory/handle/thread leak trend, deadlock watchdog, wrong-answer oracle from P5.
- REQ-SIM-51 (P8) Fault injection with Toxiproxy (latency, reset, timeout, slow close) in front of source DBs and governance DB, plus clock jumps; expectation: 503/fail-closed, never wrong data, recovery after fault removal.

Stage 4 (weekly)
- REQ-SIM-60 (P9) More dialects (Oracle, SQL Server, DuckDB, PostgreSQL containers), skewed/large data, special characters, extreme timestamps.
- REQ-SIM-61 (P10, optional) Replay of BI query patterns (Metabase, Power BI, dbt) as hand-curated, anonymised pattern files.

## 9. Delivery and rollout

1. Stage 1 order: P1 (corpus + gate wiring), P3 (harness), P2 (workflow). Independent lanes possible; all share REQ-SIM-01..08 scaffolding (guard, seed, report, notices) delivered first.
2. Stage 2 starts after Stage 1 findings are triaged (generator reuses the corpus and fixtures).
3. Master plan row `REQ-SIMTEST` links this PRD; status updated per stage.

## 10. Open questions (recommended default)

1. O1 Exact licence grant of sqllogictest test files (runner is GPL/BSD/MIT/CC0 choice). Default: import only files after confirming the terms in the fossil repo; if unclear, generate equivalent statements with our own script from the documented grammar.
2. O2 TPC: commit nothing; job-time generation only (REQ-SIM-11). Default accepted unless owner wants TPC fidelity, which needs EULA legal review.
3. O3 Write endpoints in P2: Default: fuzz read paths and admin read endpoints; mutating endpoints only against a throwaway fixture host and excluded from the production-like profile.
4. O4 CsCheck vs FsCheck: Default CsCheck (Apache-2.0, C#-native, seed/shrink), decided in a one-day spike at Stage 2 start.
5. O5 Load tool: Default k6 as external binary (AGPL acceptable unmodified, not distributed). NBomber only if the owner buys a business subscription.
6. O6 JOB: Default out of scope (IMDB non-commercial data); synthetic skewed data in P9 instead.
7. O7 Nightly slot: Default stagger after parity (02:00) and SQL gate: P2 at 03:30, P3 smoke inside the SQL gate or its own job at 04:30, to keep the 60 min budget per job.
8. O8 Reference DB for P1: Default SQLite (already in gate) for sqllogictest; PostgreSQL container for TPC-shaped queries when Testcontainers is available, else DuckDB.
9. O9 Schemathesis version pin and update cadence. Default: pin exact version, update monthly via Dependabot-style manual bump.
