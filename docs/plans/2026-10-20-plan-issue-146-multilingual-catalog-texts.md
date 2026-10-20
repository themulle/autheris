# Implementation Plan: PLAN-CAT-146-BC - Multilingual Catalog Texts, Language Negotiation & Multilingual Search (Steps B & C)

**Document ID:** `PLAN-CAT-146-BC`  
**Date:** 2026-10-20  
**Status:** PROPOSED (Phase 2 Architectural Specification)  
**Author:** Solution Architect (Phase 2, csharp-architect)  
**Input PRD:** [`REQ-CAT-146`](2026-10-17-req-issue-146-multilingual-catalog-texts.md) (Document ID: `REQ-CAT-146`)  
**Predecessor Plan:** [`PLAN-CAT-146-A`](2026-10-17-plan-issue-146-stepA-catalog-display-name.md) (`displayName` in MCP Tools)  
**Related Plans:** [`PLAN-CAT-138-INC2-5`](2026-10-20-plan-issue-138-catalog-at-scale-inc2-5.md) (`CATALOG_STATE`, search freshness, `CatalogVisibleBitSet`), [`PLAN-GOV-111`](2026-10-16-plan-issue-111-dataset-tags-catalog-visibility.md) (catalog visibility tiers `Hidden`/`Summary`/`Full`), [`REQ-GOV-158`](2026-10-17-req-issue-158-consent-description.md) (per-language consent descriptions)  
**Target Branch:** `feat/issue-146-multilingual-catalog-texts`  
**Target Runtimes & Dialects:** .NET 10, C# 13, SQLite 3, PostgreSQL 16, SQL Server 2022  
**Size:** M (Step B: Localized Texts & Language Negotiation [M], Step C: Multilingual Search & Diacritic Folding [S])  

---

## 1. Executive Summary & Architecture Overview

Issue #146 enhances Autheris from a single-language catalog into a multilingual data discovery gateway. While Step A delivered table-level `displayName` in MCP tools, Steps B and C complete the end-to-end multilingual capability across Domain, Storage, Application, API/GraphQL, MCP, and Search:

```
┌────────────────────────────────────────────────────────────────────────────────────────┐
│               MULTILINGUAL CATALOG ARCHITECTURE (STEPS B & C)                          │
│                                                                                        │
│   INGESTION & STORAGE (Step B)              CONSUMPTION & NEGOTIATION (Step B)         │
│   ┌──────────────────────────────┐          ┌──────────────────────────────────────┐   │
│   │ Table & TableColumn Models   │          │ ILanguageNegotiator                  │   │
│   │ LocalizedTexts (Value Object)│          │ 1. Explicit ?language= / param       │   │
│   │ LocalizedTextsValidator      │          │ 2. HTTP Accept-Language (RFC 4647)   │   │
│   └──────────────┬───────────────┘          │ 3. Fallback to DefaultLanguage ("en")│   │
│                  │                          └──────────────────┬───────────────────┘   │
│                  ▼                                             ▼                       │
│   ┌──────────────────────────────┐          ┌──────────────────────────────────────┐   │
│   │ Baseline DDL & Shape (x3)    │          │ Consumers & API Protocols            │   │
│   │ TABLES.texts_json            │          │ - MCP: list_datasets, describe_      │   │
│   │ TABLE_COLUMNS.display_name   │          │ - REST: /api/v1/catalog/tables       │   │
│   │ TABLE_COLUMNS.texts_json     │          │   (Content-Language, Vary)           │   │
│   │ Repositories (x3):           │          │ - GraphQL: catalog(language:)        │   │
│   │ Zero-Alloc System.Text.Json  │          └──────────────────────────────────────┘   │
│   │ Bump content_version ONLY    │                                                     │
│   └──────────────┬───────────────┘                                                     │
│                  │                                                                     │
│                  ▼                                                                     │
│   MULTILINGUAL SEARCH & DIACRITIC FOLDING (Step C)                                     │
│   ┌────────────────────────────────────────────────────────────────────────────────┐   │
│   │ CatalogSearchEngine & SmartSchemaTokenizer                                     │   │
│   │ - Multilingual document builder: Default texts + All translations (redacted)  │   │
│   │ - DiacriticNormalizer: German umlauts (ä->ae, ö->oe, ü->ue, ß->ss) & NFD accent│   │
│   │   stripping (ä->a, é->e, ñ->n) matching "auftraege" = "aufträge" = "auftrage"  │   │
│   │ - Fail-Closed: Preserves CatalogVisibleBitSet & Summary/Full visibility tiers  │   │
│   └────────────────────────────────────────────────────────────────────────────────┘   │
└────────────────────────────────────────────────────────────────────────────────────────┘
```

### Key Architectural Decisions

| # | Topic | Decision | Architectural Rationale |
|---|---|---|---|
| **D-01** | Storage Topology | Single `texts_json TEXT NULL` column in `TABLES` and `TABLE_COLUMNS` across all three DBMS engines. | **Anti-Pattern Rejected:** Child translation tables (`TABLE_TRANSLATIONS`, `COLUMN_TRANSLATIONS`) would create 300,000+ rows at scale, degrading refresh performance. Translations are never joined or filtered in SQL. |
| **D-02** | Default Language Invariance | Default language remains in primary columns (`Table.DisplayName`, `Description`, `LongDescription`, `TableColumn.DisplayName`, `Description`). Translations never overwrite default columns. | Guarantees 100% backward compatibility for existing code paths, indexing, and callers unaware of localization. Storing default language in `texts_json` is strictly rejected on write. |
| **D-03** | Language Tag Normalization | RFC 5646 / BCP 47 canonical tags (e.g., `en`, `de`, `en-US`, `de-CH`, `zh-Hant`), verified using zero-allocation Span parser. | Industry compliance, deterministic dictionary lookup, prevents duplicate or malformed tags. |
| **D-04** | Language Negotiation | RFC 4647 Lookup algorithm: Explicit parameter $\to$ HTTP `Accept-Language` with q-values $\to$ Configured `DefaultLanguage`. Fallback chain resolution with field-by-field cascade. | Invalid or unsupported tags gracefully fall back to default language without error; responses explicitly state effective and requested language. |
| **D-05** | Cache & Version Freshness | Text updates increment `content_version` (NOT `structure_version`) in `CATALOG_STATE`. REST endpoints emit `Vary: Accept-Language`. | Text edits do not alter GraphQL types/schema structure; triggers search index and metadata cache refreshes without costly GraphQL schema rebuilds. |
| **D-06** | Search Indexing & Diacritic Folding | Single multilingual BM25/vector document per table containing technical names, default texts, and all stored translations. Tokenizer folds German umlauts and NFD accents. | Avoids redundant per-language indexes. Queries matching "auftraege", "aufträge", or "auftrage" resolve to the same table. Zero-allocation fast path for ASCII text. |
| **D-07** | Fail-Closed Governance | Text retrieval and search indexing strictly enforce caller's `CatalogVisibleBitSet` and visibility tier (`Hidden`, `Summary`, `Full`). | A caller in `Summary` tier never receives column translations. Hidden datasets are never indexed into visible results or leaked via search ranking. |

