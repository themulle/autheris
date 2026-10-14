# ADR-017: Multi-Node State Synchronisation, AST Dialect Generator und RBAC-Konsolidierung

## Status
Teilweise umgesetzt; §2 durch das Amendment vom 2026-10-11 ergänzt (09.10.2026: AR-01 Epochen-Keying, AR-02/AR-03 ReBAC-Pull-Validierung, AR-04 Atomare DP-/FinOps-Cluster-Zähler umgesetzt; Session-/HitL-Migration ausstehend)

## Kontext
Nach erfolgreicher Behebung aller 100 operativen Sicherheitsbefunde der Runden 4 und 5 (ADR-016) verbleiben drei architektonische Strukturthemen:
1. **Cluster-HA (K-K14):** `HitLStepUpApprovalService`, `McpSessionStore` und `TokenRevocationService` arbeiten prozesslokal im In-Memory-Speicher (`ConcurrentDictionary`). In horizontal skalierten Kubernetes-/Multi-Node-Umgebungen führt dies zu State-Inkonsistenzen bei Step-Up-Tickets und Session-Routing.
2. **Lexer-/Dialekt-Differentials (SQ-01, SQ-02, SQ-05):** Der SQL-Rewriter manipuliert Tokens im Trino-Stream. Dies erfordert Denylists und Lexer-Guards für Fremddialekte (T-SQL, PostgreSQL, SQLite, Oracle).
3. **Fragmentierte Rollenprüfungen (K-K10):** Autorisierungsprüfungen sind über drei Mechanismen verstreut (`GatewayPolicies.HasAnyRole`, `Sid.GetUserRoles`, `context.User.IsInRole`).

## Entscheidung

### 1. Pluggable Multi-Node Cluster State (K-K14)
- Einführung von `IDistributedClusterStateProvider` mit Implementierungen für **Redis** (`RedisClusterStateProvider` via StackExchange.Redis) und In-Memory (`InMemoryClusterStateProvider`).
- **Hybrid-Caching (L1 Memory + L2 Distributed Store):** Extrem schnelle lokale In-Memory-Prüfungen mit sofortiger Pub/Sub-Invalidierung über Cluster-Events.
- **Fail-Closed-Semantik:** Bei Cluster-Partitionierung greifen lokale Sicherheitsregeln, administrative Aktionen (z. B. Step-Up-Genehmigungen) werden fail-closed abgewiesen.
- Migration von `HitLStepUpApprovalService`, `McpSessionStore`, `FocusCostAccountingService` und `TokenRevocationService` auf die Cluster-State-Infrastruktur.

### 2. AST Target Dialect Generator (SQ-01, SQ-02, SQ-05)
- Übergang von heuristischem Token-Rewriting zu einer echten **Compiler-Pipeline**:
  1. ParseTree -> Dialektneutraler typisierter **AST (Intermediate Representation)** via `SqlAstBuilder`.
  2. **Security & RLS Visitor** (`AstSecurityVisitor`): Wendet Mandantenfilter, Row-Level-Security-Subqueries und Spaltenmaskierungen direkt auf AST-Knoten an.
  3. **Dialect Code Generators**: Spezifische Generatoren für `PostgreSql` (`"..."`, `$1`), `SqlServer` (`[...]`, `TOP / OFFSET FETCH`, `@p1`), `Sqlite` (`?1`), `DuckDb`, `Snowflake` und `Oracle` (`"..."` mit Uppercase-Folding, `:p1`, Omission von `AS` bei Tabellen-Aliasen in `FROM`, `NUMBER(1)`-Booleans und `OFFSET ... ROWS FETCH NEXT ... ROWS ONLY`).
- **Garantie:** Syntaktische Token-Differentials, Kommentar-Einschleusung und Dialekt-Bypässe sind konstruktiv unmöglich. ~~Default in WebSQL auf `AstCompiler` gesetzt.~~ (Ersetzt durch die Amendment-Sektion unten: Der Default ist `LegacyTokenStream`, `AstCompiler` ist opt-in.)

### 3. Einheitliche RBAC/ABAC Engine (K-K10)
- Etablierung des typisierten `GatewayRole`-Enums mit definierter Vererbungshierarchie.
- `ClaimsNormalizationMiddleware`: Mappt Active Directory Windows-SIDs, OIDC-Claims und Zertifikate beim Request-Eintritt auf kanonische Claims.
- `IGatewayRoleEvaluator`: Single Source of Truth für alle Autorisierungsentscheidungen mit Unterstützung für mandantenqualifizierte Rollen (`TenantId:Role`).

## Konsequenzen

### Positiv
- Vollständige Multi-Node- und Multi-Region-Fähigkeit ohne Session-Affinity/Sticky Sessions.
- Physische Immunität gegen SQL-Lexer- und Syntax-Differentials.
- Einheitliche, transparente und auditierbare Autorisierung an allen Gateway-Schnittstellen.

### Negativ
- Zusätzliche optionale Infrastrukturabhängigkeit (Redis oder NATS Cluster im Produktionsbetrieb).
- Höherer anfänglicher Entwicklungsaufwand für den AST-Code-Generator.

## Anhang: Inventar verbleibender lokaler Zustände (Stand 09.10.2026, AR-04-Rest)

