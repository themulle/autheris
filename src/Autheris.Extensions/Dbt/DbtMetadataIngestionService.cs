namespace Autheris.Extensions.Dbt;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using Autheris.Application.DataCatalog.Services;
using Autheris.Application.Dbt.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

public sealed class DbtMetadataIngestionService : IDbtMetadataIngestionService
{
    private readonly IDbtProposalRepository _proposalRepository;
    private readonly ITableMetadataRepository _metadataRepository;
    private readonly ILineageGraphStore _lineageGraphStore;
    private readonly IPolicyEpochRepository? _epochRepository;
    private readonly Microsoft.Extensions.Options.IOptions<Autheris.Domain.Options.GatewayOptions>? _gatewayOptions;
    private readonly Autheris.Application.SqlEndpoints.Services.SqlEndpointLoader? _sqlEndpointLoader;
    private readonly ITableRelationRepository? _relationRepository;
    private readonly VirtualFilterAdministrationService? _virtualFilterAdmin;
    private readonly ILogger<DbtMetadataIngestionService> _logger;

    public DbtMetadataIngestionService(
        IDbtProposalRepository proposalRepository,
        ITableMetadataRepository metadataRepository,
        ILineageGraphStore lineageGraphStore,
        ILogger<DbtMetadataIngestionService> logger)
        : this(proposalRepository, metadataRepository, lineageGraphStore, null, null, null, null, null, logger)
    {
    }

    public DbtMetadataIngestionService(
        IDbtProposalRepository proposalRepository,
        ITableMetadataRepository metadataRepository,
        ILineageGraphStore lineageGraphStore,
        IPolicyEpochRepository? epochRepository,
        ILogger<DbtMetadataIngestionService> logger)
        : this(proposalRepository, metadataRepository, lineageGraphStore, epochRepository, null, null, null, null, logger)
    {
    }

    public DbtMetadataIngestionService(
        IDbtProposalRepository proposalRepository,
        ITableMetadataRepository metadataRepository,
        ILineageGraphStore lineageGraphStore,
        ITableRelationRepository? relationRepository,
        ILogger<DbtMetadataIngestionService> logger)
        : this(proposalRepository, metadataRepository, lineageGraphStore, null, null, null, relationRepository, null, logger)
    {
    }

    public DbtMetadataIngestionService(
        IDbtProposalRepository proposalRepository,
        ITableMetadataRepository metadataRepository,
        ILineageGraphStore lineageGraphStore,
        IPolicyEpochRepository? epochRepository,
        Microsoft.Extensions.Options.IOptions<Autheris.Domain.Options.GatewayOptions>? gatewayOptions,
        Autheris.Application.SqlEndpoints.Services.SqlEndpointLoader? sqlEndpointLoader,
        ILogger<DbtMetadataIngestionService> logger)
        : this(proposalRepository, metadataRepository, lineageGraphStore, epochRepository, gatewayOptions, sqlEndpointLoader, null, null, logger)
    {
    }

    public DbtMetadataIngestionService(
        IDbtProposalRepository proposalRepository,
        ITableMetadataRepository metadataRepository,
        ILineageGraphStore lineageGraphStore,
        IPolicyEpochRepository? epochRepository,
        Microsoft.Extensions.Options.IOptions<Autheris.Domain.Options.GatewayOptions>? gatewayOptions,
        Autheris.Application.SqlEndpoints.Services.SqlEndpointLoader? sqlEndpointLoader,
        ITableRelationRepository? relationRepository,
        ILogger<DbtMetadataIngestionService> logger)
        : this(proposalRepository, metadataRepository, lineageGraphStore, epochRepository, gatewayOptions, sqlEndpointLoader, relationRepository, null, logger)
    {
    }

