# F-AI-09: Native Vector Database & RAG Egress (pgvector, Qdrant, Milvus)

**Status:** [Done] (100% GA – Next-Gen)  
**Components:** [`IVectorRecordSource.cs`](file:///root/lis-git/autheris/src/Autheris.Application/Connectors/IVectorRecordSource.cs), [`VectorRagEgressExecutor.cs`](file:///root/lis-git/autheris/src/Autheris.Application/Vector/VectorRagEgressExecutor.cs)

---

## 1. Overview & Problem Statement

Enterprise Retrieval-Augmented Generation (RAG) applications frequently bypass API gateways, connecting directly to vector databases (such as pgvector, Qdrant, or Milvus) and inadvertently leaking multi-tenant chunks and unredacted PII. F-AI-09 integrates vector database sources directly into the Autheris Zero-Trust pipeline. Vector similarity queries pass through mandatory tenant isolation filters, Casbin ABAC evaluation, and in-stream PII redaction before reaching the RAG consumer.

---

## 2. Business Value

- **Zero-Trust RAG Security**: Ensures embeddings and retrieved document chunks never cross tenant boundaries or leak sensitive personal data.
- **Unified Federation**: Enables single GraphQL queries joining structured relational data (e.g. ERP order history) with unstructured vector knowledge embeddings.
- **Audit Trail for AI Context**: Every document chunk fed into an LLM context is logged to the immutable WORM audit chain.

---

## 3. Architecture & Capabilities

- Native support for pgvector (PostgreSQL), Qdrant, and Milvus vector stores.
- Dynamic cosine, dot-product, and Euclidean distance scoring pushed down to database vector indexes.
- Real-time PII masking applied to chunk text payloads prior to returning to the caller.

---

## 4. Usage Example

```graphql
# Query semantic vector embeddings with structured metadata join
query SearchKnowledgeBase {
  vectorSearch(
    collection: "internal_policies"
    vector: [0.024, -0.015, 0.142, 0.089]
    topK: 3
    filter: { department: "HumanResources" }
  ) {
    id
    similarityScore
    contentChunk # PII automatically masked
    department
    metadata {
      documentId
      version
    }
  }
}
```

---

## 5. Configuration Example

```json
{
  "Gateway": {
    "VectorSources": {
      "Policies": {
        "Provider": "PgVector",
        "ConnectionString": "Host=pg-vector.corp.local;Database=Knowledge;Username=autheris_app;Password=secret",
        "CollectionTable": "hr_document_chunks",
        "EmbeddingDimensions": 1536,
        "DistanceMetric": "Cosine",
        "EnableInStreamPiiScrubbing": true
      }
    }
  }
}
```
