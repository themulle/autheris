# Implementation Plan: PLAN-CAT-138-INC2-5 - Catalog at Scale (Increments 2 through 5)

**Document ID:** `PLAN-CAT-138-INC2-5`  
**Date:** 2026-10-20  
**Status:** APPROVED WITH CONDITIONS (Phase 2 Architecture & Phase 3 Security Review complete)  
**Author:** Solution Architect (Phase 2, csharp-architect)  
**Input PRD:** [`REQ-CAT-138`](2026-10-17-req-issue-138-catalog-at-scale.md) (Increments INC-138-2, INC-138-3, INC-138-4, INC-138-5)  
**Builds on:** [`PLAN-CAT-138-INC1`](2026-10-17-plan-issue-138-inc1-catalog-visibility-service.md) (`ICatalogVisibilityService`), [`PLAN-REL-151`](2026-10-17-plan-issue-151-composite-key-relations.md) (`TABLE_RELATION_KEYS`), [`PLAN-BUG-157`](2026-10-17-req-issue-157-columnless-table-graphql-startup.md) (`OmittedTables`)  
**Target Branch:** `feat/issue-138-catalog-at-scale-inc2-5`  
**Size:** L (4 increments delivered in coordinated work packages)  

---

## 1. Executive Summary & Architectural Scope

This plan specifies the architecture and implementation for Increments 2 through 5 of Issue #138, addressing the scaling bottlenecks of the Autheris catalog at 5,000+ tables:

```
┌────────────────────────────────────────────────────────────────────────────────────────┐
│                                 AUTHERIS CATALOG AT SCALE                              │
│                                                                                        │
│   INC-138-2 (Wave 3)                  INC-138-3 (Wave 3)                               │
│   ┌─────────────────────────────┐     ┌────────────────────────────────────────────┐   │
│   │ CATALOG_STATE Baseline Table│     │ Caller Visible-Set BitSet Cache            │   │
│   │ (structure_ver, content_ver)│────▶│ (user:{tenant}:{oid}:{scopes}:{roles})     │   │
│   │ ICatalogVersionProvider     │     │ Pre-Filter BEFORE BM25 / Vector Ranking    │   │
│   │ Immutable CatalogSnapshot   │     │ Bounded Batch ReBAC (chunks of 32)         │   │
│   └─────────────────────────────┘     └────────────────────────────────────────────┘   │
│                 │                                           │                          │
│                 ▼                                           ▼                          │
│   INC-138-4 (Wave 3)                  INC-138-5 (Wave 4)                               │
│   ┌─────────────────────────────┐     ┌────────────────────────────────────────────┐   │
│   │ Bulk Relation Loading       │     │ Optional Honest Embeddings                 │   │
│   │ (GetAllRelationsAsync)      │     │ (None [BM25 default], LocalOnnx, Ollama)   │   │
│   │ TABLE_RELATIONS Indexes     │     │ Hash projection removed from fusion        │   │
│   │ GraphQL: Rebuild on         │     │ Operator-mounted ONNX with SHA-256 check   │   │
│   │ structure_version ONLY      │     │ Graceful Degradation & Embedding Cache     │   │
│   └─────────────────────────────┘     └────────────────────────────────────────────┘   │
└────────────────────────────────────────────────────────────────────────────────────────┘
```

### Key Architectural Shifts

| # | Current Flaw / Anti-Pattern | Scaled Architecture (INC 2–5) | Impact |
|---|---|---|---|
| **1** | Startup-only snapshot, no version counter, full table hash (`CatalogVersionProvider`) | Baseline `CATALOG_STATE` table with `structure_version` and `content_version`. One SELECT, two integers. In-memory `CatalogSnapshot` updated on version bump or background poll (15s). | Zero startup drift; idle cost = 1 query / 15s; instantaneous freshness. |
| **2** | Post-ranking candidate clamp (`Math.Clamp(limit * 5, 20, 100)`), followed by visibility drop | Remove clamp. BitSet pre-filter over snapshot ordinals: rank ONLY within caller's permitted set. | Caller with 50/5,000 tables finds matching tables regardless of global rank. |
| **3** | Unbounded `Task.WhenAll` authorization checks per table per request | Bounded parallel batches (size 32) on cache miss; caller visible-set BitSet cached per caller key, validated on every lookup against catalog versions, policy epoch, and ReBAC generation (1s). | Zero per-table checks on cache hit; fail-closed revocation bound < 1s + poll. |
| **4** | Relations loaded sequentially in loop (N queries); O(N²) reverse relation scanning; full GraphQL rebuild on any edit | `ITableRelationRepository.GetAllRelationsAsync` (1 query); `TABLE_RELATIONS` indexes; dictionary grouping for reverse relations; GraphQL schema rebuild triggered on `structure_version` ONLY. | GraphQL schema build drops from seconds to milliseconds; descriptions never rebuild schema. |
| **5** | Hardcoded decorative `LocalOnnx` setting; token hash projection disguised as vector search | `EmbeddingProvider` enum (`None`, `LocalOnnx`, `Ollama`). `None` = honest keyword-only BM25. Operator-mounted ONNX with SHA-256 check or secure Ollama client. Hash projection removed from default fusion. In-memory vector caching by SHA-256 of text. | Honest vector status; degraded health on failure; zero runtime egress in offline setups. |

---

## 2. Increment Specifications

### 2.1 INC-138-2: Catalog Version Counter, Change Events & Immutable CatalogSnapshot

#### Baseline DDL & Shape Snapshots
New baseline table `CATALOG_STATE` across all three database engines in `0001` baseline migrations:

- **PostgreSQL Baseline (`src/Autheris.Infrastructure/Persistence/Migrations/PostgreSqlBaselineSchema.cs`)**:
  ```sql
  CREATE TABLE IF NOT EXISTS CATALOG_STATE (
      id BIGINT PRIMARY KEY CHECK (id = 1),
      structure_version BIGINT NOT NULL DEFAULT 1,
      content_version BIGINT NOT NULL DEFAULT 1,
      updated_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
  );

  INSERT INTO CATALOG_STATE (id, structure_version, content_version, updated_at)
  VALUES (1, 1, 1, '1970-01-01T00:00:00Z')
  ON CONFLICT (id) DO NOTHING;
  ```

- **SQLite Baseline (`src/Autheris.Infrastructure/Persistence/Migrations/SqliteBaselineSchema.cs`)**:
  ```sql
  CREATE TABLE IF NOT EXISTS CATALOG_STATE (
      id INTEGER PRIMARY KEY CHECK (id = 1),
      structure_version INTEGER NOT NULL DEFAULT 1,
      content_version INTEGER NOT NULL DEFAULT 1,
      updated_at TEXT NOT NULL
  );

  INSERT OR IGNORE INTO CATALOG_STATE (id, structure_version, content_version, updated_at)
  VALUES (1, 1, 1, '1970-01-01T00:00:00Z');
  ```