    public DbtMetadataIngestionService(
        IDbtProposalRepository proposalRepository,
        ITableMetadataRepository metadataRepository,
        ILineageGraphStore lineageGraphStore,
        IPolicyEpochRepository? epochRepository,
        Microsoft.Extensions.Options.IOptions<Autheris.Domain.Options.GatewayOptions>? gatewayOptions,
        Autheris.Application.SqlEndpoints.Services.SqlEndpointLoader? sqlEndpointLoader,
        ITableRelationRepository? relationRepository,
        VirtualFilterAdministrationService? virtualFilterAdmin,
        ILogger<DbtMetadataIngestionService> logger)
    {
        _proposalRepository = proposalRepository ?? throw new ArgumentNullException(nameof(proposalRepository));
        _metadataRepository = metadataRepository ?? throw new ArgumentNullException(nameof(metadataRepository));
        _lineageGraphStore = lineageGraphStore ?? throw new ArgumentNullException(nameof(lineageGraphStore));
        _epochRepository = epochRepository;
        _gatewayOptions = gatewayOptions;
        _sqlEndpointLoader = sqlEndpointLoader;
        _relationRepository = relationRepository;
        _virtualFilterAdmin = virtualFilterAdmin;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<DbtSyncResult> IngestManifestFileAsync(string filePath, bool dryRun = false, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ValidateSafeFilePath(filePath);
        var fullPath = Path.GetFullPath(filePath);
        ValidateSafeFilePath(fullPath);

        if (!File.Exists(fullPath))
        {
            return new DbtSyncResult(false, 0, 0, 0, [], $"Dbt manifest file not found at: {fullPath}");
        }

        await using var stream = File.OpenRead(fullPath);
        return await IngestManifestStreamAsync(stream, dryRun, ct).ConfigureAwait(false);
    }

    private static void ValidateSafeFilePath(string fullPath)
    {
        if (fullPath.Contains('\0'))
        {
            throw new System.Security.SecurityException("File path must not contain null bytes.");
        }

        var ext = Path.GetExtension(fullPath);
        if (!string.Equals(ext, ".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new System.Security.SecurityException($"Invalid file extension '{ext}'. Only JSON dbt artifacts (.json) are permitted.");
        }

        var normalized = fullPath.Replace('\\', '/').ToLowerInvariant();
        if (normalized.StartsWith("/etc") ||
            normalized.StartsWith("/proc") ||
            normalized.StartsWith("/sys") ||
            normalized.StartsWith("/dev") ||
            normalized.StartsWith("/var") ||
            normalized.StartsWith("/run") ||
            normalized.StartsWith("/root") ||
            normalized.StartsWith("/bin") ||
            normalized.StartsWith("/sbin") ||
            normalized.StartsWith("/usr") ||
            normalized.Contains("/.ssh") ||
            normalized.Contains("/appsettings") ||
            normalized.Contains("windows/system32"))
        {
            throw new System.Security.SecurityException($"Access to restricted path '{fullPath}' is strictly forbidden.");
        }
    }

    public async Task<DbtSyncResult> IngestManifestStreamAsync(Stream manifestStream, bool dryRun = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifestStream);

        var warnings = new List<string>();
        IReadOnlyList<DbtModelDefinition> models;
        IReadOnlyList<DbtRelationshipDefinition> relationships;

        try
        {
            (models, relationships) = await DbtArtifactStreamingParser.ParseManifestWithRelationshipsAsync(manifestStream, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse dbt manifest stream.");
            return new DbtSyncResult(false, 0, 0, 0, [], $"Manifest parse failure: {ex.Message}");
        }

        _logger.LogInformation("Parsed {Count} dbt models and {RelCount} relationships from manifest.", models.Count, relationships.Count);

        var generatedProposals = 0;
        var lineageNodesToUpdate = new List<LineageNode>();

        // Build a mapping from unique_id to TableIdentifier string
        var uniqueIdToTableId = models.ToDictionary(m => m.UniqueId, m => m.ToTableIdentifier().ToString(), StringComparer.OrdinalIgnoreCase);

        // Build child map (downstream nodes)
        var childMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in models)
        {
            var myTableId = m.ToTableIdentifier().ToString();
            foreach (var depUniqueId in m.DependsOnNodes)
            {
                if (uniqueIdToTableId.TryGetValue(depUniqueId, out var parentTableId))
                {
                    if (!childMap.TryGetValue(parentTableId, out var children))
                    {
                        children = [];
                        childMap[parentTableId] = children;
                    }
                    if (!children.Contains(myTableId))
                    {
                        children.Add(myTableId);
                    }
                }
            }
        }

        foreach (var model in models)
        {
            ct.ThrowIfCancellationRequested();

            var tableId = model.ToTableIdentifier();
            var ownerTeam = model.Meta.GetValueOrDefault("owner");

            // Deduplication: get existing pending proposals for this table
            var existingPending = dryRun
                ? []
                : await _proposalRepository.GetPendingProposalsAsync(tableId, ct).ConfigureAwait(false);

            // 1. Zero-Trust Proposal Evaluation (SEC-DBT-01)
            foreach (var (colName, colDef) in model.Columns)
            {
                var isPii = false;
                var suggestedRule = "REDACT";

                if (colDef.Meta.TryGetValue("pii", out var piiVal) &&
                    (string.Equals(piiVal, "true", StringComparison.OrdinalIgnoreCase) || string.Equals(piiVal, "1", StringComparison.OrdinalIgnoreCase)))
                {
                    isPii = true;
                }

                if (colDef.Tags.Any(t => t.Contains("email", StringComparison.OrdinalIgnoreCase)) ||
                    colName.Contains("email", StringComparison.OrdinalIgnoreCase))
                {
                    isPii = true;
                    suggestedRule = "MASK_EMAIL";
                }
                else if (colDef.Tags.Any(t => t.Contains("ssn", StringComparison.OrdinalIgnoreCase)) ||
                         colName.Contains("ssn", StringComparison.OrdinalIgnoreCase))
                {
                    isPii = true;
                    suggestedRule = "REDACT";
                }
                else if (colDef.Tags.Any(t => t.Contains("pseudonym", StringComparison.OrdinalIgnoreCase)))
                {
                    isPii = true;
                    suggestedRule = "HMAC_SHA256";
                }

                if (isPii)
                {
                    // Check if already pending review to avoid duplicate proposals
                    if (existingPending.Any(p => string.Equals(p.ColumnName, colName, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    var sourceTag = colDef.Tags.FirstOrDefault() ??
                        (colDef.Meta.ContainsKey("pii") ? "meta.pii=true" : $"column:{colName}");

                    var proposal = new DbtMetadataProposal(
                        Id: Guid.NewGuid(),
                        Table: tableId,
                        ColumnName: colName,
                        SuggestedRuleType: suggestedRule,
                        SuggestedSensitivity: "HIGH",
                        SuggestedOwnerTeam: ownerTeam,
                        SourceDbtTag: sourceTag,
                        Status: DbtProposalStatus.PendingReview,
                        CreatedAt: DateTimeOffset.UtcNow
                    );

                    if (!dryRun)
                    {
                        await _proposalRepository.AddProposalAsync(proposal, ct).ConfigureAwait(false);
                    }

                    generatedProposals++;
                }

                // Column-level RLS filter auto-sync (F-DBT-6)
                if (colDef.Meta.TryGetValue("rls_filter", out var colRls) && !string.IsNullOrWhiteSpace(colRls))
                {
                    if (!existingPending.Any(p => string.Equals(p.ColumnName, colName, StringComparison.OrdinalIgnoreCase) && p.SuggestedRuleType.StartsWith("RLS_FILTER:")))
                    {
                        var proposal = new DbtMetadataProposal(
                            Id: Guid.NewGuid(),
                            Table: tableId,
                            ColumnName: colName,
                            SuggestedRuleType: $"RLS_FILTER:{colRls}",
                            SuggestedSensitivity: "HIGH",
                            SuggestedOwnerTeam: ownerTeam,
                            SourceDbtTag: "meta.rls_filter",
                            Status: DbtProposalStatus.PendingReview,
                            CreatedAt: DateTimeOffset.UtcNow
                        );

                        if (!dryRun)
                        {
                            await _proposalRepository.AddProposalAsync(proposal, ct).ConfigureAwait(false);
                        }

                        generatedProposals++;
                    }
                }
            }

            // Model-level Policy & RLS auto-sync (F-DBT-6)
            if (model.Meta.TryGetValue("casbin_roles", out var casbinRoles) && !string.IsNullOrWhiteSpace(casbinRoles))
            {
                if (!existingPending.Any(p => string.Equals(p.ColumnName, "*", StringComparison.OrdinalIgnoreCase) && p.SuggestedRuleType.StartsWith("CASBIN_ROLES:")))
                {
                    var proposal = new DbtMetadataProposal(
                        Id: Guid.NewGuid(),
                        Table: tableId,
                        ColumnName: "*",
                        SuggestedRuleType: $"CASBIN_ROLES:{casbinRoles}",
                        SuggestedSensitivity: "HIGH",
                        SuggestedOwnerTeam: ownerTeam,
                        SourceDbtTag: "meta.casbin_roles",
                        Status: DbtProposalStatus.PendingReview,
                        CreatedAt: DateTimeOffset.UtcNow
                    );

                    if (!dryRun)
                    {
                        await _proposalRepository.AddProposalAsync(proposal, ct).ConfigureAwait(false);
                    }

                    generatedProposals++;
                }
            }

            if (model.Meta.TryGetValue("rls_filter", out var modelRls) && !string.IsNullOrWhiteSpace(modelRls))
            {
                if (!existingPending.Any(p => string.Equals(p.ColumnName, "*", StringComparison.OrdinalIgnoreCase) && p.SuggestedRuleType.StartsWith("RLS_FILTER:")))
                {
                    var proposal = new DbtMetadataProposal(
                        Id: Guid.NewGuid(),
                        Table: tableId,
                        ColumnName: "*",
                        SuggestedRuleType: $"RLS_FILTER:{modelRls}",
                        SuggestedSensitivity: "HIGH",
                        SuggestedOwnerTeam: ownerTeam,
                        SourceDbtTag: "meta.rls_filter",
                        Status: DbtProposalStatus.PendingReview,
                        CreatedAt: DateTimeOffset.UtcNow
                    );

                    if (!dryRun)
                    {
                        await _proposalRepository.AddProposalAsync(proposal, ct).ConfigureAwait(false);
                    }

                    generatedProposals++;
                }
            }

            // 2. Lineage Node Construction
            var myTableIdStr = tableId.ToString();
            var downstream = childMap.TryGetValue(myTableIdStr, out var cList) ? cList : (IReadOnlyCollection<string>)[];

            lineageNodesToUpdate.Add(new LineageNode(
                Id: myTableIdStr,
                Name: model.Name,
                Type: LineageNodeType.Table,
                DownstreamNodeIds: downstream,
                OwnerTeam: ownerTeam,
                OwnerEmail: model.Meta.GetValueOrDefault("owner_email")
            ));

            // 3. Omnichannel Documentation Sync (F-DOC-01): Synchronize dbt model and column descriptions
            if (!dryRun)
            {
                var existingMeta = await _metadataRepository.GetTableMetadataAsync(tableId, ct).ConfigureAwait(false);
                if (existingMeta != null)
                {
                    var updatedTableObj = new Table
                    {
                        Id = existingMeta.Table.Id,
                        SourceType = existingMeta.Table.SourceType,
                        SourceName = existingMeta.Table.SourceName,
                        SchemaName = existingMeta.Table.SchemaName,
                        TableName = existingMeta.Table.TableName,
                        DisplayName = existingMeta.Table.DisplayName,
                        Description = model.Description ?? existingMeta.Table.Description,
                        LongDescription = model.Meta.GetValueOrDefault("long_description") ?? existingMeta.Table.LongDescription,
                        Sensitivity = existingMeta.Table.Sensitivity,
                        RequiresFourEyes = existingMeta.Table.RequiresFourEyes,
                        IsActive = existingMeta.Table.IsActive,
                        DataSourceType = existingMeta.Table.DataSourceType,
                        HttpEndpoint = existingMeta.Table.HttpEndpoint,
                        PluginName = existingMeta.Table.PluginName
                    };

                    var updatedColumns = new List<TableColumn>();
                    foreach (var c in existingMeta.Columns)
                    {
                        var matchingCol = model.Columns.TryGetValue(c.ColumnName, out var dCol) ? dCol : null;
                        var colDesc = matchingCol?.Description ?? c.Description;
                        var colLongDesc = matchingCol?.Meta.GetValueOrDefault("long_description") ?? c.LongDescription;
                        var colMeta = matchingCol?.Meta ?? c.Meta;

                        updatedColumns.Add(new TableColumn
                        {
                            Id = c.Id,
                            TableId = c.TableId,
                            ColumnName = c.ColumnName,
                            DataType = c.DataType,
                            IsSensitive = c.IsSensitive,
                            Description = colDesc,
                            LongDescription = colLongDesc,
                            Meta = colMeta
                        });
                    }

                    var updatedMetadata = new TableMetadata
                    {
                        Table = updatedTableObj,
                        Identifier = existingMeta.Identifier,
                        Columns = updatedColumns,
                        ColumnMaskingRules = existingMeta.ColumnMaskingRules,
                        PrimaryKeyColumns = existingMeta.PrimaryKeyColumns
                    };

                    await _metadataRepository.UpsertTableMetadataAsync(updatedMetadata, ct).ConfigureAwait(false);
                }
            }

            // 4. SQL-to-API Sync (Option B): Automatically generate .sql query file in queries directory for declarative API exposure
            if (!dryRun && _sqlEndpointLoader != null && _gatewayOptions?.Value?.SqlEndpoints?.AutoSyncFromDbt == true)
            {
                try
                {
                    string queriesDir = _gatewayOptions.Value.SqlEndpoints.Directory;

                    // SEC H-20: identifiers are validated and quoted, header values sanitized, paths contained and
                    // non-dbt endpoint files are never overwritten (enforced in SqlEndpointLoader).
                    _sqlEndpointLoader.SyncDbtModelDefinitionToFile(
                        directoryPath: queriesDir,
                        name: model.Name,
                        schema: model.Schema,
                        columns: model.Columns.Keys,
                        summary: model.Description ?? $"dbt model {model.Name}",
                        dataSource: model.Database);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to auto-sync dbt model '{ModelName}' to SQL endpoints directory.", model.Name);
                    warnings.Add($"SQL endpoint auto-sync rejected for dbt model: {ex.Message}");
                }
            }
        }

        // R-31: Persist relationships into the governance repository
        var importedRelations = 0;
        if (_relationRepository != null && relationships.Count > 0)
        {
            var modelNameToTableId = new Dictionary<string, TableIdentifier>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in models)
            {
                modelNameToTableId[m.Name] = m.ToTableIdentifier();
                modelNameToTableId[m.UniqueId] = m.ToTableIdentifier();
            }

            foreach (var rel in relationships)
            {
                if (modelNameToTableId.TryGetValue(rel.ParentModelOrTable, out var parentId) &&
                    modelNameToTableId.TryGetValue(rel.ChildModelOrTable, out var childId))
                {
                    var tableRel = new TableRelation
                    {
                        Id = Guid.NewGuid(),
                        ParentTableIdentifier = parentId,
                        ChildTableIdentifier = childId,
                        RelationName = rel.Name,
                        JoinKeysParent = [rel.ParentColumn],
                        JoinKeysChild = [rel.ChildColumn],
                        Cardinality = RelationCardinality.OneToMany
                    };

                    if (!dryRun)
                    {
                        try
                        {
                            await _relationRepository.CreateRelationAsync(tableRel, ct).ConfigureAwait(false);
                            importedRelations++;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to import table relation '{Relation}' between {Parent} and {Child}.",
                                rel.Name, parentId, childId);
                        }
                    }
                    else
                    {
                        importedRelations++;
                    }
                }
            }
        }

        // Apply lineage updates
        if (!dryRun && lineageNodesToUpdate.Count > 0)
        {
            _lineageGraphStore.UpdateGraph(lineageNodesToUpdate);
        }

        _logger.LogInformation("Completed dbt ingestion: {Models} models, {Proposals} proposals, {Lineage} lineage nodes, {Relations} relations.",
            models.Count, generatedProposals, lineageNodesToUpdate.Count, importedRelations);

        return new DbtSyncResult(
            Success: true,
            ParsedModelsCount: models.Count,
            GeneratedProposalsCount: generatedProposals,
            UpdatedLineageNodesCount: lineageNodesToUpdate.Count,
            Warnings: warnings,
            ErrorMessage: null,
            ImportedRelationsCount: importedRelations
        );
    }

    private static readonly HashSet<string> ValidMaskingRuleTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "REDACT", "NULLIFY", "HMAC", "HMAC_SHA256", "HASH", "MASK_EMAIL", "MASK_IBAN", "MASK_PHONE", "REGEX"
    };

    public async Task<DbtGovernanceSyncResult> IngestGovernanceFileAsync(string filePath, bool dryRun = false, bool replace = false, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ValidateSafeFilePath(filePath);
        var fullPath = Path.GetFullPath(filePath);
        ValidateSafeFilePath(fullPath);

        if (!File.Exists(fullPath))
        {
            return new DbtGovernanceSyncResult(false, 0, 0, 0, 0, 0, [], $"Dbt governance file not found at: {fullPath}");
        }

        await using var stream = File.OpenRead(fullPath);
        return await IngestGovernanceStreamAsync(stream, dryRun, replace, ct).ConfigureAwait(false);
    }

    public async Task<DbtGovernanceSyncResult> IngestGovernanceStreamAsync(Stream governanceStream, bool dryRun = false, bool replace = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(governanceStream);

        var warnings = new List<string>();
        int updatedTables = 0;
        int updatedColumns = 0;
        int maskingRulesCount = 0;
        int removedMaskingRulesCount = 0;
        int relaxedMaskingRulesCount = 0;
        int virtualFiltersCount = 0;
        int accessProfilesCount = 0;

        using var doc = await JsonDocument.ParseAsync(
            governanceStream,
            new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip },
            ct).ConfigureAwait(false);
        var root = doc.RootElement;

        var effectiveReplace = replace || (root.ValueKind == JsonValueKind.Object &&
            ((root.TryGetProperty("mode", out var modeProp) && string.Equals(modeProp.GetString(), "replace", StringComparison.OrdinalIgnoreCase)) ||
             (root.TryGetProperty("replace", out var rProp) && rProp.ValueKind == JsonValueKind.True)));

        // Process classifications / models
        JsonElement modelsArray = default;
        if (root.ValueKind == JsonValueKind.Array)
        {
            modelsArray = root;
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("classifications", out var cProp) && cProp.ValueKind == JsonValueKind.Array)
            {
                modelsArray = cProp;
            }
            else if (root.TryGetProperty("models", out var mProp) && mProp.ValueKind == JsonValueKind.Array)
            {
                modelsArray = mProp;
            }
        }

