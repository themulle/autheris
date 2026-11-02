# Implementation Plan: PARITY-FU-002 GraphQL Mutation & Subscription Sweep in Nightly Parity

**Document Type:** Architecture & Implementation Plan  
**ID:** `PLAN-PARITY-FU-002`  
**PRD Reference:** [`docs/plans/2026-11-02-req-parity-fu-002-graphql-mutations-subscriptions.md`](2026-11-02-req-parity-fu-002-graphql-mutations-subscriptions.md)  
**Tracking Plan:** [`docs/plans/2026-10-23-plan-nightly-parity-tests.md`](2026-10-23-plan-nightly-parity-tests.md)  
**Master Plan:** [`docs/plans/00-master-plan-overview.md`](00-master-plan-overview.md) (Track 4)  
**Date:** 2026-11-02  
**Status:** IN PROGRESS 🚀  

---

## 1. Architectural Approach & Component Design

### 1.1 Operation Syntax in `GraphQlSpecModel`
Currently, `GraphQlSpecModel.Build` prepends `{ ` (an anonymous query).
We will add an `operationType` parameter to `Build`:
```csharp
public string? Build(GqlField field, GqlInput? extra, string operationType = "query")
```
When `operationType == "mutation"`, it emits:
```graphql
mutation { fieldName(arg1: val1, ...) { leafField } }
```
When `operationType == "subscription"`, it emits:
```graphql
subscription { fieldName(arg1: val1, ...) { leafField } }
```

### 1.2 Isolated Host for Mutations (`IsolatedParityHost`)
Because mutations modify governance/consent tables and reload catalog caches, mutations must never execute against the shared `ParityHostFixture` used by the main read sweeps.
- An ephemeral `IsolatedParityHost` helper will spin up a fresh SQLite temporary workspace (`Guid.NewGuid()`), seed it using `ParitySeed.Create`, register catalog tables, execute the mutation tests, and completely delete the temporary database directory upon disposal.

### 1.3 Sweep Execution & Invariant Assertions
For each of the 9 mutation root fields:
1. Introspect arguments and generate minimal calls (`model.Build(field, arg, "mutation")`).
2. Post mutation to `/graphql` with authenticated `X-Test-User-Sid` and `X-Test-Roles: ClusterAdmin`.
3. Assert:
   - `ParityAssert.NoServerError(result, query)` (strictly no 500 status, no `INTERNAL_SERVER_ERROR`).
   - `ParityAssert.StableCode(result)` (stable catalogue code or success).
   - `ParityAssert.NoLeak(result.RawBody)` (zero stack traces, exception names, or internal path disclosures).

### 1.4 Realtime Subscriptions (`SpecGraphQlSubscriptionTests`)
1. Create a host instance with `Gateway:GraphQL:Subscriptions:Enabled = true`.
2. Introspect schema: verify `SubscriptionRoot` is present and contains `onTableChanged`.
3. Verify subscription execution over WebSocket / socket protocol:
   - Send subscription initialization message (`graphql-transport-ws` protocol or HotChocolate socket client).
   - Test valid table subscription (`onTableChanged(table: "default_main_parity_sales")`).
   - Test unauthorized / missing parameters fail-closed (`INVALID_QUERY`, `AUTH_NOT_AUTHENTICATED`).
   - Verify zero unhandled 5xx errors or server leaks.

---

## 2. Step-by-Step Implementation Steps

1. **Step 1:** Extend `GraphQlSpecModel.cs` to support `operationType` ("query", "mutation", "subscription").
2. **Step 2:** Implement `IsolatedParityHost` in `tests/Autheris.Tests.Parity/Infrastructure/IsolatedParityHost.cs`.
3. **Step 3:** Implement `SpecGraphQlMutationTests.cs` covering all 9 mutation root fields against the isolated host.
4. **Step 4:** Implement `SpecGraphQlSubscriptionTests.cs` verifying the subscription root and WebSocket transport lifecycle.
5. **Step 5:** Run full local test suite (`Autheris.Tests.Parity`, `Autheris.Tests.Architecture`, `Autheris.Tests.Unit`) to guarantee zero regressions.
6. **Step 6:** Code review, quality gate verification, update tracking documentation in `00-master-plan-overview.md` and `2026-10-23-plan-nightly-parity-tests.md`.