---

## 2. Component Architecture & Layering

Strictly conforms to Clean Architecture: `Domain` $\to$ `Application` $\to$ `Infrastructure` $\to$ `Presentation` (`Api` / `GraphQL` / `Mcp`).

```
src/
├── Autheris.Domain/
│   ├── Model/
│   │   ├── TableModels.cs             # LocalizedTexts, LocalizedTextEntry, Table & TableColumn updates
│   │   ├── McpDatasetModels.cs        # McpDatasetColumn.DisplayName, McpDatasetList.Language, etc.
│   │   └── CatalogApiModels.cs        # CatalogDatasetDetail & Summary localization additions
│   └── Options/
│       └── CatalogLocalizationOptions.cs # DefaultLanguage, SupportedLanguages (max 16)
│
├── Autheris.Application/
│   ├── Catalog/
│   │   ├── Localization/
│   │   │   ├── Bcp47LanguageTag.cs       # Span-based BCP 47 parser & canonicalizer
│   │   │   ├── ILanguageNegotiator.cs    # Negotiation contract & LanguageNegotiationResult
│   │   │   ├── LanguageNegotiator.cs     # RFC 4647 lookup & Accept-Language parser
│   │   │   └── LocalizedTextsValidator.cs# Ingestion & Admin write validation
│   │   └── Search/
│   │       ├── DiacriticNormalizer.cs    # Fast-path German umlaut & NFD accent folding
│   │       ├── SmartSchemaTokenizer.cs   # Multilingual & diacritic-aware tokenization
│   │       └── CatalogSearchEngine.cs    # Multilingual document builder & caller language projection
│   └── Mcp/Services/
│       ├── McpDatasetCatalog.cs       # Language negotiation in list_datasets, describe_dataset
│       └── McpDatasetTools.cs         # JSON Schema definitions with 'language' argument
│
├── Autheris.Infrastructure/
│   ├── Persistence/
│   │   ├── Migrations/
│   │   │   ├── SqliteBaselineSchema.cs     # 0001 DDL: texts_json & display_name
│   │   │   ├── PostgreSqlBaselineSchema.cs # 0001 DDL: texts_json & display_name
│   │   │   ├── SqlServerBaselineSchema.cs  # 0001 DDL: texts_json & display_name
│   │   │   └── Shape/                      # Updated governance-shape.*.json snapshots
│   │   ├── Catalog/
│   │   │   └── CatalogLocalizationJsonContext.cs # System.Text.Json Source Generator
│   │   ├── SqliteGovernanceRepository.Catalog.cs     # Zero-alloc read/write & content_version bump
│   │   ├── PostgreSqlGovernanceRepository.Catalog.cs # Zero-alloc read/write & content_version bump
│   │   └── SqlServerGovernanceRepository.Catalog.cs  # Zero-alloc read/write & content_version bump
│
└── Autheris.Api / Autheris.GraphQL/
    ├── Endpoints/CatalogApiEndpoints.cs   # GET /api/v1/catalog/tables & datasets (?language, Accept-Language)
    └── Types/QueryTypes.cs               # GraphQL catalog(language:) resolver & TableMetadataDto
```

---

## 3. Detailed Specifications

### 3.1 Step B: Domain Models & Options

#### `Autheris.Domain/Model/TableModels.cs`
```csharp
public sealed record LocalizedTextEntry(
    string? DisplayName = null,
    string? Description = null,
    string? LongDescription = null);

public sealed record LocalizedTexts
{
    public static readonly LocalizedTexts Empty = new(ImmutableDictionary<string, LocalizedTextEntry>.Empty);

    public ImmutableDictionary<string, LocalizedTextEntry> Entries { get; init; }

    public LocalizedTexts() : this(ImmutableDictionary<string, LocalizedTextEntry>.Empty) { }

    public LocalizedTexts(ImmutableDictionary<string, LocalizedTextEntry> entries)
    {
        Entries = entries ?? ImmutableDictionary<string, LocalizedTextEntry>.Empty;
    }

    public bool IsEmpty => Entries.IsEmpty;

    /// <summary>
    /// Field-by-field fallback: resolves each field through the fallback chain,
    /// ultimately falling back to default texts (R-146-3, AC-146-3).
    /// </summary>
    public (string DisplayName, string? Description, string? LongDescription) Resolve(
        IEnumerable<string> fallbackChain,
        string defaultDisplayName,
        string? defaultDescription,
        string? defaultLongDescription)
    {
        string? resolvedDisplayName = null;
        string? resolvedDescription = null;
        string? resolvedLongDesc = null;

        foreach (var lang in fallbackChain)
        {
            if (Entries.TryGetValue(lang, out var entry))
            {
                if (resolvedDisplayName == null && !string.IsNullOrWhiteSpace(entry.DisplayName))
                    resolvedDisplayName = entry.DisplayName;
                if (resolvedDescription == null && !string.IsNullOrWhiteSpace(entry.Description))
                    resolvedDescription = entry.Description;
                if (resolvedLongDesc == null && !string.IsNullOrWhiteSpace(entry.LongDescription))
                    resolvedLongDesc = entry.LongDescription;

                if (resolvedDisplayName != null && resolvedDescription != null && resolvedLongDesc != null)
                    break;
            }
        }

        return (
            resolvedDisplayName ?? defaultDisplayName,
            resolvedDescription ?? defaultDescription,
            resolvedLongDesc ?? defaultLongDescription
        );
    }
}
```

- Update `Table`:
  ```csharp
  public LocalizedTexts Texts { get; init; } = LocalizedTexts.Empty;
  ```
