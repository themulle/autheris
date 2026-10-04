# F-PERF-11: Multi-Tenant Isolated Query Plan Cache & Kestrel Tuning\n\n**Status:** [Done] (100% GA – Benchmark)  \n**Komponenten:** [`CompiledSqlQueryPlanCache.cs`](file:///root/lis-git/gql/gql/src/Autheris.Application/Sql/CompiledSqlQueryPlanCache.cs), [`ICompiledSqlQueryPlanCache.cs`](file:///root/lis-git/gql/gql/src/Autheris.Application/Sql/ICompiledSqlQueryPlanCache.cs)\n\n---\n\n## 1. Übersicht & Problemstellung
Das wiederholte Parsen und Planen von Queries im Hot Path limitiert den Durchsatz in Hochlast-Szenarien (`hasura/graphql-bench`). Geteilte Caches bergen zudem RLS-Cache-Poisoning-Risiken.

## 2. Architektur & Umsetzung
- Lock-free Plan-Lookup via `XxHash3` 64-Bit-Hashing in < 1 µs.
- Strikte Isolierung per Composite Key `(QueryHash, Dialect, TenantId, RlsHash)` gegen Cache-Poisoning (SEC-CACHE-01).
- Kestrel Socket- und Server-GC-Tuning mit Dynamic Adaptation (DATAS).

## 3. Business Value
- Maximale Request-Verarbeitung pro Sekunde bei absolut sicherer Mandantentrennung.\n