        if (modelsArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in modelsArray.EnumerateArray())
            {
                ct.ThrowIfCancellationRequested();
                var tableNameStr = item.TryGetProperty("table", out var tProp) ? tProp.GetString() ?? "" :
                                  (item.TryGetProperty("name", out var nProp) ? nProp.GetString() ?? "" : "");
                if (string.IsNullOrWhiteSpace(tableNameStr)) continue;

                var itemSchema = item.TryGetProperty("schema", out var sProp2) ? sProp2.GetString() : null;
                var itemDatabase = item.TryGetProperty("database", out var dbProp2) ? dbProp2.GetString() : null;

                var tableId = ParseTableIdentifier(tableNameStr, itemSchema, itemDatabase);
                var existingMeta = await _metadataRepository.GetTableMetadataAsync(tableId, ct).ConfigureAwait(false);
                if (existingMeta == null)
                {
                    var allTables = await _metadataRepository.GetAllTablesAsync(ct).ConfigureAwait(false);
                    existingMeta = allTables.FirstOrDefault(m =>
                        string.Equals(m.Identifier.Schema, tableId.Schema, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(m.Identifier.TableName, tableId.TableName, StringComparison.OrdinalIgnoreCase));
                    if (existingMeta != null)
                    {
                        tableId = existingMeta.Identifier;
                    }
                }
                if (existingMeta == null)
                {
                    // R-51: Distinguish routine / procedure from missing catalog tables
                    if (IsRoutine(tableId.TableName, item))
                    {
                        warnings.Add($"Routine '{tableId}' nicht anwendbar (Routine); skipped governance update.");
                    }
                    else
                    {
                        warnings.Add($"Table '{tableId}' not found in catalog; skipped governance update.");
                    }
                    continue;
                }

                var sensitivity = item.TryGetProperty("sensitivity", out var sProp) ? sProp.GetString() ?? existingMeta.Table.Sensitivity : existingMeta.Table.Sensitivity;
                var origin = item.TryGetProperty("origin", out var oProp) ? oProp.GetString() : "dbt_sample";
                var description = item.TryGetProperty("description", out var dProp) ? dProp.GetString() : existingMeta.Table.Description;
                var historyJson = item.TryGetProperty("history", out var hProp) ? hProp.GetRawText() : null;
                var reviewJson = item.TryGetProperty("classification_review", out var crProp) ? crProp.GetRawText() : null;

                var updatedTable = new Table
                {
                    Id = existingMeta.Table.Id,
                    SourceType = existingMeta.Table.SourceType,
                    SourceName = existingMeta.Table.SourceName,
                    SchemaName = existingMeta.Table.SchemaName,
                    TableName = existingMeta.Table.TableName,
                    DisplayName = existingMeta.Table.DisplayName,
                    Description = description,
                    LongDescription = reviewJson ?? existingMeta.Table.LongDescription,
                    DocumentationSource = origin ?? existingMeta.Table.DocumentationSource,
                    Sensitivity = sensitivity,
                    RequiresFourEyes = existingMeta.Table.RequiresFourEyes,
                    IsActive = existingMeta.Table.IsActive,
                    DataSourceType = existingMeta.Table.DataSourceType,
                    HttpEndpoint = existingMeta.Table.HttpEndpoint,
                    PluginName = existingMeta.Table.PluginName
                };

                var updatedCols = new List<TableColumn>();
                var updatedMaskingRules = new Dictionary<string, MaskingRule>(existingMeta.ColumnMaskingRules, StringComparer.OrdinalIgnoreCase);

                JsonElement colsElement = default;
                if (item.TryGetProperty("columns", out var ce)) colsElement = ce;

                foreach (var col in existingMeta.Columns)
                {
                    var colSensitivity = col.IsSensitive;
                    var colDesc = col.Description;
                    var colMeta = new Dictionary<string, string>(col.Meta ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);

                    if (colsElement.ValueKind == JsonValueKind.Object && colsElement.TryGetProperty(col.ColumnName, out var colObj))
                    {
                        if (colObj.TryGetProperty("sensitivity", out var csProp))
                        {
                            var cs = csProp.GetString();
                            // B-03: Use Table.IsSensitivityHigh (SensitivityRank >= 3) instead of != "PUBLIC"
                            colSensitivity = Table.IsSensitivityHigh(cs);
                            if (!string.IsNullOrWhiteSpace(cs)) colMeta["sensitivity"] = cs;
                        }
                        if (colObj.TryGetProperty("description", out var cdProp) && cdProp.GetString() is string cd && !string.IsNullOrWhiteSpace(cd))
                        {
                            colDesc = cd;
                        }
                        if (colObj.TryGetProperty("origin", out var coProp) && coProp.GetString() is string co)
                        {
                            colMeta["origin"] = co;
                        }
                        if (colObj.TryGetProperty("history", out var chProp))
                        {
                            colMeta["history"] = chProp.GetRawText();
                        }
                        if (colObj.TryGetProperty("classification_review", out var ccrProp))
                        {
                            colMeta["classification_review"] = ccrProp.GetRawText();
                        }

                        // B-01 / B-02: Handling masking_rule (none, null, false, replace mode, validation)
                        if (colObj.TryGetProperty("masking_rule", out var mrProp))
                        {
                            if (mrProp.ValueKind is JsonValueKind.Null or JsonValueKind.False)
                            {
                                // Explicit removal
                                if (updatedMaskingRules.Remove(col.ColumnName))
                                {
                                    removedMaskingRulesCount++;
                                }
                            }
                            else if (mrProp.ValueKind == JsonValueKind.String)
                            {
                                var rawRule = mrProp.GetString()?.Trim() ?? string.Empty;
                                if (string.Equals(rawRule, "none", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(rawRule, "null", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(rawRule, "false", StringComparison.OrdinalIgnoreCase) ||
                                    string.IsNullOrWhiteSpace(rawRule))
                                {
                                    // B-01: "none", "null", "false", or empty treated as no rule / remove
                                    if (updatedMaskingRules.Remove(col.ColumnName))
                                    {
                                        removedMaskingRulesCount++;
                                    }
                                }
                                else
                                {
                                    var upperRule = rawRule.ToUpperInvariant();
                                    if (!ValidMaskingRuleTypes.Contains(upperRule))
                                    {
                                        // B-01: reject unknown rule type with 400
                                        warnings.Add($"Column '{col.ColumnName}' has unknown masking rule '{rawRule}'.");
                                        return new DbtGovernanceSyncResult(
                                            Success: false,
                                            UpdatedTablesCount: updatedTables,
                                            UpdatedColumnsCount: updatedColumns,
                                            MaskingRulesCount: maskingRulesCount,
                                            VirtualFiltersCount: virtualFiltersCount,
                                            AccessProfilesCount: accessProfilesCount,
                                            Warnings: warnings,
                                            ErrorMessage: $"Unknown masking rule '{rawRule}' on column '{col.ColumnName}'.",
                                            RemovedMaskingRulesCount: removedMaskingRulesCount,
                                            RelaxedMaskingRulesCount: relaxedMaskingRulesCount
                                        );
                                    }

                                    var newRule = new MaskingRule
                                    {
                                        RuleType = upperRule,
                                        Replacement = upperRule == "REDACT" ? "[REDACTED]" : null
                                    };

                                    if (existingMeta.ColumnMaskingRules.TryGetValue(col.ColumnName, out var existingRule))
                                    {
                                        int oldStrength = CatalogGovernanceRatchet.MaskingRuleStrength(existingRule);
                                        int newStrength = CatalogGovernanceRatchet.MaskingRuleStrength(newRule);
                                        if (newStrength < oldStrength)
                                        {
                                            relaxedMaskingRulesCount++;
                                        }
                                        else
                                        {
                                            maskingRulesCount++;
                                        }
                                    }
                                    else
                                    {
                                        maskingRulesCount++;
                                    }

                                    updatedMaskingRules[col.ColumnName] = newRule;
                                }
                            }
                        }
                        else if (effectiveReplace)
                        {
                            // B-02: replace mode - column without masking_rule loses its rule
                            if (updatedMaskingRules.Remove(col.ColumnName))
                            {
                                removedMaskingRulesCount++;
                            }
                        }

                        updatedColumns++;
                    }

                    updatedCols.Add(new TableColumn
                    {
                        Id = col.Id,
                        TableId = col.TableId,
                        ColumnName = col.ColumnName,
                        DataType = col.DataType,
                        IsSensitive = colSensitivity,
                        Description = colDesc,
                        LongDescription = col.LongDescription,
                        Meta = colMeta
                    });
                }

                var updatedMetadata = new TableMetadata
                {
                    Table = updatedTable,
                    Identifier = existingMeta.Identifier,
                    Columns = updatedCols,
                    ColumnMaskingRules = updatedMaskingRules,
                    PrimaryKeyColumns = existingMeta.PrimaryKeyColumns
                };

                if (!dryRun)
                {
                    await _metadataRepository.UpsertTableMetadataAsync(updatedMetadata, ct).ConfigureAwait(false);
                }
                updatedTables++;
            }
        }

        // R-50: Process virtual filters
        JsonElement filtersProp = default;
        if (root.ValueKind == JsonValueKind.Object &&
            (root.TryGetProperty("virtual_filters", out filtersProp) ||
             root.TryGetProperty("virtualFilters", out filtersProp) ||
             root.TryGetProperty("filters", out filtersProp)))
        {
            var filters = ParseVirtualFilters(filtersProp);
            foreach (var filter in filters)
            {
                virtualFiltersCount++;
                if (!dryRun && _virtualFilterAdmin != null)
                {
                    try
                    {
                        var actor = new VirtualFilterActor(new Sid("S-1-5-21-DBT-SYNC"), IsSync: true);
                        await _virtualFilterAdmin.SaveFilterAsync(filter, actor, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        warnings.Add($"Failed to import virtual filter '{filter.Name}': {ex.Message}");
                    }
                }
            }
        }

        // R-50: Process access profiles
        JsonElement profilesProp = default;
        if (root.ValueKind == JsonValueKind.Object &&
            (root.TryGetProperty("access_profiles", out profilesProp) ||
             root.TryGetProperty("accessProfiles", out profilesProp) ||
             root.TryGetProperty("profiles", out profilesProp)))
        {
            var profiles = ParseAccessProfiles(profilesProp);
            foreach (var profile in profiles)
            {
                accessProfilesCount++;
                if (!dryRun && _virtualFilterAdmin != null)
                {
                    try
                    {
                        var actor = new VirtualFilterActor(new Sid("S-1-5-21-DBT-SYNC"), IsSync: true);
                        await _virtualFilterAdmin.SaveProfileAsync(profile, actor, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        warnings.Add($"Failed to import access profile '{profile.Name}': {ex.Message}");
                    }
                }
            }
        }

        return new DbtGovernanceSyncResult(
            Success: true,
            UpdatedTablesCount: updatedTables,
            UpdatedColumnsCount: updatedColumns,
            MaskingRulesCount: maskingRulesCount,
            VirtualFiltersCount: virtualFiltersCount,
            AccessProfilesCount: accessProfilesCount,
            Warnings: warnings,
            RemovedMaskingRulesCount: removedMaskingRulesCount,
            RelaxedMaskingRulesCount: relaxedMaskingRulesCount
        );
    }

    private static bool IsRoutine(string tableName, JsonElement item)
    {
        if (tableName.StartsWith("sp_", StringComparison.OrdinalIgnoreCase) ||
            tableName.StartsWith("fn_", StringComparison.OrdinalIgnoreCase) ||
            tableName.StartsWith("usp_", StringComparison.OrdinalIgnoreCase) ||
            tableName.StartsWith("ufn_", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (item.TryGetProperty("type", out var typeProp) &&
            typeProp.GetString() is string typeStr &&
            (string.Equals(typeStr, "routine", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(typeStr, "procedure", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(typeStr, "function", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (item.TryGetProperty("materialization", out var matProp) &&
            matProp.GetString() is string matStr &&
            (string.Equals(matStr, "routine", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(matStr, "procedure", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(matStr, "function", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    private static List<VirtualFilter> ParseVirtualFilters(JsonElement element)
    {
        var result = new List<VirtualFilter>();
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                var filter = ParseSingleFilter(prop.Name, prop.Value);
                if (filter != null) result.Add(filter);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(name)) continue;
                var filter = ParseSingleFilter(name, item);
                if (filter != null) result.Add(filter);
            }
        }
        return result;
    }

    private static VirtualFilter? ParseSingleFilter(string name, JsonElement val)
    {
        var tenantStr = val.TryGetProperty("tenant", out var tProp) ? tProp.GetString() : null;
        var tenant = !string.IsNullOrWhiteSpace(tenantStr) ? new TenantId(tenantStr) : TenantId.LegacySingleTenant;
        var source = val.TryGetProperty("source", out var sProp) ? sProp.GetString() ?? "" : "";
        var sql = val.TryGetProperty("sql", out var sqlProp) ? sqlProp.GetString() : null;
        var validFrom = val.TryGetProperty("valid_from", out var vfProp) ? vfProp.GetString() : null;
        var validTo = val.TryGetProperty("valid_to", out var vtProp) ? vtProp.GetString() : null;

        var keyCols = new List<string>();
        if (val.TryGetProperty("key_columns", out var kcProp) && kcProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var k in kcProp.EnumerateArray()) if (k.GetString() is string s) keyCols.Add(s);
        }

        var supersedes = new List<string>();
        if (val.TryGetProperty("supersedes", out var supProp) && supProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in supProp.EnumerateArray()) if (s.GetString() is string str) supersedes.Add(str);
        }

        StructuredFilterDefinition? structured = null;
        if (val.TryGetProperty("from", out var fromProp) && fromProp.ValueKind == JsonValueKind.Object)
        {
            var fromTableStr = fromProp.TryGetProperty("table", out var ftProp) ? ftProp.GetString() ?? "" : "";
            var fromAlias = fromProp.TryGetProperty("alias", out var faProp) ? faProp.GetString() ?? "" : "";
            var parts = fromTableStr.Split('.');
            var fromTable = parts.Length == 2 ? new TableIdentifier(source, parts[0], parts[1]) : new TableIdentifier(source, "dbo", fromTableStr);

            var joins = new List<FilterJoin>();
            if (val.TryGetProperty("joins", out var joinsProp) && joinsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var j in joinsProp.EnumerateArray())
                {
                    var jTableStr = j.TryGetProperty("table", out var jt) ? jt.GetString() ?? "" : "";
                    var jAlias = j.TryGetProperty("alias", out var ja) ? ja.GetString() ?? "" : "";
                    var jLeft = j.TryGetProperty("left", out var jl) ? jl.GetString() ?? "" : "";
                    var jRight = j.TryGetProperty("right", out var jr) ? jr.GetString() ?? "" : "";
                    var jParts = jTableStr.Split('.');
                    var jTable = jParts.Length == 2 ? new TableIdentifier(source, jParts[0], jParts[1]) : new TableIdentifier(source, "dbo", jTableStr);
                    joins.Add(new FilterJoin(jTable, jAlias, jLeft, jRight));
                }
            }

            var whereConditions = new List<FilterCondition>();
            if (val.TryGetProperty("where", out var whereProp) && whereProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var w in whereProp.EnumerateArray())
                {
                    var col = w.TryGetProperty("column", out var wc) ? wc.GetString() ?? "" : "";
                    var opStr = w.TryGetProperty("op", out var wo) ? wo.GetString() ?? "eq" : "eq";
                    var wVal = w.TryGetProperty("value", out var wv) ? wv.GetString() : null;
                    var op = opStr.ToLowerInvariant() switch
                    {
                        "eq" => FilterConditionOperator.Eq,
                        "neq" => FilterConditionOperator.NotEq,
                        "is_null" => FilterConditionOperator.IsNull,
                        _ => FilterConditionOperator.IsNotNull
                    };
                    whereConditions.Add(new FilterCondition(col, op, wVal));
                }
            }

            structured = new StructuredFilterDefinition
            {
                From = fromTable,
                FromAlias = fromAlias,
                Joins = joins,
                Where = whereConditions
            };
        }

        return new VirtualFilter
        {
            TenantId = tenant,
            Name = name,
            Source = source,
            Sql = sql,
            Structured = structured,
            KeyColumns = keyCols,
            ValidFromColumn = validFrom,
            ValidToColumn = validTo,
            Supersedes = supersedes
        };
    }

    private static List<AccessProfile> ParseAccessProfiles(JsonElement element)
    {
        var result = new List<AccessProfile>();
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                var profile = ParseSingleProfile(prop.Name, prop.Value);
                if (profile != null) result.Add(profile);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(name)) continue;
                var profile = ParseSingleProfile(name, item);
                if (profile != null) result.Add(profile);
            }
        }
        return result;
    }

    private static AccessProfile? ParseSingleProfile(string name, JsonElement val)
    {
        var tenantStr = val.TryGetProperty("tenant", out var tProp) ? tProp.GetString() : null;
        var tenant = !string.IsNullOrWhiteSpace(tenantStr) ? new TenantId(tenantStr) : TenantId.LegacySingleTenant;
        var scope = val.TryGetProperty("scope", out var scProp) ? scProp.GetString() ?? "*" : "*";

        UncoveredPolicy? uncovered = null;
        if (val.TryGetProperty("uncovered", out var uProp) && uProp.GetString() is string uStr)
        {
            uncovered = string.Equals(uStr, "skip", StringComparison.OrdinalIgnoreCase) ? UncoveredPolicy.Skip : UncoveredPolicy.Deny;
        }

        GranteeType granteeType = GranteeType.User;
        Sid? granteeSid = null;
        string? roleName = null;

        if (val.TryGetProperty("grantee", out var gProp) && gProp.ValueKind == JsonValueKind.Object)
        {
            var gTypeStr = gProp.TryGetProperty("type", out var gt) ? gt.GetString() : "user";
            granteeType = (gTypeStr?.ToLowerInvariant()) switch
            {
                "group" => GranteeType.Group,
                "role" => GranteeType.Role,
                "serviceprincipal" => GranteeType.ServicePrincipal,
                _ => GranteeType.User
            };
            if (gProp.TryGetProperty("sid", out var gs) && gs.GetString() is string s)
            {
                granteeSid = new Sid(s);
            }
            if (gProp.TryGetProperty("role", out var gr) && gr.GetString() is string r)
            {
                roleName = r;
            }
        }

        var bindings = new List<FilterBinding>();
        if (val.TryGetProperty("bindings", out var bProp) && bProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var b in bProp.EnumerateArray())
            {
                var fName = b.TryGetProperty("filter", out var fn) ? fn.GetString() ?? "" : "";
                var target = b.TryGetProperty("target", out var tgt) ? tgt.GetString() : null;
                var timeCol = b.TryGetProperty("time_column", out var tc) ? tc.GetString() : null;
                Dictionary<string, string>? map = null;
                if (b.TryGetProperty("map", out var m) && m.ValueKind == JsonValueKind.Object)
                {
                    map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kv in m.EnumerateObject())
                    {
                        if (kv.Value.GetString() is string mv) map[kv.Name] = mv;
                    }
                }
                bindings.Add(new FilterBinding
                {
                    FilterName = fName,
                    TargetPattern = target,
                    TimeColumn = timeCol,
                    ColumnMap = map
                });
            }
        }

        return new AccessProfile
        {
            TenantId = tenant,
            Name = name,
            GranteeType = granteeType,
            GranteeSid = granteeSid,
            RoleName = roleName,
            Scope = scope,
            Uncovered = uncovered,
            Bindings = bindings
        };
    }

    private static TableIdentifier ParseTableIdentifier(string raw, string? explicitSchema = null, string? explicitDatabase = null)
    {
        var parts = raw.Split('.');
        if (parts.Length == 1)
        {
            var db = !string.IsNullOrWhiteSpace(explicitDatabase) ? explicitDatabase : "default";
            var sc = !string.IsNullOrWhiteSpace(explicitSchema) ? explicitSchema : "public";
            return new TableIdentifier(db, sc, parts[0]);
        }
        if (parts.Length == 2)
        {
            var db = !string.IsNullOrWhiteSpace(explicitDatabase) ? explicitDatabase : "default";
            return new TableIdentifier(db, parts[0], parts[1]);
        }
        return new TableIdentifier(parts[0], parts[1], parts[2]);
    }

    public async Task<DbtMetadataProposal> ApproveProposalAsync(Guid proposalId, string reviewedBy, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewedBy);

        var proposal = await _proposalRepository.GetProposalByIdAsync(proposalId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Dbt metadata proposal '{proposalId}' not found.");

        // SEC EX-18: enforce proposal status check – only PendingReview proposals can be approved.
        if (proposal.Status != DbtProposalStatus.PendingReview)
        {
            throw new InvalidOperationException(
                $"Dbt proposal '{proposalId}' cannot be approved because its status is '{proposal.Status}' (expected '{DbtProposalStatus.PendingReview}').");
        }

        // R-EXT-1: every check runs before the status changes, so "Approved" always means "applied".
        // RLS filter and Casbin role proposals have no column masking equivalent; they must be implemented through
        // consent row filters or the Casbin policy file and are rejected here instead of being marked Approved.
        bool isPolicyOrRls = proposal.SuggestedRuleType.StartsWith("RLS_FILTER:", StringComparison.OrdinalIgnoreCase)
            || proposal.SuggestedRuleType.StartsWith("CASBIN_ROLES:", StringComparison.OrdinalIgnoreCase);
        if (isPolicyOrRls)
        {
            throw new InvalidOperationException(
                $"Dbt proposal '{proposalId}' ({proposal.SuggestedRuleType.Split(':')[0]}) cannot be applied automatically. " +
                "Implement it as a consent row filter or Casbin policy and reject the proposal.");
        }

        var tableMeta = await _metadataRepository.GetTableMetadataAsync(proposal.Table, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Dbt proposal '{proposalId}' targets table '{proposal.Table}', which is not in the catalog.");

        var newRule = new MaskingRule
        {
            RuleType = proposal.SuggestedRuleType,
            Replacement = proposal.SuggestedRuleType == "REDACT" ? "[REDACTED]" : null
        };

        // EXT-1: Ratchet enforcement – approve must never weaken existing stronger masking rules
        if (tableMeta.ColumnMaskingRules.TryGetValue(proposal.ColumnName, out var existingRule) &&
            CatalogGovernanceRatchet.MaskingRuleStrength(existingRule) > CatalogGovernanceRatchet.MaskingRuleStrength(newRule))
        {
            _logger.LogWarning(
                "Dbt proposal {Id} suggests rule {SuggestedRule} for {Table}.{Column}, but existing masking rule {ExistingRule} is stronger.",
                proposalId, proposal.SuggestedRuleType, proposal.Table, proposal.ColumnName, existingRule.RuleType);
            throw new InvalidOperationException(
                $"Dbt proposal '{proposalId}' would weaken the existing masking rule '{existingRule.RuleType}' of column '{proposal.ColumnName}'; reject the proposal.");
        }

        var updatedRules = new Dictionary<string, MaskingRule>(tableMeta.ColumnMaskingRules, StringComparer.OrdinalIgnoreCase)
        {
            [proposal.ColumnName] = newRule
        };

        await _metadataRepository.UpsertTableMetadataAsync(tableMeta with { ColumnMaskingRules = updatedRules }, ct).ConfigureAwait(false);

        if (_epochRepository != null)
        {
            await _epochRepository.IncrementTableEpochAsync(proposal.Table, ct).ConfigureAwait(false);
        }

        _logger.LogInformation("Applied approved dbt proposal {Id} to table {Table} column {Column} with rule {Rule}.",
            proposalId, proposal.Table, proposal.ColumnName, proposal.SuggestedRuleType);

        return await _proposalRepository.UpdateProposalStatusAsync(proposalId, DbtProposalStatus.Approved, reviewedBy, ct).ConfigureAwait(false);
    }

    public async Task<DbtMetadataProposal> RejectProposalAsync(Guid proposalId, string reviewedBy, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewedBy);

        var proposal = await _proposalRepository.GetProposalByIdAsync(proposalId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Dbt metadata proposal '{proposalId}' not found.");

        if (proposal.Status != DbtProposalStatus.PendingReview)
        {
            throw new InvalidOperationException(
                $"Dbt proposal '{proposalId}' cannot be rejected because its status is '{proposal.Status}' (expected '{DbtProposalStatus.PendingReview}').");
        }

        return await _proposalRepository.UpdateProposalStatusAsync(proposalId, DbtProposalStatus.Rejected, reviewedBy, ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<DbtMetadataProposal>> GetPendingProposalsAsync(TableIdentifier? table = null, CancellationToken ct = default)
    {
        return _proposalRepository.GetPendingProposalsAsync(table, ct);
    }
}