- Update `TableColumn`:
  ```csharp
  public string? DisplayName { get; init; }
  public LocalizedTexts Texts { get; init; } = LocalizedTexts.Empty;
  ```

#### `Autheris.Domain/Options/CatalogLocalizationOptions.cs`
```csharp
public sealed class CatalogLocalizationOptions
{
    public const string SectionName = "Gateway:Catalog";

    public string DefaultLanguage { get; init; } = "en";
    public IReadOnlyList<string> SupportedLanguages { get; init; } = ["en", "de"];

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(DefaultLanguage))
            throw new OptionsValidationException(nameof(DefaultLanguage), typeof(CatalogLocalizationOptions),
                ["DefaultLanguage must not be empty."]);

        if (!Bcp47LanguageTag.IsValid(DefaultLanguage, out var canonicalDefault))
            throw new OptionsValidationException(nameof(DefaultLanguage), typeof(CatalogLocalizationOptions),
                [$"DefaultLanguage '{DefaultLanguage}' is not a valid BCP 47 language tag."]);

        if (SupportedLanguages == null || SupportedLanguages.Count == 0 || SupportedLanguages.Count > 16)
            throw new OptionsValidationException(nameof(SupportedLanguages), typeof(CatalogLocalizationOptions),
                ["SupportedLanguages must contain between 1 and 16 entries."]);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var lang in SupportedLanguages)
        {
            if (!Bcp47LanguageTag.IsValid(lang, out var canonicalTag))
                throw new OptionsValidationException(nameof(SupportedLanguages), typeof(CatalogLocalizationOptions),
                    [$"SupportedLanguage '{lang}' is not a valid BCP 47 language tag."]);
            if (!seen.Add(canonicalTag))
                throw new OptionsValidationException(nameof(SupportedLanguages), typeof(CatalogLocalizationOptions),
                    [$"Duplicate language tag '{lang}' in SupportedLanguages."]);
        }

        if (!seen.Contains(canonicalDefault))
            throw new OptionsValidationException(nameof(DefaultLanguage), typeof(CatalogLocalizationOptions),
                [$"DefaultLanguage '{DefaultLanguage}' must be present in SupportedLanguages."]);
    }
}
```

---

### 3.2 Step B: DDL, Schema Snapshots & Persistence

#### Baseline DDL Additions (`0001` migrations)
Edit in place across SQLite, PostgreSQL, and SQL Server baseline schemas:
- **`TABLES`**:
  - SQLite: `texts_json TEXT`
  - PostgreSQL: `texts_json TEXT`
  - SQL Server: `texts_json NVARCHAR(MAX) NULL`
- **`TABLE_COLUMNS`**:
  - SQLite: `display_name TEXT`, `texts_json TEXT`
  - PostgreSQL: `display_name TEXT`, `texts_json TEXT`
  - SQL Server: `display_name NVARCHAR(512) NULL`, `texts_json NVARCHAR(MAX) NULL`

#### Governance Shape Snapshots
Update `governance-shape.sqlite.json`, `governance-shape.postgresql.json`, `governance-shape.sqlserver.json`:
- `TABLES` columns append `texts_json` (type: `TEXT`/`NVARCHAR`, family: `string`).
- `TABLE_COLUMNS` columns append `display_name` and `texts_json` (type: `TEXT`/`NVARCHAR`, family: `string`).

#### Zero-Allocation Serialization & Error Handling
- Create `CatalogLocalizationJsonContext`:
  ```csharp
  [JsonSourceGenerationOptions(
      PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
      DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
  [JsonSerializable(typeof(Dictionary<string, LocalizedTextEntry>))]
  internal sealed partial class CatalogLocalizationJsonContext : JsonSerializerContext { }
  ```
- Repository reads (`GetStructuralTableMetadataAsync`, `GetAllTablesAsync`):
  ```csharp
  // Read texts_json column safely:
  var textsJson = reader.IsDBNull(textsColIdx) ? null : reader.GetString(textsColIdx);
  LocalizedTexts texts = LocalizedTexts.Empty;
  if (!string.IsNullOrWhiteSpace(textsJson))
  {
      try
      {
          var dict = JsonSerializer.Deserialize(textsJson, CatalogLocalizationJsonContext.Default.DictionaryStringLocalizedTextEntry);
          if (dict != null)
              texts = new LocalizedTexts(dict.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase));
      }
      catch (Exception ex) when (ex is JsonException or FormatException)
      {
          // SEC-146-6: Malformed texts_json treated as empty, logged once without text content
          _logger?.LogWarning(ex, "Catalog row {TableId} has invalid texts_json and was loaded without translations.", tableId);
      }
  }
  ```
- Repository writes (`UpsertTableMetadataAsync`):
  - Serialize `table.Texts` to `@textsJson` (or `DBNull.Value` if empty).
  - In column loop, bind `@displayName` and `@textsJson`.
  - **Version Bumping Rule:**
    - If structural schema changed (columns added/dropped/altered, key definition replaced): `BumpCatalogVersionInternalAsync(conn, tx, structureDelta: 1, contentDelta: 0, ct)`.
    - If text/metadata-only update: `BumpCatalogVersionInternalAsync(conn, tx, structureDelta: 0, contentDelta: 1, ct)`.

---

### 3.3 Step B: Language Negotiation & Validation

#### Negotiation Flow (`ILanguageNegotiator`)
```csharp
public sealed record LanguageNegotiationResult(
    string EffectiveLanguage,
    string? RequestedLanguage,
    bool IsFallback,
    IReadOnlyList<string> FallbackChain);

public interface ILanguageNegotiator
{
    LanguageNegotiationResult Negotiate(string? explicitLanguage, string? acceptLanguageHeader);
}
```

**Resolution Algorithm (RFC 4647 Lookup):**
1. **Explicit parameter** (`language` arg from MCP/GraphQL or `?language=`):
   - Check length $\le 35$ characters.
   - If syntactically valid BCP 47:
     - Perform subtag truncation lookup against `SupportedLanguages` (e.g., `de-CH` $\to$ `de`).
     - If match found: return `EffectiveLanguage: match`, `RequestedLanguage: explicitLanguage`, `IsFallback: false`, `FallbackChain: [match, DefaultLanguage]`.
   - If invalid syntax or unsupported:
     - Return `EffectiveLanguage: DefaultLanguage`, `RequestedLanguage: explicitLanguage`, `IsFallback: true`, `FallbackChain: [DefaultLanguage]`.