Im Rahmen der Architekturüberarbeitung (Plan `plan-architektur-distributed-state-invalidation.md`, AR-01 bis AR-04) wurden Access Profile Caches, ReBAC Generation Caches, FinOps Hard-Limit-Zähler und Differential Privacy Epsilon-Budgets auf den Cluster-Store migriert bzw. per Epochen-/Pull-Validierung abgesichert.

Folgende prozesslokale Zustände verbleiben vorerst im Monolithen und werden wie folgt bewertet:

| Komponente | Zustandstyp | Sicherheitsrelevanz | Bewertung & Folgeplan |
|---|---|---|---|
| `HitLStepUpApprovalService` | In-Memory Tickets (`ConcurrentDictionary`) | **Hoch** | Single-Node-Genehmigung; in Multi-Node-Umgebung müssen Step-Up-Tickets clusterweit verifiziert werden. Eigener Architekturplan zur Migration auf Cluster-Store mit Fail-Closed-Semantik. |
| `McpSessionStore` | In-Memory Sessions | **Mittel** | Pod-Neustart oder Lastverteilung auf andere Replicas führt zu MCP-Session-Verlust / Re-Handshake. Funktionale Auswirkung, kein Autorisierungsbypass. |
| `ClientTierResolver` | In-Memory Tier-Cache | **Niedrig** | Statische/Konfigurierte Tier-Mappings; geringe Updatefrequenz, Re-Fetch bei Cache-Miss unkritisch. |
| `DbtHealthCircuitBreaker` | Circuit Breaker State | **Niedrig** | Health/Circuit-Breaker-Status lokal pro Instanz isoliert; lokale Auswertung verhindert Kaskadierungsausfälle. |
| `SubgraphCanaryRouter` | Canary Weights / Counters | **Niedrig** | Lokale Routing-Verteilung führt über Law of Large Numbers zu korrekten globalen Verhältnissen. |
| `Golden Queries Cache` | Read-Cache für Referenzqueries | **Keine** | Reiner Lese-Cache zur Performance-Optimierung ohne Rechteprüfung oder State-Mutation. |


## Amendment 2026-10-11: §2 AST Target Dialect Generator (supersedes the §2 "Garantie" paragraph)

### Decision

1. **Target state: one code path.** The governed SQL path has exactly one implementation: the AST compiler (token guards, parse, typed AST, simplify the user tree, inject typed security predicates and masks, verify coverage, validate dialect capabilities, emit, check the emitted text, bind). The token-stream rewriter (`RlsListener`), the `WebSql:SqlRewriterEngine` setting and the `ShadowDualRun` mode are removed in a single, revertable cutover commit. After the cutover there is no runtime engine switch and no shadow mode; rollback is a revert of that commit.
2. **Engine selection until the cutover.** The AST compiler is opt-in. The default engine is the legacy token-stream rewriter (`LegacyTokenStream`). The earlier statement that `AstCompiler` is the WebSQL default is withdrawn. Confidence comes from a pre-merge evidence gate (differential corpus against legacy, property and grammar fuzzing, execution on real engines, security regression suites), not from a production observation window.
3. **All values are bound.** User literals, tenant, policy and mask values reach the database as bind parameters. Only validated structural integers (row limits, ordinals, frame offsets, type parameters) and keywords are emitted inline. Row filters and column masks are typed AST nodes; raw trusted SQL fragments are not used.
4. **Dialect limits come from a capability table** (bind-parameter count, IN-list size, identifier length). A statement over a limit is rejected with a typed error and is never split or truncated.
5. **Tiers.** Production at the compiler tier: SQL Server, PostgreSQL, DuckDB and Oracle (Oracle with a runtime driver, TCPS and non-privileged accounts; the driver license is reviewed before release). Databricks is Experimental until a live warehouse run passes. Snowflake is experimental (generator only, never executable in production). ANSI is internal.
6. **DML on the same path.** `INSERT`, `UPDATE`, `DELETE` and `MERGE` are compiled by the same pipeline. The check option, the unfiltered-DML guard, policy-column and masked-column write protection and the forced tenant are enforced by the compiler for every dialect, independent of database features. Statements that need a row-count check run only through the checked DML executor.
7. **Tenant equality is byte-exact** on every dialect regardless of column collation or session settings. Tenant ids that differ only in case are denied per request (`TENANT_ID_COLLISION`); `Gateway:TenantIsolation:StrictCollisionStartup` additionally refuses the start outside Development.
8. Input token guards (`SqlTokenSecurityOptions`) remain as defense in depth.

### Assurance wording

The former statement that token differentials, comment injection and dialect bypasses are "constructively impossible" is withdrawn. The security properties are named invariants (INV-1 to INV-16 in the implementation plan) that are enforced in code and tested by the compiler's test suites and the evidence gate.

### Consequences

- Positive: one compiler for governed SQL; no policy drift between engines; structural removal of raw-fragment splicing; typed, sanitized errors.
- Negative: admin-authored row-filter predicates are written in canonical (Trino) syntax; consumers that render text (GraphQL tree compiler and other text consumers) keep the legacy text path until they are moved to the typed policy model; Databricks evidence depends on a Spark/Delta proxy.

Reference: [F-DIALECT-02](../features/f-dialect-02-ast-sql-compiler.md), implementation plan `docs/plans/2026-10-10-plan-ast-dialect-generator.md` (decisions SD-1 to SD-8 in section 1).