- **SQL Server Baseline (`src/Autheris.Infrastructure/Persistence/Migrations/SqlServerBaselineSchema.cs`)**:
  ```sql
  IF OBJECT_ID(N'dbo.CATALOG_STATE', N'U') IS NULL
  BEGIN
      CREATE TABLE dbo.CATALOG_STATE (
          id BIGINT NOT NULL CONSTRAINT PK_CATALOG_STATE PRIMARY KEY CONSTRAINT CK_CATALOG_STATE_SINGLETON CHECK (id = 1),
          structure_version BIGINT NOT NULL DEFAULT 1,
          content_version BIGINT NOT NULL DEFAULT 1,
          updated_at DATETIMEOFFSET NOT NULL DEFAULT SYSDATETIMEOFFSET()
      );
      INSERT INTO dbo.CATALOG_STATE (id, structure_version, content_version, updated_at)
      VALUES (1, 1, 1, '1970-01-01T00:00:00Z');
  END;
  ```

- **Shape Files Update**:
  Add `CATALOG_STATE` definition to:
  - `src/Autheris.Infrastructure/Persistence/Migrations/Shape/governance-shape.postgresql.json`
  - `src/Autheris.Infrastructure/Persistence/Migrations/Shape/governance-shape.sqlite.json`
  - `src/Autheris.Infrastructure/Persistence/Migrations/Shape/governance-shape.sqlserver.json`

#### Transactional Version Bumping Rules
Inside `PostgreSqlGovernanceRepository`, `SqliteGovernanceRepository`, and `SqlServerGovernanceRepository`:

```sql
UPDATE CATALOG_STATE
SET structure_version = structure_version + @structureDelta,
    content_version = content_version + @contentDelta,
    updated_at = CURRENT_TIMESTAMP
WHERE id = 1
RETURNING structure_version, content_version, updated_at;
```

- **`structure_version` incremented on:**
  - Table registration, activation toggle, deactivation, deletion.
  - Column additions, renames, type changes, removals.
  - Primary key / composite key modifications (`ReplaceTableKeyDefinitionAsync`).
  - Relation additions, updates, cross-data-source opt-in toggles (`CreateRelationAsync`, `SetRelationCrossDataSourceOptInAsync`).
  - Row filter evaluation strategy changes (`SetRowFilterEvaluationAsync`).
- **`content_version` incremented on:**
  - Short descriptions, long descriptions, display names, translations.
  - Dataset tags (`Table.Tags`).
  - Table ownership and delegations (`DelegateDataOwnershipAsync`).
  - Table visibility tiers (`Table.Visibility`: Hidden / Summary / Full).
- **Rule:** A write modifying ONLY content fields MUST NEVER increment `structure_version`.

#### Version Provider & Event Publishing
- **Interface & Record (`src/Autheris.Application/Catalog/ICatalogVersionProvider.cs`)**:
  ```csharp
  namespace Autheris.Application.Catalog;

  public readonly record struct CatalogVersion(long StructureVersion, long ContentVersion)
  {
      public string Formatted => $"{StructureVersion}:{ContentVersion}";
      public override string ToString() => Formatted;
  }

  public sealed record CatalogChangedEvent(
      long StructureVersion,
      long ContentVersion,
      DateTimeOffset Timestamp);

  public interface ICatalogVersionProvider
  {
      public const string CatalogChangedEventChannel = "catalog:changed";

      ValueTask<CatalogVersion> GetCatalogVersionAsync(CancellationToken ct = default);
      Task<string> GetVersionAsync(CancellationToken ct = default);
  }
  ```
- **Implementation (`src/Autheris.Infrastructure/Persistence/Catalog/DbCatalogVersionProvider.cs`)**:
  - Replaces `CatalogVersionProvider.cs` (delete hash-over-all-metadata).
  - Executes a single indexed query: `SELECT structure_version, content_version FROM CATALOG_STATE WHERE id = 1`.
  - Registered as `Singleton` in `GatewayCatalogServiceExtensions`.
- **Post-Commit Event (`catalog:changed`)**:
  - Upon committing a catalog change transaction, the repository publishes `CatalogChangedEvent` to `IEventBus`.
  - Subscribers must treat the event as a best-effort wake-up signal; background polling remains authoritative.

#### In-Memory `CatalogSnapshot` & Entry Keying
- **`CatalogEntryKey` (`src/Autheris.Domain/Model/CatalogSearchModels.cs`)**:
  ```csharp
  namespace Autheris.Domain.Model;

  public readonly record struct CatalogEntryKey(string Kind, string Id)
  {
      public const string KindTable = "table";
      public const string KindProcedure = "procedure";

      public static CatalogEntryKey ForTable(TableIdentifier table) => new(KindTable, table.ToString());
      public static CatalogEntryKey ForTable(Guid tableId) => new(KindTable, tableId.ToString("D"));
  }
  ```
- **`CatalogSnapshot` (`src/Autheris.Application/Catalog/CatalogSnapshot.cs`)**:
  ```csharp
  namespace Autheris.Application.Catalog;

  public sealed class CatalogSnapshot
  {
      public CatalogVersion Version { get; }
      public IReadOnlyList<TableMetadata> AllTables { get; }
      public IReadOnlyDictionary<TableIdentifier, TableMetadata> TablesByIdentifier { get; }
      public IReadOnlyDictionary<Guid, TableMetadata> TablesById { get; }
      public IReadOnlyList<TableRelation> AllRelations { get; }
      public IReadOnlyDictionary<TableIdentifier, IReadOnlyList<TableRelation>> RelationsByParent { get; }
      public IReadOnlyDictionary<TableIdentifier, IReadOnlyList<TableRelation>> RelationsByChild { get; }
      public IReadOnlyDictionary<TableIdentifier, int> TableOrdinals { get; }
      public IReadOnlyList<TableIdentifier> OrdinalToTableMap { get; }
      public DateTimeOffset CreatedAt { get; }

      public CatalogSnapshot(
          CatalogVersion version,
          IReadOnlyList<TableMetadata> tables,
          IReadOnlyList<TableRelation> relations,
          DateTimeOffset createdAt);
  }
  ```
- **`CatalogSnapshotBackgroundService`**:
  - Background service polling `ICatalogVersionProvider` every `CatalogSearchOptions.RefreshSeconds` (default 15s).
  - Subscribed to `catalog:changed` on `IEventBus` for immediate wake-up.
  - When version differs from current snapshot:
    1. Debounced single-flight execution (atomic gate via `Interlocked.Exchange`).
    2. Loads `GetAllTablesAsync` and `GetAllRelationsAsync` in ONE batch.
    3. Builds new `CatalogSnapshot` and atomically swaps reference via `Volatile.Write`.
    4. Triggers background search index refresh. In-flight requests continue reading previous snapshot seamlessly.

