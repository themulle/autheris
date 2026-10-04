# F-AI-07: Dynamic Semantic Schema Pruning & Just-in-Time MCP Tools

**Status:** **100% (GA) ✅ (Implementiert & Security-Audited 2026-10-02)**  
**Komponenten:** [`ISemanticToolPruner.cs`](file:///root/lis-git/gql/gql/src/Autheris.Application/AI/Interfaces/ISemanticToolPruner.cs), [`SemanticToolPruner.cs`](file:///root/lis-git/gql/gql/src/Autheris.Application/AI/Services/SemanticToolPruner.cs), [`McpProtocolHandler.cs`](file:///root/lis-git/gql/gql/src/Autheris.Application/AI/Services/McpProtocolHandler.cs), [`McpOptions.cs`](file:///root/lis-git/gql/gql/src/Autheris.Domain/Options/GatewayOptions.cs)  
**Referenzen:** [`implementation-plan-welle-1-2026-10-02.md`](file:///root/lis-git/gql/gql/docs/architecture/implementation-plan-welle-1-2026-10-02.md), [`security-review-welle-1-2026-10-02.md`](file:///root/lis-git/gql/gql/docs/threat-model/security-review-welle-1-2026-10-02.md)

---

## 1. Übersicht & Problemstellung
Große Enterprise-Supergraphs mit hunderten Typen sprengen das Token-Budget im System-Prompt von LLMs (30.000 bis 60.000 Tokens nur für Werkzeugsignaturen). Dies führt zu hohen Inferenzkosten, Latenzen und Fehlentscheidungen der Agenten.

## 2. Architektur & Umsetzung
- **Semantische Vorfilterung zur Laufzeit:** Der `SemanticToolPruner` vergleicht den Benutzer-Prompt mit Metadaten aus dbt, OpenMetadata und den registrierten MCP-Tools.
- **Just-in-Time (JIT) Tool-Injektion:** Anstatt hunderte Schemadefinitionen zu übergeben, werden dynamisch nur die $N$ relevantesten Tools (Standard: bis zu 8) in die MCP `tools/list`-Antwort aufgenommen.
- **Token-Budget Bounding:** Konfigurierbares Token-Limit (`MaxToolDefinitionTokens`, Standard: 4.000 Tokens) verhindert Kontext-Exhaustion und Denial of Wallet.
- **Fail-Closed & Fallback:** Reines Vektor-/Distanz- und Schlüsselwortmatching ohne LLM-Inferenz im Pruner selbst; immun gegen Prompt-Injections. Bei nicht eindeutigen Prompts werden konfigurierte Standard-Tools ausgeliefert.

## 3. Konfigurationsbeispiel (`appsettings.json`)
```json
{
  "Gateway": {
    "Mcp": {
      "DynamicToolPruning": {
        "Enabled": true,
        "MaxTools": 8,
        "MaxToolDefinitionTokens": 4000,
        "SemanticThreshold": 0.45
      }
    }
  }
}
```

## 4. Business Value & Differenzierung
- **Bis zu 80 % Ersparnis** bei System-Prompt-Tokens für autonome KI-Agenten.
- **Reduzierte Time-to-First-Token (TTFT)** und drastisch verminderte Halluzinationsrate bei Agentic-Workflows.
- **Wettbewerbsvorteil gegen Apollo GraphOS:** Apollo exponiert rohe Schemas ungefiltert; Autheris kuratiert den Modell-Kontext semantisch.