2. **HTTP `Accept-Language` header** (if no explicit parameter):
   - Guard: Header length $\le 512$ chars, max 8 language ranges.
   - Parse quality values `q` (0.0 to 1.0, default 1.0). Sort descending by `q`.
   - For each tag in order: perform RFC 4647 subtag truncation lookup against `SupportedLanguages`.
   - On first match: return `EffectiveLanguage: match`, `RequestedLanguage: matchedTag`, `IsFallback: false`, `FallbackChain: [match, DefaultLanguage]`.
3. **Default Language Fallback**:
   - If header is absent or no ranges match: return `EffectiveLanguage: DefaultLanguage`, `RequestedLanguage: null`, `IsFallback: false`, `FallbackChain: [DefaultLanguage]`.

#### Admin & Ingestion Validation (`LocalizedTextsValidator`)
Applied during metadata upserts, OpenAPI import, and admin catalog mutations:
1. **Language Tag Checks**:
   - Normalized via `Bcp47LanguageTag.Validate`. Length $\le 35$ characters.
   - Must be present in `SupportedLanguages`.
   - Must NOT equal `DefaultLanguage` (R-146-4: single source of truth).
   - Maximum 16 language entries per `LocalizedTexts` object.
2. **String Limits**:
   - `DisplayName`: $\le 128$ characters, no control characters.
   - `Description`: $\le 4,000$ characters, no control characters except standard whitespace.
   - `LongDescription`: $\le 32,000$ characters, allowed whitespace: `\r`, `\n`, `\t`.
3. **Injection Prevention**:
   - Reject HTML/script injection tags (e.g., `<script`, `javascript:`, `<iframe`, `<object`).
   - Remove/reject invisible characters and bidi override controls (SEC-CAT146A-03).
4. **Fail-Closed Reporting**:
   - On violation, throw `ValidationException` detailing the constraint and field name.
   - **Crucial Rule:** NEVER echo the caller's text in the error message or log (SEC-146-3, SEC-146-4).

---

### 3.4 Step B: Consumer Wiring & API Protocols

#### 1. MCP Tools (`list_datasets`, `describe_dataset`, `search_catalog`)
- **Schema Updates in `McpDatasetTools.cs`**:
  Add optional property to tool schemas:
  ```json
  "language": {
    "type": "string",
    "maxLength": 35,
    "description": "Optional BCP 47 language tag (e.g. 'de', 'en') to return localized display names and descriptions."
  }
  ```
- **Execution in `McpDatasetCatalog.cs`**:
  - Call `_languageNegotiator.Negotiate(languageArg, httpContext?.Request?.Headers.AcceptLanguage)`.
  - In `ListDatasetsAsync`: Resolve `Table.DisplayName` and `Table.Description` using the negotiated fallback chain. Populate `McpDatasetList.Language` and `RequestedLanguage`.
  - In `DescribeDatasetAsync`: Resolve table and column display names and descriptions. Fall back empty column display names to `ColumnName`. Populate `McpDatasetDescription.Language` and `RequestedLanguage`.
  - In `SearchCatalogAsync`: Resolve hit `DisplayName` in the caller's negotiated language.

#### 2. REST API (`CatalogApiEndpoints.cs`)
- Support endpoints:
  - `GET /api/v1/catalog/tables` and `GET /api/v1/catalog/tables/{id}`
  - `GET /api/v1/catalog/datasets` and `GET /api/v1/catalog/datasets/{id}`
- Query parameter: `?language=...`
- Header: `Accept-Language`
- Behavior:
  - Invoke `_languageNegotiator.Negotiate(queryLanguage, httpContext.Request.Headers.AcceptLanguage)`.
  - Set response headers:
    - `Content-Language: {EffectiveLanguage}`
    - `Vary: Accept-Language`
  - Return JSON DTOs with localized fields and effective `language`.

#### 3. GraphQL API (`QueryTypes.cs`)
- Update query signature:
  ```graphql
  catalog(
    domain: String,
    first: Int,
    after: Int,
    search: String,
    tag: String,
    language: String
  ): [TableMetadataDto!]!
  ```
- In `GetCatalogAsync`:
  - Negotiate language using `language` parameter or HTTP `Accept-Language` from `IHttpContextAccessor`.
  - Project `TableMetadataDto`:
    - `DisplayName`: Resolved display name.
    - `Description`: Resolved description.
    - `Language`: Effective language tag.

---

### 3.5 Step C: Multilingual Search & Diacritic Folding

#### 1. Search Document Construction (`CatalogSearchEngine.cs`)
When `RebuildIndexAsync` builds documents:
```csharp
var docBuilder = new StringBuilder(1024);

// Technical names
docBuilder.Append($"{t.Identifier.Domain} {t.Identifier.Schema} {t.Identifier.TableName} ");

// Default language texts (scrubbed)
if (!string.IsNullOrWhiteSpace(t.Table.DisplayName))
    docBuilder.Append($"{SecretScrubber.Redact(t.Table.DisplayName)} ");
if (!string.IsNullOrWhiteSpace(t.Table.Description))
    docBuilder.Append($"{SecretScrubber.Redact(t.Table.Description)} ");
if (!string.IsNullOrWhiteSpace(t.Table.LongDescription))
    docBuilder.Append($"{SecretScrubber.Redact(t.Table.LongDescription)} ");

// All table translation texts (scrubbed)
foreach (var (_, entry) in t.Table.Texts.Entries)
{
    if (!string.IsNullOrWhiteSpace(entry.DisplayName))
        docBuilder.Append($"{SecretScrubber.Redact(entry.DisplayName)} ");
    if (!string.IsNullOrWhiteSpace(entry.Description))
        docBuilder.Append($"{SecretScrubber.Redact(entry.Description)} ");
    if (!string.IsNullOrWhiteSpace(entry.LongDescription))
        docBuilder.Append($"{SecretScrubber.Redact(entry.LongDescription)} ");
}

// Columns: technical name, types, default texts & translations (scrubbed)
foreach (var c in t.Columns)
{
    docBuilder.Append($"{c.ColumnName} {c.DataType} ");
    if (!string.IsNullOrWhiteSpace(c.DisplayName))
        docBuilder.Append($"{SecretScrubber.Redact(c.DisplayName)} ");
    if (!string.IsNullOrWhiteSpace(c.Description))
        docBuilder.Append($"{SecretScrubber.Redact(c.Description)} ");

    foreach (var (_, colEntry) in c.Texts.Entries)
    {
        if (!string.IsNullOrWhiteSpace(colEntry.DisplayName))
            docBuilder.Append($"{SecretScrubber.Redact(colEntry.DisplayName)} ");
        if (!string.IsNullOrWhiteSpace(colEntry.Description))
            docBuilder.Append($"{SecretScrubber.Redact(colEntry.Description)} ");
    }
}
```

