# ADR-017: Multi-Node State Synchronisation, AST Dialect Generator und RBAC-Konsolidierung

## Status
Teilweise umgesetzt (09.10.2026: AR-01 Epochen-Keying, AR-02/AR-03 ReBAC-Pull-Validierung, AR-04 Atomare DP-/FinOps-Cluster-Zähler umgesetzt; Session-/HitL-Migration ausstehend)

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
- **Garantie:** Syntaktische Token-Differentials, Kommentar-Einschleusung und Dialekt-Bypässe sind konstruktiv unmöglich. Default in WebSQL auf `AstCompiler` gesetzt.

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

