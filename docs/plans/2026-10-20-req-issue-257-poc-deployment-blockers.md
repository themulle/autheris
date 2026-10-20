# REQ-OPS-257: PoC Deployment Blockers Remediation (Serilog Guard Resiliency, Release Pipeline, Diagnostic Clarity & Schema Alignment)

**Document ID:** `REQ-OPS-257`  
**Date:** 2026-10-20  
**Status:** PRD APPROVED / READY FOR ARCHITECTURAL PLAN (Phase 1 Complete)  
**Author:** Principal Enterprise Product Manager & Technical Strategist  
**Issue Reference:** GitHub Issue #257  
**Parent Plan:** [00-master-plan-overview.md](00-master-plan-overview.md)  
**Target Delivery Window:** v1.2.6 Hotfix / Immediate Sprint  
**Classification:** Operational Resiliency, Observability & Release Infrastructure (P0 / Blocking)  

---

## 1. Executive Summary & Strategic Fit

During field trial evaluations and Proof-of-Concept (PoC) deployments of Autheris v1.2.x, operational and diagnostic blockers were identified that impede smooth adoption, container rollouts, and rapid troubleshooting:

1. **Configuration Resiliency (CrashLoopBackOff on Upgrades):** The startup configuration guard (`SerilogSectionGuard`) abruptly throws an unhandled `InvalidOperationException` if a legacy `"Serilog"` section is present in `appsettings.json` or Helm ConfigMaps. In customer Kubernetes clusters rolling forward from v1.2.4, this induces immediate container CrashLoopBackOff instead of permitting a graceful migration to OpenTelemetry logging.
2. **Release Pipeline Integrity (GHCR Tagging & Supply-Chain Signing):** The container build-and-publish workflow (`.github/workflows/docker-publish.yml`) must consistently publish semantic version tags (`v*.*.*`) to the GitHub Container Registry (`ghcr.io/themulle/autheris:vX.Y.Z`) with cryptographic Cosign keyless signatures, operating alongside branch tags (`main`).
3. **Diagnostic Clarity in Zero-Trust Policy Decision Point:** When a request is evaluated by `TableAccessPolicy`, if the Relationship-Based Access Control gate (`RebacTableGate.IsAllowedAsync`) succeeds (`Allowed`), but Consent Resolution denies access, the resulting denial reasons lack explicit attribution. Operators cannot readily determine which evaluation phase caused the denial without inspecting deep trace spans.
4. **Governance Database Schema Alignment (80 -> 83 Schema Elements):** The addition of the `CATALOG_STATE` table and `TABLE_RELATIONS` indexing introduced in #138/#252 expanded the verified schema footprint from 80 to 83 objects. The `--migrate-governance` CLI and documentation must guarantee deterministic schema alignment and re-seeding without data loss.

This PRD establishes the enterprise requirements, non-negotiable security boundaries, backward-compatibility allowances, and acceptance criteria to unblock all PoC deployments.

---

## 2. Verified Current State & Gap Analysis

| Component | Current Implementation | Observed Failure / Limitation | Target Requirement |
|---|---|---|---|
| **Logging Configuration Guard** | `src/Autheris.Api/Configuration/SerilogSectionGuard.cs:16-22` unconditionally executes `throw new InvalidOperationException(...)` when the `"Serilog"` section exists and has children. | Upgrading pods with legacy ConfigMaps crash instantly before OpenTelemetry initialization can log structured diagnostic messages. | Soften hard crash to a high-visibility deprecation warning; support environment override `AUTHERIS_ALLOW_LEGACY_CONFIG=true` while strictly preserving `SEC-OT06-14` header guards. |
| **Container Release CI/CD** | `.github/workflows/docker-publish.yml:111-145` computes tags conditionally based on `REF_TYPE` and `EVENT_NAME`. | Tags must guarantee that `ghcr.io/themulle/autheris:vX.Y.Z` is signed via Cosign keyless OIDC, matching the strict identity regex. | Verify release tag push triggers `ghcr.io/themulle/autheris:vX.Y.Z` and `latest` generation, signed by digest, alongside `main` branch pushes. |
| **Zero-Trust Policy Diagnostics** | `src/Autheris.Application/Policy/TableAccessPolicy.cs:266-287` evaluates `IsRebacDeniedAsync`, then proceeds to `ResolveConsentAsync`. Denied reasons are forwarded as raw strings from `ConsentResolutionService`. | When ReBAC is Allowed and Consent is Denied, operators and audit logs cannot distinguish whether the rejection was ReBAC-level or Consent-level. | Augment `TableAccessDecision.DeniedReasons` with explicit diagnostic attribution: `(ReBAC: Allowed, Consent: Denied - Zero-Trust requires an active ALLOW consent for subject or subject's groups)`. |
| **Database Migration & Seeding** | Baseline migrations and shape tests in `tests/Autheris.Tests.Unit/Governance/GovernanceSchemaShapeSnapshotTests.cs:118-132` require `CATALOG_STATE` and `TABLE_RELATIONS` indexes (`IX_TABLE_RELATIONS_PARENT`, `IX_TABLE_RELATIONS_CHILD`). | Transition from 80 to 83 database schema elements requires documented verification in `--migrate-governance` and master plan tracking. | Document schema evolution (80->83), ensure `--migrate-governance` re-seeding operates idempotently across SQLite, PostgreSQL, and SQL Server. |