#### 2. Diacritic Normalization & Folding (`DiacriticNormalizer.cs`)
High-performance diacritic folding with zero-allocation ASCII fast-path:
```csharp
public static class DiacriticNormalizer
{
    public static string NormalizeAndFold(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        ReadOnlySpan<char> span = text.AsSpan();
        if (System.Text.Ascii.IsValid(span))
        {
            return text.ToLowerInvariant();
        }

        // 1. Fold German Umlauts & Sharp S:
        // ä -> ae, ö -> oe, ü -> ue, ß -> ss
        var folded = FoldGermanUmlauts(text);

        // 2. Unicode Form D (NFD) decomposition to strip remaining combining diacritical marks
        var normalizedD = folded.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalizedD.Length);
        foreach (char ch in normalizedD)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(ch);
            }
        }

        return sb.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
    }
}
```

#### 3. Tokenizer Integration (`SmartSchemaTokenizer.cs`)
- When tokenizing indexing documents and search queries:
  - If a token contains umlauts (e.g. `aufträge`), emit both:
    1. The German phonetic expansion: `auftraege` (`ä` $\to$ `ae`)
    2. The accent-stripped form: `auftrage` (`ä` $\to$ `a`)
    3. The normalized base token: `aufträge`
  - Result: Searching `auftraege`, `aufträge`, or `auftrage` matches identically!

#### 4. Search Execution Invariance
- Candidate filtering runs `isAllowed(id)` before scoring.
- `CatalogVisibleBitSet` pre-filter and `Summary`/`Full` visibility tiers are strictly maintained.
- If a caller has `Summary` tier on a table:
  - Column translations are dropped from consideration.
  - Search hit returns `DisplayName` in caller's negotiated language.

---

## 4. Work Packages & Implementation Tasks

```
┌────────────────────────────────────────────────────────────────────────────────────────┐
│                              WORK PACKAGE BREAKDOWN                                    │
│                                                                                        │
│   WP-1: Domain Models & Options                WP-2: Baseline DDL, Shapes & Repos      │
│   - LocalizedTexts & LocalizedTextEntry        - 0001 DDL updates (SQLite, PG, MSSQL)  │
│   - Table & TableColumn updates                - governance-shape.*.json updates       │
│   - CatalogLocalizationOptions                 - Zero-alloc System.Text.Json context   │
│   - Bcp47LanguageTag parser                    - Repositories read/write + versioning  │
│                                                                                        │
│   WP-3: Negotiation & Validation Service       WP-4: Multilingual Search & Diacritics  │
│   - ILanguageNegotiator & LanguageNegotiator   - DiacriticNormalizer (fast ASCII path) │
│   - LocalizedTextsValidator                    - SmartSchemaTokenizer expansion        │
│   - Fail-closed error codes                    - CatalogSearchEngine document builder  │
│                                                                                        │
│   WP-5: Consumer Endpoints & Protocols         WP-6: Security & Integration Suite      │
│   - MCP list_datasets & describe_dataset       - End-to-end multi-language test matrix │
│   - REST /api/v1/catalog/tables & datasets     - SEC-146 verification suite            │
│   - GraphQL catalog(language:) query           - Performance & zero-alloc validation   │
└────────────────────────────────────────────────────────────────────────────────────────┘
```

### Work Package 1: Domain Models, BCP 47 Parser & Configuration (Size: S)
- **Files Modified / Created:**
  - `src/Autheris.Domain/Model/TableModels.cs`
  - `src/Autheris.Domain/Model/McpDatasetModels.cs`
  - `src/Autheris.Domain/Model/CatalogApiModels.cs`
  - `src/Autheris.Domain/Options/CatalogLocalizationOptions.cs`
  - `src/Autheris.Application/Catalog/Localization/Bcp47LanguageTag.cs`
  - `src/Autheris.Domain/Options/GatewayOptions.cs`
- **Tasks:**
  1. Define `LocalizedTextEntry` and `LocalizedTexts` in `TableModels.cs`.
  2. Add `LocalizedTexts Texts` to `Table` and `TableColumn`; add `string? DisplayName` to `TableColumn`.
  3. Implement `Bcp47LanguageTag` with span-based syntax validation and canonicalization.
  4. Create `CatalogLocalizationOptions` with `Validate()` method; bind to `Gateway:Catalog`.
  5. Update `McpDatasetModels.cs` and `CatalogApiModels.cs` with `DisplayName`, `Language`, `RequestedLanguage`.

### Work Package 2: Baseline DDL, Shape Snapshots & Repository Persistence (Size: M)
- **Files Modified / Created:**
  - `src/Autheris.Infrastructure/Persistence/Migrations/SqliteBaselineSchema.cs`
  - `src/Autheris.Infrastructure/Persistence/Migrations/PostgreSqlBaselineSchema.cs`
  - `src/Autheris.Infrastructure/Persistence/Migrations/SqlServerBaselineSchema.cs`
  - `src/Autheris.Infrastructure/Persistence/Migrations/Shape/governance-shape.sqlite.json`
  - `src/Autheris.Infrastructure/Persistence/Migrations/Shape/governance-shape.postgresql.json`
  - `src/Autheris.Infrastructure/Persistence/Migrations/Shape/governance-shape.sqlserver.json`
  - `src/Autheris.Infrastructure/Persistence/Catalog/CatalogLocalizationJsonContext.cs`
  - `src/Autheris.Infrastructure/Persistence/SqliteGovernanceRepository.Catalog.cs`
  - `src/Autheris.Infrastructure/Persistence/PostgreSqlGovernanceRepository.Catalog.cs`
  - `src/Autheris.Infrastructure/Persistence/SqlServerGovernanceRepository.Catalog.cs`
