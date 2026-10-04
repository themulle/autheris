# ADR-017: Multi-Node State Synchronisation, AST Dialect Generator und RBAC-Konsolidierung

## Status
Vorgeschlagen

## Kontext
Nach erfolgreicher Behebung aller 100 operativen Sicherheitsbefunde der Runden 4 und 5 (ADR-016) verbleiben drei architektonische Strukturthemen:
1. **Cluster-HA (K-K14):** `HitLStepUpApprovalService`, `McpSessionStore` und `TokenRevocationService` arbeiten prozesslokal im In-Memory-Speicher (`ConcurrentDictionary`). In horizontal skalierten Kubernetes-/Multi-Node-Umgebungen führt dies zu State-Inkonsistenzen bei Step-Up-Tickets und Session-Routing.
2. **Lexer-/Dialekt-Differentials (SQ-01, SQ-02, SQ-05):** Der SQL-Rewriter manipuliert Tokens im Trino-Stream. Dies erfordert Denylists und Lexer-Guards für Fremddialekte (T-SQL, PostgreSQL, SQLite).
3. **Fragmentierte Rollenprüfungen (K-K10):** Autorisierungsprüfungen sind über drei Mechanismen verstreut (`GatewayPolicies.HasAnyRole`, `Sid.GetUserRoles`, `context.User.IsInRole`).

## Entscheidung

### 1. Pluggable Multi-Node Cluster State (K-K14)
- Einführung von `IDistributedClusterStateProvider` mit Implementierungen für **Redis** (StackExchange.Redis) und **NATS** (JetStream KV / PubSub).
- **Hybrid-Caching (L1 Memory + L2 Distributed Store):** Extrem schnelle lokale In-Memory-Prüfungen mit sofortiger Pub/Sub-Invalidierung über Cluster-Events.
- **Fail-Closed-Semantik:** Bei Cluster-Partitionierung greifen lokale Sicherheitsregeln, administrative Aktionen (z. B. Step-Up-Genehmigungen) werden fail-closed abgewiesen.

### 2. AST Target Dialect Generator (SQ-01, SQ-02, SQ-05)
- Übergang von heuristischem Token-Rewriting zu einer echten **Compiler-Pipeline**:
  1. ParseTree -> Dialektneutraler typisierter **AST (Intermediate Representation)**.
  2. **Security & RLS Visitor**: Wendet Mandantenfilter, Row-Level-Security-Subqueries und Spaltenmaskierungen direkt auf AST-Knoten an.
  3. **Dialect Code Generators**: Spezifische Generatoren für `PostgreSql` (`"..."`, `$1`), `SqlServer` (`[...]`, `TOP / OFFSET FETCH`, `@p1`) und `Sqlite`.
- **Garantie:** Syntaktische Token-Differentials, Kommentar-Einschleusung und Dialekt-Bypässe sind konstruktiv unmöglich.

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