---

### 2.2 INC-138-3: Bulk Visibility & Pre-Filter Ranking

#### Pre-Filter Architecture
- **Flaw Removed:** Delete `int candidatePoolSize = Math.Clamp(query.Limit * 5, 20, 100);` from `CatalogSearchEngine.cs:152`.
- **Pre-Filtering:** Rather than ranking all tables and discarding hidden ones afterwards, search receives the caller's permitted tables as a `CatalogVisibleBitSet` before BM25 / vector evaluation.
- **BM25 & Vector Scanners:** Only examine documents whose `Ordinal` is set in the caller's `CatalogVisibleBitSet`. Scoring and heap insertion costs are strictly proportional to visible tables.

#### Dense Ordinal BitSet
```csharp
namespace Autheris.Application.Catalog.Search;

public sealed class CatalogVisibleBitSet
{
    private readonly ulong[] _words;
    public int Capacity { get; }
    public int SetCount { get; }

    public CatalogVisibleBitSet(int capacity)
    {
        Capacity = capacity;
        _words = new ulong[(capacity + 63) / 64];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set(int ordinal)
    {
        if ((uint)ordinal < (uint)Capacity)
            _words[ordinal >> 6] |= 1UL << (ordinal & 63);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsSet(int ordinal)
    {
        return (uint)ordinal < (uint)Capacity &&
               (_words[ordinal >> 6] & (1UL << (ordinal & 63))) != 0;
    }
}
```
*Memory footprint:* 5,000 tables = 625 bytes per caller bitset.

#### Caller Visible-Set Cache & Epoch Validation
- **Cache Key (`CatalogVisibilityCacheKey`)**:
  ```csharp
  public readonly record struct CatalogVisibilityCacheKey(
      TenantId Tenant,
      Sid SubjectSid,
      string RolesKey,
      string ScopeKey,
      CallerClass CallerClass)
  {
      public override string ToString() =>
          $"user:{Tenant.Value}:{SubjectSid.Value}:{ScopeKey}:{RolesKey}:{CallerClass}";
  }
  ```
- **Cache Entry (`CachedVisibleSetEntry`)**:
  ```csharp
  public sealed class CachedVisibleSetEntry
  {
      public CatalogVisibleBitSet BitSet { get; }
      public CatalogVersion CatalogVersion { get; }
      public long PolicyEpoch { get; }
      public long RebacGeneration { get; }
      public DateTimeOffset HardExpiresAt { get; }
  }
  ```
- **Validation on Every Lookup**:
  1. `entry.CatalogVersion == currentSnapshot.Version` (O(1) struct comparison).
  2. `entry.RebacGeneration == currentTenantGeneration` (evaluated via Zanzibar generation cache with 1s window, O(1) in-memory check).
  3. `entry.PolicyEpoch == currentPolicyEpoch` (read from `IPolicyEpochRepository`).
  4. `DateTimeOffset.UtcNow < entry.HardExpiresAt` (hard TTL, e.g. 60s).
  - *If valid:* Return `entry.BitSet`. **Zero per-table permission checks. Zero database queries.**
  - *If invalid or generation read fails:* Cache miss. **Fail closed.**
- **Cache Miss Evaluation**:
  - Evaluate `ICatalogVisibilityService.GetVisibleAsync`.
  - ReBAC evaluation partitioned into bounded chunks of 32 (`RebacBatchSize = 32`) with max 8 chunks in flight (`Parallel.ForEachAsync`), leveraging `IRebacEvaluator.BatchCheckAsync` with decision caching.
  - Map allowed tables to snapshot ordinals, build `CatalogVisibleBitSet`, and store in bounded LRU cache (`ICatalogVisibilityCache`, max 10,000 callers, max 32 MB).

---

### 2.3 INC-138-4: Bulk Relations & Measured GraphQL Schema Refresh

#### Bulk Relation Repository
- **Interface Extension (`src/Autheris.Application/Interfaces/IGovernanceRepository.cs`)**:
  ```csharp
  public interface ITableRelationRepository
  {
      Task<IReadOnlyList<TableRelation>> GetRelationsForTableAsync(TableIdentifier parentTable, CancellationToken ct = default);
      Task<IReadOnlyList<TableRelation>> GetAllRelationsAsync(CancellationToken ct = default);
      Task<RelationWriteResult> CreateRelationAsync(TableRelation relation, CancellationToken ct = default);
      Task<bool> SetRelationCrossDataSourceOptInAsync(Guid relationId, bool allowCrossDataSource, CancellationToken ct = default);
  }
  ```
- **Repository Implementation (`PostgreSqlGovernanceRepository.Catalog.cs`, `SqliteGovernanceRepository.Catalog.cs`, `SqlServerGovernanceRepository.Catalog.cs`)**:
  - Single SQL query loading all relations from `TABLE_RELATIONS` joined with `TABLE_RELATION_KEYS` (ordered by `key_ordinal`).
  - Eliminates the previous sequential loop of N queries in `CatalogSchemaModel.cs:225-229`.

#### Baseline DDL Indexes on `TABLE_RELATIONS`
Add dedicated single-column indexes on `parent_table_id` and `child_table_id` in baseline DDL and shape snapshots:

- **PostgreSQL Baseline (`PostgreSqlBaselineSchema.cs`)**:
  ```sql
  CREATE INDEX IF NOT EXISTS ix_table_relations_parent ON TABLE_RELATIONS (parent_table_id);
  CREATE INDEX IF NOT EXISTS ix_table_relations_child ON TABLE_RELATIONS (child_table_id);
  ```
- **SQLite Baseline (`SqliteBaselineSchema.cs`)**:
  ```sql
  CREATE INDEX IF NOT EXISTS IX_TABLE_RELATIONS_PARENT ON TABLE_RELATIONS (parent_table_id);
  CREATE INDEX IF NOT EXISTS IX_TABLE_RELATIONS_CHILD ON TABLE_RELATIONS (child_table_id);
  ```
- **SQL Server Baseline (`SqlServerBaselineSchema.cs`)**:
  ```sql
  IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_table_relations_parent' AND object_id = OBJECT_ID(N'dbo.TABLE_RELATIONS'))
      CREATE INDEX ix_table_relations_parent ON dbo.TABLE_RELATIONS (parent_table_id);
  IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_table_relations_child' AND object_id = OBJECT_ID(N'dbo.TABLE_RELATIONS'))
      CREATE INDEX ix_table_relations_child ON dbo.TABLE_RELATIONS (child_table_id);
  ```
- **Update Shape Files:** Add new index objects to `TABLE_RELATIONS` in all three `governance-shape.*.json` snapshots.

