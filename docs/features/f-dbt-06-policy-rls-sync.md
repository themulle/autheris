# F-DBT-6: Policy & RLS Auto-Sync aus dbt Metadaten\n\n**Status:** [Done] (100% GA – Wave 1)  \n**Komponenten:** [`InMemoryDbtProposalRepository.cs`](file:///root/lis-git/gql/gql/src/Autheris.Infrastructure/Persistence/InMemoryDbtProposalRepository.cs), [`DbtMetadataIngestionService.cs`](file:///root/lis-git/gql/gql_extensions/src/Autheris.Extensions/Dbt/DbtMetadataIngestionService.cs)\n\n---\n\n## 1. Übersicht & Problemstellung
Data Governance- und Sicherheitsregeln (ABAC/RLS) werden oft im dbt-Modell (`meta.casbin_roles`, `meta.rls_filter`) definiert, müssen aber mühsam separat im Gateway konfiguriert werden.

## 2. Architektur & Umsetzung
- Ingestion liest `meta`-Blöcke aus dbt-Modellen aus.
- Erstellt strukturierte Proposals im Proposal-Repository.
- Erfordert Vier-Augen-Freigabe (`Four-Eyes Principle`) durch autorisierte Sicherheitsadministratoren vor der Aktivierung im Hot Path.

## 3. Business Value
- Schließt die Lücke zwischen Data Governance und Data Engineering.
- Zero-Trust Sicherheit: Keine ungeprüfte automatische Rechtevergabe.\n