---

## 3. Benchmark & Gatekeeper Protocol (Anti-Patterns & Alternatives)

### 3.1 Rejected Anti-Patterns
1. **Silently ignoring forbidden credentials in config (`Gateway:Telemetry:Otlp:Headers`):**
   - *Rejected:* Weakening the security guard for OTLP headers would violate `SEC-OT06-14` and permit sensitive bearer tokens or API keys to be committed into plain `appsettings.json`.
   - *Approved Alternative:* Keep `Gateway:Telemetry:Otlp:Headers` guard strictly fatal (`throw InvalidOperationException`), but soften only the obsolete format section (`Serilog`).
2. **Bypassing Consent when ReBAC is Allowed:**
   - *Rejected:* Treating ReBAC as an authorization bypass for Consent violates NIST SP 800-207 Zero-Trust and GDPR Art. 9/15. Both layers must grant consent independently (fail-closed intersection).
   - *Approved Alternative:* Maintain fail-closed consent evaluation, but enrich the diagnostic `DeniedReasons` string so troubleshooting is self-explanatory.
3. **Implicit Container Tagging without Cryptographic Provenance:**
   - *Rejected:* Pushing unverified or unsigned tags to GHCR breaks SLSA provenance Level 3 requirements and SEC Rule 17a-4 compliance.
   - *Approved Alternative:* Enforce Cosign keyless OIDC signing by digest for both semantic release tags and main branch commits.

---

## 4. Functional & Technical Requirements

### 4.1 AC-1: Configuration Resiliency (`SerilogSectionGuard`)
- **REQ-OPS-257-1.1 (Graceful Deprecation Warning):**
  In `src/Autheris.Api/Configuration/SerilogSectionGuard.cs`, when a `"Serilog"` configuration section is detected, the guard MUST NOT terminate the process with an unhandled `InvalidOperationException` by default if legacy configuration tolerance is active.
- **REQ-OPS-257-1.2 (Environment Variable Override):**
  Support `AUTHERIS_ALLOW_LEGACY_CONFIG=true` (case-insensitive) or default graceful deprecation mode. When enabled, the guard emits a high-visibility warning to standard error / console:
  `"[DEPRECATION WARNING] The 'Serilog' configuration section is obsolete and ignored (replaced by OpenTelemetry in BL-06). Please migrate logging configuration to 'Logging:LogLevel' and 'Gateway:Telemetry:Otlp'."`
- **REQ-OPS-257-1.3 (Non-Negotiable AppSec Guard SEC-OT06-14):**
  The guard for `Gateway:Telemetry:Otlp:Headers` MUST remain strictly enforced. If `Gateway:Telemetry:Otlp:Headers` contains non-empty text or child keys in configuration files, it MUST throw `InvalidOperationException` immediately to prevent credential leaks in version-controlled config.

### 4.2 AC-2: Release Pipeline GHCR Tagging & Cosign Verification
- **REQ-OPS-257-2.1 (Semantic Tag Ingestion):**
  In `.github/workflows/docker-publish.yml`, when a release tag matching `v*.*.*` is pushed, the workflow MUST build and push:
  - `ghcr.io/themulle/autheris:vX.Y.Z` (exact release version)
  - `ghcr.io/themulle/autheris:latest`
  - `ghcr.io/themulle/autheris:getting-started`
- **REQ-OPS-257-2.2 (Main Branch Protection):**
  When pushing to the `main` branch, the workflow MUST publish `ghcr.io/themulle/autheris:main` and `ghcr.io/themulle/autheris:sha-<COMMIT_SHA>`, strictly excluding the `latest` tag (enforcing `SC-01`).
