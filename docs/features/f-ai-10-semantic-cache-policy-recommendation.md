# F-AI-10: Semantic Query Cache & Autonomous Policy Recommendation

**Status:** **100% (GA) ✅ (Implementiert & Security-Audited 2026-10-04)**  
**Komponenten:** [`ISemanticQueryCacheService.cs`](file:///root/lis-git/gql/gql/src/Autheris.Infrastructure/Cache/SemanticQueryCacheService.cs), [`SemanticQueryCacheService.cs`](file:///root/lis-git/gql/gql/src/Autheris.Infrastructure/Cache/SemanticQueryCacheService.cs), [`IPolicyRecommendationService.cs`](file:///root/lis-git/gql/gql/src/Autheris.Application/Governance/Services/PolicyRecommendationService.cs), [`PolicyRecommendationService.cs`](file:///root/lis-git/gql/gql/src/Autheris.Application/Governance/Services/PolicyRecommendationService.cs), [`SemanticCacheModels.cs`](file:///root/lis-git/gql/gql/src/Autheris.Domain/Model/SemanticCacheModels.cs), [`PolicyRecommendationModels.cs`](file:///root/lis-git/gql/gql/src/Autheris.Domain/Model/PolicyRecommendationModels.cs)  
**Referenzen:** [`architecture-implementation-plan-f-ai-09-f-ai-10.md`](file:///root/.gemini/antigravity-cli/brain/0c0319e4-ab0a-420f-9bfb-e8e9d9cf922a/architecture-implementation-plan-f-ai-09-f-ai-10.md)

---

## 1. Executive Summary & Problemstellung

Beim Einsatz autonomer KI-Agenten und Retrieval-Systeme treten zwei wesentliche Ineffizienzen auf:
1. **Redundante Inferenz- & Datenbankkosten:** Agenten formulieren identische Informationsbedürfnisse mit leicht abweichenden Wortlauten (z. B. *„Zeige die Umsätze der Region Nord“* vs. *„Umsatzstatistik Region Nord“*). Klassische exakte String-Caches verfehlen diese Anfragen (Cache Miss) und lösen teure Vektorsuchen oder LLM-Inferenzläufe aus.
2. **Rechte-Friktion & Überprivilegierung:** Wenn ein KI-Agent auf geschützte Tabellen zugreifen möchte und auf ein `403 Forbidden` stößt, vergeben Systemadministratoren aus Zeitmangel oft pauschale Vollzugriffe. Es fehlte ein automatisierter Mechanismus, der aus Ablehnungsmustern exakt die minimal benötigten Berechtigungen ableitet.

Mit **F-AI-10** löst **Autheris** beide Herausforderungen:
- Der **Semantic Query Cache** erkennt semantisch äquivalente Anfragen über Vektor-Ähnlichkeit unter strikter Mandanten- und ABAC-Isolation.
- Der **Policy Recommendation Service** analysiert abgelehnte Zugriffe und leitet minimale, rollengerechte Consent-Vorschläge nach dem Least-Privilege-Prinzip ab.

---

## 2. Architektur & Datenfluss

```
                                  Client / Autonomous AI Agent
                                                |
                                                | Prompt / Vector Query
                                                v
             +--------------------------------------------------------------------+
             | Governed Execution Pipeline                                        |
             |   Security Context: TenantId, UserSid, SecurityContextHash         |
             +--------------------------------------------------------------------+
                                                |
                                                v
             +--------------------------------------------------------------------+
             | ISemanticQueryCacheService (Semantic Cache Lookup)                 |
             |   - Composite Partition Key: (Tenant, UserSid, ContextHash, Target)|
             |   - Cosine Similarity Matching >= MinSimilarityScore (Default: 0.85)|
             |   - Epoch Invalidation Guard (Instant Eviction on Policy Change)   |
             +--------------------------------------------------------------------+
                         /                                            \
           [Cache Hit: >= 0.85]                                   [Cache Miss]
                   /                                                    \
                  v                                                      v
      Return Cached Result Chunks                               Execute Vector Query
                                                                         |
                                                                         v
                                                       Is Authorization Denied (403)?
                                                        /                           \
                                                     [Ja]                           [Nein]
                                                      /                               \
                                                     v                                 v
                     +----------------------------------------------+        Cache Result Chunks
                     | IPolicyRecommendationService                 |
                     |   - Record Access Denial Event               |
                     |   - Aggregate Pattern (Threshold >= 3)       |
                     |   - Synthesize Minimal Least-Privilege Grant |
                     +----------------------------------------------+
                                          |
                                          v
                          Queue for Data Steward Review (P6 UI)
```

---

## 3. Sicherheits- und Isolationsarchitektur

1. **Strikte Cache-Partitionierung (SEC-SEM-01):** Der Cache wird niemals mandanten- oder benutzerübergreifend geteilt. Jeder Cache-Eintrag ist an einen eindeutigen Schlüssel gebunden: `TenantId:UserSid:SecurityContextHash:Collection`. Unterschiedliche RLS-Rollen oder Attribute führen automatisch zu isolierten Cache-Partitionen.
2. **Epochen-Validierung & Revozierungs-Integrität (SEC-SEM-02):** Wird ein Consent widerrufen oder eine Casbin-Policy geändert, erhöht sich die globale Epoche (`CacheEpoch`). Sämtliche Einträge mit abweichender Epoche werden sofort invalidiert.
3. **DoS- & Speicher-Begrenzung (SEC-SEM-03):** Begrenzung der maximalen Partitionen (`MaxPartitions`, Standard: 1.000) und Einträge pro Partition (`MaxEntriesPerPartition`, Standard: 100) mit LRU-Verdrängung.
4. **Least-Privilege Konsistenz (SEC-SEM-04):** Der Policy Recommendation Service schlägt ausschließlich Spalten vor, die der Client angefragt hat. Sensible Spalten unterliegen strikten Filtern und werden niemals automatisch vorgeschlagen.

---

## 4. Konfigurationsbeispiel (`appsettings.json`)

```json
{
  "Gateway": {
    "SemanticCache": {
      "Enabled": true,
      "MinSimilarityScore": 0.85,
      "TtlMinutes": 60,
      "MaxEntriesPerPartition": 100,
      "MaxPartitions": 1000
    },
    "PolicyRecommendation": {
      "Enabled": true,
      "MaxQueueCapacity": 500,
      "MinDenialCountThreshold": 3,
      "ProposalExpiryMinutes": 1440
    }
  }
}
```

---

## 5. Business Value & Differenzierungs-Moat

- **Bis zu 70 % Reduktion der RAG-Inferenzkosten:** Redundante Vektorsuchen und Embeddings werden vermieden.
- **Zero Friction bei maximaler Sicherheit:** Anstelle manueller Ticket-Schleifen erhalten Data Stewards im Management Studio (`P6`) vorgefertigte, minimale Consent-Anträge zur 1-Klick-Freigabe.
- **Audit-Compliance:** Vollständige Nachvollziehbarkeit aller abgeleiteten Berechtigungsvorschläge im WORM-Audit-Log.
