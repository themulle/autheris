# Lines-of-Code Estimates: Issue #107, BL-06, Issue #99

**Date:** 2026-10-16
**Purpose:** Owner-requested size estimate per work package before Phase 4 implementation.
**Parent Plan:** [00-master-plan-overview.md](00-master-plan-overview.md), backlog in [2026-10-16-backlog-intake.md](2026-10-16-backlog-intake.md)

Plans estimated (each lives on its feature branch):

| Feature | Plan | Branch |
|---|---|---|
| Issue #107 row-filter key-set cache | [PLAN-GOV-107](https://github.com/themulle/autheris/blob/feat/issue-107-rowfilter-keyset-cache/docs/plans/2026-10-15-plan-issue-107-row-filter-key-set-cache.md) | `feat/issue-107-rowfilter-keyset-cache` |
| BL-06 OpenTelemetry | [PLAN-OPS-BL06](https://github.com/themulle/autheris/blob/feat/bl-06-open-telemetry/docs/plans/2026-10-16-plan-bl-06-open-telemetry.md) | `feat/bl-06-open-telemetry` |
| Issue #99 CDC fan-out | [PLAN-CDC-99](https://github.com/themulle/autheris/blob/feat/issue-99-cdc-fanout/docs/plans/2026-10-15-plan-issue-99-cdc-fanout-and-hardening.md) | `feat/issue-99-cdc-fanout` |

---

## 1. Method

- **Scope per WP** taken from each plan's file/ownership list, fixed contracts (signatures already shown), mandatory acceptance tests and mandatory security tests (`SEC-RF107-*`, `SEC-OT06-*`, `ST-01..ST-15`).
- **Change size of existing files** judged by reading them (current line counts, number of call sites to migrate, e.g. 8 prometheus-net files with ~44 counter lines, 41 `SetTag`/`SetSafeTag` sites, 3 `LogGeneratedSql` sites, Serilog in 5 files).
- **Calibration** against comparable code in the repository:
  - `VirtualFilterKeySetCache` (106 lines) + `SqlVirtualFilterKeySetProvider` (306) with tests 118 + 311; virtual-filter commits `ffe2c26c` (src +505 / tests +611) and `bddc0f7a` (src +616 -627 / tests +738): security-relevant features in this codebase run at roughly **1.2-1.5 test lines per production line**.
  - Per-engine governance repository partials (`*GovernanceRepository.CloudEventSubscriptions.cs` 130-136 lines, `.WebhookOutbox.cs` 172-184, `.Consent.cs` ~1,600) and the existing `CatalogKeyDefinitionContractTestsBase` (314).
  - `MssqlChangeTrackingPoller` (414), `StreamRlsPolicyEnforcer` (375), `ClusterScheduledService` (245).
  - **Measured in-flight work** for issue #99: the worktrees `feat/issue-99-i1-a|b|c` (committed + uncommitted + untracked, 2026-10-16) already hold ~4,980 production and ~2,935 test lines. These measured values anchor the I1 estimates and the I2 extrapolation.
- **Counting rules:** physical lines incl. comments/XML docs and blank lines, C# only unless noted. "Changed/deleted" = existing lines modified or removed (a rewritten line counts once). Generated artefacts are **excluded** and listed separately: `governance-shape.*.json` snapshots, `packages.lock.json`, golden output files. YAML/Helm/collector configuration is reported separately.
- **Ranges** express uncertainty; totals are the sum of the low and high ends (the realistic outcome is usually in the middle third).
- Confidence: **high** = mostly measured or mechanically derivable; **medium** = design fixed, size depends on edge-case count; **low** = algorithmic core whose size depends on findings during TDD.

---

## 2. Issue #107 - Row-Filter Key-Set Cache

Already committed (contracts `0ef745b0`, not included below): 743 lines (+3 -3) in 27 files, of which ~160 shape JSON and 102 test lines.

| WP | Prod LOC added | Prod LOC changed/deleted | Test LOC | Prod files new / changed | Test files new / changed | Confidence | Main assumptions |
|---|---|---|---|---|---|---|---|
| **WP-A** Settings, persistence, admin surface | 1,350-1,850 | 60-120 | 1,450-2,000 | 3-4 / 16-20 | 9-11 / 2-3 | medium | Resolver/validator fill existing stubs (~80 + ~90); admin service ~180; 3 engines x (Consent read/write of 3 columns + `GetRowFilterAsync` + `UpdateRowFilterEvaluationAsync` with epoch bump, ~120-160) and (Catalog load + `SetRowFilterEvaluationAsync`, ~90-120); MCP plan/apply incl. `PlanKind` + optimistic concurrency + TOTP ~200-300; REST partial ~150-200 + catalog routes ~60-90. Tests: 27-case resolver matrix data-driven, repository contract base + 3 subclasses (~400-500), endpoint auth/400/404 (~250-350), MCP plan tests. DDL already in contracts. |
| **WP-B** Engine key-query compiler, site registry, substitution | 800-1,150 | 40-80 | 1,350-1,950 | 1-3 / 7-9 | 4 / 0-1 | medium-low | `PolicyKeyQueryCompiler` 450-700 (A-3 derivation rules, tenant rewriter reuse, dialect row-limit syntax, coverage proof, `CompilerHooks` for mutation tests) is the main uncertainty; substitution (polarity, padding, Oracle chunking, reference identity) 250-350; registry ~40; builder/Casbin registration 20-60 changed. Tests: golden key SQL per dialect + one `NotDerivable` test per A-3 rule + 4 mutation tests (600-900), substitution (400-550), golden statements x3 dialects (300-450), architecture test. |
| **WP-C** Runtime: cache, loader, stage, integration, epochs, diagnostics | 1,700-2,400 | 120-220 | 2,600-3,800 | 9-11 / 11-13 | 12-14 / 1-2 | medium-low | Stage 400-550 (13 fallback reasons, age re-check, tenant cross-check), loader 250-350 (per-engine isolation SEC-RF107-12, timeout, `MaxKeys + 1`), value converter 200-300 (type-pair allowlist), cache key 120-160 (length-prefixed SHA-256), single-flight cache 150-200 (+ VF wrapper refactor), authoritative epochs 80-120, factory/rewriter/executor integration 200-300, DI + diagnostics 60-100. Tests dominated by `RowFilterKeySetStageTests` (900-1,300, all `SEC-RF107` stage cases), plus parity variant over 11 channels and SQL Server/PostgreSQL Testcontainers (550-850), benchmark 80-120. |
| **Total #107 (remaining)** | **3,850-5,400** | **220-420** | **5,400-7,750** | **13-18 / 34-42** | **25-29 / 3-6** | medium | Generated: none expected beyond the committed shape JSON. |

---

## 3. BL-06 - OpenTelemetry (incl. Serilog removal and prometheus-net migration)

| WP | Prod LOC added | Prod LOC changed/deleted | Test LOC | Prod files new / changed / deleted | Test files new / changed | Confidence | Main assumptions |
|---|---|---|---|---|---|---|---|
| **WP-0** Contracts + small pure types | 1,250-1,700 | 0-10 | 900-1,250 | 13-15 / 0-1 / 0 | 6-7 / 0 | medium-high | `TelemetryOptions` with 7 nested option classes + validator (350-460), attribute/span/metric catalogs (330-480), `SqlTextScrubber` dialect lexer (250-350, real implementation), exception policy, names, tenant context, policy contract + `ClampTo`, `SourceCommandTelemetry` stub. Tests incl. FsCheck scrubber property. |
| **WP-A1** Composition root, ingress/egress, correlation | 950-1,350 | 100-150 | 1,200-1,700 | 11 / 10-12 / 1 | 9-10 / 0-1 | medium | `GatewayTelemetryServiceExtensions` 250-350, sampler, propagator (validation of `traceparent`/`tracestate`), span redaction + tenant enrichment processors, OTLP HTTP client factory (mTLS, no redirect), outbound propagation policy, `TraceIdentifierMiddleware`. Deletes `OpenTelemetryTracingMiddleware` (53) and the OTel block in `GatewayCoreServiceExtensions` (~15). Tests: `TelemetryTestHost` harness (200-300) + AC-1/2/3/7/8 tests. |
| **WP-A2** Logs (Serilog removal) | 450-650 | 90-130 | 650-900 | 5 / 7-8 / 1 | 3-4 / 1 | medium | `JsonStdoutLogExporter` 150-220, `LogRedactionProcessor` 180-250, `RedactedException`, `SerilogSectionGuard`, bootstrap JSON line. Deletes `SensitiveLogPropertyEnricher` (52), Serilog lines in `Program.cs`, csproj, `appsettings.json` (12), `LogGeneratedSql` option. DEP-8 test (78) rewritten. |
| **WP-A3** Deployment | 0 C# (300-500 YAML/config) | 30-60 YAML | 50-100 (Helm template checks) | 1-2 / 5 / 0 | 1 / 0 | medium-low | Collector example (agent + gateway, tail sampling, redaction, routing) 150-250, Helm values/deployment/configmap 100-170, podman-compose profile 30-50, CI validate step. |
| **WP-B1** Per-source policy hook | 150-220 | 0 | 200-300 | 3 / 0 / 0 | 1-2 / 0 | high | Global provider, `DataSourceLoggers`, DI extension; FsCheck on `ClampTo`. |
| **WP-B2** Existing spans cleanup | 20-40 | 80-150 | 150-250 | 0 / 7 / 0 | 1 / 2-4 | medium-high | 41 `SetTag`/`SetSafeTag` call sites in 7 files moved to the catalog; MCP drops session id/principal; compiler source rename. |
| **WP-B3** Data-path spans | 700-1,000 | 80-150 | 1,050-1,500 | 3-4 / 18-22 / 0 | 6-8 / 0-2 | medium-low | `SourceCommandTelemetry` full (200-280, zero-alloc path), decision/governance-store telemetry, GraphQL listener; scopes at ~12 executor call sites (150-250), 3 repositories + SQLite provider, audit append, 3 `LogGeneratedSql` sites. Tests incl. architecture test "every `Execute*Async` in a scope" and allocation test. |
| **WP-C1** Metrics + prometheus-net migration | 400-650 | 150-250 (+30-50 YAML) | 500-800 | 2-3 / 12-14 / 0 | 3-4 / 4-8 | medium | Rename 32 `GatewayDiagnostics` instruments, delete `SetSafeTag`; migrate 8 prometheus-net files (~44 counter lines); new catalog instruments 150-250, `TelemetryViews`, `AsyncJobDiagnostics`, cluster-scope gauges with lease rule, meter adapter; drop `prometheus-net` from Infrastructure; `prometheusrule.yaml`. Golden `/metrics` names file excluded. |
| **WP-C2** Exporter resilience + benchmark | 180-280 | 0 | 200-300 | 2-3 / 0-1 / 0 | 1 / 0 | medium | Dropped-telemetry counter (60-100), `TelemetryOverheadBenchmark` (120-180, counted as production-side code). |
| **WP-C3** Async propagation | 300-450 | 60-120 | 500-750 | 0-1 / 18-20 / 0 | 3-4 / 2 | medium | Job origin + worker span, `WEBHOOK_OUTBOX.trace_parent` (3 baselines + 3 repository partials), dispatcher/delivery span + link, `PropagateTraceContext`, Redis event bus envelope with legacy read, CDC poll-cycle span (depends on #99 merge; may move to a follow-up). Shape JSON excluded. |
| **WP-INT** Canary + end-to-end (Stream B lead) | 0-50 | 0 | 850-1,350 | 0 / 0-2 / 0 | 5-8 / 0 | low-medium | Shared canary harness over 7 channels + driver-error canary (500-800), OTLP collector container tests gRPC + HTTP/protobuf (200-300), identity-leak tests. |
| **Total BL-06** | **4,400-6,390** | **560-960** | **6,250-9,200** | **~40-45 / ~75-90 / 2** | **~39-50 / ~10-18** | medium | Plus 330-560 YAML/config lines. Outright deletions in production ~250-350 lines (2 files deleted, Serilog wiring, OTel block, `SetSafeTag`, prometheus-net counter declarations, `UseHttpMetrics`, `LogGeneratedSql` option); lock-file churn excluded. |

Stream view: **WP-0** 1,250-1,700 / tests 900-1,250; **Stream A** (A1-A3) 1,400-2,000 C# + 330-560 YAML / tests 1,900-2,700; **Stream B** (B1-B3 + INT) 870-1,310 / tests 2,250-3,400; **Stream C** (C1-C3) 880-1,380 / tests 1,200-1,850.

---

## 4. Issue #99 - CDC Fan-Out and Change-Tracking Hardening

Already committed (I1-0 `3f69d9db`, not included below): 1,821 lines in 22 files (of which ~825 shape JSON and 165 test lines).
**Measured in-flight state (2026-10-16):** I1-A src 1,477 / tests 957; I1-B src +1,540 -563 / tests 251; I1-C src 1,962 / tests 1,557. The I1 rows below are **final** sizes (written + remaining).

| WP | Prod LOC added | Prod LOC changed/deleted | Test LOC | Prod files new / changed / deleted | Test files new / changed | Confidence | Main assumptions |
|---|---|---|---|---|---|---|---|
| **I1-A** Stream/key persistence | 1,500-1,650 | 20-50 | 1,000-1,200 | 9-10 / 3-5 / 0 | 6 / 1-2 | high | Shared `CdcStreamSqlCore` (647) + 6 thin engine partials + in-memory store + append validation exist; remaining: read-only watermark store, DI, interface cleanup. |
| **I1-B** Producer | 1,600-1,900 | 560-650 | 1,700-2,300 | 8 / 4-6 / 2 | 7-9 / 1-2 | medium-high | Production code largely written (poller rewrite, activator, event builder, producer, partitioned service); `MssqlChangeTrackingHostedService` (186) and `InMemoryMssqlWatermarkStore` (50) deleted. Remaining mostly tests: activator per code, builder, producer, `SqlServerChangeTrackingDatabase` helper + least-privilege CT integration tests (600-850), architecture allowlist update. |
| **I1-C** Crypto, stream services, compat, admin | 2,150-2,450 | 150-300 | 1,850-2,200 | 11-12 / 5-6 / 0 | 8-10 / 0-1 | medium-high | Crypto, keyring, codec, retention, signal, tail hub, KEK provider, admin endpoints exist. Remaining: compat feed in `Subscription.cs`, `CdcSubscriptionGovernor`, `CdcWebhookDeliveryService`, startup check, DI, their tests. |
| **I1-E** End-to-end | 30-120 | 10-40 | 500-800 | 0 / 1-3 / 0 | 2-4 / 0-1 | medium | Failover with overlapping leases on real governance DBs (ST-07), AC-2 counting decorator, AC-14; wiring fixes only. |
| **I2-0** Contracts | 220-320 | 10-30 | 80-150 | 6 / 2-3 / 0 | 1 / 0 | high | Records/enums/interfaces of 3.3 (I2), options `IdentityMaxAgeDays`, `MaxConcurrentCatchUps`, audit constants already present. Shape JSON for I2 tables (~600) excluded. |
| **I2-A** Consumer framework | 1,900-2,600 | 80-150 | 2,000-2,650 | 14-16 / 8-10 / 0 | 8-10 / 1-2 | medium | Extrapolated from I1-A: DDL 3 tables + 4 columns x3 engines (150-180), consumer store core + engine partials + in-memory incl. `CommitWithOutboxAsync` (850-1,200), consumer service/runner (500-700: lease, start positions, gap, backpressure, backoff, dead letters, failed state), metrics, consumer admin endpoints + service (300-450). Tests: store contract base (~800) + subclasses, runner (800-1,100 incl. ST-11 property test), endpoints. |
| **I2-B** Governed delivery | 1,100-1,600 | 200-350 | 1,650-2,300 | 8 / 2-3 / 0 | 8 / 1-2 | medium | Enforcer split (resolver 250-350, context 120-180; `StreamRlsPolicyEnforcer` 375 lines re-implemented on the parts), transition evaluator, delete-key renderer, change authorizer (200-300), identity resolver, delivery gate, audit batcher. Unit-heavy, Stryker targets; case-insensitive tenant tests updated (SEC-CDC99-03). |
| **I2-C** Receivers | 1,000-1,450 | 250-450 | 1,350-1,900 | 2-3 / 10-12 / 0 | 6-7 / 1-2 | medium-low | Webhook registration rules (D-B4/D-B5, SEC-CDC99-02), webhook receiver handler, send-time gate + delete dead-lettering in 3 repositories, removal of the MSSQL path in `CdcWebhookDeliveryService`, GraphQL `fromOffset`/catch-up reader/overflow/new fields, hub authorise-before-enqueue, `cdc_all`. E2E fan-out tests. |
| **I3-A** Plugin contract & binding | 450-700 | 30-60 | 580-830 | 4 / 4-5 / 0 | 4-5 / 0-1 | medium | `GovernedCdcChange`, receiver options + validator, `PluginManager` bus exclusion + `TryGetCdcListener` (type-identity check), binder with D-B5 checks and audit, architecture tests. |
| **I3-B** Dispatcher & fixture plugin | 200-300 | 10-30 | 780-1,170 | 1 / 1-2 / 0 | 7-9 / 0-1 | medium | Handler with timeout and abandoned-handler cap; fixture plugin project (4 listeners + csproj, counted as test code), trust-list fixture, AC-1 (three kinds) and AC-10 integration. |
| **Total #99 (I1-A..I3-B)** | **10,150-13,090** | **1,320-2,110** | **11,490-15,500** | **~63-68 / ~40-55 / 2** | **~57-69 / ~5-13** | medium | Of this, ~4,980 production and ~2,935 test lines already exist in the I1 worktrees; **remaining ~5,200-8,100 production, ~8,550-12,550 test**. |

Increment view: **I1** (A, B, C, E) 5,280-6,120 / tests 5,050-6,500; **I2** (0, A, B, C) 4,220-5,970 / tests 5,080-7,000; **I3** 650-1,000 / tests 1,360-2,000.

---

## 5. Summary

| Feature | Prod LOC added | Prod LOC changed/deleted | Test LOC | Main risk to the estimate |
|---|---|---|---|---|
| #107 (WP-A..C, remaining) | 3,850-5,400 | 220-420 | 5,400-7,750 | Size of the key-query compiler (A-3 rules) and of the stage fallback matrix. |
| BL-06 (WP-0, Streams A-C, WP-INT) | 4,400-6,390 (+330-560 YAML) | 560-960 | 6,250-9,200 | Number of data-path executor sites and the canary harness breadth; CDC span part coupled to #99. |
| #99 (I1-A..I3-B) | 10,150-13,090 | 1,320-2,110 | 11,490-15,500 | I2 consumer runner and GraphQL catch-up semantics; I1 is largely measured. |
| **All three** | **18,400-24,880** | **2,100-3,490** | **23,140-32,450** | |

Generated artefacts excluded throughout (shape JSON, lock files, golden output files).