#### Optimization of `CatalogSchemaModel.BuildAsync`
- In `src/Autheris.GraphQL/Catalog/CatalogSchemaModel.cs`:
  1. Replace `foreach (var builder in tableBuilders) GetRelationsForTableAsync(...)` with single `await relationRepository.GetAllRelationsAsync(ct)`.
  2. Pre-index relations:
     ```csharp
     var relationsByParent = allRelations.ToLookup(r => r.ParentTableIdentifier);
     var relationsByChild = allRelations.ToLookup(r => r.ChildTableIdentifier);
     ```
  3. Replace O(N²) nested scan (`foreach (var other in tableBuilders)` at lines 368-379) with direct dictionary lookup:
     ```csharp
     foreach (var rel in relationsByChild[meta.Identifier])
     {
         if (!tableMapById.TryGetValue(rel.ParentTableIdentifier, out var other))
             continue;
         // Validate keys and executability in O(1)...
     }
     ```

#### Selective Schema Rebuild on `structure_version` Only
- In `src/Autheris.GraphQL/Catalog/CatalogGraphQlTypeModule.cs`:
  - Constructor injects `ICatalogVersionProvider` (singleton).
  - Schema change detection:
    ```csharp
    var version = await _versionProvider.GetCatalogVersionAsync(ct).ConfigureAwait(false);
    if (_builtStructureVersion.HasValue && version.StructureVersion == _builtStructureVersion.Value)
    {
        // Structure has not changed! Skip schema build.
        return false;
    }
    ```
  - Content-only changes (descriptions, tags, owners) **never** trigger schema rebuilding.
  - Hot Chocolate schema swap runs off request path, non-blocking. Emits structured log with `RebuildDurationMs`, `TableCount`, and `TypeCount`.

---

### 2.4 INC-138-5: Optional Embeddings (Honest by Default)

#### Provider Configuration & Enum
- **Enum (`src/Autheris.Domain/Options/CatalogSearchOptions.cs`)**:
  ```csharp
  public enum CatalogEmbeddingProvider
  {
      None = 0,
      LocalOnnx = 1,
      Ollama = 2
  }

  public sealed class CatalogSearchOptions
  {
      public const string SectionName = "Gateway:CatalogSearch";

      public bool Enabled { get; set; } = true;
      public CatalogEmbeddingProvider EmbeddingProvider { get; set; } = CatalogEmbeddingProvider.None;

      // LocalOnnx Settings
      public string? ModelPath { get; set; }
      public string? TokenizerPath { get; set; }
      public string? ModelSha256 { get; set; }

      // Ollama Settings
      public string? OllamaEndpoint { get; set; }
      public string? OllamaModelName { get; set; }
      public bool OllamaInsecureAllowHttp { get; set; } = false;

      // Ranking & Search Settings
      public int RefreshSeconds { get; set; } = 15;
      public int RrfConstantK { get; set; } = 60;
      public int DefaultLimit { get; set; } = 5;
      public float MinVectorSimilarity { get; set; } = 0.25f;
      public int MaxQueryLength { get; set; } = 256;
      public CatalogSearchFieldWeights FieldWeights { get; set; } = new();
  }

  public sealed class CatalogSearchFieldWeights
  {
      public double TechnicalName { get; set; } = 10.0;
      public double DisplayName { get; set; } = 8.0;
      public double TagsAndOwner { get; set; } = 5.0;
      public double ShortDescription { get; set; } = 3.0;
      public double LongDescription { get; set; } = 2.0;
      public double ColumnNames { get; set; } = 2.0;
      public double ColumnDescriptions { get; set; } = 1.0;
  }
  ```
- **Validation on Startup (`CatalogSearchOptionsValidator`)**:
  - If `EmbeddingProvider == LocalOnnx`:
    - `ModelPath` and `TokenizerPath` must be non-empty and point to existing files on disk.
    - `ModelSha256` must be a valid 64-character hex string.
  - If `EmbeddingProvider == Ollama`:
    - `OllamaEndpoint` must be an absolute URI.
    - `OllamaModelName` must be non-empty.

#### Removal of Fake Hash Generator
- `LocalDeterministicEmbeddingGenerator` removed from production DI registration.
- Kept solely in test assembly (`Autheris.Tests.Unit/AI/DeterministicTestEmbeddingGenerator.cs`) for unit testing vector math.
- Production default is `CatalogEmbeddingProvider.None` (pure keyword BM25 with BM25F field weighting).

#### `LocalOnnx` Embedding Generator
- Implements `ICatalogEmbeddingGenerator`.
- Operates strictly offline (zero runtime network egress).
- Before initializing ONNX runtime session, computes SHA-256 of `ModelPath` and verifies equality with configured `ModelSha256`. If mismatched, aborts load and throws `InvalidOperationException`.
- Loads tokenizer vocab and ONNX session using CPU execution provider.
- Normalizes output embeddings with `TensorPrimitives.Divide` to unit vectors.

#### `Ollama` Embedding Generator
- Implements `ICatalogEmbeddingGenerator`.
- Uses `IHttpClientFactory` registered via `AddSecureOutboundHandlers`.
- Redacts all inputs with `SecretScrubber.Redact` before dispatch.
- Emits startup log: `Ollama embedding provider initialized with endpoint: {Endpoint}, model: {Model}` (no credentials logged).

#### Graceful Degradation & Health Reporting
- If configured provider fails loading (missing file, hash mismatch, endpoint connection error):
  - Log error at `LogLevel.Error`.
  - Set `EffectiveProvider = CatalogEmbeddingProvider.None`.
  - Report health status as `Degraded`.
  - Gateway starts cleanly with keyword-only BM25.
- Search queries requesting `mode = semantic` or `hybrid` with `EffectiveProvider == None`:
  - Answer with BM25 keyword results.
  - Surface `effective_mode: "keyword"` in search hit metadata.
  - Never throw and never generate fake synthetic vector scores.
- Health Check (`GET /health`):
  ```json
  {
    "status": "Degraded",
    "details": {
      "configured_provider": "LocalOnnx",
      "effective_provider": "None",
      "effective_mode": "keyword",
      "vector_ready": false,
      "indexed_tables": 5000,
      "catalog_version": "12:45",
      "last_indexed_at": "2026-10-20T02:00:00Z"
    }
  }
  ```

#### In-Memory Document Embedding Cache
- `ConcurrentDictionary<string, ReadOnlyMemory<float>>` keyed by `SHA-256(scrubbedDocumentText)`.
- During background index refresh:
  - If document text SHA-256 is present in cache, reuses existing vector immediately.
  - Only new or modified tables incur embedding generation.
  - Snapshot rebuild overhead drops to near zero for unchanged tables.

---

## 3. Work Package Breakdown (Phase 4 Execution Plan)

