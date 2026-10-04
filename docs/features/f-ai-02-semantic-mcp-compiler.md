# F-AI-02: Semantic MCP Compiler & Schema Grounding\n\n**Status:** [Done] (100% GA – Wave 1)  \n**Komponenten:** [`SemanticMcpCompiler.cs`](file:///root/lis-git/gql/gql/src/Autheris.Application/Mcp/Services/SemanticMcpCompiler.cs), [`AiDataGuardrailService.cs`](file:///root/lis-git/gql/gql/src/Autheris.Application/Mcp/Services/AiDataGuardrailService.cs)\n\n---\n\n## 1. Übersicht & Problemstellung
Autonome KI-Agenten scheitern oft an reinen API-Signaturen ohne fachliche Semantik (Halluzinationen über Kennzahlen, falsche Filter).

## 2. Architektur & Umsetzung
- Kompiliert dbt-Beschreibungen, Model Grains und OpenMetadata Business Glossaries in MCP-Tool-Definitionen.
- Stellt dynamische MCP-Ressourcen bereit (`glossary://`, `dbt://models/...`).
- Verankert Fachsemantik direkt in den Prompt- und Tool-Constraints für LLMs.

## 3. Business Value
- Drastische Reduktion von Halluzinationen bei LLM-gestützten Datenabfragen.
- Sofortige Nutzbarkeit für Claude, Cursor, AutoGen und Custom Enterprise Agents.\n