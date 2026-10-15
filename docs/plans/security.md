# Security Review, Hardening & Compliance Remediation Roadmap

**Document ID:** `PLAN-SECURITY-REVIEW`  
**Location:** `docs/plans/security.md`  
**Status:** WAVES A, B & C (Low) COMPLETED ✅ · DEPLOYMENT REMAINDERS OPEN 📋  
**Last Updated:** 2026-10-14 · **Baseline:** `main` at `a4137626`  
**Parent Plan:** [`00-master-plan-overview.md`](00-master-plan-overview.md)  
**Implementation Plan:** [`implementation/impl-security-wave-c-2.md`](implementation/impl-security-wave-c-2.md); global order in [`implementation/00-implementation-roadmap.md`](implementation/00-implementation-roadmap.md)  
**Role:** Application Security Engineer (`csharp-security-expert`)  
**Scope:** Full-solution security review covering all seven production assemblies (`Autheris.Api`, `Autheris.Application`, `Autheris.Domain`, `Autheris.Infrastructure`, `Autheris.Extensions`, `Autheris.GraphQL`, `TrinoSqlEngine`), deployment manifests, and supply chain.

---

## 1. Status Overview

| Severity | Findings | Status |
|:---|:---:|:---|
| **Critical** | 0 | None identified. |
| **High** | 6 (`SR-H-01`..`06`) | Fixed and verified (PR #73). |
| **Medium** | 26 (`SR-M-01`..`26`) | Fixed and verified (AST X1 consolidation). |
| **Low** | 24 (`SR-L-01`..`24`) | Fixed and verified (implementation Waves 1–3, PRs #78, #81, #82). |
| **Deployment** | 9 (`SR-D-01`..`09`) | `SR-D-01`..`05` fixed; `SR-D-06`..`09` partially resolved, remainders open (section 2). |

Finding texts and the delivered remediation are in git history (`git show 317e65b8:docs/plans/security.md`).

---

## 2. Open Remediation Backlog

Details, file/line evidence and design in [`implementation/impl-security-wave-c-2.md`](implementation/impl-security-wave-c-2.md).

| Finding ID | Area | Open Remainder |
|:---|:---|:---|
| **SR-D-06** | `deploy/helm`, `GatewayApplicationBuilderExtensions.cs` | Helm ServiceMonitor auth is done. Open: dedicated metrics-scrape policy on the gateway (`MapMetrics()` uses only a generic `RequireAuthorization()`); wire the required production settings in `templates/configmap.yaml`. |
| **SR-D-07** | Build | `AutherisIncludeTestAuth` defaults to `false`. Open: a test asserting that a Release publish contains no TestAuth handler; drop the redundant workflow flag. |
| **SR-D-08** | `deploy/` benchmark stack | Proxy runs non-root with `cap_drop`. Open: `TrustServerCertificate=True` in `podman-compose.yaml` and `appsettings.Benchmark.json`; proxy service not `read_only`. |
| **SR-D-09** | Images, `Directory.Build.props` | SDK pinned via `global.json`. Open: patch-level image tags next to the digests; `NuGetAuditLevel` still `moderate` (decision D5). |
| Follow-up | Webhooks, events | Remove the deprecated legacy `X-Autheris-Signature` header (`EmitLegacySignature`) and `DocumentDepositedEvent.SourceFilePath` (no production deployment yet). |
| **COMPLETED ✅** | SQL compiler gate | All 18 entries (57 unique statements) in `tests/Autheris.Tests.SqlCompilerGate/Corpus/accepted-differences.json` audited, documented with concrete security justifications, and signed off (`approved`, see [`2026-10-15-plan-sqlgate-cutover-and-accepted-differences.md`](2026-10-15-plan-sqlgate-cutover-and-accepted-differences.md)). |
| Follow-up | Error hygiene | ~25 endpoint `ex.Message` sites remain on the allow-list of the raw-exception guard test; retired by `WP-ARC-C-05` (ProblemDetails). |

---

## 3. Architectural Security Strengths Preserved

- **Deny-by-Default Authorization:** Casbin ABAC engine enforces deny-first evaluation with immutable rule snapshots and fail-closed error handling.
- **AST-Level Pushdown:** Row-level security predicates and column masks are compiled directly into database ASTs; masked columns in aggregates, `GROUP BY` and whole-row references are rejected.
- **Egress & SSRF Hardening:** `SecureOutboundHttp` pins destination IP addresses in `ConnectCallback`, disallows redirects to private networks, and blocks metadata endpoints.
- **Plugin Sandbox:** Dynamically loaded extensions are verified against cryptographic SHA-256 hashes and isolated in collectible `AssemblyLoadContext` instances.
- **Cryptographic WORM Audit:** Immutable audit trail with HKDF-derived keys, SHA-256 chaining, monotonic anchors and a fail-closed chain-violation latch.