| WP | Increment | Description | Target Files | Size |
|---|---|---|---|:---:|
| **WP-1** | INC-138-2 | **Baseline DDL & Version Provider:** Add `CATALOG_STATE` table to 3 baselines + 3 shapes. Add version bump helper to repos. Implement `DbCatalogVersionProvider`, delete old hash provider. Add `CatalogChangedEvent` publishing. | `Migrations/*BaselineSchema.cs`, `Shape/governance-shape.*.json`, `Persistence/*GovernanceRepository.Catalog.cs`, `ICatalogVersionProvider.cs`, `DbCatalogVersionProvider.cs` | M |
| **WP-2** | INC-138-2 | **Immutable CatalogSnapshot & Fresh Index:** Implement `CatalogSnapshot` model with ordinals. Implement `CatalogSnapshotBackgroundService` (polling + event wake-up). Update `CatalogSearchEngine` to consume snapshot and key by `CatalogEntryKey`. Remove `*_id` guessed join paths. | `CatalogSnapshot.cs`, `CatalogSnapshotBackgroundService.cs`, `CatalogSearchEngine.cs`, `CatalogSearchOptions.cs`, `GatewayCatalogServiceExtensions.cs` | M |
| **WP-3** | INC-138-3 | **BitSet Pre-Filtering & Bounded Batch ReBAC:** Implement `CatalogVisibleBitSet`. Remove `limit * 5` clamp from search. Wire bitset filter before BM25/vector scoring. Implement `CatalogVisibilityCache` with epoch validation and bounded batch ReBAC (chunks of 32). | `CatalogVisibleBitSet.cs`, `CatalogVisibilityCache.cs`, `CatalogSearchEngine.cs`, `Bm25SearchIndex.cs`, `VectorSearchIndex.cs`, `CatalogVisibilityService.cs` | M |
| **WP-4** | INC-138-4 | **Bulk Relations & Measured GraphQL Refresh:** Add `GetAllRelationsAsync` to `ITableRelationRepository` and 3 repos. Add `TABLE_RELATIONS` indexes to baselines and shapes. Optimize `CatalogSchemaModel.BuildAsync` with child-table grouping. Inject `ICatalogVersionProvider` into `CatalogGraphQlTypeModule` comparing `structure_version` only. | `IGovernanceRepository.cs`, `*GovernanceRepository.Catalog.cs`, `Migrations/*BaselineSchema.cs`, `Shape/governance-shape.*.json`, `CatalogSchemaModel.cs`, `CatalogGraphQlTypeModule.cs` | M |
| **WP-5** | INC-138-5 | **Optional Embeddings & Health Honesty:** Implement `CatalogEmbeddingProvider` enum, `LocalOnnxEmbeddingGenerator` (SHA-256 check), `OllamaEmbeddingGenerator` (secure client). Remove fake hash from fusion. Implement graceful degradation, vector caching by text SHA-256, and `/health` reporting. | `CatalogSearchOptions.cs`, `CatalogSearchOptionsValidator.cs`, `ICatalogEmbeddingGenerator.cs`, `LocalOnnxEmbeddingGenerator.cs`, `OllamaEmbeddingGenerator.cs`, `CatalogSearchEngine.cs`, `CatalogHealthCheck.cs` | L |
| **WP-6** | Cross-Inc | **Integration & Scale Test Suite:** Two-instance parity tests, 1-second revocation window verification, 5,000-table synthetic benchmark, and architecture tests. | `tests/Autheris.Tests.Unit/*`, `tests/Autheris.Tests.Integration/*`, `tests/Autheris.Tests.Architecture/*` | M |

---

## 4. Detailed Component & Interface Specifications

### 4.1 Dependency Injection Registration Matrix

```csharp
// GatewayCatalogServiceExtensions.cs
public static IServiceCollection AddAutherisCatalog(this IServiceCollection services, IConfiguration configuration)
{
    // Options
    services.AddOptions<CatalogSearchOptions>()
        .Bind(configuration.GetSection(CatalogSearchOptions.SectionName))
        .ValidateOnStart();
    services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<CatalogSearchOptions>, CatalogSearchOptionsValidator>());

    // INC-138-2: Version Counter & Snapshot
    services.TryAddSingleton<ICatalogVersionProvider, DbCatalogVersionProvider>();
    services.TryAddSingleton<ICatalogSnapshotProvider, CatalogSnapshotProvider>();
    services.AddHostedService<CatalogSnapshotBackgroundService>();

    // INC-138-3: Visible-Set BitSet Cache
    services.TryAddSingleton<ICatalogVisibilityCache, CatalogVisibilityLruCache>();
    services.TryAddScoped<ICatalogVisibilityService, CatalogVisibilityService>();

    // INC-138-5: Embedding Generator (Honest by Default)
    services.TryAddSingleton<ICatalogEmbeddingGeneratorFactory, CatalogEmbeddingGeneratorFactory>();
    services.TryAddSingleton<ICatalogEmbeddingGenerator>(sp => sp.GetRequiredService<ICatalogEmbeddingGeneratorFactory>().CreateGenerator());

    // Search Engine
    services.TryAddSingleton<ICatalogSearchEngine, CatalogSearchEngine>();

    return services;
}
```

### 4.2 Lifetime Safety & Anti-Captive Dependency Validation
- `ICatalogVersionProvider`: **Singleton** (stateless ADO.NET connection factory query).
- `ICatalogSnapshotProvider`: **Singleton** (holds immutable reference swapped atomically).
- `ICatalogVisibilityCache`: **Singleton** (in-memory bounded LRU cache).
- `ICatalogSearchEngine`: **Singleton** (reads immutable snapshot via `Volatile.Read`).
- `ICatalogVisibilityService`: **Scoped** (evaluates per-request caller context; never injected into Singletons).
- `CatalogGraphQlTypeModule`: **Singleton** (receives `ICatalogVersionProvider` via DI; compares structure version).

---

## 5. Security & Verification Requirements

| Requirement | Implementation Guardrail |
|---|---|
| **SEC-138-1 Fail Closed** | Any error in reading `CATALOG_STATE`, policy epoch, or ReBAC generation treats the cache entry as invalid / miss; visibility defaults to empty set. |
| **SEC-138-2 No Information Leakage** | `CatalogChangedEvent` carries versions and timestamps only. No table names, domains, or tenant identifiers are broadcast on the bus. |
| **SEC-138-3 Zero Oracle in Search** | BitSet pre-filter runs BEFORE BM25 and vector scoring. Hidden tables are never scored, never occupy candidate pool slots, and never alter pagination totals. |
| **SEC-138-4 Column Hygiene Preserved** | Column matching in `CatalogSearchEngine` checks visible columns only. Hits based solely on unpermitted columns are dropped. |
| **SEC-138-5 Offline Supply Chain Safety** | `LocalOnnx` embeddings require operator mounting and strict SHA-256 pre-verification. Zero runtime internet egress. |
| **SEC-138-6 Outbound Model Protection** | `Ollama` uses `AddSecureOutboundHandlers` with egress allowlisting. Catalog texts are scrubbed via `SecretScrubber.Redact` before dispatch. |
| **SEC-138-7 Cache Isolation** | `CatalogVisibilityCacheKey` binds tenant, subject SID, scopes, roles, and caller class. No cache entry is ever shared across principals. |

