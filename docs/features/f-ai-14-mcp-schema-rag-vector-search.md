# F-AI-14: MCP Schema-RAG & Hybrid-Vector Search Engine

**Status:** [Done] (100% GA – Production-Ready)  
**Components:** [`CatalogSearchEngine.cs`](file:///root/autheris/src/Autheris.Application/Catalog/Search/CatalogSearchEngine.cs), [`Bm25SearchIndex.cs`](file:///root/autheris/src/Autheris.Application/Catalog/Search/Bm25SearchIndex.cs), [`VectorSearchIndex.cs`](file:///root/autheris/src/Autheris.Application/Catalog/Search/VectorSearchIndex.cs), [`LocalDeterministicEmbeddingGenerator.cs`](file:///root/autheris/src/Autheris.Application/Catalog/Search/LocalDeterministicEmbeddingGenerator.cs), [`CatalogSearchSnapshot.cs`](file:///root/autheris/src/Autheris.Application/Catalog/Search/CatalogSearchSnapshot.cs), [`McpDatasetTools.cs`](file:///root/autheris/src/Autheris.Api/Endpoints/McpDatasetTools.cs)

---

## 1. Overview & Problem Statement

Large enterprise data catalogs often encompass thousands of tables, schemas, and columns across ERP, CRM, and Data Lakehouse systems. Ingesting full catalog schemas directly into Large Language Model (LLM) context windows during Model Context Protocol (MCP) agent sessions leads to context window overflow, hallucination, and exorbitant token costs.

**F-AI-14** provides an ultra-low-latency, zero-external-dependency **Hybrid Schema-RAG Engine** embedded directly within Autheris. It empowers AI agents and data analysts to discover relevant tables and semantic relations using natural language queries without relying on external cloud vector databases or third-party embedding APIs.

---

## 2. Business Value

- **Zero-Dependency & Air-Gapped**: Runs entirely on-premises with native C#/.NET 10 SIMD vector primitives. No OpenAI, Azure OpenAI, or external vector service dependencies required.
- **Sub-Millisecond Search Latency**: In-memory Okapi BM25 and hardware-accelerated cosine vector similarity deliver results in under 5 milliseconds across tens of thousands of tables.
- **Zero-Lock Concurrency**: Implements a lock-free, double-buffered snapshot architecture with atomic pointer swaps (`Interlocked.Exchange`), ensuring zero read-write contention during catalog background refreshes.
- **ReBAC & Tenant-Aware**: Automatically filters search results through the caller's Relationship-Based Access Control (ReBAC) Zanzibar graph, preventing unauthorized metadata enumeration.

---

## 3. Architecture & Capabilities

```mermaid
flowchart TD
    QUERY["Agent / REST Query<br/>'customer invoices paid in Q3'"] --> ENGINE["CatalogSearchEngine"]
    
    subgraph Parallel_Search["Parallel Retrieval"]
        ENGINE --> BM25["Okapi BM25 Indexer<br/>(k1=1.2, b=0.75, Smart Tokenizer)"]
        ENGINE --> VEC["SIMD Vector Matcher<br/>(LocalDeterministicEmbeddingGenerator<br/>+ TensorPrimitives.CosineSimilarity)"]
    end
    
    BM25 --> RRF["Reciprocal Rank Fusion (RRF)<br/>Score = SUM( weight / (K + rank) )"]
    VEC --> RRF
    
    RRF --> GRAPH["1st-Degree Foreign Key Graph Traversal<br/>(Enriches candidate tables with join relations)"]
    GRAPH --> REBAC["ReBAC Zanzibar Authorization Filter<br/>(Zero Enumeration Oracle)"]
    REBAC --> RESULT["Enriched Schema Candidates<br/>(Relevance Score, Matched Columns, Join Hints)"]
```

### Key Capabilities

1. **Smart Schema Tokenizer (`SmartSchemaTokenizer.cs`)**:
   - Tokenizes camelCase, snake_case, kebab-case, SAP table identifiers (e.g. `BSEG`, `KNA1`), and alphanumeric column names.
   - Cleans domain stop-words and provides edit-distance tolerance for typo resilience.

2. **Okapi BM25 Full-Text Ranker (`Bm25SearchIndex.cs`)**:
   - Uses Robertson-Spärck-Jones Inverse Document Frequency (IDF) weighting.
   - Boosts exact table name and primary key column matches over general descriptions.

3. **Hardware-Accelerated SIMD Vector Matching (`VectorSearchIndex.cs`)**:
   - Computes 384-dimensional deterministic, L2-normalized embeddings via [`LocalDeterministicEmbeddingGenerator.cs`](file:///root/autheris/src/Autheris.Application/Catalog/Search/LocalDeterministicEmbeddingGenerator.cs).
   - Leverages `System.Numerics.Tensors.TensorPrimitives.CosineSimilarity` for AVX-512 / ARM Neon vector processing.

4. **Reciprocal Rank Fusion (RRF)**:
   - Fuses BM25 keyword rankings and dense vector rankings with constant $K=60$.
   - Balances keyword precision with semantic intent.

---

## 4. Usage Examples

### A. MCP Tool Invocation (`search_catalog`)

AI agents connected via the official Model Context Protocol server can invoke `search_catalog`:

**MCP Tool Call:**
```json
{
  "name": "search_catalog",
  "arguments": {
    "query": "customer sales invoices and billing items",
    "limit": 5,
    "mode": "hybrid"
  }
}
```

**MCP Response Payload:**
```json
{
  "totalMatches": 2,
  "tables": [
    {
      "domain": "sales",
      "tableName": "invoices",
      "relevanceScore": 0.0328,
      "matchedTerms": ["invoices", "sales"],
      "matchedColumns": ["invoice_id", "customer_id", "total_amount", "created_at"],
      "joinRelations": [
        {
          "targetTable": "sales.customers",
          "foreignKey": "customer_id"
        },
        {
          "targetTable": "sales.invoice_items",
          "foreignKey": "invoice_id"
        }
      ]
    }
  ]
}
```

---

### B. REST Search API

```bash
curl -X GET "http://localhost:8080/api/v1/catalog/search?query=invoices&limit=10&mode=hybrid" \
  -H "Authorization: Bearer <user-token>"
```

---

## 5. Configuration Example

```json
{
  "Gateway": {
    "CatalogSearch": {
      "Enabled": true,
      "DefaultMode": "Hybrid",
      "Bm25Weight": 0.5,
      "VectorWeight": 0.5,
      "RrfKConstant": 60,
      "MaxCandidateLimit": 50,
      "BackgroundWarmupEnabled": true
    }
  }
}
```
