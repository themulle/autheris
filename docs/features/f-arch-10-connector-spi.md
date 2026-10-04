# F-ARCH-10: Standardized Connector SPI (Trino Pattern)

**Status:** [Done] (100% GA – Wave 1)  
**Components:** [`IDataSourceExecutor.cs`](file:///root/lis-git/autheris/src/Autheris.Application/DataSources/IDataSourceExecutor.cs), [`IConnectorPlugin.cs`](file:///root/lis-git/autheris/src/Autheris.Application/Connectors/IConnectorPlugin.cs), [`ConnectorPluginManager.cs`](file:///root/lis-git/autheris/src/Autheris.Application/Connectors/ConnectorPluginManager.cs)

---

## 1. Overview & Problem Statement

To prevent tight coupling between core gateway orchestration and backend data stores, F-ARCH-10 implements a standardized Service Provider Interface (SPI) modeled after Apache Trino. Connectors implement unified contracts (`MetadataProvider`, `DataSplitter`, `RecordBatchExecutor`), allowing new enterprise stores (e.g. SAP HANA, Snowflake, Elasticsearch) to be integrated seamlessly without altering central governance or masking logic.

---

## 2. Business Value

- **Modular Architecture**: Engineering teams can build and test custom database connectors independently from core gateway releases.
- **Guaranteed Governance Consistency**: Every connector automatically benefits from centralized RLS injection, PII column masking, and WORM audit logging.
- **Reduced Maintenance Overhead**: Consistent error handling, health probing, and connection pooling across all heterogeneous sources.

---

## 3. Architecture & Capabilities

- Pluggable connector lifecycle (`InitializeAsync`, `DiscoverSchemaAsync`, `ExecuteBatchAsync`).
- Isolated `AssemblyLoadContext` support preventing third-party dependency conflicts.
- Capability negotiation (e.g. reporting support for predicate pushdown, projection, and sorting).

---

## 4. Usage Example

```csharp
// Example custom connector implementing the standardized SPI
public class CustomWarehouseConnector : IConnectorPlugin
{
    public string ConnectorType => "CustomWarehouse";

    public async Task<ConnectorCapabilities> GetCapabilitiesAsync(CancellationToken ct)
    {
        return new ConnectorCapabilities(
            SupportsPredicatePushdown: true,
            SupportsProjectionPushdown: true,
            SupportsBatchExecution: true
        );
    }

    public async Task<IAsyncEnumerable<RecordBatch>> ExecuteSplitAsync(
        DataSplit split,
        QueryFilter filter,
        CancellationToken ct)
    {
        // Executes against backend and yields governed record batches
        ...
    }
}
```

---

## 5. Configuration Example

```json
{
  "Gateway": {
    "ConnectorPlugins": {
      "PluginDirectory": "plugins/connectors",
      "AutoLoadOnStartup": true,
      "IsolationMode": "AssemblyLoadContext"
    }
  }
}
```