---

## 6. Comprehensive Test & Verification Matrix

### 6.1 Unit Test Suite
- `CatalogVersionCounterTests`:
  - `TableCreated_BumpsStructureVersion_LeavesContentVersionUnchanged`
  - `DescriptionOrTagUpdated_BumpsContentVersion_LeavesStructureVersionUnchanged`
  - `CompositeKeyOrRelationUpdated_BumpsStructureVersion`
  - `DbCatalogVersionProvider_ReturnsTypedCatalogVersion`
- `CatalogSnapshotTests`:
  - `SnapshotCreation_AssignsDenseOrdinals`
  - `AtomicSwap_AllowsConcurrentReadsWithoutLocking`
- `CatalogVisibleBitSetTests`:
  - `BitSet_SetAndIsSet_MatchesOrdinals`
  - `BitSet_5000Tables_Under1KB`
- `CatalogVisibilityCacheTests`:
  - `CacheHit_ZeroDatabaseQueries_ZeroRebacChecks`
  - `StructureVersionMismatch_TriggersCacheMiss`
  - `RebacGenerationMismatch_TriggersCacheMiss`
  - `FailedGenerationRead_FailsClosedToMiss`
  - `TwoCallersDifferentScopes_NeverShareBitSet`
- `CatalogSearchPreFilterTests`:
  - `CandidatePoolClampRemoved_HiddenTablesDoNotDisplaceVisibleHits`
  - `CallerWith50Tables_FindsTableRanked3000Overall`
- `BulkRelationsTests`:
  - `GetAllRelationsAsync_LoadsAllRelationsInOneQuery`
  - `CatalogSchemaModel_ReverseRelationsUseChildLookup_NoN2Scan`
  - `CatalogGraphQlTypeModule_ContentOnlyChange_SkipsSchemaRebuild`
  - `CatalogGraphQlTypeModule_StructureChange_RebuildsSchema`
- `EmbeddingProviderTests`:
  - `DefaultProvider_IsNone_PureKeywordBm25`
  - `FakeDeterministicHash_RemovedFromDefaultFusion`
  - `LocalOnnx_CorrectSha256_LoadsSuccessfully`
  - `LocalOnnx_MismatchedSha256_FailsLoad_FallsBackToKeyword_ReportsDegraded`
  - `Ollama_UsesSecureOutboundHandlers_RedactsText`
  - `DocumentEmbeddingCache_ReusesVectorsByTextSha256`

### 6.2 Integration & Parity Test Suite
- `MultiReplicaFreshnessTests`:
  - Two gateway instances connected to shared DB and event bus.
  - Register new table on Instance A -> searchable on Instance B within refresh interval.
  - Disable event bus -> polling alone discovers table within 15s.
- `RevocationLatencyTests`:
  - Remove ReBAC tuple -> table disappears from search and MCP listing on both instances within 1s window + poll interval.
- `ScaleCatalogBenchmark` (5,000 tables, 100,000 columns, 5,000 relations):
  - GraphQL schema build finishes in < 10 seconds.
  - Search latency p95 < 300 ms for caller seeing all tables and caller seeing 50 tables.
  - Zero per-table checks on cache hits.

### 6.3 Architecture Rules (Cecil / NetArchTest)
- No catalog consumer outside `CatalogVisibilityService` calls `CatalogVisibility.VisibleTablesAsync`.
- No direct usage of `LocalDeterministicEmbeddingGenerator` in `src/Autheris.Api` or `src/Autheris.Application`.
- `ITableRelationRepository` methods called only via `CatalogSnapshot` or `CatalogSchemaModel`.
- `ICatalogVersionProvider` must have singleton lifetime.

---

## 7. Migration & Rollback Strategy

1. **In-Place Baseline Schema Evolution:**
   - No migration scripts (`0002_...`).
   - `0001` baseline files edited in place:
     - Table `CATALOG_STATE` added.
     - Indexes `ix_table_relations_parent` and `ix_table_relations_child` added.
   - All three embedded shape snapshots (`governance-shape.*.json`) regenerated and verified via `GovernanceSchemaShapeSnapshotTests`.
2. **Backward Compatibility:**
   - `ICatalogVersionProvider.GetVersionAsync(ct)` retained, returning string `"{structure}:{content}"`.
   - Existing callers (`CdcTableActivator`, etc.) remain fully functional.
3. **Rollback Plan:**
   - Git revert on `feat/issue-138-catalog-at-scale-inc2-5` restores previous baseline DDL and startup snapshot engine without data migration friction (pre-production gate).

---

## 8. Phase 3 Security & Compliance Review (Binding Security Conditions)

**Audit Conducted By:** Application Security Expert (`csharp-security-expert`)  
**Audit Standard:** Zero-Trust Architecture, Fail-Closed Security, Principle of Least Privilege, Timing-Attack Defense (`CryptographicOperations.FixedTimeEquals`), SSRF Prevention (`AddSecureOutboundHandlers`), Supply-Chain Verification, OWASP Top 10 API Security.  
**Review Verdict:** **APPROVED WITH CONDITIONS (`SEC-CAT138-20..28`)**

### 8.1 Threat Modeling & Security Posture Assessment

Scaling the catalog to 5,000+ tables introduces dense in-memory bitset caching, decoupled version polling, bulk relation queries, and optional external embedding generation. Without rigorous fail-closed security controls, these components introduce high-severity attack vectors:

1. **Epoch Regression & Stale Metadata Races (Tampering / Stale Authorization):** Asynchronous version updates across cluster nodes or aborted database transactions could allow version counters to desynchronize or regress, causing nodes to serve stale authorization states or miss revocations.
2. **Pre-Filter Bypass & Candidate Pool Oracles (Information Disclosure):** If BM25 ranking or vector similarity computes metrics across unpermitted tables before filtering, hidden dataset existence, frequency distributions, or column metadata can leak through ranking statistics or score differentials.
3. **Multi-Tenant Cache Collision & Revocation Lag (Elevation of Privilege):** Caching visible-set bitsets across callers risks cross-tenant bleed if tenant ID or scopes are improperly bound in cache keys, or unauthorized access if ReBAC tuple removals fail to invalidate cached sets immediately.
4. **Bulk Relation Metadata Leakage (Information Disclosure):** Bulk relation queries (`GetAllRelationsAsync`) loading multi-source relations could inadvertently bypass column masking or four-eyes cross-source governance gates.
5. **Embedding Supply Chain & Sandboxing Risks (Remote Code Execution / Tampering):** Loading untrusted local ONNX models introduces native code execution and path traversal risks; external Ollama embedding endpoints introduce SSRF and cleartext data exfiltration risks.
6. **Hallucinated Vector Fallback (Integrity / Spoofing):** Silent generation of fake vector scores or synthetic projections when embeddings fail compromises data integrity and misleads agentic callers.

