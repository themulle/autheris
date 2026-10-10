# Battle Card: Autheris vs. Hasura Enterprise (DDN)

Comprehensive competitive breakdown between **Autheris Enterprise Gateway** and **Hasura Enterprise / Data Delivery Network (DDN)**.

---

## 🥊 Executive Comparison

| Dimension | 🚀 Autheris Enterprise Gateway | 🔷 Hasura Enterprise (DDN) |
|---|---|---|
| **Architecture & Lock-in** | **Open Architecture:** Standard ANTLR4 AST rewriting; no proprietary metadata lock-in | **Proprietary Metadata Lock-in:** Requires Hasura metadata engines, Hasura CLI, and proprietary schema definitions |
| **Total Cost of Ownership (TCO)** | **Cost-Effective & Predictable:** Flat enterprise licensing without core or tenant extortion | **Extremely Expensive:** Steep per-core pricing, high enterprise tier minimums |
| **Cross-Domain Federation** | **Embedded DuckDB OLAP:** Multi-source joins executed in zero-copy in-memory DuckDB batches | **Distributed Subgraphs / Connector Fabric:** High complexity to join SQL with REST APIs |
| **Workflow Governance** | **4-Eyes SoD & ServiceNow / Jira Webhooks:** Interactive challenge responses with JIT consent tickets | **Static Permissions:** Binary Allow/Deny; no native human-in-the-loop (HitL) approval workflows |
| **Lakehouse & Big Data** | **Native Apache Iceberg & Parquet:** Zero-trust pushdown to lakehouses via DuckDB and Arrow Flight | **Relational DB First:** Limited modern lakehouse catalog integration without extra SaaS components |
| **Extensibility Language** | **First-Class C# (.NET 10):** Direct access to ASP.NET Core DI, Spans, and native NuGet packages | **TypeScript / Go / Rust Plugins:** Requires building separate connector services for custom logic |

---

## 🏆 Key Advantages of Autheris

1. **Freedom from Proprietary Metadata Engines:**  
   Migrating away from Hasura is notoriously difficult because all permissions and relationship graphs are encoded in proprietary Hasura metadata files. Autheris utilizes open standards (Casbin ABAC, dbt manifests, standard OpenAPI) with zero platform lock-in.
2. **Just-in-Time Access & Interactive Challenges:**  
   Hasura returns a hard `403 Forbidden` if a user lacks access. Autheris supports **Interactive Step-Up Challenges**: when an analyst requests confidential VIP data, Autheris triggers an approval ticket in ServiceNow, issues a challenge response, and unblocks the query transparently once approved.