- **Tasks:**
  1. Update `TABLES` and `TABLE_COLUMNS` DDL in `0001` migrations across SQLite, PostgreSQL, and SQL Server.
  2. Update governance shape JSON files and verify shape assertion tests pass.
  3. Generate `CatalogLocalizationJsonContext` for source-generated JSON serialization.
  4. Implement `texts_json` read/write in repository `GetTableMetadataAsync`, `GetAllTablesAsync`, and `UpsertTableMetadataAsync`.
  5. Ensure malformed `texts_json` logs warning with table ID only and loads table with empty translations (SEC-146-6).
  6. Enforce that pure text updates bump `content_version` (NOT `structure_version`) in `CATALOG_STATE`.

### Work Package 3: Language Negotiation & Write Validation (Size: S)
- **Files Modified / Created:**
  - `src/Autheris.Application/Catalog/Localization/ILanguageNegotiator.cs`
  - `src/Autheris.Application/Catalog/Localization/LanguageNegotiator.cs`
  - `src/Autheris.Application/Catalog/Localization/LocalizedTextsValidator.cs`
- **Tasks:**
  1. Implement `LanguageNegotiator` supporting RFC 4647 lookup, subtag truncation (`de-CH` $\to$ `de`), and HTTP `Accept-Language` q-value parsing.
  2. Implement `LocalizedTextsValidator` enforcing field limits, character restrictions, HTML/script tag rejection, and default language exclusion.
  3. Wire services in `Autheris.Application` DI registration.

### Work Package 4: Multilingual Search & Diacritic Folding (Step C) (Size: S)
- **Files Modified / Created:**
  - `src/Autheris.Application/Catalog/Search/DiacriticNormalizer.cs`
  - `src/Autheris.Application/Catalog/Search/SmartSchemaTokenizer.cs`
  - `src/Autheris.Application/Catalog/Search/CatalogSearchEngine.cs`
- **Tasks:**
  1. Create `DiacriticNormalizer` with ASCII fast-path, German umlaut folding, and Unicode Form D accent stripping.
  2. Update `SmartSchemaTokenizer.Tokenize` to expand umlaut variants so `auftraege`, `aufträge`, and `auftrage` match.
  3. Update `CatalogSearchEngine.RebuildIndexAsync` to concatenate default texts and all translations into the search document, redacted with `SecretScrubber.Redact`.
  4. Ensure `Search` resolves hit `DisplayName` in caller's negotiated language while strictly enforcing `CatalogVisibleBitSet`.

### Work Package 5: API Endpoints, GraphQL & MCP Consumer Wiring (Size: M)
- **Files Modified / Created:**
  - `src/Autheris.Application/Mcp/Services/McpDatasetTools.cs`
  - `src/Autheris.Application/Mcp/Services/McpDatasetCatalog.cs`
  - `src/Autheris.Api/Endpoints/CatalogApiEndpoints.cs`
  - `src/Autheris.GraphQL/Types/QueryTypes.cs`
- **Tasks:**
  1. Add `language` parameter to `list_datasets`, `describe_dataset`, and `search_catalog` in `McpDatasetTools.cs`.
  2. Wire language negotiation and text resolution in `McpDatasetCatalog.cs`; emit `language` and `requestedLanguage`.
  3. Map `GET /api/v1/catalog/tables` and `/api/v1/catalog/tables/{id}` in `CatalogApiEndpoints.cs` with `?language=` and `Accept-Language`, emitting `Content-Language` and `Vary: Accept-Language`.
  4. Add `language: String` argument to `catalog` query in `QueryTypes.cs`; project resolved texts and `language` on `TableMetadataDto`.

### Work Package 6: Comprehensive Security & Integration Testing (Size: S)
- **Files Modified / Created:**
  - `tests/Autheris.Tests.Unit/Catalog/LanguageNegotiationTests.cs`
  - `tests/Autheris.Tests.Unit/Catalog/LocalizedTextsValidatorTests.cs`
  - `tests/Autheris.Tests.Unit/Catalog/DiacriticNormalizerTests.cs`
  - `tests/Autheris.Tests.Unit/Catalog/CatalogSearchMultilingualTests.cs`
  - `tests/Autheris.Tests.Unit/Mcp/McpMultilingualCatalogTests.cs`
  - `tests/Autheris.Tests.Integration/CatalogMultilingualIntegrationTests.cs`
  - `tests/Autheris.Tests.Unit/Security/MultilingualCatalogSecurityTests.cs`
- **Tasks:**
  1. Author unit tests for negotiation, validation, and diacritics.
  2. Author repository and consumer integration tests.
  3. Author security test suite verifying SEC-146-1 through SEC-146-8.

---

## 5. Security Requirements & Fail-Closed Guardrails

| Requirement | Threat Vector | Mitigation Strategy | Enforcement Mechanism |
|---|---|---|---|
| **SEC-146-1** No Data Leakage | Hidden table display name/description leaks via translations or search | Translations inherit table visibility tier (`Hidden`/`Summary`/`Full`). Filtered out before scoring or listing. | ReBAC & `CatalogVisibleBitSet` pre-filter; verified in `MultilingualCatalogSecurityTests`. |
| **SEC-146-2** Column Visibility | Column translations leaked to unconsented or `Summary` view callers | Column translations are returned only for columns the caller is permitted to see. `Summary` view drops all column data. | `Shape(isAdminView, table) == CatalogOutputShape.Summary` strips columns; `Deny` columns omitted. |
| **SEC-146-3** Prompt Injection | Untrusted translation text injected into LLM tool instructions or system guidance | Translations are strictly emitted as data fields; never interpolated into tool instructions or guidance strings. Control characters and HTML script tags rejected on write. | `LocalizedTextsValidator` rejects `<script`, `javascript:`, control chars. Read-side `McpCatalogText` guards display names. |
| **SEC-146-4** Header / Input Hardening | Malformed or oversized `language` parameter or `Accept-Language` header | String length capped (tag $\le 35$ chars, header $\le 512$ chars, max 8 tags). Invalid tags gracefully resolve to default without throwing. Never reflected raw in response headers. | `LanguageNegotiator` parses with spans; `Content-Language` emits only canonical tags from `SupportedLanguages`. |
| **SEC-146-5** DoS via Bloat | Massive JSON maps or unbounded languages exhaust gateway memory | Max 16 languages per entry; field caps: DisplayName (128), Description (4,000), LongDescription (32,000). Total supported languages capped at 16. | Validated at write time; rejected fail-closed before persistence. |
| **SEC-146-6** Catalog Load Resilience | Corrupted `texts_json` row in database crashes entire catalog load | Deserialization errors are caught per row; table is loaded with `LocalizedTexts.Empty` and warning logged without echoing row content. | Repository try-catch around `texts_json` parsing; no row content logged. |
| **SEC-146-7** Metadata Audit Hygiene | PII or malicious text logged in metadata audit trail | Metadata write audit records record table ID and language tag list only, never text content. | Auditing interceptor logs language metadata only. |
| **SEC-146-8** Cache Segregation | Shared proxy serves German response to English requester | REST responses emit `Vary: Accept-Language`. MCP responses are uncacheable. | ASP.NET Core response header configuration. |

