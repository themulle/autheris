# PRD: Decomposition of GraphQL Monolithic Mutation and Query Types (ARCH-MNT-21)

**Document ID:** `REQ-GQL-SPLIT-21`  
**Date:** November 2, 2026  
**Status:** APPROVED  
**Author:** Product Manager  
**Architecture Lead:** Solution Architect  
**Security Lead:** Application Security Expert  
**Master Plan Reference:** [`docs/plans/00-master-plan-overview.md`](00-master-plan-overview.md) (Backlog Item 11, `ARCH-MNT-21`)

---

## 1. Context & Problem Statement

In the Autheris GraphQL subsystem, all GraphQL mutations and queries are presently declared within two monolithic source files:
- [`src/Autheris.GraphQL/Types/MutationTypes.cs`](../../src/Autheris.GraphQL/Types/MutationTypes.cs) (1,385 lines, tracked in `source-size-ceilings.txt`)
- [`src/Autheris.GraphQL/Types/QueryTypes.cs`](../../src/Autheris.GraphQL/Types/QueryTypes.cs) (788 lines)

These files violate the single responsibility principle and act as architectural "god classes":
1. **Frequent Merge Conflicts & Cognitive Overhead:** Every team or agent modifying Consent, Catalog sync, GDPR disclosure, or Access workflows must edit these single files, creating cross-cutting Git conflicts.
2. **Architecture Ratchet Violation:** `MutationTypes.cs` is explicitly allowlisted in [`tests/Autheris.Tests.Architecture/Baselines/source-size-ceilings.txt`](../../tests/Autheris.Tests.Architecture/Baselines/source-size-ceilings.txt) at a ceiling of 1,385 lines. Under ARCH-MNT-02, source files exceeding 1,000 lines must be decomposed so that ceilings ratchet downward and are eventually retired.
3. **No Schema SDL Invariance Net:** The codebase currently lacks a strict automated golden test asserting that the generated GraphQL Schema Definition Language (SDL) remains byte-for-byte identical across refactoring cycles.

---

## 2. Goals & Non-Goals

### 2.1 Goals
- **Decompose `MutationTypes.cs` and `QueryTypes.cs`** into cohesive `partial` class segments organized by domain aggregate (e.g. Consent, Catalog, Compliance, Core).
- **Enforce Source-Size Ratchet:** Ensure every decomposed file strictly remains under the 1,000-line limit, allowing the `MutationTypes.cs|1385` entry to be completely purged from `source-size-ceilings.txt`.
- **Zero API or Behavioral Drift (SDL Invariance):** The generated GraphQL schema definition (SDL), field names, arguments, return types, directives, and documentation must remain 100% byte-for-byte identical to the pre-refactor state.
- **Strict Authorization Preservation:** All existing authorization attributes, role guards (`[Authorize]`, `RequireRole`), and `IHttpContextAccessor` SID extractions must be preserved without alteration.
- **Dedicated SDL Golden Test:** Introduce a regression test (`GraphQlSchemaGoldenTests`) asserting that the runtime schema SDL matches an frozen pre-refactor baseline.

### 2.2 Non-Goals
- Adding new GraphQL root fields, mutations, or queries.
- Altering internal business logic, database queries, or Casbin evaluation.
- Changing HotChocolate pipeline options or DI lifetime registrations.

---

## 3. Detailed Scope & Decomposition Structure

### 3.1 Mutation Types Decomposition (`Autheris.GraphQL.Types`)
The monolithic `Mutation` class will be decomposed into `public sealed partial class Mutation`:
- **`Mutation.cs`** (Core / Payloads): Shared payload classes (`OpenMetadataSyncPayload`, `DataCatalogSyncPayload`, `ConsentRequestPayload`) and common private authentication helpers (`GetAuthenticatedUserSid`, `RequireRole`).
- **`Mutation.Consent.cs`** (Consent & Access Requests):
  - `SetConsentDescriptionAsync`
  - `RequestTableAccessAsync` (overloads)
  - `WithdrawAccessRequestAsync`
  - `ApproveConsentRequestAsync` (overloads)
  - `RejectConsentRequestAsync` (overloads)
  - `RevokeConsentAsync` (overloads)
- **`Mutation.Catalog.cs`** (Catalog & Schema Sync):
  - `ReloadSchemaAsync`
  - `SyncOpenMetadataAsync`
  - `SyncDataCatalogAsync`

### 3.2 Query Types Decomposition (`Autheris.GraphQL.Types`)
The `Query` class will be decomposed into `public sealed partial class Query`:
- **`Query.cs`** (Core / Payloads): Shared payload and DTO models (`TableRecordPayload`, `TableMetadataDto`) and common private authentication helpers (`GetAuthenticatedUserSid`, `RequireRole`).
- **`Query.Catalog.cs`** (Catalog & Tables):
  - `GetTableAsync`
  - `GetCatalogAsync` (overloads)
  - `GetTableConsumersAsync`
- **`Query.Consent.cs`** (Consent & Access Introspection):
  - `MyAccessAsync`
  - `MyAccessRequestsAsync`
  - `PendingApprovalsForMeAsync`
  - `MyActiveConsentsAsync`
  - `CalculateConsentRevocationImpactAsync`
- **`Query.Compliance.cs`** (GDPR & Data Disclosure):
  - `GetGdprDataDisclosureReportAsync`
  - `ExportGdprDataDisclosureReportPdfBase64Async`

---

## 4. Requirements Specification

### 4.1 Functional Requirements
- **`REQ-GQL-SPLIT-01` (Partial Class Parity):** All decomposed files must reside within namespace `Autheris.GraphQL.Types` as `public sealed partial class Mutation` and `public sealed partial class Query`.
- **`REQ-GQL-SPLIT-02` (DI Binding Stability):** `AddQueryType<Query>()` and `AddMutationType<Mutation>()` in `GatewayServiceCollectionExtensions.GraphQL.cs` must remain completely unchanged.
- **`REQ-GQL-SPLIT-03` (SDL Invariance Golden):** A test in `Autheris.Tests.Unit/GraphQL/` must construct the HotChocolate schema executor and assert that `schema.Print()` strictly matches the pre-refactor golden SDL file.
- **`REQ-GQL-SPLIT-04` (Ratchet Cleanup):** `src/Autheris.GraphQL/Types/MutationTypes.cs|1385` must be removed from `tests/Autheris.Tests.Architecture/Baselines/source-size-ceilings.txt`, and no new file may exceed 1,000 lines.

### 4.2 Non-Functional & Security Requirements
- **`SEC-GQL-SPLIT-01` (Fail-Closed & Zero-Trust):** All authorization checks, user SID resolution, and role validation must retain identical execution order and behavior.
- **`SEC-GQL-SPLIT-02` (Audit Parity):** All mutation audit events (`AuditEventWriter.WriteEventAsync`) and parameters must be retained with exact field naming and tenant attribution.

---

## 5. Verification & Test Criteria
1. **Schema Golden Test:** `GraphQlSchemaGoldenTests.RuntimeSchema_MatchesPreRefactorSdl` passes.
2. **Architecture Suite:** `dotnet test tests/Autheris.Tests.Architecture` passes with 0 failures, verifying `SourceSizeCeilingTests` and ratchet enforcement.
3. **Unit Suite:** `dotnet test tests/Autheris.Tests.Unit` passes 100% of existing GraphQL, Consent, Catalog, and Security test fixtures.
4. **Parity Suite:** `SpecGraphQlMutationTests` and `SpecGraphQlSubscriptionTests` pass against isolated hosts without alteration.
