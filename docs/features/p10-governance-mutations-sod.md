# P10: Enterprise Governance Mutations & 4-Eyes SoD\n\n**Status:** [Done] (100% GA – Core Foundation)  \n**Komponenten:** [`MutationTypes.cs`](file:///root/lis-git/gql/gql/src/Autheris.GraphQL/Types/MutationTypes.cs), [`RedisIdempotencyStore.cs`](file:///root/lis-git/gql/gql/src/Autheris.Infrastructure/Caching/RedisIdempotencyStore.cs), [`ConsentRecertificationWorkflowService.cs`](file:///root/lis-git/gql/gql/src/Autheris.Application/Workflows/ConsentRecertificationWorkflowService.cs)\n\n---\n\n## 1. Übersicht & Problemstellung
Rechtevergabe darf nicht durch Einzelpersonen ohne Vier-Augen-Kontrolle erfolgen (Separation of Duties).

## 2. Architektur & Umsetzung
- GraphQL Mutations (`requestConsent`, `approveConsent`, `rejectConsent`) mit Anti-Self-Approval.
- 24h-Idempotenz-Keys via Redis gegen versehentliche Doppelanträge.
- Automatisierter 30-Tage DSGVO-Rezertifizierungs- und Eskalationsworkflow.

## 3. Business Value
- Einhaltung von Funktionstrennung und Governance-Richtlinien.\n