| Threat Category | Potential Attack Vector | Autheris Mitigation & Zero-Trust Guardrail |
|---|---|---|
| **Spoofing** | Caller assumes another tenant's or role's cached visible set. | Cache key strictly binds `TenantId`, `SubjectSid`, `ScopeKey`, `RolesKey`, and `CallerClass` (`SEC-CAT138-23`). |
| **Tampering** | Model file swapped on disk; version counter rolls back or desynchronizes. | Constant-time SHA-256 pre-verification (`FixedTimeEquals`); strict transactional version coupling and epoch monotonicity guard (`SEC-CAT138-20`, `SEC-CAT138-26`). |
| **Repudiation** | ReBAC evaluation or cache invalidations occur without traceable epoch linkage. | Policy epoch (`IPolicyEpochRepository`) and ReBAC generation bound to cache entries and audited (`SEC-CAT138-23`). |
| **Information Disclosure** | Search ranks hidden tables; SSRF via Ollama; bulk relations leak unpermitted FKs. | BitSet pre-filter runs BEFORE scoring (`SEC-CAT138-22`); SSRF handler blocks loopback/metadata IPs (`SEC-CAT138-27`); bulk relations gated by four-eyes and visible columns (`SEC-CAT138-25`). |
| **Denial of Service** | Unbounded `Task.WhenAll` over 5,000 tables exhausts gateway thread pool. | Clamped ReBAC chunk batching (`RebacBatchSize = 32`, max 8 concurrent) with timeout and LRU memory bounding (`SEC-CAT138-24`). |
| **Elevation of Privilege** | Path traversal in ONNX path loads arbitrary host binaries. | Strict path validation, operator allowlist, and sandboxed execution with zero network egress (`SEC-CAT138-26`). |

---

### 8.2 Binding Security Conditions (`SEC-CAT138-20..28`)

The following 9 binding security conditions are mandatory requirements for Phase 4 implementation. Every condition must be verified by automated security test cases marked with `[Trait("Category", "Security")]`:

#### `SEC-CAT138-20: Monotonicity & Transactional Coupling of Catalog State`
- Updates to `CATALOG_STATE` (`structure_version`, `content_version`) MUST execute inside the identical database transaction as the catalog schema or metadata mutation.
- Version delta increments MUST be strictly positive (`@structureDelta >= 0`, `@contentDelta >= 0`, with at least one $> 0$).
- If a catalog transaction aborts or rolls back, version changes MUST roll back atomically with zero side-effects.
- Transaction concurrency MUST acquire row-level locks on `CATALOG_STATE` (`id = 1`) in PostgreSQL and SQL Server, and use `BEGIN IMMEDIATE` in SQLite, preventing phantom versions or lost increments.
- Monotonicity Guard: When background pollers evaluate polled versions, any observed decrement (`polled.StructureVersion < current.StructureVersion` or `polled.ContentVersion < current.ContentVersion`) MUST be treated as an epoch regression anomaly, logged at `LogLevel.Critical`, and rejected fail-closed without modifying the active snapshot.

#### `SEC-CAT138-21: Fail-Closed Snapshot Generation & Schema Integrity`
- If an individual table or relation definition fails validation, has corrupted schema metadata, or throws an unhandled error during snapshot loading:
  - The corrupted entity MUST be omitted fail-closed (`OmittedTables` / omitted relations) and increment a degraded entity counter.
  - Partial or malformed column/key definitions MUST NEVER be included in `CatalogSnapshot`.
  - Incomplete or corrupted relations MUST NEVER be exposed in navigation or search.
- Complete Load Failure: If `GetAllTablesAsync` or `GetAllRelationsAsync` fails completely (e.g. database timeout or transient loss of connection), the snapshot build process MUST abort cleanly without mutating or replacing the active `CatalogSnapshot` reference. The existing valid immutable snapshot continues serving reads.

