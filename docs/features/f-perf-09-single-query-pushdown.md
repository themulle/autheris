# F-PERF-09: GraphQL-to-SQL AST Single-Query Compiler\n\n**Status:** [Done] (100% GA – Wave 3)  \n**Komponenten:** [`SingleQueryAstCompiler.cs`](file:///root/lis-git/gql/gql/src/Autheris.Application/Sql/SingleQueryAstCompiler.cs), [`ISingleQueryAstCompiler.cs`](file:///root/lis-git/gql/gql/src/Autheris.Application/Sql/ISingleQueryAstCompiler.cs)\n\n---\n\n## 1. Übersicht & Problemstellung
Klassische Gateways nutzen DataLoader, was bei tief verschachtelten Abfragen zu $N+1$-Netzwerk-Roundtrips und extrem hohem Heap-Allokationsdruck führt.

## 2. Architektur & Umsetzung
- Kompiliert mehrstufige Auswahlsätze in ein einziges relationales Statement.
- Nutzt relationale JSON-Aggregation (`FOR JSON PATH` in MSSQL, `json_agg` in Postgres, `json_group_array` in SQLite).
- Reduziert Netzwerk-Roundtrips auf exakt 1.
- Integrierter Type-Coercion-Layer für Geospatial, Binärdaten und Präzisions-Decimals.

## 3. Business Value
- Bis zu 90% geringerer Gateway-Speicherverbrauch und drastisch gesteigerter Durchsatz.\n