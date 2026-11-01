# PRD: GraphQL Root Lists Signed Keyset Cursor (Relay Connections) (#420)

**Document ID:** `REQ-GQL-420`  
**Related Issues:** [#420](https://github.com/themulle/autheris/issues/420), [#417](https://github.com/themulle/autheris/issues/417), [#390](https://github.com/themulle/autheris/issues/390), [#403](https://github.com/themulle/autheris/issues/403), [#404](https://github.com/themulle/autheris/issues/404)  
**Author:** Product Management (`product-manager`)  
**Status:** PROPOSED (Phase 1 qualified)

---

## 1. Executive Summary & Problem Statement

In Autheris v1.6.0, GraphQL root query fields (`Query.<dataset>`) support pagination exclusively via `first` (limit) and `offset`. 

### The Problem
1. **Full Re-Sort on Source Database:** Every offset-paged query (e.g. `OFFSET 1000 ROWS FETCH NEXT 100 ROWS ONLY`) forces the underlying database engine to scan and sort all skipped rows from scratch. On multi-million row tables with descending indexes (Issue #390), this results in severe latency spikes and timeouts.
2. **Deep Paging Hard Ceiling:** Offset values are capped by `GraphQL.MaxAllowedOffset` (default 10,000). Queries attempting to page past this threshold are rejected.
3. **Admission Cost Pressure:** The GraphQL admission cost model (`QueryCostAnalyzerRule`) prices queries based on `first * selectedFields * DefaultListMultiplier`, capped by `MaxAllowedComplexity` (default 500). Callers are forced into small page sizes (`first: 20`), leading to repetitive, expensive offset requests on the database.
4. **Asymmetry with REST:** Autheris already provides an HMAC-signed keyset pagination mechanism for REST streaming endpoints (`KeysetCursorEncoder`, `/api/v1/data/streams/tables/{id}`), but GraphQL lacks native keyset cursor support.

---

## 2. Market & Competitive Analysis

| Platform | Keyset Pagination Support | Security & Tamper Resistance | RLS / Data Masking Integration |
| :--- | :--- | :--- | :--- |
| **Apollo Router / Federation** | Subgraph-delegated; no gateway-managed keyset cursors. | Cursors are passed transparently; no gateway-level HMAC tamper verification. | Delegated to subgraphs. |
| **Hasura DDN** | Relay Cursor Connections (`first/after`). | Base64-encoded column values; susceptible to tampering and parameter injection unless validated. | In-database permissions. |
| **Autheris (Target State)** | Native Relay Cursor Connections (`first/after`, `pageInfo`, `edges.cursor`). | Cryptographically HMAC-signed tokens binding Tenant, Table, Query Shape, and Caller SID. Zero cleartext key leakage. | Fail-closed: cursors disabled or refused if key columns are masked or restricted by RLS/ABAC. |

---

## 3. Product Requirements & Architectural Scope

### 3.1 Non-Breaking Additive Schema Design
To maintain backward compatibility with existing GraphQL clients:
- The existing root query fields (`Query.<dataset>(first, offset, where, orderBy)`) remain completely untouched.
- An additive connection field is generated for each eligible table in the catalog:
  ```graphql
  extend type Query {
    """
    Relay-compliant keyset cursor connection for {Table}.
    Uses cryptographic HMAC keyset cursors to prevent full sorts on the source.
    """
    {tableQueryName}Connection(
      first: Int = 100,
      after: String,
      where: {TableFilterInput},
      orderBy: [{TableOrderByInput}!]
    ): {TableTypeName}Connection!
  }
  ```

### 3.2 Relay Types Specification
The following type hierarchy is dynamically registered by `CatalogGraphQlTypeModule`:
```graphql
type PageInfo {
  hasNextPage: Boolean!
  hasPreviousPage: Boolean!
  startCursor: String
  endCursor: String
}

type {TableTypeName}Edge {
  cursor: String!
  node: {TableTypeName}!
}

type {TableTypeName}Connection {
  edges: [{TableTypeName}Edge!]!
  pageInfo: PageInfo!
  totalCount: Int
}
```

### 3.3 Cryptographic Keyset Binding (`KeysetCursorContext`)
All GraphQL cursors must be generated and validated using `IKeysetCursorEncoder`:
- **Token Format:** Opaque Base64Url JSON payload signed with gateway HMAC secret key.
- **Context Binding:**
  - `TenantId`: Caller's tenant identifier.
  - `DataSource`: Catalog domain.
  - `TableName`: Target dataset table name.
  - `Columns`: Declared primary key columns (in canonical order).
  - `Direction`: Resolved sort directions (honoring #390/#403/#427 physical index directions).
  - `RequestHash`: SHA-256 digest of filter (`where`) and projection (`selectionSet`) to prevent cursor injection into arbitrary queries.
  - `Caller`: Caller's primary SID.

### 3.4 Zero-Trust & Fail-Closed Guardrails
1. **Tamper & Replay Defense:** Replaying a cursor against another table, tenant, query shape, or with modified key values must fail closed. Under `SEC-PG-12` (Production enumeration shield), errors must return the standard shielded error without disclosing internal token structure.
2. **Key Visibility Invariant (#412):** If any column of the primary key is masked (via dynamic masking rule) or inaccessible under row-level / column-level policy, connection queries must omit cursors or reject keyset seeking, preventing side-channel leakage of masked key values.
3. **Execution Tree Rewriting:** The query execution must compile the `after` cursor into a keyset seek predicate (`WHERE (pk1 > val1) OR (pk1 = val1 AND pk2 > val2)` honoring ASC/DESC directions) rather than `OFFSET`.

---

## 4. Acceptance Criteria & Test Specifications

- [ ] **REQ-GQL-420-01:** `CatalogGraphQlTypeModule` generates Relay-compliant `{dataset}Connection` fields with `first`, `after`, `where`, and `orderBy`.
- [ ] **REQ-GQL-420-02:** When `after` is omitted, the first page returns `edges` with opaque cursors and `pageInfo.hasNextPage`.
- [ ] **REQ-GQL-420-03:** When valid `after` cursor is passed, execution generates a keyset seek predicate and retrieves subsequent rows without full table sort.
- [ ] **REQ-GQL-420-04:** Cursors generated for Tenant A or Table X cannot be replayed against Tenant B or Table Y (fails closed).
- [ ] **REQ-GQL-420-05:** Cursors generated with filter `{ status: "ACTIVE" }` cannot be used with `{ status: "PENDING" }` (fails closed on `RequestHash` mismatch).
- [ ] **REQ-GQL-420-06:** In Production environment, malformed or invalid cursors produce byte-identical shielded error responses (`SEC-PG-12`).