---

## 6. Comprehensive TDD Test Matrix

### Unit Tests
- **`LanguageNegotiationTests`**:
  - `Negotiate_ExplicitSupportedTag_ReturnsExactMatch` (`de` $\to$ `de`)
  - `Negotiate_ExplicitSubtag_TruncatesToParentTag` (`de-CH` $\to$ `de`)
  - `Negotiate_ExplicitUnsupportedTag_FallsBackToDefault` (`fr` $\to$ `en`, `IsFallback=true`)
  - `Negotiate_ExplicitInvalidSyntax_FallsBackToDefault` (`xx_$$` $\to$ `en`, `IsFallback=true`)
  - `Negotiate_AcceptLanguageHeader_HonorsQualityWeights` (`de-CH,de;q=0.9,en;q=0.5` $\to$ `de`)
  - `Negotiate_AcceptLanguageHeader_Unsupported_FallsBackToDefault`
  - `Negotiate_OversizedHeader_TruncatesAndFallsBackSafely`
- **`LocalizedTextsValidatorTests`**:
  - `Validate_ValidTranslations_Passes`
  - `Validate_TagEqualsDefaultLanguage_RejectsWrite` (R-146-4)
  - `Validate_TagNotInSupportedLanguages_RejectsWrite`
  - `Validate_MoreThan16Languages_RejectsWrite`
  - `Validate_OversizedDisplayName_RejectsWrite` (> 128 chars)
  - `Validate_OversizedDescription_RejectsWrite` (> 4,000 chars)
  - `Validate_OversizedLongDescription_RejectsWrite` (> 32,000 chars)
  - `Validate_DisallowedControlCharacters_RejectsWrite`
  - `Validate_HtmlScriptTags_RejectsWrite` (`<script>alert(1)</script>`)
  - `Validate_ErrorDetails_NeverEchoesPayload` (SEC-146-3)
- **`DiacriticNormalizerTests`**:
  - `Normalize_PureAscii_ReturnsOriginalInstanceWithoutAllocation`
  - `Normalize_GermanUmlauts_FoldsCorrectly` (`Aufträge` $\to$ `auftraege`, `Groß` $\to$ `gross`)
  - `Normalize_AccentedCharacters_DecomposesAndStrips` (`café` $\to$ `cafe`, `año` $\to$ `ano`)
- **`SmartSchemaTokenizerTests`**:
  - `Tokenize_WordWithUmlauts_EmitsBothGermanExpansionAndAccentStrippedVariants`
  - `Tokenize_SearchQuery_MatchesIndexedUmlautVariants`
- **`CatalogSearchMultilingualTests`**:
  - `Search_GermanQuery_MatchesTableWithGermanTranslation`
  - `Search_DiacriticQueryVariants_AllMatch` ("auftraege", "aufträge", "auftrage")
  - `Search_FrenchQuery_MatchesFrenchColumnTranslation`
  - `Search_HitDisplayName_EmittedInCallerNegotiatedLanguage`

### Integration Tests
- **`CatalogMultilingualIntegrationTests`**:
  - `Repository_RoundTrip_PersistsAndReadsTextsJson_AllEngines` (SQLite, PostgreSQL, SQL Server)
  - `Repository_MalformedTextsJson_LoadsTableWithEmptyTranslations` (SEC-146-6)
  - `Repository_TextOnlyUpdate_BumpsContentVersion_NotStructureVersion`
  - `Mcp_ListDatasets_ReturnsNegotiatedLanguageAndFallbackMetadata`
  - `Mcp_DescribeDataset_ReturnsLocalizedColumnsAndFallbackChain`
  - `RestApi_GetCatalogTables_EmitsContentLanguageAndVaryHeaders`
  - `GraphQL_CatalogQuery_ResolvesLanguageArgument`

### Security Tests (`[Trait("Category","Security")]`)
- **`MultilingualCatalogSecurityTests`**:
  - `SEC01_HiddenTable_TranslationsNeverAppearInListOrSearch`
  - `SEC02_SummaryTier_ColumnTranslationsNeverExposed`
  - `SEC03_DeniedColumn_TranslationNeverMatchedOrReturned`
  - `SEC04_AcceptLanguage_HeaderInjectionAttempt_SanitizedSafely`
  - `SEC05_MalformedTextsJson_NeverThrowsOrCrashesCatalogLoad`

---

## 7. Review Checklist

1. [x] **Unidirectional dependencies:** Domain has zero references to Infrastructure or Api.
2. [x] **No redundant abstractions:** Single `ILanguageNegotiator` interface required for testability and DI; direct value object operations on `LocalizedTexts`.
3. [x] **DI lifetimes correct:** `ILanguageNegotiator` is Singleton (stateless, options-backed); no captive dependencies.
4. [x] **Async all the way & CancellationTokens:** Propagated through all repository and search execution boundaries.
5. [x] **Modern C# 13 idioms:** `readonly record struct`, immutable dictionaries, primary constructors, collection expressions (`[...]`), `System.Text.Ascii.IsValid`.
6. [x] **Zero-allocation hot paths:** Fast ASCII span checks before normalization; System.Text.Json source generators for database JSON.
7. [x] **Fail-closed security:** Strict visibility pre-filtering; no information leakage in error messages or logs.


---

## 8. Phase 3 Security & Compliance Review (Audit Report)

**Document ID:** `SEC-CAT-146`  
**Reviewer:** Phase 3 Security Expert (`csharp-security-expert`)  
**Date:** 2026-10-20  
**Status:** APPROVED WITH CONDITIONS  
**Scope:** Architectural Plan `PLAN-CAT-146-BC` (Multilingual Catalog Texts, Language Negotiation & Multilingual Search - Steps B & C)

### 8.1 Security Evaluation Summary

