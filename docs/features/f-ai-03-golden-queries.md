# F-AI-03: Dynamic Few-Shot Golden Query Injection\n\n**Status:** [Done] (100% GA – Wave 2)  \n**Komponenten:** [`GoldenQueryService.cs`](file:///root/lis-git/gql/gql/src/Autheris.Application/Mcp/Services/GoldenQueryService.cs), `examples://{domain}/{table}` MCP-Ressourcen, Tool `get_golden_queries`\n\n---\n\n## 1. Übersicht & Problemstellung
Bei komplexen verschachtelten Filtern und Aggregationen scheitert Zero-Shot Prompting bei KI-Agenten in 20-30% der Fälle.

## 2. Architektur & Umsetzung
- Ingestion verifizierter Produktionsabfragen aus Audit-Logs.
- Bounded Cache (max. 5.000 Einträge, 64 KB Input Validation / SEC-03).
- Dynamische Bereitstellung passender Abfragebeispiele als Few-Shot Kontext für MCP-Agenten.

## 3. Business Value
- Hebt die First-Try-Erfolgsrate autonomer Agenten auf über 95%.\n