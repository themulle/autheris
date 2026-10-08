# F-AI-07: Dynamic Semantic Schema Pruning & Just-in-Time MCP Tools

**Status:** [Done] (100% GA – Wave 2)  
**Components:** [`IDynamicSemanticSchemaPruner.cs`](file:///root/autheris/src/Autheris.Application/Mcp/Interfaces/IDynamicSemanticSchemaPruner.cs), [`DynamicSemanticSchemaPruner.cs`](file:///root/autheris/src/Autheris.Application/Mcp/Services/DynamicSemanticSchemaPruner.cs)

---

## 1. Overview & Problem Statement

Enterprise schemas often span hundreds of tables and thousands of fields. Exposing an entire enterprise catalog to an LLM context window exhausts context tokens, drives up inference costs, and overwhelms agent reasoning with irrelevant tools. F-AI-07 employs semantic vector embeddings and session intent analysis to dynamically prune tool catalogs down to the 5–10 most relevant tools in real time ('Just-in-Time MCP Tool Generation').

---

## 2. Business Value

- **70%+ Context Window Savings**: Decreases prompt token consumption by stripping away hundreds of irrelevant tools.
- **Sharper Agent Focus**: Significantly increases reasoning accuracy by eliminating distractors and ambiguous tool choices.
- **Reduced Latency**: Faster TTFT (Time to First Token) due to substantially smaller system and tool prompt payloads.

---

## 3. Architecture & Capabilities

- Vector similarity matching between agent prompt queries and catalog tool descriptions.
- Session-aware tool retention keeping recently accessed entity tools warm in the active session.
- Dynamic MCP `tools/list_changed` notification triggering real-time client-side tool synchronization.

---

## 4. Usage Example

```bash
# Agent sends high-level intent to dynamically prune tools
curl -X POST http://localhost:8080/mcp \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer <agent-token>" \
  -d '{
    "jsonrpc": "2.0",
    "id": 6,
    "method": "tools/prune_by_intent",
    "params": {
      "intent": "Analyze quarterly customer invoice overdue payments and ledger status",
      "maxTools": 5
    }
  }'

# Response returns only billing- and invoice-related tools, pruning hundreds of others
```

---

## 5. Configuration Example

```json
{
  "Gateway": {
    "Mcp": {
      "DynamicSchemaPruning": {
        "Enabled": true,
        "DefaultMaxToolsPerSession": 10,
        "SimilarityThreshold": 0.75,
        "VectorEmbeddingModel": "text-embedding-3-small"
      }
    }
  }
}
```