The architectural plan provides a solid Zero-Trust foundation for multilingual catalog discovery, adhering to Clean Architecture and strict fail-closed principles. However, localized free-text ingested into catalog metadata and subsequently ingested by LLM agents presents new threat vectors that necessitate binding controls:
1. **Tier Invariance & Leakage:** Hidden datasets and non-consented column translations must never be accessible or discoverable through multilingual queries or search projections.
2. **Untrusted Text Ingestion & Injection:** Display names and descriptions are untrusted inputs; strict character whitelisting, script tag rejection, and length bounding are required.
3. **Log & Response Echoing:** Validation errors must fail closed without echoing user payloads into logs or HTTP response bodies.
4. **ReDoS Defense:** Language negotiation and tag validation must avoid regular expressions vulnerable to catastrophic backtracking.
5. **Storage Deserialization Hygiene:** Corrupted database rows must deserialize safely without leaking raw bytes into application logs.

### 8.2 Binding Security Conditions (SEC-CAT146-*)

The implementation team must strictly enforce the following binding security conditions:

- **SEC-CAT146-01: Zero Data Leakage Across Visibility Tiers (`Hidden` & `Summary`)**  
  - If a table is classified as `Hidden` or caller has no access, neither default nor translated texts (display name, descriptions, column texts) must ever be leaked in MCP `list_datasets`/`describe_dataset`, REST `/catalog/tables`, GraphQL `catalog`, or `search_catalog`.
  - In `Summary` tier, column translations must NEVER be returned to unconsented callers; `Columns` collection must remain empty (`[]`), and `LongDescription` must remain null.

- **SEC-CAT146-02: Search Index Pre-Filter Invariance & `ShapeHit` Hygiene**  
  - Caller-level visibility filtering via `CatalogVisibleBitSet` / `isVisiblePredicate` MUST be strictly evaluated BEFORE any BM25 scoring or vector similarity ranking. Hidden table terms must never influence candidate counts, scoring distributions, or RRF ranking.
  - Search hits that match exclusively on translated texts of columns the caller cannot access (`Deny` column access level) must be dropped fail-closed before results are returned (`ShapeHit` pruning).

- **SEC-CAT146-03: Input Sanitization, Unicode Hygiene & Anti-Reflection**  
  - On write/ingestion, `LocalizedTextsValidator` must strictly reject HTML/script tags (`<script`, `javascript:`, `<iframe`, `<object`), bidi override controls (`\u202A`–`\u202E`, `\u2066`–`\u2069`), and control characters (except `\r`, `\n`, `\t` in descriptions).
  - Hard length clamps: `DisplayName` $\le$ 128 characters, `Description` $\le$ 4,000 characters, `LongDescription` $\le$ 32,000 characters. Max 16 languages per entity.
  - Validation exceptions and error logs must NEVER echo the untrusted input payload or snippet (zero reflection).

- **SEC-CAT146-04: Span-Based RFC 5646 Parsing & ReDoS Defense**  
  - RFC 5646 / BCP 47 language tag parsing in `Bcp47LanguageTag` and `LanguageNegotiator` must use linear zero-allocation span parsing (`ReadOnlySpan<char>`) without backtracking regular expressions.
  - Malformed or oversized `language` parameters (max 35 chars) or `Accept-Language` headers (max 512 chars, max 8 ranges) must safely fall back to `DefaultLanguage` without throwing unhandled exceptions or exposing server paths.

- **SEC-CAT146-05: Storage Deserialization Fail-Closed & Redacted Logging**  
  - Corrupted or invalid `texts_json` rows must fail closed (treated as `LocalizedTexts.Empty`).
  - Repository catch blocks must log ONLY the table identifier and exception type (`JsonException`); neither raw JSON bytes nor exception message snippets containing payload fragments may be emitted.

- **SEC-CAT146-06: Cache Segregation & Canonical Header Validation**  
  - All localized REST endpoints must emit `Vary: Accept-Language` and `Content-Language`.
  - The `Content-Language` response header must ONLY be populated with validated, canonicalized tags from `SupportedLanguages`, preventing HTTP response splitting or header injection.

- **SEC-CAT146-07: Diacritic Folding Determinism & Bounded Expansion**  
  - `DiacriticNormalizer` must operate with bounded string allocation (ASCII fast-path without allocation, expansion ratio capped) and maintain identical token normalization across both index ingestion and query execution.
  - Diacritic folding must not permit semantic bypassing of table or column deny rules.

### 8.3 Mandatory Security Test Cases (`[Trait("Category", "Security")]`)

The test suite in `tests/Autheris.Tests.Unit/Security/MultilingualCatalogSecurityTests.cs` must implement and pass the following security test cases:

1. **`T-CAT146-01: HiddenTable_TranslationsNeverLeaked_InMcpRestGraphQlOrSearch`**  
   Assert that neither default nor translated texts of a `Hidden` table appear in MCP `list_datasets`/`describe_dataset`, REST, GraphQL, or `search_catalog` hit results.
2. **`T-CAT146-02: SummaryTier_ColumnTranslations_NeverReturnedToUnconsentedCaller`**  
   Assert that callers with `Summary` tier never receive column translations or column metadata in `describe_dataset` or REST endpoints.
3. **`T-CAT146-03: DeniedColumn_Translations_FilteredFromDescribeAndPrunedFromSearchHits`**  
   Assert that columns denied to the caller do not have translations returned in `describe_dataset` and hits matching exclusively on denied column translations are pruned (`ShapeHit`).
4. **`T-CAT146-04: LocalizedTextsValidator_RejectsHtmlScriptBidiAndControlChars_WithoutPayloadEcho`**  
   Assert that script tags (`<script>`), bidi override characters, and control characters are rejected, and the resulting exception message does not contain the input payload.
5. **`T-CAT146-05: LanguageNegotiator_ReDoSAndHeaderInjection_SafelyFallsBackToDefault`**  
   Assert that deeply nested, repetitive, or malformed `Accept-Language` headers and `language` arguments resolve to `DefaultLanguage` in $O(N)$ linear time without throwing.
6. **`T-CAT146-06: MalformedTextsJson_DeserializesToEmpty_LogsWithoutRawBytesLeak`**  
   Assert that corrupt `texts_json` in SQLite/PostgreSQL/SQL Server loads as `LocalizedTexts.Empty` and logged warnings do not contain raw payload bytes.
7. **`T-CAT146-07: RestEndpoints_EmitVaryAndCanonicalContentLanguageHeaders`**  
   Assert that REST responses emit `Vary: Accept-Language` and `Content-Language` matching only configured canonical tags.
