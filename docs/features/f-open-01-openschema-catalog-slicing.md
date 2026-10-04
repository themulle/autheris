# F-OPEN-01: OpenSchema Mode, Multi-File OpenAPI & Catalog Slicing\n\n**Status:** [Done] (100% GA – Wave 2)  \n**Komponenten:** Domain-Scoping `/graphql/{domain}`, Catalog Slicing mit Paging/Filtering, Multi-File OpenAPI\n\n---\n\n## 1. Übersicht & Problemstellung
Sehr große Schemas mit zehntausenden Typen führen zu Introspection-Timeouts in IDEs und unübersichtlichen Monolith-Dokumentationen.

## 2. Architektur & Umsetzung
- Domänenweises Schema-Scoping (`/graphql/{domain}`).
- Paginierter und filterbarer Katalog-Zugriff.
- Aufteilung der OpenAPI-Spezifikationen nach Domänen.

## 3. Business Value
- Skaliert auf Enterprise-Landschaften mit tausenden Tabellen ohne Performance-Einbußen.\n