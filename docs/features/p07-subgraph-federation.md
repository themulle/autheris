# P7: Subgraph Federation Router (Hot Chocolate Fusion)\n\n**Status:** [Done] (100% GA – Core Foundation)  \n**Komponenten:** [`SubgraphSecurityDelegatingHandler.cs`](file:///root/lis-git/gql/gql/src/Autheris.GraphQL/Federation/SubgraphSecurityDelegatingHandler.cs), [`SubgraphResultMaskingMiddleware.cs`](file:///root/lis-git/gql/gql/src/Autheris.GraphQL/Federation/SubgraphResultMaskingMiddleware.cs)\n\n---\n\n## 1. Übersicht & Problemstellung
Föderierte Subgraphen müssen performant orchestriert werden, ohne die Zero-Trust-Sicherheit aufzugeben.

## 2. Architektur & Umsetzung
- Hot Chocolate Fusion Subgraph Router (v16.6.7).
- Zero-Trust Client Token Forwarding an Subgraphen.
- In-Memory Result Masking auf aggregierten föderierten Abfrageergebnissen.

## 3. Business Value
- Hochperformante Federation ohne Vertrauensvorschuss an externe Subgraphen.\n