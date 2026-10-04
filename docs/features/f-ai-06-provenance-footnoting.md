# F-AI-06: Explainable AI & Revisionssichere Provenance Footnotes\n\n**Status:** [Done] (100% GA – Wave 1)  \n**Komponenten:** [`McpProvenanceEnricher.cs`](file:///root/lis-git/gql/gql/src/Autheris.Application/Mcp/Services/McpProvenanceEnricher.cs)\n\n---\n\n## 1. Übersicht & Problemstellung
Regulatorische Anforderungen (z. B. EU AI Act) fordern lückenlose Nachvollziehbarkeit darüber, auf welchen Datengrundlagen KI-Entscheidungen basieren.

## 2. Architektur & Umsetzung
- Injiziert einen revisionssicheren `_provenance`-Block in jede MCP-Tool-Antwort.
- Dokumentiert Upstream-dbt-Modell, Commit-SHA, OpenMetadata-URN, Datenfrische und angewandte Maskierungsregeln.

## 3. Business Value
- Erfüllung der Transparenz- und Nachweispflichten des EU AI Acts und interner Compliance-Audits.\n