#### `SEC-CAT138-22: Zero-Trust Pre-Filter Invariance & Search Hygiene`
- BM25 and vector ranking scanners MUST strictly evaluate only documents whose `Ordinal` bit is set in the caller's pre-filtered `CatalogVisibleBitSet`.
- Unpermitted tables MUST NEVER enter candidate heaps, be scored, influence term frequency / document length statistics, or affect search pagination counts (`total_hits`).
- Search Hygiene (`W0-C1`): Any hit whose relevance is derived exclusively from column names or column descriptions that the caller is not permitted to see (via consent or restricted scope rules #143) MUST be dropped fail-closed.
- `total_hits` in search response MUST reflect only visible matching tables returned to the caller, never global matching table counts.

#### `SEC-CAT138-23: Multi-Epoch Caller Cache Invalidation & Isolation`
- The cache key MUST strictly incorporate all authorization boundaries: `user:{TenantId}:{SubjectSid}:{ScopeKey}:{RolesKey}:{CallerClass}`. No cache entry may ever be shared across differing tenants, user identities, scopes, or caller roles.
- Lookup Validation: Every lookup in `CatalogVisibilityCache` MUST validate the cached entry against:
  1. `entry.CatalogVersion == currentSnapshot.Version` (structure and content versions).
  2. `entry.PolicyEpoch == currentPolicyEpoch` (read from `IPolicyEpochRepository`).
  3. `entry.RebacGeneration == currentTenantGeneration` (evaluated against Zanzibar tenant generation cache within a 1s window).
  4. `DateTimeOffset.UtcNow < entry.HardExpiresAt` (hard TTL, max 60s).
- Fail-Closed: If any epoch check fails, or if reading the policy epoch or ReBAC generation throws or times out, the lookup MUST treat the entry as an immediate cache miss (fail closed).
- LRU & Memory Bounds: The cache MUST enforce strict bounded size (`MaxCallers = 10,000`, `MaxMemoryBytes = 32 MB`) using LRU eviction to prevent memory exhaustion DoS.

#### `SEC-CAT138-24: Bounded Batching & DoS Defense in ReBAC Evaluation`
- On cache miss, ReBAC evaluation MUST NOT issue unbounded parallel fan-outs (`Task.WhenAll` over 5,000+ tables).
- Table evaluation MUST be partitioned into bounded chunks (`RebacBatchSize = 32`) with at most 8 parallel chunk tasks in flight (`Parallel.ForEachAsync` with `MaxDegreeOfParallelism = 8`), leveraging `IRebacEvaluator.BatchCheckAsync` with decision caching.
- Evaluation MUST honor request cancellation tokens and execute with a hard timeout (e.g. 5 seconds) to prevent thread-pool starvation or denial-of-service under heavy concurrent load.

#### `SEC-CAT138-25: Bulk Relations Tenant Isolation & Four-Eyes Cross-Source Gates`
- `GetAllRelationsAsync` MUST enforce tenant isolation boundaries. It must never return relations linking across tenant boundaries unless explicit multi-tenant cross-visibility is governed.
- Cross-Data-Source Gates: Relations linking tables across distinct data sources MUST enforce the four-eyes cross-source gate (`PLAN-BUG-75`, `PLAN-FED-01`). Unapproved cross-data-source relations (`cross_data_source_opt_in = false`) MUST NOT be surfaced in `CatalogSnapshot.AllRelations` or GraphQL schema generation.
- Navigation Hygiene: In search results and GraphQL relation fields, a relation is only navigable if the caller has verified visibility to BOTH the parent table and child table, and all foreign key columns are visible (not masked/suppressed).

#### `SEC-CAT138-26: LocalOnnx Supply Chain & File Sandbox`
- Path Traversal Defense: `ModelPath` and `TokenizerPath` must be validated against path traversal attacks (`..`, symlinks). Paths MUST resolve to regular files within an operator-configured allowlisted directory or absolute path.
- Constant-Time SHA-256 Verification: Prior to loading any ONNX model into native runtime memory, the file SHA-256 hash MUST be calculated and compared against configured `ModelSha256` using constant-time comparison `CryptographicOperations.FixedTimeEquals` to prevent side-channel timing attacks and supply-chain tampering.
- Zero Egress: `LocalOnnxEmbeddingGenerator` operates in an isolated sandbox with zero network egress. No outbound network sockets, HTTP requests, or DNS queries may be initiated during model loading or inference.

#### `SEC-CAT138-27: Ollama SSRF Defense & Egress Control`
- SSRF Prevention: `OllamaEndpoint` MUST be validated against strict outbound network policies. Outbound HTTP requests MUST be routed through `IHttpClientFactory` configured via `SecureOutboundHttp.AddSecureOutboundHandlers(EgressIntegrations.Catalog)` (or dedicated `EgressIntegrations.Ollama`).
- Loopback & Private IP Fencing: Private IP ranges (RFC 1918), loopback (`127.0.0.1`), and cloud metadata endpoints (`169.254.169.254`) are blocked unless the endpoint host is explicitly declared in `OutboundEgressOptions.TrustedIntegrations` allowlist.
- Transport Security: HTTPS is required by default. Plain HTTP is rejected unless `OllamaInsecureAllowHttp == true` is explicitly configured by the operator.
- Payload Sanitization & Confidentiality: Free-text descriptions, tags, and display names sent to Ollama MUST be sanitized via `SecretScrubber.Redact`. Zero row values or sensitive cleartext credentials may be dispatched. HTTP authorization headers or bearer tokens must be scrubbed from request logs.

#### `SEC-CAT138-28: Degradation Safety & Non-Hallucination Invariant`
- Graceful Degradation: If an embedding provider (LocalOnnx or Ollama) fails to initialize, encounters hash mismatch, or times out during inference, the gateway MUST NOT crash or reject requests.
- Automatic Fallback: System must automatically degrade to `CatalogEmbeddingProvider.None` (pure keyword BM25).
- Health & Mode Honesty: Health check endpoint (`GET /health`) must report `Status: "Degraded"` with `effective_provider: "None"` and `vector_ready: false`.
- Non-Hallucination Invariant: When running in degraded or `None` provider mode, requests with `mode = semantic` or `hybrid` MUST answer using BM25 keyword matching and return `effective_mode: "keyword"`. System MUST NEVER generate synthetic vector scores or fall back to fake hash projections.

---

### 8.3 Mandatory Security Test Requirements (`[Trait("Category", "Security")]`)

The following 9 security tests must be implemented in test assemblies under `tests/Autheris.Tests.Unit/Catalog/Security/` and `tests/Autheris.Tests.Integration/Catalog/Security/`:

| Test ID | Test Method Name | Invariant Verified |
|---|---|---|
| **T-CAT138-20** | `CatalogState_TransactionRollback_DoesNotBumpVersions_AndMonotonicityRegression_Rejected` | Verifies rollback prevents version bump; verifies decremented version is rejected as anomaly fail-closed (`SEC-CAT138-20`). |
| **T-CAT138-21** | `CatalogSnapshot_CorruptedTableOrRelation_IsOmittedFailClosed_WithoutCrashing` | Verifies corrupted entity is omitted fail-closed, error counter incremented, and valid snapshot serves intact (`SEC-CAT138-21`). |
| **T-CAT138-22** | `CatalogSearch_PreFilterBitSet_PreventsHiddenTableCandidateInclusionAndStatsLeak` | Verifies hidden tables never enter candidate heap or alter BM25 term frequencies/scoring (`SEC-CAT138-22`). |
| **T-CAT138-23** | `CatalogVisibilityCache_EpochChangeOrRebacBump_TriggersImmediateMiss_FailsClosed` | Verifies instant cache miss upon `structure_version`, `content_version`, policy epoch, or ReBAC generation change (`SEC-CAT138-23`). |
| **T-CAT138-24** | `CatalogVisibility_RebacBatching_ClampedTo32_AndEnforcesTimeout` | Verifies batch size is clamped to 32, max 8 parallel tasks in flight, and cancellation token propagates cleanly (`SEC-CAT138-24`). |
| **T-CAT138-25** | `BulkRelations_TenantIsolation_AndFourEyesCrossSourceGate_Enforced` | Verifies relations respect tenant isolation, unapproved cross-source relations are omitted, and masked FKs drop navigation (`SEC-CAT138-25`). |
| **T-CAT138-26** | `LocalOnnx_PathTraversal_AndSha256Mismatch_AbortsLoad_ZeroEgress` | Verifies path traversal (`../`) rejected, mismatched hash throws fail-closed via `FixedTimeEquals`, and no outbound sockets open (`SEC-CAT138-26`). |
| **T-CAT138-27** | `OllamaClient_SsrfBlocked_HttpRejectedWithoutInsecureFlag_PayloadRedacted` | Verifies loopback/metadata endpoints blocked by SSRF handler, HTTP rejected without flag, and payload secrets redacted (`SEC-CAT138-27`). |
| **T-CAT138-28** | `EmbeddingProvider_OnFailure_DegradesToKeywordOnly_ReportsDegraded_NoFakeScores` | Verifies provider initialization failure degrades cleanly to keyword BM25 with `effective_mode: "keyword"` and zero fake scores (`SEC-CAT138-28`). |

---

### 8.4 Review Verdict & Sign-off

- **Verdict:** **APPROVED WITH CONDITIONS**
- **Security Conditions:** `SEC-CAT138-20` through `SEC-CAT138-28` are binding and gate Phase 4 implementation.
- **Mandatory Test Suite:** `T-CAT138-20` through `T-CAT138-28` (`[Trait("Category", "Security")]`) must achieve 100% pass rate in CI before merge.

