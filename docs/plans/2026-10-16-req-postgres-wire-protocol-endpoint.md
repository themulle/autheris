# REQ: PostgreSQL Wire Protocol (v3) Endpoint — Qualification, Options & PRD

**ID:** `REQ-PROTO-PGWIRE` · **Date:** 2026-10-16 · **Branch:** `feat/scale-5000-users`
**Role:** Product Manager (Phase 1 of the 6-phase lifecycle) · **Status:** PROPOSED 📝 (awaiting owner decision on the open questions in section 13)
**Input:** Owner-supplied comparison (German) of two ways to expose a PostgreSQL wire protocol endpoint with DuckDB.NET execution: Option 1 (pure C# in-process server) and Option 2 (Rust sidecar using the `pgwire` crate).
**Companion analyses:** the scale-5000 performance and governance analyses were removed after completion (see git history; H-6 Basic-auth cost, NAT limiter).

All source paths are relative to `src/` unless stated otherwise.

---

## 0. Executive Summary

**Verdict: qualified GO with conditions.** A PostgreSQL wire endpoint is a legitimate, high-value client-reach feature (psql, Npgsql, pgjdbc, DBeaver, DataGrip, Power BI, Tableau, Metabase all speak it natively). Neither pasted option is acceptable as written:

- **Option 1 as sketched is rejected.** It answers `SSLRequest` with `N` (no TLS), sends `AuthenticationOk` without authenticating, runs client SQL directly on DuckDB (bypassing row filters, masking, consents and audit), and implements only the Simple Query protocol. Npgsql and pgjdbc — and therefore Power BI, Tableau, Metabase, DBeaver and DataGrip — use the **Extended Query protocol** and binary formats. Simple-only covers psql and scripts, nothing else in the stated audience.
- **Option 2 (Rust `pgwire` sidecar) is not recommended.** It solves only the protocol framing, which is the *smallest* part of the work. The hard parts — governed `pg_catalog`/`information_schema` emulation, PostgreSQL-dialect SQL acceptance, authentication, governance, audit and type mapping after masking — stay in C#. In return it adds a second language, a second process, a second trust boundary that terminates TLS and sees user secrets in clear text, a Cargo supply chain, and an IPC hop on every row.

**Recommendation (section 8):**

1. **Phase 0 (1–2 weeks, before any protocol code):** a compatibility spike on the existing **Trino protocol** endpoint with the real target tools (Power BI, Tableau, Metabase, DBeaver, DataGrip via their Trino/Starburst connectors). Fix the cheap gaps found there (for example `system.jdbc.*` metadata, PAT auth for BI tools). This decides whether a PG endpoint is *needed* or only *wanted*, and its auth/PAT work is reused by the PG endpoint either way.
2. **If the owner confirms PG-native clients as a requirement: Option 3 — a native C# implementation of PG v3 including the Extended Query protocol**, hosted inside the existing Kestrel process via `ConnectionHandler` (no raw `TcpListener`), plugged into `IGovernedSqlExecutionService` with a new transport `PgWire`. Borrow (do not depend on) permissively licensed references: Npgsql (PostgreSQL License) for message/type codecs and OIDs, Rust `pgwire` and `datafusion-postgres` (MIT/Apache-2.0) for state-machine and `pg_catalog` design, Google PGAdapter (Apache-2.0) for client-compatibility catalog emulation.

Weighted scores (section 7): Option 0 **4.05**, Option 3 **4.00**, Option 2 **3.00**, Option 1 **2.70**. Option 0 and Option 3 are close because they answer different questions: Option 0 is the cheapest way to serve the BI audience today, Option 3 is the right way to build a PG endpoint if one is required. Options 1 and 2 are dominated.

---

## 1. Problem Statement & Goal

### 1.1 Problem
Many enterprise users and tools default to a PostgreSQL connection dialog. Today, Autheris exposes governed SQL over HTTP-based protocols only (WebSQL, Trino REST, HTTP/Arrow IPC "Flight SQL", OData, GraphQL, REST, MCP). Tools without a Trino connector, or users who are not allowed to install drivers (locked-down desktops, Excel/Power BI with built-in connectors only), cannot reach governed data without a custom integration. That pushes users towards direct database credentials — the exact bypass Autheris exists to prevent.

### 1.2 Goal
Expose a **governed, TLS-only, authenticated PostgreSQL v3 wire endpoint** so that standard PostgreSQL clients and drivers can browse the caller's *governed* catalog and run *governed* read queries, with exactly the same row filters, masking, consents, Casbin/ReBAC decisions, query governor limits and fail-closed audit as every other Autheris transport.

### 1.3 Non-goals
- Not a PostgreSQL server. No storage, no DDL, no `COPY FROM`, no replication protocol, no extensions, no `LISTEN/NOTIFY` (CDC has its own transport, `F-CDC-*`).
- Not a connection pooler/proxy to a real PostgreSQL source. Client SQL never reaches a source unparsed.
- Not a full PostgreSQL SQL dialect. A documented subset is accepted; everything else fails with SQLSTATE `0A000`.

---

## 2. Target Users & Clients

| Persona | Client | Protocol facts that matter |
|---|---|---|
| Data engineer / analyst (CLI) | `psql`, `pgcli`, shell scripts | libpq, Simple Query; `\d`, `\dt`, `\l` issue `pg_catalog` queries |
| .NET developers | Npgsql (any app), EF Core | **Extended Query always**; binary result format requested for all columns; parameters sent binary; on open, loads the type catalog with a large `pg_type` join (unless `Server Compatibility Mode=NoTypeLoading`) |
| Java tools | pgjdbc (DBeaver, DataGrip, Metabase, Tableau JDBC, Superset via psycopg is Python) | Extended Query by default (`preferQueryMode=extended`); `DatabaseMetaData` runs `pg_catalog` joins (`pg_class`, `pg_namespace`, `pg_attribute`, `pg_type`, `pg_description`, `format_type`, `pg_get_expr`) |
| BI analysts | Power BI (PostgreSQL connector, bundles Npgsql), Tableau (PostgreSQL connector), Metabase | Long-lived pooled connections; generated SQL with `::` casts, `DATE_TRUNC`, `EXTRACT`, `ILIKE`, `LIMIT/OFFSET`; startup `SET`/`SHOW` statements |
| Python users | psycopg 3 / psycopg2, SQLAlchemy | psycopg2: simple protocol with client-side interpolation; psycopg 3: extended for parameterized queries |

**Consequence:** the Extended Query protocol plus binary encoding for the core types plus a governed `pg_catalog` subset are **MVP requirements**, not later phases, if the BI audience is in scope.

---

## 3. Existing Capabilities (must be reused, not duplicated)

| Capability | Where | Reuse in PG endpoint |
|---|---|---|
| Governed SQL pipeline (parse → Casbin/ReBAC/consents → AST rewrite with row filters & masking → dialect emit → execute → audit) | `Autheris.Application/Sql/Services/GovernedSqlExecutionService.cs`, `GovernedSqlExecutor.cs`, `GovernedSqlRewriter.cs`, `TrinoSqlEngine/GovernedSqlCompiler.cs` | **The only execution path.** New transport constant `SqlTransports.PgWire` in `Autheris.Application/Sql/Interfaces/IGovernedSqlExecutionService.cs` |
| Streaming execution with `DbDataReader` callback | `IGovernedSqlExecutionService.ExecuteGovernedQueryAsync(..., Func<DbDataReader, CancellationToken, Task> rowWriter, ...)` | Encode `DataRow` directly from the reader into the pipe; **do not** use `StreamRowsAsync` (allocates a dictionary per row, `GovernedSqlExecutionService.cs:155-200`) or `ExecuteQueryBufferedAsync` |
| Result column types | `Autheris.Application/Sql/SqlResultColumns.cs` (`SqlResultColumn` with CLR type, precision, scale, `DataTypeName`) | Source for `RowDescription` OIDs, typmod, format codes |
| Input grammar | `TrinoSqlEngine/SqlBase.g4` (Trino dialect; `?` parameters; `::` only as static method call) | PG dialect subset must be normalized onto this AST (section 9.5) |
| Function allowlist | `TrinoSqlEngine/SqlFunctionAllowlists.cs`, `SqlFunctionPolicy.cs` | PG function names map to allowlisted functions or are rejected |
| Target dialects | `TrinoSqlEngine/TargetSqlDialect.cs` (PostgreSql, SqlServer, Oracle, Sqlite, DuckDb, Databricks, …) | Unchanged; PG endpoint is a *front-end* dialect concern only |
| Governed metadata introspection | `Autheris.Application/Sql/Services/TrinoMetadataQueryHandler.cs` (`SHOW CATALOGS/SCHEMAS/TABLES`, `information_schema.*` filtered by caller visibility and consents) | Visibility logic is the source for the emulated `pg_catalog` / `information_schema` |
| DuckDB.NET (1.5.6, `DuckDB.NET.Data.Full`) | `Autheris.Application/Olap/DuckDbOlapEngine.cs` (in-memory, `enable_external_access=false`, `lock_configuration=true`, `MaxConcurrentQueries` semaphore clamp 1..64, default 4), `FederatedDuckDbExecutionService.cs`, `OlapStagingService.cs` | DuckDB only ever sees **already governed rows** (OLAP staging, cross-source federation). The PG endpoint reaches it only through the existing federated path when `WebSql:CrossSource:AllowedTransports` lists `PgWire`. Candidate engine for the catalog emulation (section 9.4) |
| Trino REST protocol | `Autheris.Api/Endpoints/WebSqlEndpoints*.cs` (`/v1/statement`), `Autheris.Application/Sql/Services/WebSqlStatementManager.cs` (per-user/total session caps 10/1000) | Option 0 baseline; session-cap pattern reused |
| "Arrow Flight SQL" | `Autheris.Api/Endpoints/ArrowFlightSqlEndpoints.cs`, `Autheris.Application/Serialization/ArrowFlightSqlServer.cs` | **HTTP JSON + Arrow IPC stream, not gRPC Flight SQL.** Standard Flight SQL JDBC/ODBC drivers cannot connect today (the class summary "for JDBC/ODBC" overstates this) |
| JWT validation outside the HTTP pipeline | `Autheris.Api/Security/JwtSocketTokenValidator.cs` (`ISocketTokenValidator`, used by GraphQL WebSockets) | Token-as-password validation |
| Token revocation | `Autheris.Application/Interfaces/ITokenRevocationService.cs` | Per-query revocation check on long-lived connections |
| Basic auth (PBKDF2-SHA256 600k ≈ **184 ms/op**) | `Autheris.Api/Security/PasswordHasher.cs:14`, `BasicAuthenticationHandler.cs`, `BasicAuthAttemptGuard.cs` (30 s per-node success cache, lockout) | Only as a discouraged fallback; lockout logic reused |
| Rate limiting | `Autheris.Api/Middleware/RateLimitingMiddleware.cs` (pre-auth IP, post-auth SID), `IRateLimiterService` (`Autheris.Infrastructure/RateLimiting`, Redis/in-memory) | Connection-attempt and per-SID query limits |
| Query governor | `Autheris.Domain/Options/Resilience/QueryGovernorOptions.cs` (per-tenant concurrency) | Applies unchanged via the pipeline |
| Fail-closed audit | `Autheris.Application/Audit/IAuditWriter.cs` (`RecordRequiredAsync` throws `AuditUnavailableException`) | Session/auth/query events; HTTP `AccessAuditMiddleware` does **not** apply to raw TCP, so the endpoint must call `IAuditWriter` explicitly |
| Error mapping | `Autheris.Application/Sql/Errors/SqlCompilerErrorMapper.cs` | Map to SQLSTATE codes with the same generic, non-leaking messages |
| Kestrel hosting & limits | `Autheris.Api/Program.cs:28-40`, `HostingLimitsOptions` (`Autheris.Domain/Options/Security/OutboundEgressOptions.cs:65`) | Host the PG listener as a Kestrel endpoint with `ConnectionHandler`; inherits connection limits and graceful shutdown |
| Trusted proxies | `Autheris.Api/Security/ITrustedProxyValidator.cs` | PROXY protocol v2 client IP behind L4 load balancers |
| Npgsql 10.0.3 (client) | already a dependency | Conformance test harness (real client against our server) |

No existing PG wire server code or plan was found in `src/` or `docs/` (searched for `pgwire`, `StartupMessage`, "wire protocol").

---

## 4. Critical Qualification of the Pasted Proposal

| # | Claim / element of the proposal | Assessment |
|---|---|---|
| Q1 | `SSLRequest` answered with `N` | **Reject.** Credentials (passwords or tokens) would cross the network in clear text. A governance gateway must answer `S`, perform TLS 1.2+/1.3, and terminate any connection that tries to continue in plain text. Dev-only exception tied to the existing `Insecure.danger_allow_insecure_transport` switch and loopback. |
| Q2 | `AuthenticationOk` directly after `StartupMessage` | **Reject.** The `user` field of the StartupMessage is untrusted input. Authentication must happen before any catalog or data access; fail-closed on validator, revocation or audit unavailability. |
| Q3 | "Simple Query → DuckDB command" | **Reject.** Raw client SQL against DuckDB bypasses Casbin, consents, row filters, masking, query governor and audit. DuckDB in Autheris operates on governed rows only. All statements go through `IGovernedSqlExecutionService`. |
| Q4 | Simple Query is enough for psql/scripts | True, but the stated audience (Npgsql, DBeaver, DataGrip, Power BI, Tableau, Metabase) needs Extended Query, binary formats and `pg_catalog`. A simple-only server would be a psql demo. |
| Q5 | "Zero dependencies, in-memory access" (Option 1 pro) | The in-memory benefit is irrelevant: data lives in source databases and is fetched via pushdown. Zero dependencies is a real advantage and is preserved by Option 3. |
| Q6 | "Little protocol code" (Option 2 pro) | True for framing. Framing is roughly a quarter of the total effort (section 7.3). Catalog emulation, dialect, auth, governance and type mapping are not provided by `pgwire`. |
| Q7 | Arrow IPC over gRPC/UDS to reduce overhead (Option 2) | Still one extra serialize/deserialize per row batch plus a second process with its own memory and CPU. The DataRow encoding cost itself is unchanged. |
| Q8 | Implicit assumption: a PG endpoint is required | **Unproven.** The Trino protocol (existing, documented as compatible with DBeaver's Trino driver and Superset, `docs/features/f-data-02-governed-websql.md:19`) may already serve Tableau, Metabase, DBeaver and Power BI through their Trino/Starburst connectors. Verify first (Phase 0). |

---

## 5. Rejected Anti-Patterns

| Anti-pattern | Why it is rejected | Safe alternative |
|---|---|---|
| Plain-text PG endpoint (`SSLRequest → N`) | Credential and data exposure; violates zero-trust | TLS mandatory; optional PG 17 direct TLS (ALPN `postgresql`) |
| Trust-on-StartupMessage (`AuthenticationOk` without proof) | Identity spoofing | Token/PAT over TLS, later SCRAM-SHA-256 / GSSAPI |
| Raw SQL on DuckDB or on the source | Governance bypass | Governed pipeline only, new transport `PgWire` |
| Inlining Bind parameters into SQL text | SQL injection through the side door | Map `$n` to bound parameters (`BoundParameter`) end to end |
| Multi-statement Simple Query (`SELECT 1; DELETE …`) executed piecewise without per-statement governance | Smuggling | MVP: exactly one statement per `Query` message (trailing `;` allowed); otherwise `0A000` |
| Buffering a full result before sending | Memory exhaustion at 5,000 users | Stream rows from `DbDataReader` into the pipe with flush-based backpressure |
| Per-connection source sessions (a PG connection pins a source DB connection) | 5,000 idle BI sockets would exhaust source pools | Stateless per-query execution against pooled sources; session holds only identity + settings |
| Sidecar holding credentials, JWKS, policies or masking logic | Second policy decision point, drift, larger attack surface | If a sidecar is ever used, it is a dumb framer: no secrets, no decisions (section 6.3) |
| Real PostgreSQL in front (FDW, `pg_duckdb`, PgBouncer to a source) | Duplicated or bypassed governance; `pg_duckdb` embeds DuckDB *inside* Postgres — the opposite direction | Not pursued |
| Exposing the real `pg_catalog` of a PostgreSQL source | Leaks non-governed tables, roles, functions | Emulated, per-caller governed catalog |
| Using the client-supplied `application_name` for authorization | Untrusted | Audit label only, length-capped and sanitized |
| Detailed error messages revealing unauthorized table/column existence | Existence oracle | Same generic messages as `SqlCompilerErrorMapper`; unauthorized objects behave as non-existent (`42P01`/`42703`) |

---

## 6. Options

### 6.0 Option 0 — Do not build a PG endpoint; close gaps on existing protocols
Use Trino connectors (DBeaver Trino driver, Tableau Trino connector, Metabase Starburst driver, Power BI Starburst connector, Trino CLI/JDBC/ODBC) against `/v1/statement`.
- **Gaps known from code:** no `system.jdbc.*` tables (Trino JDBC `DatabaseMetaData` uses them; catalog browsing in DBeaver/DataGrip may fail); per-connection Basic auth cost (perf analysis H-6); NAT-shared pre-auth limiter; no PATs. "Flight SQL" is HTTP-only, so Option 0 cannot rely on standard Flight SQL drivers unless a gRPC Flight SQL service is added (separate, larger item).
- **Pros:** reuses everything; no new listener; lowest risk.
- **Cons:** does not serve PG-only tools (Power BI's PostgreSQL connector, Excel, psql, apps built on Npgsql/pgjdbc); some Trino/Starburst ODBC drivers require separate installation or commercial terms (to verify in the spike).

### 6.1 Option 1 — Pure C#, Simple Query only (as pasted, then hardened)
Even hardened (TLS + token auth + governed pipeline), it serves psql, pgcli and psycopg2 scripts only. Npgsql cannot run a parameterized query against it, so Power BI fails; pgjdbc can be forced into `preferQueryMode=simple` but DBeaver/Metabase/Tableau users would need per-connection tweaks. Effort is not much lower than Option 3 because TLS, auth, catalog emulation and dialect are the same. **Dominated by Option 3.**

### 6.2 Option 2 — Rust sidecar with `pgwire`
- Framing, Simple + Extended, TLS via rustls, SCRAM are provided by the crate.
- **Trust boundary:** the sidecar terminates TLS and therefore sees passwords/tokens in clear text. Either it validates them (duplicated auth, JWKS, revocation → drift) or forwards them over IPC (the IPC channel becomes credential-bearing and needs mTLS or UDS peer-credential checks).
- **Unsolved by the crate:** `pg_catalog`/`information_schema` emulation (only `datafusion-postgres` provides one, for DataFusion's catalog, not ours), PG dialect, type mapping after masking, cancel routing, audit.
- **Ops:** second artifact, Rust toolchain in CI, `cargo audit`/SBOM, second container image or process supervisor, two sets of metrics/logs, coordinated versioning of the IPC contract.
- **Performance:** extra hop per batch; backpressure must be propagated across IPC explicitly.
- **Team fit:** .NET-only codebase; every protocol bug needs Rust skills.

### 6.3 Option 3 — Native C# implementation including Extended Query (recommended if a PG endpoint is required)
- Kestrel endpoint (`ListenAnyIP(5432, l => l.UseConnectionHandler<PgWireConnectionHandler>())`) with `System.IO.Pipelines`: zero-copy parsing, pooled buffers, built-in connection limits and graceful shutdown. TLS via `SslStream` after `SSLRequest` (the first 8 bytes are plain text, so Kestrel's `UseHttps` connection middleware cannot be used directly; direct TLS with ALPN `postgresql` can).
- Full message set: Startup, SSLRequest, GSSENCRequest (`N`), CancelRequest, auth exchange, Query, Parse/Bind/Describe/Execute/Close/Sync/Flush, ErrorResponse/NoticeResponse, ParameterStatus, BackendKeyData, ReadyForQuery with transaction status, portal suspension (`Execute` row limit → `PortalSuspended`).
- Same process, same DI, same governance, same audit, same metrics.
- Borrow from permissive references (section 6.5), no runtime dependency added.

### 6.4 Rejected sub-variants
- **2b — Rust `pgwire` in-process via FFI:** unsafe interop, native build matrix per OS/arch, async runtime inside .NET process. Rejected.
- **0b — Real gRPC Arrow Flight SQL service:** valuable for ADBC/Flight SQL JDBC/ODBC drivers, but a separate requirement; tracked as an open question, not as a substitute for PG.

### 6.5 Open-Source Alternatives & Build/Buy/Borrow Decision

**License constraint.** Autheris is licensed under the **Business Source License 1.1** (`LICENSE`). Incoming code may be Apache-2.0, MIT, BSD, ISC or the PostgreSQL License (attribution in `THIRD_PARTY_NOTICES.md`/`NOTICE`, as already done for `TrinoSqlEngine`). GPL/AGPL code cannot be copied or linked. Third-party BSL/CCL/proprietary-licensed code (CockroachDB, Materialize) must not be copied — design reference only.

Decision legend: **(a)** integrate as a dependency/sidecar as-is · **(b)** copy/port parts natively (license permitting) · **(c)** use an existing .NET implementation · **(r)** read-only design reference.

Licenses and maturity below reflect the state known at the time of writing and must be re-verified in Phase 2 (pin exact versions/commits when porting).

| Candidate | Language / License | Maturity & activity | Extended Query | TLS / SCRAM | Fit with governed pipeline | Security & supply chain | Effort | Decision |
|---|---|---|---|---|---|---|---|---|
| **`pgwire` crate** (sunng87) | Rust / MIT OR Apache-2.0 | Active; used by GreptimeDB, datafusion-postgres and others | Yes (`ExtendedQueryHandler`, portals, describe) | rustls; cleartext, MD5, SCRAM-SHA-256 | Only as Option 2 sidecar; all governance via IPC to C# | New language, Cargo tree, second process; TLS terminated outside .NET | Medium protocol, high ops | **(r) + (b)**: port the state machine and message layout ideas to C# (permissive); **not (a)** |
| **`datafusion-postgres`** | Rust / Apache-2.0 | Active, younger | Yes (via `pgwire`) | Via `pgwire` | Its `pg_catalog` emulation targets DataFusion's catalog, not ours | Same as above | — | **(r)**: best open reference for *which* `pg_catalog` tables/columns pgjdbc, psql and DBeaver need |
| **Google PGAdapter** | Java / Apache-2.0 | Production (Cloud Spanner PG interface); extensive client test matrix (pgjdbc, Npgsql, psycopg, node-postgres) | Yes | TLS; auth delegated to Google credentials | Architecturally identical problem (PG wire in front of a non-PG engine with catalog emulation) | JVM sidecar not acceptable | — | **(r) + (b)**: copy the *approach and query fingerprints* for catalog emulation and client-specific startup queries (Apache-2.0 permits porting with attribution) |
| **buenavista** (jwills) | Python / Apache-2.0 | Experimental, low activity | Partial | Limited | DuckDB-backed proxy; no governance concept | Python runtime | — | **(r)** only (DuckDB type mapping ideas) |
| DuckDB PG-compatible server efforts ("duckdb pgwire" / "quack"-style projects) | Various | Unverified/experimental at time of writing | Varies | Varies | Execute directly on DuckDB — conflicts with Q3 | Unknown provenance | — | **Reject**; re-check in Phase 2 only as reference |
| **`pg_duckdb`** | C++ / MIT | Active | n/a (it is a PostgreSQL extension, not a wire server) | n/a | Embeds DuckDB in PostgreSQL — opposite direction | Would need a real PostgreSQL | — | **Reject** (not applicable) |
| **PgBouncer** | C / ISC | Mature | Transparent pass-through (prepared statement tracking since 1.21) | TLS, SCRAM pass-through | Pooler, not a server | n/a | — | **(r)**: SCRAM and cancel-request handling reference |
| **pgcat** | Rust / MIT | Active | Pass-through | TLS, SCRAM/MD5 | Pooler/sharder | n/a | — | **(r)**: framing and cancel-key routing across backends (relevant for multi-replica cancel) |
| **RisingWave** wire layer | Rust / Apache-2.0 | Production | Yes | TLS | Engine-specific | n/a | — | **(r)**; porting allowed but Rust-specific |
| **CockroachDB** `pgwire` | Go / proprietary (formerly BSL/CCL) | Production | Yes | Yes | — | **License incompatible for copying** | — | **(r)** design reading only |
| **Materialize** pgwire | Rust / BSL 1.1 | Production | Yes | Yes | — | **Do not copy** (third-party BSL) | — | **(r)** design reading only |
| **ROAPI** | Rust / permissive (verify) | Moderate; PG frontend via the older `convergence` crate, simple-query oriented | Limited | Limited | Read-only APIs over DataFusion | n/a | — | **Reject** as dependency; minor reference |
| **jackc/pgproto3** | Go / MIT | Mature (pgx) | Message codecs both directions | n/a (codec level) | — | — | — | **(r) + (b)**: compact, complete frontend/backend message codec reference |
| **Npgsql** (client) incl. test-suite server mock (`PgServerMock`/`PgPostmasterMock` style helpers) and type-OID constants | C# / PostgreSQL License (permissive) | Mature, already a dependency (10.0.3) | Client side complete; mock writes backend messages | Client TLS/SCRAM | Codec semantics, binary encodings, OIDs | Already in the supply chain | Low | **(b)**: port binary codecs/OID tables and mock patterns with attribution; **(a)** as conformance-test client |
| .NET PG v3 **server** libraries | C# | No mature, maintained server-side implementation known at the time of writing (only experiments/samples) | — | — | — | Unvetted | — | **(c) not available**; re-scan NuGet/GitHub in Phase 2 and record the result |
| PostgreSQL source tree (`pg_type.dat`, protocol docs) | C / PostgreSQL License | Authoritative | — | — | — | — | — | **(r)**: canonical OIDs, catalog column definitions, SQLSTATE list |

**Build/buy/borrow conclusion:** *Build* the server natively in C# (no usable .NET server exists, and no sidecar is acceptable); *borrow* permissively licensed designs and codecs (Npgsql, `pgwire`, pgproto3, PGAdapter catalog fingerprints, `datafusion-postgres` catalog shape); *buy/integrate* nothing at runtime. The scoring matrix reflects this as Option 3 "native C# with borrowed references".

---

## 7. Evaluation

### 7.1 Weighted Scoring Matrix (1 = poor, 5 = excellent)

Gate: any option scoring below 4 on *Security & governance fit* after hardening cannot be recommended.

| Criterion | Weight | Opt 0 Trino/existing | Opt 1 C# simple-only | Opt 2 Rust `pgwire` sidecar | Opt 3 C# full extended (borrowed refs) |
|---|---:|---:|---:|---:|---:|
| Security & governance fit (single PDP, TLS, auth, fail-closed audit) | 25 | 5 | 2 (as pasted: 1) | 3 | 5 |
| Client compatibility for stated audience | 25 | 2 | 1 | 4 | 4 |
| Operational simplicity (one artifact, one runtime) | 10 | 5 | 5 | 2 | 5 |
| Effort / time-to-value | 15 | 5 | 4 | 3 | 2 |
| Performance & scalability at 5,000 users | 10 | 4 | 3 | 3 | 4 |
| Maintainability (team skills, one language) | 10 | 4 | 3 | 2 | 3 |
| Supply chain & licensing | 5 | 5 | 5 | 2 | 5 |
| **Weighted score (out of 5)** | 100 | **4.05** | **2.70** | **3.00** | **4.00** |

Option 2 fails the security gate unless the sidecar is reduced to a secret-free framer, which removes most of its advantage (it still terminates TLS). Option 1 fails client compatibility.

### 7.2 Why Option 3 over Option 2 despite more protocol code
- The `pgwire` crate saves the framing (~4–6 engineer-weeks) but adds IPC contract, auth delegation, packaging, CI and monitoring (~5–6 engineer-weeks) — net neutral at best.
- The dominant work (catalog emulation, dialect, type mapping, conformance) is identical in both and lives in C# in both.
- Option 3 keeps one policy decision point, one TLS termination inside the trust boundary, one audit writer.

### 7.3 Rough effort (engineer-weeks, to be refined by the architect)

| Work package | Opt 1 | Opt 2 | Opt 3 |
|---|---:|---:|---:|
| Framing, startup, TLS, auth exchange, simple + extended, cancel, errors | 3 | 2–3 (crate) | 5–7 |
| IPC contract, sidecar auth delegation, packaging, CI, SBOM, monitoring | – | 5–6 | – |
| Type codecs text + binary (core types), post-masking type mapping | 1 | 2 | 2 |
| Governed `pg_catalog` / `information_schema` subset | 3–4 | 4–6 | 4–6 |
| PG dialect subset onto Trino AST | 3–4 | 3–4 | 3–4 |
| Conformance matrix, fuzzing, load tests | 2 | 3–4 | 3–4 |
| **Total** | **12–14 (psql only)** | **19–25** | **17–23** |

Option 0 spike plus gap fixes: 2–4 engineer-weeks.

---

## 8. Recommendation & Rationale

1. **Phase 0 — Prove the need (Option 0 spike, 1–2 weeks).** Test Power BI, Tableau, Metabase, DBeaver and DataGrip against `/v1/statement` with their Trino/Starburst connectors; record what works, what breaks (expected: `system.jdbc.*` metadata, auth UX) and what requires paid/extra drivers. Deliver the reusable pieces regardless of outcome: **Personal Access Tokens** (high-entropy, SHA-256-hashed, revocable, scoped, expiring — no PBKDF2 cost, fixes perf finding H-6) and the NAT-aware limiter.
2. **Decision gate (owner).** If PG-only clients (Power BI PostgreSQL connector, Excel, Npgsql/pgjdbc apps, psql) are confirmed in scope, proceed with **Option 3**.
3. **Option 3 delivery in stages** (section 11): MVP = TLS + PAT/JWT auth + Simple and Extended Query + binary core types + governed catalog subset for psql, Npgsql, pgjdbc/DBeaver; then Power BI/Tableau/Metabase hardening; then SCRAM/GSSAPI and optional DML.

Top reasons: one policy decision point and one trust boundary; Extended Query is mandatory for the audience; the framing a sidecar would save is the minor part of the work; no runtime dependency or second language; Kestrel `ConnectionHandler` + Pipelines meets the 5,000-user connection budget.

---

## 9. Functional Requirements (Option 3)

### 9.1 Connection & transport
- FR-1 Dedicated Kestrel endpoint, disabled by default (`Gateway:PgWire:Enabled=false`), configurable port (default 5432), bind address and certificate (PFX/PEM, hot reload).
- FR-2 `SSLRequest` → `S` + TLS 1.2/1.3 only (no renegotiation, strong cipher suites). Any other first message on a non-TLS connection → `FATAL 28000` "TLS required" and close. `GSSENCRequest` → `N`. Optional: direct TLS with ALPN `postgresql` (PG 17+ clients).
- FR-3 Protocol version 3.0. For requested minor versions > 0, answer `NegotiateProtocolVersion` (3.2 support, including longer cancel keys, is a later phase).
- FR-4 Optional PROXY protocol v2 from trusted L4 load balancers only (`ITrustedProxyValidator`), to obtain the real client IP for limits and audit.

### 9.2 Authentication & session identity (fail-closed)
- FR-5 MVP methods, both via `AuthenticationCleartextPassword` **over TLS only** (precedent: Azure Database for PostgreSQL Entra tokens, AWS RDS IAM tokens):
  - (a) OIDC/Entra **access token** as password → `ISocketTokenValidator` + `ITokenRevocationService`.
  - (b) Autheris **PAT** as password (from Phase 0) → constant-time hash lookup, revocation, expiry, scope `pgwire`.
- FR-6 Local Basic-auth users (PBKDF2) disabled by default on this endpoint; if enabled, reuse `BasicAuthAttemptGuard` lockout and success cache.
- FR-7 StartupMessage `user` must match the authenticated subject (UPN/email/configured alias) or be the literal configured placeholder; mismatch → `28000`. `database` maps to an Autheris data source / domain (like `X-Trino-Catalog`); unknown or not visible → `3D000` with a generic message.
- FR-8 Long-lived connections: token `exp` and revocation are checked **before every statement**; on expiry/revocation send `FATAL 28000` and close. Maximum connection lifetime configurable (default = token lifetime, PAT: 12 h).
- FR-9 Any failure of token validation, revocation lookup or audit → connection refused (fail-closed).
- Later: SCRAM-SHA-256 for local users (requires storing SCRAM verifiers alongside PBKDF2/Argon2 hashes), SCRAM-SHA-256-PLUS channel binding, GSSAPI/Kerberos (reusing Negotiate infrastructure).

### 9.3 Query protocol
- FR-10 Simple Query: one statement per message (trailing `;` allowed); empty query → `EmptyQueryResponse`.
- FR-11 Extended Query: `Parse` (named/unnamed statements, parameter type OIDs), `Bind` (text and binary parameter formats, per-column result formats), `Describe` (statement/portal: `ParameterDescription`, `RowDescription`/`NoData`), `Execute` with row limit → `PortalSuspended`, `Close`, `Sync`, `Flush`; error → discard until `Sync`.
- FR-12 `$n` parameters are bound as typed parameters through the governed pipeline; never inlined.
- FR-13 Describe of a statement must not execute it against a source; column types come from a governed *compile-only* path (or a `LIMIT 0` governed execution if no compile-time typing exists — architect to decide, audited as a catalog read).
- FR-14 Session statements handled locally: `BEGIN/START TRANSACTION/COMMIT/ROLLBACK/END` as read-only no-ops with correct `ReadyForQuery` status (`I`/`T`/`E`); allowlisted `SET`/`SHOW`/`RESET` (`application_name`, `extra_float_digits`, `DateStyle` ISO only, `client_encoding` UTF8 only, `search_path`, `TimeZone`, `statement_timeout` capped by governor); `SELECT version()`, `current_schema()`, `current_user`, `current_setting(…)`, `pg_backend_pid()`. Others → `0A000`.
- FR-15 `CancelRequest`: per-connection random secret key, constant-time compare, rate-limited; cluster-aware routing (key encodes replica id; cross-replica forwarding via existing Redis bus) or documented as best-effort per replica.
- FR-16 MVP is **read-only**: `INSERT/UPDATE/DELETE/MERGE/COPY/DDL/LISTEN/NOTIFY/DO/CALL` → `0A000` (DML via governed pipeline is a later phase).

### 9.4 Governed catalog emulation
- FR-17 `pg_catalog` / `information_schema` queries are classified before execution and answered by an isolated **CatalogQueryEngine** that sees only the caller-visible metadata (same visibility rules as `TrinoMetadataQueryHandler`, including consents), never by a source database.
- FR-18 MVP coverage (minimum): `pg_namespace`, `pg_class`, `pg_attribute`, `pg_type`, `pg_index` (PK only), `pg_description` (catalog descriptions), `pg_database`, `pg_settings` (subset), `pg_roles`/`pg_user` (caller only), `pg_proc` (empty or allowlisted functions), `information_schema.schemata/tables/columns`; functions `format_type`, `pg_get_expr` (constant), `pg_table_is_visible`, `has_*_privilege` (true only for visible objects), `obj_description`, `col_description`, casts to `regclass`/`regtype`/`oid`.
- FR-19 Must answer the startup/introspection queries of: psql (`\l`, `\dn`, `\dt`, `\d table`), Npgsql type loading, pgjdbc `DatabaseMetaData.getSchemas/getTables/getColumns/getPrimaryKeys/getTypeInfo`, DBeaver navigator, Power BI navigator, Tableau and Metabase sync.
- FR-20 Engine choice for the architect: (a) per-visibility-hash in-memory DuckDB populated with *empty* tables mirroring governed schemas, relying on DuckDB's PG-compatibility catalog views (reuse `DuckDbOlapEngine` hardening: `enable_external_access=false`, `lock_configuration=true`), or (b) PGAdapter-style fingerprinted known queries with C# result builders, or a hybrid. Unknown catalog queries → empty result or `0A000`, never a passthrough.
- FR-21 Stable synthetic OIDs per (tenant, object) so that clients caching OIDs keep working across catalog refreshes; no information about invisible objects derivable from OID gaps (hash-based OIDs, not sequential).

### 9.5 PostgreSQL dialect subset
- FR-22 Normalize PG-specific syntax onto the existing Trino AST using the existing lexer/parser (no regex rewriting of SQL): `expr::type` → `CAST`, `$n` → parameter, `ILIKE`, `DATE_TRUNC`/`EXTRACT`, `LIMIT/OFFSET`, `E'…'` strings, `IS [NOT] DISTINCT FROM`, `COALESCE/NULLIF`, `||`. Functions resolved through `SqlFunctionAllowlists`.
- FR-23 Everything else → `42601` (syntax) or `0A000` (feature not supported) with the generic message style.

### 9.6 Results & type mapping
- FR-24 `RowDescription` derived from the **governed** result (after masking): a masked column whose value type changes (e.g. format-preserving mask to string) is announced with the post-masking type.
- FR-25 Type map (text and binary): `bool` 16, `bytea` 17, `int8` 20, `int2` 21, `int4` 23, `text` 25, `oid` 26, `json` 114, `float4` 700, `float8` 701, `unknown` 705, `varchar` 1043, `date` 1082, `time` 1083, `timestamp` 1114, `timestamptz` 1184, `interval` 1186, `numeric` 1700 (typmod from precision/scale), `uuid` 2950, `jsonb` 3802; arrays later. Unknown source types → `text`.
- FR-26 Binary format required for all MVP types (Npgsql requests binary results and sends binary parameters).
- FR-27 Row limits/truncation from the pipeline (`SqlRowLimit`) end with `CommandComplete SELECT n` plus a `NoticeResponse` (`01000`, "result truncated by gateway limit").

### 9.7 Errors
- FR-28 Map pipeline errors to SQLSTATE: `28000/28P01` auth, `42501` denied operation (generic), `42P01/42703` unknown *or invisible* object, `42601` syntax, `0A000` unsupported, `57014` canceled/timeout, `53300` too many connections, `53400` limit exceeded, `08P01` protocol violation, `XX000` internal (no stack traces, correlation id in `detail`).

---

## 10. Non-Functional Requirements

| ID | Requirement | Target |
|---|---|---|
| NFR-1 Security | Fail-closed on auth, revocation, policy, audit errors; TLS-only; no plain-text credentials in logs (`SensitiveLogPropertyEnricher`); generic errors | 100% of negative security tests pass |
| NFR-2 Protocol robustness | Max message length (default 1 MiB query text, 2 MiB total frame, consistent with the 2 MB WebSQL body cap), max parameters 32,767 (protocol limit) but capped (default 1,000), max prepared statements per connection (default 256), max open portals (default 16), startup timeout 10 s, auth timeout 10 s | Fuzzing with malformed lengths/types never crashes the process or allocates beyond caps |
| NFR-3 Streaming | Rows encoded from `DbDataReader` directly into `PipeWriter`; flush every ~64 KiB and await `FlushAsync` (backpressure to the reader) | Peak memory per active query independent of result size (≤ 256 KiB buffering) |
| NFR-4 Connection scale | Idle connection footprint ≤ 32 KiB; configurable max connections per replica (default 2,000), per principal (default 20), per IP pre-auth; idle timeout (default 30 min); TCP keepalive | 5,000 idle + 500 active connections across 3 replicas without degradation |
| NFR-5 No source pinning | A PG connection never holds a source DB connection or transaction between statements | Source pool usage = concurrent statements, not open sockets |
| NFR-6 Auth cost | Token/PAT validation p99 < 5 ms (cached JWKS, cached revocation epoch); no per-connection PBKDF2 by default | Reconnect storm of 500 connections/s per replica sustainable |
| NFR-7 Throughput | DataRow encoding ≥ 1M cells/s per core (text), first-row overhead vs. WebSQL streaming < 2 ms | Benchmark in `benchmarks/Autheris.Benchmarks` |
| NFR-8 Rate limits | Pre-auth connection attempts per IP (PROXY-aware), failed-auth lockout (reuse `BasicAuthAttemptGuard` logic), post-auth per-SID statement rate via `IRateLimiterService` | Configurable; defaults documented |
| NFR-9 Audit | Events: connection accepted/rejected, auth success/failure (reason class), session end (duration, statements, rows, bytes), every statement with transport `PgWire`, connection id, client IP, `application_name` (sanitized label), compiled-SQL digest (`CompiledSqlAuditDigester`). `RecordRequiredAsync` for ALLOW decisions: audit failure → statement fails | 100% coverage in integration tests |
| NFR-10 Observability | Metrics: `autheris_pgwire_connections{state}`, `_auth_total{method,result}`, `_statements_total{protocol,result,sqlstate}`, `_rows_sent_total`, `_bytes_sent_total`, `_catalog_queries_total{handled}`, `_cancel_requests_total`; OpenTelemetry span per statement linked to governance spans | Dashboards + alerts defined in Phase 2 |
| NFR-11 Load balancing | L4 (TCP) LB with PROXY v2; connections are sticky by nature; graceful drain on shutdown (`FATAL 57P01` admin shutdown after grace period) | Rolling deploy without data errors for idle clients |
| NFR-12 Compatibility matrix | Automated tests with Npgsql (in-repo), pgjdbc and psql in CI (containers); manual certification for Power BI, Tableau, Metabase, DBeaver, DataGrip | Matrix documented per release |
| NFR-13 Licensing | Ported code only from Apache-2.0/MIT/BSD/ISC/PostgreSQL-licensed sources, attributed in `THIRD_PARTY_NOTICES.md` | Zero GPL/AGPL/third-party-BSL code |

---

## 11. Scope: MVP vs. Later Phases

| Stage | Scope |
|---|---|
| **Phase 0 (prerequisite)** | Trino-protocol BI spike + report; PATs; NAT-aware limiter; `system.jdbc.*` metadata if the spike shows it is the main blocker |
| **MVP (PG-1)** | TLS-only listener; JWT + PAT auth; Simple + Extended Query; text + binary for core types; governed catalog subset for psql, Npgsql type loading, pgjdbc/DBeaver; PG dialect subset; read-only; streaming; limits; audit; metrics; per-replica cancel |
| **PG-2** | Power BI, Tableau, Metabase, DataGrip certification and catalog extensions; cluster-wide cancel; PROXY v2; arrays; `COPY TO STDOUT` (governed, streaming export) |
| **PG-3** | SCRAM-SHA-256 (+ channel binding) for local users; GSSAPI/Kerberos; protocol 3.2; optional governed DML (requires DML policy parity with WebSQL) |
| **Out of scope** | Replication protocol, `LISTEN/NOTIFY`, DDL, extensions, server-side cursors beyond portals |

---

## 12. Acceptance Criteria (MVP)

1. A non-TLS connection attempt that skips `SSLRequest`, or a client with `sslmode=disable`, is rejected with SQLSTATE `28000`; no credential bytes are read on a plain-text connection.
2. Invalid, expired, revoked or wrong-audience tokens and PATs are rejected with `28P01`/`28000`; the event is audited; repeated failures trigger the lockout/rate limit.
3. If the audit store is unavailable, an otherwise allowed statement fails and no row is sent (fail-closed), and the error is generic.
4. For a table with a row filter and a masked column, `psql`, Npgsql and pgjdbc return exactly the same rows and masked values as `POST /api/v1/sql` for the same principal and query (golden-file parity test).
5. Tables the caller is not allowed to see do not appear in `\dt`, `pg_class`, `information_schema.tables`, Npgsql `GetSchema`, or pgjdbc `getTables`, and querying them yields the same error as a non-existent table.
6. A parameterized query via Npgsql (`$1` binary int4/text/timestamptz parameters, binary results) succeeds; parameters reach the source as bound parameters (verified via source query log/interceptor), never as literals.
7. `Execute` with a row limit returns `PortalSuspended` and resumes correctly; `Sync` after an error recovers the connection.
8. A 10-million-row query streams with bounded gateway memory (NFR-3) and stops source reading when the client stops reading (backpressure test).
9. `CancelRequest` with the correct key cancels a running statement (`57014`); a wrong key has no effect and is rate-limited.
10. Multi-statement Simple Query, DML, DDL and unknown catalog queries return `0A000`/`42601` without touching a source.
11. Token expiry during an open connection causes `FATAL 28000` before the next statement executes.
12. Load test: 2,000 idle + 200 active connections per replica with p99 statement overhead < 5 ms over WebSQL and no source-pool exhaustion.
13. Fuzz test (malformed lengths, unknown message types, oversized frames, truncated TLS) runs for a defined budget without crash, hang or unbounded allocation.
14. `dotnet build Autheris.sln` and the full unit + integration suites are green; the endpoint is disabled by default and startup validation rejects `Enabled=true` without a certificate outside Development.

---

## 13. Open Questions for the Owner

1. **Need:** Which concrete tools *must* connect via PostgreSQL (rather than via a Trino/Starburst connector)? Is "no driver installation" a hard requirement for Power BI/Excel users?
2. **Power BI topology:** Desktop only, or Power BI Service via the on-premises data gateway (affects auth UX: tokens expire; PATs or gateway service principal needed)?
3. **Auth:** Are PATs acceptable as the primary credential for BI tools? Should local Basic users be allowed on this endpoint at all? Is Kerberos/GSSAPI needed in the first release (Windows SSO for Tableau/Power BI)?
4. **Read-only:** Is read-only acceptable for the first release?
5. **Network:** Will port 5432 (or another port) be exposed through an L4 load balancer, and does it support PROXY protocol v2?
6. **DuckDB scope:** Is the DuckDB reference in the request meant as the execution engine (rejected for raw SQL, Q3) or only as the cross-source/OLAP engine (already supported through the governed federated path)?
7. **Flight SQL:** Should a real gRPC Arrow Flight SQL service (ADBC/Flight SQL JDBC/ODBC drivers) be evaluated as a separate requirement?
8. **Connection budget:** Expected number of concurrent BI connections (Tableau Server / Power BI gateway pools) to size NFR-4 defaults.

---

## 14. Next Phases

| Phase | Owner role | Deliverable |
|---|---|---|
| 0 | Product Manager + Developer | Trino/BI compatibility spike report; PAT requirement (`docs/plans/YYYY-MM-DD-req-personal-access-tokens.md`) |
| 2 | `csharp-architect` | `docs/plans/YYYY-MM-DD-plan-postgres-wire-protocol-endpoint.md`: Kestrel `ConnectionHandler` design, message codec layering (Pipelines, `SequenceReader`), session state machine, catalog engine decision (FR-20 spike), PG-dialect normalizer design, compile-only describe path, cancel routing, options + startup validation, test harness (Npgsql in-proc, pgjdbc/psql containers), re-scan for .NET server libraries and pin ported references |
| 3 | `csharp-security-expert` | Threat model (TLS downgrade, auth bypass, catalog existence oracle, OID enumeration, cancel-key brute force, slowloris on startup, memory DoS via frame lengths, multi-statement smuggling, parameter type confusion); fail-closed test list |
| 4 | `csharp-tdd-developer` (Sonnet) | Red-Green-Refactor per work package: codec → startup/TLS/auth → simple → extended → catalog → dialect → streaming/limits → audit/metrics |
| 5 | `csharp-code-reviewer` | Allocation review of the hot path (zero-alloc DataRow encoding, pooled buffers), C# 13 idioms |
| 6 | `documentation-expert` | `docs/features/f-data-10-postgres-wire-protocol.md` (number to be confirmed), registration in `docs/features/README.md`, master-plan update |

**Master plan:** this requirement must be registered in [`00-master-plan-overview.md`](00-master-plan-overview.md) as `REQ-PROTO-PGWIRE` with status `PROPOSED 📝` (not done in this Phase 1 step, which was limited to this file).