- **REQ-OPS-257-2.3 (Keyless Cosign Signing):**
  Every pushed image digest MUST be signed via `cosign sign --yes "${IMAGE,,}@${DIGEST}"` and verified with the OIDC identity regex matching `^https://github\.com/themulle/autheris/\.github/workflows/docker-publish\.yml@refs/(heads/main|tags/v.+)$`.

### 4.3 AC-3: Diagnostic Clarity in Policy Decision Point
- **REQ-OPS-257-3.1 (Diagnostic Attribution):**
  In `src/Autheris.Application/Policy/TableAccessPolicy.cs` (lines 266–287), when:
  1. `IsRebacDeniedAsync` evaluates to `false` (i.e. ReBAC allows access), AND
  2. Consent resolution (`ResolveConsentAsync`) evaluates to Denied (`!decision.IsAllowed`),
  the returned `TableAccessDecision.DeniedReasons` MUST contain the explicit diagnostic note:
  `"(ReBAC: Allowed, Consent: Denied - Zero-Trust requires an active ALLOW consent for subject or subject's groups)"`.
- **REQ-OPS-257-3.2 (Preservation of Existing Reason Codes):**
  Existing specific denial reasons from `ConsentResolutionService` (e.g. `"Zero Trust: No active ALLOW consent granted to subject."`, `"Hard DENY: Active table-level DENY applies to subject."`) MUST be preserved alongside the diagnostic attribution note.

### 4.4 AC-4: Governance Database Schema Evolution & `--migrate-governance`
- **REQ-OPS-257-4.1 (80 -> 83 Schema Elements Coverage):**
  The schema evolution from 80 elements to 83 elements comprises:
  1. Addition of table `CATALOG_STATE` (columns: `id`, `structure_version`, `content_version`, `updated_at`).
  2. Addition of index `IX_TABLE_RELATIONS_PARENT` on `TABLE_RELATIONS(parent_table_id)`.
  3. Addition of index `IX_TABLE_RELATIONS_CHILD` on `TABLE_RELATIONS(child_table_id)`.
- **REQ-OPS-257-4.2 (`--migrate-governance` Idempotence):**
  The `--migrate-governance` CLI command executed via `Autheris.Api --migrate-governance` MUST idempotently apply the 83 schema objects and initial seeds (`CATALOG_STATE` row id=1) across SQLite, PostgreSQL, and SQL Server without failing if already present.

---

## 5. Acceptance Criteria & Test Plan

| ID | Category | Scenario | Expected Outcome |
|---|---|---|---|
| **AC-257-01** | Configuration | `appsettings.json` contains a populated `"Serilog"` section and `AUTHERIS_ALLOW_LEGACY_CONFIG=true`. | Application boots successfully, logs deprecation warning to console, no exception thrown. |
| **AC-257-02** | AppSec | `appsettings.json` contains `"Gateway:Telemetry:Otlp:Headers"` configuration. | Application fails startup with `InvalidOperationException` (SEC-OT06-14 preserved). |
| **AC-257-03** | CI Pipeline | Git tag `v1.2.6` pushed to repository. | Docker workflow builds image, tags as `ghcr.io/themulle/autheris:v1.2.6` and `latest`, signs digest with Cosign, verification passes. |
| **AC-257-04** | Diagnostic | Principal has ReBAC relation granting access, but no active consent in `CONSENTS` table. | `DecideAsync` returns `IsAllowed=false`, and `decision.DeniedReasons` includes `"(ReBAC: Allowed, Consent: Denied - Zero-Trust requires an active ALLOW consent for subject or subject's groups)"`. |
| **AC-257-05** | Migration | `Autheris.Api --migrate-governance` executed against clean or v1.1 database. | Database exits code 0; `CATALOG_STATE` and `TABLE_RELATIONS` indexes verified in shape tests. |

---

## 6. Implementation Dependencies & Timeline

- **Phase 1 (Product Management):** PRD Approved (`REQ-OPS-257`).
- **Phase 2 (Solution Architect):** Architectural plan authoring (`2026-10-20-plan-issue-257-poc-deployment-blockers.md`).
- **Phase 3 (AppSec Review):** Security sign-off on config guard softening and Cosign workflow integrity.
- **Phase 4 (C# TDD):** Code changes in `SerilogSectionGuard.cs`, `TableAccessPolicy.cs`, `.github/workflows/docker-publish.yml`, unit tests in `Autheris.Tests.Unit`.
- **Phase 5 (Code Review):** Verification against regression test suite.
