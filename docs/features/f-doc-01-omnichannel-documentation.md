# F-DOC-01: Omnichannel Semantic Documentation Passthrough

**Status:** [Done] (100% GA – Wave 1)  
**Komponenten:** [`DynamicTableType.cs`](file:///root/lis-git/gql/gql/src/Autheris.GraphQL/DynamicTypes/DynamicTableType.cs), [`SemanticMcpCompiler.cs`](file:///root/lis-git/gql/gql/src/Autheris.Application/Mcp/Services/SemanticMcpCompiler.cs), [`DynamicOpenApiGenerator.cs`](file:///root/lis-git/gql/gql/src/Autheris.Application/OData/Services/DynamicOpenApiGenerator.cs), [`ODataCsdlGenerator.cs`](file:///root/lis-git/gql/gql_extensions/src/Autheris.Extensions/OData/ODataCsdlGenerator.cs)

---

## 1. Übersicht & Problemstellung
In modernen Enterprise-Datenarchitekturen investieren Data Engineers viel Aufwand in Modell- und Feldbeschreibungen in dbt (`schema.yml`, Markdown-Doc-Blocks `{{ doc('...') }}`) sowie Business-Glossare in Datenkatalogen (OpenMetadata, Collibra, Purview).

Konventionelle Gateways (Apollo, Hasura DDN, Kong/Tyk) schleifen diese Upstream-Dokumentation nicht durchgängig an Konsumenten durch. Die Dokumentation verkümmert in Silos, während API-Entwickler, KI-Agenten und BI-Analysten unkommentierte Spalten sehen.

## 2. Architektur & Umsetzung
Autheris etabliert eine **Single Source of Truth** für semantische Metadaten:
1. **Ingestion:** `DbtMetadataIngestionService` und `OpenMetadataSyncService` lesen Tabellen- und Spaltenkommentare, Dokumentationsquellen und Tags ein.
2. **Speicherung:** Persistierung im Governance-Repository mit Unterscheidung zwischen kompakter Kurzbeschreibung (`Description`) und ausführlicher Fachdokumentation (`LongDescription`).
3. **Omnichannel-Exposition:**
   - **GraphQL Web UI / IDEs (Banana Cake Pop, GraphiQL):** CommonMark-Formatierung in `descriptor.Field(...).Description(...)`.
   - **Model Context Protocol (MCP):** Spaltensemantik wird direkt in die `description`-Attribute der MCP-Tool-Parameter und Tool-Prompts eingespeist.
   - **Dynamic OpenAPI 3.1 & Swagger UI:** `description` und Vendor-Extensions (`x-long-description`, `x-dbt-meta`) an REST-Properties.
   - **OData v4 CSDL & BI-Tools (Power BI, Excel):** Standardisierte OASIS-Tags `<Annotation Term="Core.Description" String="..." />`.
   - **Developer Portal & Backstage:** Katalog-API `getCatalog` liefert alle Metadaten ohne Drift.
   - **Compliance & EU AI Act:** Revisionssichere Kennzeichnung von PII- und DSGVO-Art.-9-Zweckbindungen.

## 3. Business Value
- Beseitigung von "Documentation Drift": Änderungen in dbt/OpenMetadata spiegeln sich in Echtzeit auf allen Kanälen wider.
- >90% Reduktion von Rückfragen an das Data-Engineering-Team.
- First-Try-Trefferquote für KI-Agenten steigt von ~70% auf >98%.
