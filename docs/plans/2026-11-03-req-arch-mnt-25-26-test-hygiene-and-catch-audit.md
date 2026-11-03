# PRD: Test File Naming, Shared Fakes Consolidation & Catch-Site Security Audit (ARCH-MNT-25 & ARCH-MNT-26)

**Document ID:** `REQ-ARCH-MNT-25-26`  
**Date:** November 3, 2026  
**Status:** APPROVED  
**Parent Plan:** [`docs/plans/2026-10-26-plan-architecture-maintainability.md`](2026-10-26-plan-architecture-maintainability.md) (`ARCH-MNT-25`, `ARCH-MNT-26`)  
**Origin Plan:** [`docs/plans/00-master-plan-overview.md`](00-master-plan-overview.md) (Track 11)

---

## 1. Executive Summary & Problem Statement

In the Architecture & Maintainability backlog (`ARCH-MNT`), two closely related quality and hygiene tracks remain open:

1. **ARCH-MNT-25 (Test File Naming & Shared Fakes Consolidation — REV-10):**
   - **Temporal/Round-Based Test File Naming:** 26 test files across `Autheris.Tests.Unit`, `Autheris.Extensions.Tests`, and `TrinoSqlEngine.Tests` are named after point-in-time review rounds, delivery waves, or issue increments (`SecurityReview20261002*`, `Round4*`, `RepeatedReviewRound*`, `Wave1*`..`Wave4*`, `*Inc1*`, `*Inc2*`) rather than the domain capabilities or security boundaries they verify.
   - **Duplicated Test Fakes:** Multiple test files redefine identical private test doubles (`StubEpochValidationService` in 9 files, `TestOptionsMonitor<T>` in 7 files, `DelegatingHandlerStub` / `RecordingHandler` / `StubHandler` in 10 files, and `CapturingLogger` / `RecordingLogger` in 7 files) even though [`tests/Autheris.Tests.Common/`](../../tests/Autheris.Tests.Common/) already provides shared infrastructure.

2. **ARCH-MNT-26 (Catch-Site Audit & Hardening — REV-07):**
   - Across `src/`, `catch` blocks are used for fail-closed policy enforcement, input parsing, resource disposal, and telemetry resilience. A systematic audit of all silent or fallback `catch` sites across `src/` is required to verify that no security-critical path fails open, leaks sensitive exception details, or swallows `OperationCanceledException` / unexpected exceptions without diagnostics.

---

## 2. Scope & Requirements

### 2.1 Functional & Maintainability Requirements (`ARCH-MNT-25`)
* **REQ-MNT25-01 (Feature-Descriptive Test File & Class Names):** Rename all 26 round-, date-, wave-, and increment-named test files and their corresponding test classes to clear, capability-descriptive names using `git mv` to preserve history.
* **REQ-MNT25-02 (Preserve Domain-Named `Robin100*` Suites):** Keep `Robin100*` architecture/security test classes in `Autheris.Tests.Architecture` unchanged, as "Robin 100" is the canonical domain name of the 100-scenario parity persona suite (`Autheris.Tests.Parity/Robin`) and is referenced by self-scanning architecture tests.
* **REQ-MNT25-03 (Traceability & Corpus Synchronization):** Update [`SecurityRegressionCatalog.cs`](../../tests/TrinoSqlEngine.Tests/Security/SecurityRegressionCatalog.cs), [`AstSecurityRegressionTests.cs`](../../tests/TrinoSqlEngine.Tests/Security/AstSecurityRegressionTests.cs), and the SQL compiler gate corpus (`Corpus/dml.sql`, `Corpus/dql.sql`) so `SecurityRegressionTraceabilityTests` and `CorpusTests` remain 100% green.
* **REQ-MNT25-04 (Shared Fakes Consolidation in `Autheris.Tests.Common`):**
  - Consolidate all 9 private `StubEpochValidationService` definitions into [`FakeEpochValidationService`](../../tests/Autheris.Tests.Common/FakeEpochValidationService.cs).
  - Consolidate all 7 private `TestOptionsMonitor<T>` definitions into [`StaticOptionsMonitor<T>`](../../tests/Autheris.Tests.Common/StaticOptionsMonitor.cs) (supporting `SetCurrent(T)` for dynamic reload tests).
  - Consolidate 9 private `DelegatingHandlerStub` / `RecordingHandler` / `StubHandler` classes into [`StubHttpMessageHandler`](../../tests/Autheris.Tests.Common/StubHttpMessageHandler.cs).
  - Add a shared thread-safe [`CapturingLogger`](../../tests/Autheris.Tests.Common/CapturingLogger.cs) / `CapturingLogger<T>` in `Autheris.Tests.Common` and replace duplicated private logger fakes across `Autheris.Tests.Unit` and `Autheris.Extensions.Tests`.
* **REQ-MNT25-05 (English Documentation Compliance):** Translate the remaining German XML doc comment in `GraphQlSubscriptionAndFederationSecurityTests.cs` (`SecurityReview20261002GraphQLTests.cs`) to English in accordance with `AGENTS.md` Section 1.

### 2.2 Security & Audit Requirements (`ARCH-MNT-26`)
* **REQ-MNT26-01 (Full `src/` Catch-Site Audit):** Inspect and classify all 421 fallback/silent `catch` sites across `src/Autheris.*` and `src/TrinoSqlEngine` into documented categories in the implementation plan.
* **REQ-MNT26-02 (Zero Fail-Open Tolerance):** Verify that every `catch` block in authentication, tenant resolution, Casbin ABAC, consent evaluation, virtual filters, row-level security, dynamic masking, egress validation, and AST SQL compilation fails closed.
* **REQ-MNT26-03 (Narrow Bare Catches in External Catalog Integration):** Remediate bare `catch` blocks in third-party catalog payload parsing (`CatalogWebhookHandler.ExtractAffectedTables` and `CollibraDataCatalogClient.ParseCollibraAssetsResponse`) and metadata JSON parsing (`CatalogSqlCore.ParseMetadataDictionary`) so only expected data-format exceptions (`JsonException`, `ArgumentException`) are caught and malformed third-party payloads are logged at `Warning` without echoing sensitive payload contents.
