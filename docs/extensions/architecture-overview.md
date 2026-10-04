# Autheris Extensions Architecture Overview

Dieses Dokument beschreibt die Architektur, Schichtentrennung und Integrationsmuster des Repositories `Autheris.Extensions`.

---

## 1. Architektur-Ziele & Entkopplung

`Autheris.Extensions` stellt optionale Enterprise-Konnektoren, Third-Party-Adapter und Integrationsdienste für das Core-Gateway bereit.

### Leitprinzipien
1. **Zero Coupling to Core Hosting**: Extensions implementieren reine Application-Interfaces aus `Autheris.Application` und `Autheris.Domain`. Sie binden sich nicht transitiv an den ASP.NET Core Kestrel-Host oder HotChocolate-Execution-Engine des Core-Gateways.
2. **Pluggable Registration**: Die Registrierung erfolgt über eine einzige Erweiterungsmethode:
   ```csharp
   services.AddGatewayExtensions(gatewayOptions, hostEnvironment);
   ```
3. **Resilient Outbound I/O**: Sämtliche HTTP-basierten Clients (`PurviewDataCatalogClient`, `CollibraDataCatalogClient`, `AlationCatalogClient`, `ServiceNowTableApiClient`, `JiraCloudRestClient`, `OpenMetadataClient`, `OpenLineageClient`) nutzen `IHttpClientFactory` mit `SocketsHttpHandler` (Connection Pooling, DNS-Refresh, Circuit Breaking).
4. **Zero-Trust & Governance-Konformität**: Sensitivitätsklassifizierungen aus Drittsystemen werden strikt validiert. Bei Erkennung von DSGVO Art. 9 Daten (Gesundheit, Biometrie, Ethnie, Glaube, Gewerkschaft) erzwingen die Adapter automatische Redaction und Vier-Augen-Freigabe.

---

## 2. Modul-Übersicht

```mermaid
graph TD
    subgraph CoreGateway ["Autheris Core"]
        CoreDomain["Autheris.Domain"]
        CoreApp["Autheris.Application"]
    end

    subgraph Extensions ["Autheris.Extensions"]
        DC["DataCatalog/<br/>Purview, Collibra, Alation, OpenMetadata"]
        DBT["Dbt/<br/>Manifest Ingestion, Lineage & Contracts"]
        ITSM["Itsm/<br/>ServiceNow & Jira Clients"]
        ODATA["OData/<br/>CSDL & Formatters for PowerBI/Excel"]
        LAKE["Lakehouse/<br/>Iceberg v2, S3 SigV4, Azure ADLS, Parquet"]
    end

    DC -->|implements IDataCatalogClient| CoreApp
    DBT -->|implements IDbtMetadataIngestionService,<br/>IDbtContractValidator, IDbtExposurePublisher| CoreApp
    ITSM -->|implements IItsmWorkflowClient| CoreApp
    ODATA -->|implements IODataHandler| CoreApp
    LAKE -->|implements ILakehouseDataSourceExecutor,<br/>ILakehouseStorageProvider| CoreApp
    Extensions -.-> CoreDomain
```

### Komponenten-Matrix

