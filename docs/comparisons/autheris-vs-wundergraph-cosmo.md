# Battle Card: Autheris vs. WunderGraph / Cosmo

Comprehensive competitive breakdown between **Autheris Enterprise Gateway** and **WunderGraph / Cosmo**.

---

## 🥊 Executive Comparison

| Dimension | 🚀 Autheris Enterprise Gateway | 🌐 WunderGraph / Cosmo |
|---|---|---|
| **Target Audience & Focus** | **Enterprise Security, Compliance & Governance:** Regulated industries (Banking, Healthcare, Gov, Enterprise Data Mesh) | **Developer Experience & Web BFFs:** Focused on web frontend developers and GraphQL-to-REST stitching |
| **Audit Trails & Lineage** | **WORM-Sealed HMAC-SHA256 Hash Chains:** Reversible SEC Rule 17a-4 and GDPR Art. 15 disclosure export | **Standard Telemetry:** Prometheus metrics and OpenTelemetry traces without tamper-proof cryptographic audit |
| **Legacy & Hybrid Identity** | **Native Kerberos, Windows AD, Service Principals, Entra ID, OIDC** with impersonation & delegation | **OIDC / JWT Only:** Limited support for legacy enterprise on-premise Active Directory and Kerberos environments |
| **Data Governance & Catalogs** | **Bidirectional Sync with Purview, Collibra, Alation:** Automatic PII detection, sensitivity levels, dynamic masking | **None:** Cosmo has no data governance catalog connectors or automated PII tagging engines |
| **Model Context Protocol (MCP)** | **First-Class AI Gateway:** Dynamic schema pruning, golden queries, token budgets, OWASP LLM01 guardrails | **Basic GraphQL Tools:** Limited agentic AI governance or token economics integration |

---

## 🏆 Key Advantages of Autheris

1. **Enterprise Compliance Readiness:**  
   Cosmo is an excellent modern GraphQL router for web developers, but fails stringent enterprise audits (BaFin, HIPAA, SEC) because it lacks tamper-evident audit logging, Four-Eyes segregation of duties, and data-owner consent governance.
2. **True Air-Gapped & Sovereign Cloud Suitability:**  
   Autheris runs completely offline without phoning home or requiring SaaS control planes, making it ideal for sovereign and air-gapped environments.
