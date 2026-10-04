# F-AI-09: Native Vector Database & RAG Egress (pgvector, Qdrant, Milvus)

**Status:** **100% (GA) ✅ (Implementiert & Security-Audited 2026-10-04)**  
**Komponenten:** [`IVectorRecordSource.cs`](file:///root/lis-git/gql/gql/src/Autheris.Application/Connectors/IVectorRecordSource.cs), [`VectorPushdownSecurityHelper.cs`](file:///root/lis-git/gql/gql/src/Autheris.Application/Connectors/VectorPushdownSecurityHelper.cs), [`ChunkPiiRedactor.cs`](file:///root/lis-git/gql/gql/src/Autheris.Application/Security/ChunkPiiRedactor.cs), [`PgVectorConnector.cs`](file:///root/lis-git/gql/gql/src/Autheris.Infrastructure/Connectors/PgVectorConnector.cs), [`QdrantVectorConnector.cs`](file:///root/lis-git/gql/gql/src/Autheris.Infrastructure/Connectors/QdrantVectorConnector.cs), [`MilvusVectorConnector.cs`](file:///root/lis-git/gql/gql/src/Autheris.Infrastructure/Connectors/MilvusVectorConnector.cs), [`GovernedExecutionKernel.cs`](file:///root/lis-git/gql/gql/src/Autheris.Application/Kernel/GovernedExecutionKernel.cs), [`GatewayMcpQueryExecutor.cs`](file:///root/lis-git/gql/gql/src/Autheris.GraphQL/Mcp/GatewayMcpQueryExecutor.cs)  
**Referenzen:** [`architecture-implementation-plan-f-ai-09-f-ai-10.md`](file:///root/.gemini/antigravity-cli/brain/0c0319e4-ab0a-420f-9bfb-e8e9d9cf922a/architecture-implementation-plan-f-ai-09-f-ai-10.md)

---

## 1. Executive Summary & Problemstellung

Autonome Enterprise-KI-Agenten und LLM-Anwendungen erfordern den nahtlosen Zugriff auf unstrukturierte Wissensquellen (PDFs, Notizen, Wissensdatenbanken, Dokument-Chunks) über Retrieval-Augmented Generation (RAG). Herkömmliche Vektor-Datenbanken (pgvector, Qdrant, Milvus) werden in Enterprise-Architekturen jedoch isoliert betrieben und umgehen häufig die im Gateway etablierten Sicherheitsmechanismen:
- **Fehlende Mandantentrennung:** Vektorsuchen durchsuchen oft globale Sammlungen ohne erzwungene Tenant-Filterung, was zu verheerenden Mandanten-Datenlecks führt.
- **Keine RLS- und ABAC-Konsistenz:** Berechtigungsregeln aus relationalen Datenbanken und Metadatenkatalogen (z. B. dbt, OpenMetadata) greifen nicht auf Dokument-Chunks durch.
- **PII-Exposition in LLM-Kontexten:** Unmaskierte personenbezogene Daten (E-Mails, IBANs, Telefonnummern) in Text-Chunks gelangen ungefiltert in den Inferenz-Prompt von KI-Modellen.

Mit **F-AI-09** integriert **Autheris** eine native Vektor- und RAG-Egress-Schicht in das standardisierte Connector-SPI (`F-ARCH-10`). Vektorabfragen werden über GraphQL und das Model Context Protocol (MCP) einheitlich gesteuert, mit relationalen Daten verknüpft und vor der Auslieferung an KI-Agenten strikt gefiltert und PII-maskiert.

---

## 2. Architektur & Datenfluss

```
+-----------------------------------------------------------------------------------+
|                        Autonomous AI Agent / MCP Client                           |
+-----------------------------------------------------------------------------------+
                                          |
                                          | MCP Tool: search_rag_context
                                          | { collection, query_vector, top_k }
                                          v
+-----------------------------------------------------------------------------------+
| GovernedExecutionKernel (ExecuteVectorQueryAsync)                                 |
|   1. SecurityPrincipalContext & Tenant Validation (Fail-Closed)                   |
|   2. Table/Collection Metadata Resolution ($catalog)                              |
|   3. Casbin ABAC & OpenFGA ReBAC Permission Verification                          |
|   4. Security Predicate Synthesis (Tenant-Filter + RLS Predicates)                 |
+-----------------------------------------------------------------------------------+
                                          | Governed VectorQueryRequest
                                          v
+-----------------------------------------------------------------------------------+
| IVectorRecordSource Connector SPI (F-ARCH-10)                                     |
|   +-----------------------+ +-----------------------+ +-------------------------+ |
|   | PgVectorConnector     | | QdrantVectorConnector | | MilvusVectorConnector   | |
|   | - Parameterized HNSW  | | - Structured Filter   | | - Boolean Expression    | |
|   | - Cosine/L2/IP Push   | |   Payload PayloadPush |   Expression Pushdown     | |
|   +-----------------------+ +-----------------------+ +-------------------------+ |
+-----------------------------------------------------------------------------------+
                                          | Raw VectorQueryResult (Document Chunks)
                                          v
+-----------------------------------------------------------------------------------+
| ChunkPiiRedactor (In-Stream PII Scrubbing)                                        |
|   - Regex-basierte Schwärzung: E-Mails, IBANs, Phone, Credit Cards                |
|   - Erhalt der semantischen Struktur bei vollständiger Maskierung von Geheimnissen |
+-----------------------------------------------------------------------------------+
                                          |
                                          v
                              Sanitized RAG Context Chunks
```

---

## 3. Sicherheitskontrollen (SEC-VEC-01 bis SEC-VEC-05)

1. **Erzwungene Mandantentrennung (SEC-VEC-01):** Jeder Vektor-Abfrageaufruf erfordert eine valide `TenantId` im Sicherheitskontext. Der Connector injiziert den Mandantenfilter deterministisch in die Where-Klausel der Vektordatenbank (z. B. `tenant_id = @tenant` in pgvector, Must-Match Payload in Qdrant).
2. **SQL- & Filter-Injection Schutz (SEC-VEC-02):** Identifier (Collection- und Tabellennamen) werden gegen eine Whitelist validiert. Vektoren und Metadatenfilter werden ausschließlich typisiert als SQL-Parameter oder strukturierte JSON/Proto-Payloads gebunden.
3. **In-Stream PII-Maskierung (SEC-VEC-03):** Der `ChunkPiiRedactor` scannt jeden zurückgegebenen Text-Chunk. Personenbezogene Daten werden vor der Rückgabe an KI-Agenten oder den GraphQL-Client unumkehrbar pseudonymisiert oder redigiert.
4. **Anti-Oracle Metadaten-Schutz (SEC-VEC-04):** Außerhalb von Entwicklungsumgebungen führen unberechtigte Zugriffe oder Abfragen auf nicht existierende Collections stets zu einem einheitlichen `403 Forbidden` ohne Leaken von internen Ablehnungsgründen oder Sammlungslisten.
5. **Top-K Bounding & DoS-Schutz (SEC-VEC-05):** Der Parameter `top_k` wird strikt auf ein konfigurierbares Maximum (Standard: 50 Chunks) begrenzt, um Paging-Exhaustion und Memory-Spikes zu verhindern.

---

## 4. Konfigurationsbeispiel (`appsettings.json`)

```json
{
  "Gateway": {
    "VectorSearch": {
      "Enabled": true,
      "MaxTopK": 50,
      "EnablePiiRedaction": true,
      "DefaultDistanceMetric": "Cosine",
      "Sources": {
        "pgvector-knowledge": {
          "Provider": "PgVector",
          "ConnectionString": "Host=postgres;Database=ragdb;Username=app;Password=secret;",
          "DefaultCollection": "documents"
        },
        "qdrant-docs": {
          "Provider": "Qdrant",
          "Endpoint": "http://qdrant:6333",
          "ApiKey": "qdrant-secret-key"
        },
        "milvus-archive": {
          "Provider": "Milvus",
          "Endpoint": "http://milvus:19530"
        }
      }
    }
  }
}
```

---

## 5. Business Value & Differenzierungs-Moat

- **Erstes föderiertes Gateway mit Vektor-Governance:** Nahtlose Verknüpfung von relationalen Metadatenkatalogen und unstrukturierten RAG-Wissensquellen.
- **Enterprise-Konformität (DSGVO / EU AI Act):** Vollständige Einhaltung von Datenschutzvorgaben durch automatisiertes Scrubbing sensibler Daten vor der Modell-Inferenz.
- **Wettbewerbsvorteil gegen Apollo GraphOS und Hasura:** Weder Apollo noch Hasura bieten native Vektor-Integrationen mit mehrstufiger Row-Level-Security und PII-Chunk-Maskierung.