| Modul | Hauptklassen / Services | Core-Schnittstellen | Anwendungsfall |
| :--- | :--- | :--- | :--- |
| **DataCatalog** | [`DataCatalogSyncService`](file:///root/gql_extensions/src/Autheris.Extensions/DataCatalog/DataCatalogSyncService.cs)<br/>[`PurviewDataCatalogClient`](file:///root/gql_extensions/src/Autheris.Extensions/DataCatalog/PurviewDataCatalogClient.cs)<br/>[`CollibraDataCatalogClient`](file:///root/gql_extensions/src/Autheris.Extensions/DataCatalog/CollibraDataCatalogClient.cs)<br/>[`DataCatalogClientFactory`](file:///root/gql_extensions/src/Autheris.Extensions/DataCatalog/DataCatalogClientFactory.cs)<br/>[`AlationCatalogClient`](file:///root/gql_extensions/src/Autheris.Extensions/DataCatalog/AlationCatalogClient.cs)<br/>[`OpenMetadataCatalogAdapter`](file:///root/gql_extensions/src/Autheris.Extensions/DataCatalog/OpenMetadataCatalogAdapter.cs) | `IDataCatalogClient`<br/>`IDataCatalogSyncService` | Bidirektionaler Sync oder Realtime-Lookup von Tabellen- und Spalten-Metadaten, DSGVO-Tags und Eigentümern. |
| **Dbt** | [`DbtMetadataIngestionService`](file:///root/gql_extensions/src/Autheris.Extensions/Dbt/DbtMetadataIngestionService.cs)<br/>[`DbtContractValidator`](file:///root/gql_extensions/src/Autheris.Extensions/Dbt/DbtContractValidator.cs)<br/>[`DbtArtifactStreamingParser`](file:///root/gql_extensions/src/Autheris.Extensions/Dbt/DbtArtifactStreamingParser.cs)<br/>[`DbtExposurePublisher`](file:///root/gql_extensions/src/Autheris.Extensions/Dbt/DbtExposurePublisher.cs) | `IDbtMetadataIngestionService`<br/>`IDbtContractValidator`<br/>`IDbtExposurePublisher` | Automatisierte Ingestion von dbt `manifest.json`, Zero-Trust Proposal Approval Workflow, Lineage Impact Graph und CI Breaking-Change Contract Enforcement. |
| **Itsm** | [`ServiceNowTableApiClient`](file:///root/gql_extensions/src/Autheris.Extensions/Itsm/ServiceNowTableApiClient.cs)<br/>[`JiraCloudRestClient`](file:///root/gql_extensions/src/Autheris.Extensions/Itsm/JiraCloudRestClient.cs)<br/>[`ItsmWebhookHandler`](file:///root/gql_extensions/src/Autheris.Extensions/Itsm/ItsmWebhookHandler.cs) | `IItsmWorkflowClient`<br/>`IItsmWebhookHandler` | Erzeugung von Genehmigungs-Tickets für sensible Datenabfragen und Vier-Augen-Prozesse. |
| **OData** | [`ODataHandler`](file:///root/gql_extensions/src/Autheris.Extensions/OData/ODataHandler.cs)<br/>[`ODataCsdlGenerator`](file:///root/gql_extensions/src/Autheris.Extensions/OData/ODataCsdlGenerator.cs)<br/>[`ODataResponseFormatter`](file:///root/gql_extensions/src/Autheris.Extensions/OData/ODataResponseFormatter.cs) | `IODataHandler` | Bereitstellung eines standardisierten OData v4 Endpunkts für Power BI, Microsoft Excel und SAP-Systeme. |
| **Lakehouse** | [`LakehouseDataSourceExecutor`](file:///root/gql_extensions/src/Autheris.Extensions/Lakehouse/Services/LakehouseDataSourceExecutor.cs)<br/>[`CompositeLakehouseStorageProvider`](file:///root/gql_extensions/src/Autheris.Extensions/Lakehouse/Services/CompositeLakehouseStorageProvider.cs)<br/>[`S3LakehouseStorageProvider`](file:///root/gql_extensions/src/Autheris.Extensions/Lakehouse/Services/S3LakehouseStorageProvider.cs)<br/>[`AzureBlobStorageProvider`](file:///root/gql_extensions/src/Autheris.Extensions/Lakehouse/Services/AzureBlobStorageProvider.cs)<br/>[`IcebergMetadataReader`](file:///root/gql_extensions/src/Autheris.Extensions/Lakehouse/Services/IcebergMetadataReader.cs)<br/>[`IcebergPartitionPruner`](file:///root/gql_extensions/src/Autheris.Extensions/Lakehouse/Services/IcebergPartitionPruner.cs) | `ILakehouseDataSourceExecutor`<br/>`ILakehouseStorageProvider`<br/>`IIcebergMetadataReader`<br/>`IIcebergPartitionPruner` | In-Process Ausführung analytischer Abfragen auf Apache Iceberg v2 Tabellen in Object Storage (AWS S3, Azure ADLS Gen2, MinIO) mit vektorisiertem Partition- & Min/Max-Pruning. |

---

## 3. Dependency Injection & Lebenszyklen


Die Abhängigkeiten werden in [`ExtensionsServiceCollectionExtensions.cs`](file:///root/gql_extensions/src/Autheris.Extensions/ExtensionsServiceCollectionExtensions.cs) konfiguriert:

- **HTTP-Clients**: Werden über Typed Clients mit Scoped/Transient-Lebensdauer registriert; `HttpMessageHandler` wird durch die Factory verwaltet (keine Socket-Erschöpfung).
- **Sync Services**: Werden als `Scoped` instanziiert, um per-Request oder per-Job State Isolation zu garantieren.
- **Hosted Services**: 
  - `OpenMetadataSyncBackgroundService`: Läuft zyklisch im Hintergrund, falls `OpenMetadata.Enabled == true`.
  - `DataCatalogSyncBackgroundService`: Läuft zyklisch im Hintergrund, falls `Catalog.Enabled == true`.

---

## 4. Sicherheit & Insecure Modes

In Dev- und Sandbox-Umgebungen können externe Testinstanzen selbstsignierte TLS-Zertifikate verwenden. Hierfür stehen die standardisierten Präfixe zur Verfügung:
- `warn_allow_self_signed_certs`: Akzeptiert nicht-vertrauenswürdige TLS-Zertifikate in Nicht-Produktionsumgebungen.
- `danger_bypass_catalog_auth`: Überspringt Bearer-Token-Header (nur für lokale Mock-Server).
- In Produktion (`environment.IsProduction()`) blockiert das Gateway den Start, wenn `danger_`-Optionen aktiviert sind.
