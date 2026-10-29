namespace Autheris.Extensions.Dbt;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using Autheris.Application.DataCatalog.Services;
using Autheris.Application.Dbt.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
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
    private readonly IRebacStore? _rebacStore;
    private readonly Autheris.Application.Governance.Interfaces.IDatasetTagImportService? _tagImport;
    private readonly Autheris.Application.Catalog.Profiling.ISyntheticSampleRowGenerator? _syntheticRowGenerator;
    private readonly IDbtTestTargetMap? _testTargetMap;
    private readonly ILogger<DbtMetadataIngestionService> _logger;

    public DbtMetadataIngestionService(
        IDbtProposalRepository proposalRepository,
        ITableMetadataRepository metadataRepository,
        ILineageGraphStore lineageGraphStore,
        ILogger<DbtMetadataIngestionService> logger,
        IDbtTestTargetMap? testTargetMap = null)
        : this(proposalRepository, metadataRepository, lineageGraphStore, null, null, null, null, null, null, null, null, testTargetMap, logger)
    {
    }

    public DbtMetadataIngestionService(
        IDbtProposalRepository proposalRepository,
        ITableMetadataRepository metadataRepository,
        ILineageGraphStore lineageGraphStore,
        IPolicyEpochRepository? epochRepository,
        ILogger<DbtMetadataIngestionService> logger)
        : this(proposalRepository, metadataRepository, lineageGraphStore, epochRepository, null, null, null, null, null, logger)
    {
    }

    public DbtMetadataIngestionService(
        IDbtProposalRepository proposalRepository,
        ITableMetadataRepository metadataRepository,
        ILineageGraphStore lineageGraphStore,
        ITableRelationRepository? relationRepository,
        ILogger<DbtMetadataIngestionService> logger)
        : this(proposalRepository, metadataRepository, lineageGraphStore, null, null, null, relationRepository, null, null, logger)
    {
    }

    public DbtMetadataIngestionService(
        IDbtProposalRepository proposalRepository,
        ITableMetadataRepository metadataRepository,
        ILineageGraphStore lineageGraphStore,
        IPolicyEpochRepository? epochRepository,
        ITableRelationRepository? relationRepository,
        ILogger<DbtMetadataIngestionService> logger)
        : this(proposalRepository, metadataRepository, lineageGraphStore, epochRepository, null, null, relationRepository, null, null, logger)
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
        : this(proposalRepository, metadataRepository, lineageGraphStore, epochRepository, gatewayOptions, sqlEndpointLoader, null, null, null, logger)
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
        : this(proposalRepository, metadataRepository, lineageGraphStore, epochRepository, gatewayOptions, sqlEndpointLoader, relationRepository, null, null, logger)
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
        : this(proposalRepository, metadataRepository, lineageGraphStore, epochRepository, gatewayOptions, sqlEndpointLoader, relationRepository, virtualFilterAdmin, null, logger)
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
        IRebacStore? rebacStore,
        ILogger<DbtMetadataIngestionService> logger)
        : this(proposalRepository, metadataRepository, lineageGraphStore, epochRepository, gatewayOptions, sqlEndpointLoader, relationRepository, virtualFilterAdmin, rebacStore, null, logger)
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
        IRebacStore? rebacStore,
        Autheris.Application.Governance.Interfaces.IDatasetTagImportService? tagImport,
        ILogger<DbtMetadataIngestionService> logger)
        : this(proposalRepository, metadataRepository, lineageGraphStore, epochRepository, gatewayOptions, sqlEndpointLoader, relationRepository, virtualFilterAdmin, rebacStore, tagImport, null, logger)
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
        IRebacStore? rebacStore,
        Autheris.Application.Governance.Interfaces.IDatasetTagImportService? tagImport,
        Autheris.Application.Catalog.Profiling.ISyntheticSampleRowGenerator? syntheticRowGenerator,
        ILogger<DbtMetadataIngestionService> logger)
        : this(proposalRepository, metadataRepository, lineageGraphStore, epochRepository, gatewayOptions, sqlEndpointLoader, relationRepository, virtualFilterAdmin, rebacStore, tagImport, syntheticRowGenerator, null, logger)
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
        IRebacStore? rebacStore,
        Autheris.Application.Governance.Interfaces.IDatasetTagImportService? tagImport,
        Autheris.Application.Catalog.Profiling.ISyntheticSampleRowGenerator? syntheticRowGenerator,
        IDbtTestTargetMap? testTargetMap,
        ILogger<DbtMetadataIngestionService> logger)
    {
        _tagImport = tagImport;
        _syntheticRowGenerator = syntheticRowGenerator;
        _testTargetMap = testTargetMap;
        _proposalRepository = proposalRepository ?? throw new ArgumentNullException(nameof(proposalRepository));
        _metadataRepository = metadataRepository ?? throw new ArgumentNullException(nameof(metadataRepository));
        _lineageGraphStore = lineageGraphStore ?? throw new ArgumentNullException(nameof(lineageGraphStore));
        _epochRepository = epochRepository;
        _gatewayOptions = gatewayOptions;
        _sqlEndpointLoader = sqlEndpointLoader;
        _relationRepository = relationRepository;
        _virtualFilterAdmin = virtualFilterAdmin;
        _rebacStore = rebacStore;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Issue #111 (A-2): the model-level tags (never the column tags) become dbt-source dataset tags after the upsert; the upsert itself neither reads
    /// nor writes tags. A failing import never fails the ingestion; counts (no values) are reported as warnings.
    /// </summary>
    private async Task ImportModelTagsAsync(TableIdentifier table, IReadOnlyList<string> tags, List<string> warnings, CancellationToken ct)
    {
        if (_tagImport is null)
        {
            return;
        }

        var outcome = await _tagImport.ImportDbtTagsAsync(table, tags, ct).ConfigureAwait(false);
        if (outcome.Failed)
        {
            warnings.Add($"Table '{table}': the dbt tags were not imported (the tag store is unavailable).");
        }
        else if (outcome.InvalidDropped > 0 || outcome.OverflowDropped > 0)
        {
            warnings.Add($"Table '{table}': dbt tags were reduced ({outcome.InvalidDropped} invalid, {outcome.OverflowDropped} over the cap dropped).");
        }
    }

    private TableMetadata ApplyDeclaredKeys(TableMetadata metadata, DbtDeclaredKeys declared)
    {
        var collector = new Autheris.Application.DataCatalog.Services.CatalogKeyDefinitionImport.Collector();
        if (declared.Invalid)
        {
            collector.AddPrimaryKey(null);
        }
        else
        {
            if (declared.PrimaryKey is not null)
            {
                collector.AddPrimaryKey(declared.PrimaryKey);
            }

            foreach (var unique in declared.UniqueConstraints)
            {
                collector.AddUnique(unique);
            }
        }

        return Autheris.Application.DataCatalog.Services.CatalogKeyDefinitionImport.Apply(metadata, collector, _logger, "dbt");
    }

    private static bool IsTenantAllowed(TableIdentifier id, DbtGovernanceImportCaller caller)
    {
        if (caller.IsClusterAdmin)
        {
            return true;
        }

        return string.Equals(id.Domain, caller.Tenant.Value, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<DbtSyncResult> IngestManifestFileAsync(string filePath, DbtGovernanceImportCaller caller, bool dryRun = false, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(caller);
        ValidateSafeFilePath(filePath);
        var fullPath = Path.GetFullPath(filePath);
        ValidateSafeFilePath(fullPath);

        if (!File.Exists(fullPath))
        {
            return new DbtSyncResult(false, 0, 0, 0, [], $"Dbt manifest file not found at: {fullPath}");
        }

        await using var stream = File.OpenRead(fullPath);
        return await IngestManifestStreamAsync(stream, caller, dryRun, ct).ConfigureAwait(false);
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

    public Task<DbtSyncResult> IngestManifestStreamAsync(Stream manifestStream, bool dryRun = false, TenantId? tenant = null, CancellationToken ct = default)
    {
        // DT-A-9: a tenant-less sync runs for the configured default tenant; without one it is refused (no legacy fallback).
        var effective = tenant ?? Autheris.Application.Tenancy.TenantResolver.EffectiveDefaultOf(_gatewayOptions?.Value.Tenancy);
        if (effective is not { } resolved)
        {
            throw new InvalidOperationException("The dbt sync has no tenant: pass one or configure Gateway:Tenancy:DefaultTenantId.");
        }

        return IngestManifestStreamAsync(manifestStream, new DbtGovernanceImportCaller(new VirtualFilterActor(new Sid("system:dbt-sync"), IsSync: true, null), resolved, tenant == null), dryRun, ct);
    }

    public async Task<DbtSyncResult> IngestManifestStreamAsync(Stream manifestStream, DbtGovernanceImportCaller caller, bool dryRun = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifestStream);
        ArgumentNullException.ThrowIfNull(caller);

        var warnings = new List<string>();
        var rejectedModels = new List<string>();
        var suppressedRelaxations = new List<DbtSuppressedRelaxation>();
        IReadOnlyList<DbtModelDefinition> allModels;
        IReadOnlyList<DbtRelationshipDefinition> relationships;
        IReadOnlyDictionary<string, IReadOnlyList<string>> testToModels = ImmutableDictionary<string, IReadOnlyList<string>>.Empty;

        try
        {
            var parsed = await DbtArtifactStreamingParser.ParseManifestWithRelationshipsAsync(manifestStream, ct).ConfigureAwait(false);
            allModels = parsed.Models;
            relationships = parsed.Relationships;
            testToModels = parsed.TestToModels;
            warnings.AddRange(parsed.Warnings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse dbt manifest stream.");
            return new DbtSyncResult(false, 0, 0, 0, [], $"Manifest parse failure: {ex.Message}");
        }

        // EXT-07: Filter models by caller tenant
        var models = new List<DbtModelDefinition>();
        var rejectedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var creation = new CatalogCreationRun();

        foreach (var m in allModels)
        {
            TableIdentifier tid;
            try
            {
                tid = m.ToTableIdentifier();
            }
            catch (ArgumentException)
            {
                // REQ-282-10: a name the identifier type refuses (dot, control character) skips this item, never the whole sync.
                var badName = DbtIdentifierSanitizer.ForLog($"{m.Database}.{m.Schema}.{m.Name}");
                var badKind = string.Equals(m.Materialization, "source", StringComparison.OrdinalIgnoreCase) ? "source" : "model";
                creation.SkippedTotal++;
                if (creation.Skipped.Count < MaxSkippedItemsReported)
                {
                    creation.Skipped.Add(new DbtSyncSkippedItem(badName, badKind, DbtSyncSkipReasons.InvalidIdentifier));
                }

                _logger.LogWarning("dbt sync skipped {Kind} '{Name}': {Reason}", badKind, badName, DbtSyncSkipReasons.InvalidIdentifier);
                continue;
            }

            if (IsTenantAllowed(tid, caller))
            {
                models.Add(m);
            }
            else
            {
                var idStr = tid.ToString();
                rejectedModels.Add(idStr);
                rejectedSet.Add(m.Name);
                rejectedSet.Add(m.UniqueId);
                rejectedSet.Add(idStr);
                warnings.Add($"Table '{idStr}': tenant '{tid.Domain}' is not the caller's tenant; only a ClusterAdmin may import into another tenant.");
            }
        }

        // EXT-08: Feed IDbtTestTargetMap with parsed test-to-models mappings
        if (_testTargetMap != null)
        {
            var modelToTable = new Dictionary<string, TableIdentifier>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in models)
            {
                var tid = m.ToTableIdentifier();
                modelToTable[m.UniqueId] = tid;
                modelToTable[m.Name] = tid;
            }
            _testTargetMap.Replace(caller.Tenant, modelToTable, testToModels);
        }

        _logger.LogInformation("Parsed {Count} dbt models ({Allowed} allowed, {Rejected} rejected) and {RelCount} relationships from manifest.",
            allModels.Count, models.Count, rejectedModels.Count, relationships.Count);

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
                OwnerEmail: model.Meta.GetValueOrDefault("owner_email"),
                TenantId: caller.Tenant.Value
            ));

            // 3. Omnichannel Documentation Sync (F-DOC-01): Synchronize dbt model and column descriptions
            // Issue #282: the read also runs in dry run (read only) so that a missing entry can be reported as "would create".
            {
                TableMetadata? existingMeta;
                var metadataUnavailable = false;
                try
                {
                    existingMeta = await _metadataRepository.GetTableMetadataAsync(tableId, ct).ConfigureAwait(false);
                }
                catch (TableMetadataUnavailableException)
                {
                    // SEC-94-01: no documentation sync (and never an upsert as a new table) for a table whose metadata is unavailable.
                    if (!dryRun)
                    {
                        warnings.Add($"Table '{tableId}': skipped documentation sync, its stored metadata is unavailable (invalid key or index definition).");
                    }

                    existingMeta = null;
                    metadataUnavailable = true;
                }

                if (existingMeta == null && !metadataUnavailable && _gatewayOptions?.Value is { } gatewayValue)
                {
                    existingMeta = await TryRegisterMissingTableAsync(model, tableId, gatewayValue, dryRun, creation, warnings, ct).ConfigureAwait(false);
                }

                if (!dryRun && existingMeta != null)
                {
                    // EXT-05: Ratchet governance properties rather than overwrite
                    var reqCols = DbtManifestRatchet.MergeRequiredFilterColumns(
                        existingMeta.Identifier,
                        existingMeta.Table.RequiredFilterColumns,
                        ParseMetaRequiredFilterColumns(model.Meta),
                        model.Meta.ContainsKey("required_filter_columns"),
                        suppressedRelaxations,
                        _logger);
                    var reqGroups = DbtManifestRatchet.MergeRequiredFilterColumnGroups(
                        existingMeta.Identifier,
                        existingMeta.Table.RequiredFilterColumnGroups,
                        ParseMetaRequiredFilterColumnGroups(model.Meta),
                        model.Meta.ContainsKey("required_filter_column_groups"),
                        suppressedRelaxations,
                        _logger);

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
                        PhysicalDatabaseName = existingMeta.Table.PhysicalDatabaseName,
                        Location = existingMeta.Table.Location,
                        HttpEndpoint = existingMeta.Table.HttpEndpoint,
                        PluginName = existingMeta.Table.PluginName,
                        Tags = existingMeta.Table.Tags,
                        Visibility = existingMeta.Table.Visibility,
                        RlsExpression = existingMeta.Table.RlsExpression,
                        MinGroupSize = existingMeta.Table.MinGroupSize,
                        SmallGroupStrategy = existingMeta.Table.SmallGroupStrategy,
                        RequiredFilterColumns = reqCols,
                        RequiredFilterColumnGroups = reqGroups,
                        ExpectedRows = existingMeta.Table.ExpectedRows
                    };

                    var updatedColumns = new List<TableColumn>();
                    foreach (var c in existingMeta.Columns)
                    {
                        var matchingCol = model.Columns.TryGetValue(c.ColumnName, out var dCol) ? dCol : null;
                        var colDesc = matchingCol?.Description ?? c.Description;
                        var colLongDesc = matchingCol?.Meta.GetValueOrDefault("long_description") ?? c.LongDescription;
                        var colMeta = DbtManifestRatchet.MergeColumnMeta(
                            existingMeta.Identifier,
                            c.ColumnName,
                            c.Meta,
                            matchingCol?.Meta,
                            suppressedRelaxations,
                            _logger);

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

                    var mergedSamples = DbtManifestRatchet.MergeSyntheticSamples(
                        existingMeta.Identifier,
                        existingMeta.PredefinedSyntheticSamples,
                        model.SyntheticSamples,
                        suppressedRelaxations,
                        _logger);

                    var updatedMetadata = new TableMetadata
                    {
                        Table = updatedTableObj,
                        Identifier = existingMeta.Identifier,
                        Columns = updatedColumns,
                        ColumnMaskingRules = existingMeta.ColumnMaskingRules,
                        PrimaryKeyColumns = existingMeta.PrimaryKeyColumns,
                        Indexes = existingMeta.Indexes,
                        PredefinedSyntheticSamples = mergedSamples
                    };

                    // Issue #94 (D-8): primary_key / unique constraints and unique tests of the manifest replace the stored definition.
                    if (model.DeclaredKeys is { } declared)
                    {
                        updatedMetadata = ApplyDeclaredKeys(updatedMetadata, declared);
                    }

                    if (mergedSamples != null)
                    {
                        _syntheticRowGenerator?.RegisterSyntheticSamples(updatedMetadata.Identifier, mergedSamples);
                    }

                    await _metadataRepository.UpsertTableMetadataAsync(updatedMetadata, ct).ConfigureAwait(false);
                    await SeedRebacStructureTuplesAsync(updatedMetadata.Identifier, ct).ConfigureAwait(false);
                    await ImportModelTagsAsync(updatedMetadata.Identifier, model.Tags, warnings, ct).ConfigureAwait(false);
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

        // R-31 / issue #151: persist relationships (all ordered key pairs, reason-coded warnings, dry-run parity)
        // EXT-07: Filter out relationships involving rejected models
        var allowedRelationships = new List<DbtRelationshipDefinition>();
        foreach (var rel in relationships)
        {
            if (rejectedSet.Contains(rel.ParentModelOrTable) || rejectedSet.Contains(rel.ChildModelOrTable))
            {
                warnings.Add($"Relationship '{rel.Name}': dropped because it targets model outside caller's tenant.");
                continue;
            }
            allowedRelationships.Add(rel);
        }

        var importedRelations = 0;
        if (_relationRepository != null && allowedRelationships.Count > 0)
        {
            importedRelations = await ImportRelationsAsync(models, allowedRelationships, dryRun, warnings, ct).ConfigureAwait(false);
        }

        // Apply lineage updates
        if (!dryRun && lineageNodesToUpdate.Count > 0)
        {
            _lineageGraphStore.UpdateGraph(lineageNodesToUpdate);
        }

        _logger.LogInformation("Completed dbt ingestion: {Models} models, {Proposals} proposals, {Lineage} lineage nodes, {Relations} relations, {Rejected} rejected.",
            models.Count, generatedProposals, lineageNodesToUpdate.Count, importedRelations, rejectedModels.Count);

        return new DbtSyncResult(
            Success: true,
            ParsedModelsCount: models.Count,
            GeneratedProposalsCount: generatedProposals,
            UpdatedLineageNodesCount: lineageNodesToUpdate.Count,
            Warnings: warnings,
            ErrorMessage: null,
            ImportedRelationsCount: importedRelations,
            RejectedModels: rejectedModels,
            SuppressedRelaxations: suppressedRelaxations
        )
        {
            CreatedTables = creation.Created,
            SkippedItems = creation.Skipped,
            SkippedItemsTruncated = creation.SkippedTotal > creation.Skipped.Count
        };
    }

    /// <summary>SEC-282-8: bounds per run so that a hostile manifest cannot flood the governance database or the response.</summary>
    private const int MaxCreatedTablesPerRun = 5000;
    private const int MaxSkippedItemsReported = 500;

    private sealed class CatalogCreationRun
    {
        public List<string> Created { get; } = [];
        public List<DbtSyncSkippedItem> Skipped { get; } = [];
        public HashSet<string> Seen { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int SkippedTotal { get; set; }
    }

    /// <summary>
    /// Issue #282: registers a manifest model or source that has no catalog entry. Returns the new entry (live) so that the update step
    /// applies descriptions, keys, tags and the ReBAC seed in the same run; null when nothing was created (skip, dry run, failure).
    /// An entry that appears between the read and the write is returned as is and never overwritten (SEC-282-5).
    /// </summary>
    private async Task<TableMetadata?> TryRegisterMissingTableAsync(
        DbtModelDefinition model,
        TableIdentifier tableId,
        Autheris.Domain.Options.GatewayOptions options,
        bool dryRun,
        CatalogCreationRun run,
        List<string> warnings,
        CancellationToken ct)
    {
        var kind = string.Equals(model.Materialization, "source", StringComparison.OrdinalIgnoreCase) ? "source" : "model";
        var displayName = DbtIdentifierSanitizer.ForLog(tableId.ToString());

        void Skip(string reason)
        {
            run.SkippedTotal++;
            if (run.Skipped.Count < MaxSkippedItemsReported)
            {
                run.Skipped.Add(new DbtSyncSkippedItem(displayName, kind, reason));
            }

            _logger.LogWarning("dbt sync did not register {Kind} '{Name}': {Reason}", kind, displayName, reason);
        }

        if (!options.Dbt.CreateMissingTables)
        {
            Skip(DbtSyncSkipReasons.CreationDisabled);
            return null;
        }

        if (!run.Seen.Add(tableId.ToString()))
        {
            Skip(DbtSyncSkipReasons.Duplicate);
            return null;
        }

        if (run.Created.Count >= MaxCreatedTablesPerRun)
        {
            Skip(DbtSyncSkipReasons.Ineligible);
            return null;
        }

        if (!DbtCatalogTableFactory.TryBuild(model, options, out var built, out var reason) || built is null)
        {
            Skip(reason ?? DbtSyncSkipReasons.Ineligible);
            return null;
        }

        if (dryRun)
        {
            run.Created.Add(displayName);
            return null;
        }

        try
        {
            // SEC-282-5: last read before the write; an entry that appeared meanwhile keeps its governance fields.
            var current = await _metadataRepository.GetTableMetadataAsync(tableId, ct).ConfigureAwait(false);
            if (current != null)
            {
                return current;
            }

            var persisted = await _metadataRepository.UpsertTableMetadataAsync(built, ct).ConfigureAwait(false);
            run.Created.Add(displayName);
            if (options.Dbt.ActivateNewTables)
            {
                warnings.Add($"Table '{displayName}': registered active without masks or consents (Dbt:ActivateNewTables is enabled).");
            }

            return persisted ?? built;
        }
        catch (TableMetadataUnavailableException)
        {
            _logger.LogWarning("dbt sync could not register {Kind} '{Name}': stored metadata is unavailable.", kind, displayName);
            Skip(DbtSyncSkipReasons.CreateFailed);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "dbt sync failed to register {Kind} '{Name}'.", kind, displayName);
            Skip(DbtSyncSkipReasons.CreateFailed);
            return null;
        }
    }

    private sealed record ResolvedRelation(
        DbtRelationshipDefinition Definition,
        DbtModelDefinition Parent,
        DbtModelDefinition Child,
        TableIdentifier ParentId,
        TableIdentifier ChildId);

    private async Task<int> ImportRelationsAsync(
        IReadOnlyList<DbtModelDefinition> models,
        IReadOnlyList<DbtRelationshipDefinition> relationships,
        bool dryRun,
        List<string> warnings,
        CancellationToken ct)
    {
        var modelByName = new Dictionary<string, DbtModelDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in models)
        {
            modelByName[m.Name] = m;
            modelByName[m.UniqueId] = m;
        }

        // 1. Resolve models (UNKNOWN_MODEL).
        var resolved = new List<ResolvedRelation>(relationships.Count);
        foreach (var rel in relationships)
        {
            if (modelByName.TryGetValue(rel.ParentModelOrTable, out var parent) && modelByName.TryGetValue(rel.ChildModelOrTable, out var child))
            {
                resolved.Add(new ResolvedRelation(rel, parent, child, parent.ToTableIdentifier(), child.ToTableIdentifier()));
            }
            else
            {
                warnings.Add(SkipWarning(rel, "UNKNOWN_MODEL"));
            }
        }

        // 2. Natural-key grouping (SEC-REL151-01): a group whose key sets differ is skipped as a whole, never last-wins and
        // independent of manifest order. Entries with identical ordered pairs are the same relation and collapse to one.
        var groups = resolved
            .GroupBy(r => $"{r.ParentId}\u0001{r.ChildId}\u0001{r.Definition.Name}", StringComparer.OrdinalIgnoreCase)
            .ToList();

        var metaCache = new Dictionary<TableIdentifier, TableMetadata?>();
        var imported = 0;
        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();
            var entries = group.ToList();
            var distinctKeySets = entries
                .Select(e => string.Join('\u0002', e.Definition.ParentColumns.Zip(e.Definition.ChildColumns, (p, c) => $"{p.ToUpperInvariant()}\u0003{c.ToUpperInvariant()}")))
                .Distinct(StringComparer.Ordinal)
                .Count();
            if (distinctKeySets > 1)
            {
                warnings.Add(SkipWarning(entries[0].Definition, "DUPLICATE_RELATION"));
                continue;
            }

            var entry = entries[0];
            var rel = entry.Definition;

            var parentMeta = await GetStoredAsync(metaCache, entry.ParentId, ct).ConfigureAwait(false);
            var childMeta = await GetStoredAsync(metaCache, entry.ChildId, ct).ConfigureAwait(false);
            var parentColumns = ResolveCanonicalColumns(rel.ParentColumns, parentMeta, entry.Parent);
            var childColumns = ResolveCanonicalColumns(rel.ChildColumns, childMeta, entry.Child);
            if (parentColumns is null || childColumns is null)
            {
                warnings.Add(SkipWarning(rel, "UNKNOWN_COLUMN"));
                continue;
            }

            var notUnique = !IsDeclaredUnique(parentColumns, parentMeta, entry.Parent);

            var tableRel = new TableRelation
            {
                Id = Guid.NewGuid(),
                ParentTableIdentifier = entry.ParentId,
                ChildTableIdentifier = entry.ChildId,
                RelationName = rel.Name,
                JoinKeysParent = parentColumns,
                JoinKeysChild = childColumns,
                Cardinality = RelationCardinality.OneToMany
                // AllowCrossDataSource is never set by the importer (SEC-REL151-04).
            };

            if (!dryRun)
            {
                try
                {
                    await _relationRepository!.CreateRelationAsync(tableRel, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to import table relation '{Relation}' between {Parent} and {Child}.",
                        DbtIdentifierSanitizer.ForLog(rel.Name), entry.ParentId, entry.ChildId);
                    warnings.Add(SkipWarning(rel, "PERSIST_FAILED"));
                    continue;
                }
            }

            imported++;
            if (notUnique)
            {
                warnings.Add($"Relation '{DbtIdentifierSanitizer.ForLog(rel.Name)}' ({DbtIdentifierSanitizer.ForLog(rel.ChildModelOrTable)} -> {DbtIdentifierSanitizer.ForLog(rel.ParentModelOrTable)}) imported with warning: PARENT_KEY_NOT_UNIQUE");
            }
        }

        return imported;
    }

    private static string SkipWarning(DbtRelationshipDefinition rel, string code) =>
        $"Relation '{DbtIdentifierSanitizer.ForLog(rel.Name)}' ({DbtIdentifierSanitizer.ForLog(rel.ChildModelOrTable)} -> {DbtIdentifierSanitizer.ForLog(rel.ParentModelOrTable)}) skipped: {code}";

    private async Task<TableMetadata?> GetStoredAsync(Dictionary<TableIdentifier, TableMetadata?> cache, TableIdentifier id, CancellationToken ct)
    {
        if (!cache.TryGetValue(id, out var meta))
        {
            meta = await _metadataRepository.GetTableMetadataAsync(id, ct).ConfigureAwait(false);
            cache[id] = meta;
        }

        return meta;
    }

    /// <summary>Canonical catalog names of the key columns: stored metadata when the table is stored, the manifest columns otherwise (dry run). Null when any column is unknown.</summary>
    private static List<string>? ResolveCanonicalColumns(IReadOnlyList<string> requested, TableMetadata? stored, DbtModelDefinition model)
    {
        var result = new List<string>(requested.Count);
        foreach (var name in requested)
        {
            string? canonical;
            if (stored is not null)
            {
                canonical = stored.GetColumn(name)?.ColumnName;
            }
            else
            {
                canonical = model.Columns.TryGetValue(name, out var def) ? def.Name : null;
            }

            if (string.IsNullOrEmpty(canonical))
            {
                return null;
            }

            result.Add(canonical);
        }

        return result;
    }

    /// <summary>True when the parent key columns equal (as a set) the primary key or a unique index, stored or declared in the manifest.</summary>
    private static bool IsDeclaredUnique(IReadOnlyList<string> parentColumns, TableMetadata? stored, DbtModelDefinition model)
    {
        if (stored is not null)
        {
            if (SameSet(parentColumns, stored.PrimaryKeyColumns) || stored.Indexes.Any(i => i.IsUnique && SameSet(parentColumns, i.Columns)))
            {
                return true;
            }
        }

        var declared = model.DeclaredKeys;
        return declared is { Invalid: false } &&
            ((declared.PrimaryKey is not null && SameSet(parentColumns, declared.PrimaryKey)) ||
             declared.UniqueConstraints.Any(u => SameSet(parentColumns, u)));
    }

    private static bool SameSet(IReadOnlyList<string> left, IReadOnlyList<string> right) =>
        left.Count > 0 && left.Count == right.Count &&
        new HashSet<string>(left, StringComparer.OrdinalIgnoreCase).SetEquals(right);

    private static readonly HashSet<string> ValidMaskingRuleTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "REDACT", "NULLIFY", "HMAC", "HMAC_SHA256", "HASH", "MASK_EMAIL", "MASK_IBAN", "MASK_PHONE", "REGEX",
        "GEO_JITTER", "PARTIAL_MASK", "TOKENIZE", "TOKENIZATION"
    };

    private static string NormalizeMaskingRuleType(string raw) => DbtMaskingRuleAliases.Normalize(raw);

    public async Task<DbtGovernanceSyncResult> IngestGovernanceFileAsync(string filePath, DbtGovernanceImportCaller caller, bool dryRun = false, bool replace = false, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(caller);
        ValidateSafeFilePath(filePath);
        var fullPath = Path.GetFullPath(filePath);
        ValidateSafeFilePath(fullPath);

        if (!File.Exists(fullPath))
        {
            return new DbtGovernanceSyncResult(false, 0, 0, 0, 0, 0, [], $"Dbt governance file not found at: {fullPath}");
        }

        await using var stream = File.OpenRead(fullPath);
        return await IngestGovernanceStreamAsync(stream, caller, dryRun, replace, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Direct governance import (R-24). Fail closed: the import may only tighten. Removing or weakening a stored masking rule
    /// (ratchet as in <see cref="ApproveProposalAsync"/>) or lowering a table / column sensitivity rejects the whole import before
    /// anything is written; such changes go through the dbt proposal workflow. Virtual filters and access profiles are saved as the
    /// calling user in the caller's tenant (see <see cref="DbtGovernanceImportCaller"/>).
    /// </summary>
    public async Task<DbtGovernanceSyncResult> IngestGovernanceStreamAsync(Stream governanceStream, DbtGovernanceImportCaller caller, bool dryRun = false, bool replace = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(governanceStream);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(caller.Actor);
        if (caller.Actor.IsSync)
        {
            // The sync actor skips the approval workflow and the managed-resource lock; a direct import never runs as the sync.
            throw new ArgumentException("The dbt governance import must run as the calling user, not as the sync actor.", nameof(caller));
        }

        var warnings = new List<string>();
        var rejections = new List<string>();
        var pendingUpserts = new List<TableMetadata>();
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

        // Removing a stored masking rule is a relaxation: it is not applied but rejects the import.
        void RejectRuleRemoval(Dictionary<string, MaskingRule> rules, TableIdentifier table, string column)
        {
            if (rules.TryGetValue(column, out var stored))
            {
                removedMaskingRulesCount++;
                rejections.Add($"Column '{table}.{column}': removing masking rule '{stored.RuleType}' requires the dbt proposal workflow.");
            }
        }

        // EXT-1 ratchet (same semantics as ApproveProposalAsync): a rule weaker than the stored one is not applied but rejects the import.
        void ApplyMaskingRule(Dictionary<string, MaskingRule> rules, TableIdentifier table, string column, MaskingRule newRule)
        {
            if (rules.TryGetValue(column, out var stored) &&
                CatalogGovernanceRatchet.IsMaskingRelaxation(stored, newRule))
            {
                relaxedMaskingRulesCount++;
                rejections.Add($"Column '{table}.{column}': masking rule '{newRule.RuleType}' would weaken the stored rule '{stored.RuleType}'; use the dbt proposal workflow.");
                return;
            }

            maskingRulesCount++;
            rules[column] = newRule;
        }

        // R-50: virtual filters and access profiles are parsed (and their tenant checked) before anything is written. A missing
        // tenant is the caller's request tenant; another tenant is accepted only for a canonical ClusterAdmin.
        var filters = new List<VirtualFilter>();
        if (root.ValueKind == JsonValueKind.Object &&
            (root.TryGetProperty("virtual_filters", out var filtersProp) ||
             root.TryGetProperty("virtualFilters", out filtersProp) ||
             root.TryGetProperty("filters", out filtersProp)))
        {
            filters = ParseVirtualFilters(filtersProp, caller.Tenant);
        }

        var profiles = new List<VirtualFilterAccessProfile>();
        if (root.ValueKind == JsonValueKind.Object &&
            (root.TryGetProperty("access_profiles", out var profilesProp) ||
             root.TryGetProperty("accessProfiles", out profilesProp) ||
             root.TryGetProperty("profiles", out profilesProp)))
        {
            profiles = ParseAccessProfiles(profilesProp, caller.Tenant);
        }

        if (!caller.IsClusterAdmin)
        {
            foreach (var filter in filters.Where(f => f.TenantId != caller.Tenant))
            {
                rejections.Add($"Virtual filter '{filter.Name}': tenant '{filter.TenantId.Value}' is not the caller's tenant; only a ClusterAdmin may import into another tenant.");
            }

            foreach (var profile in profiles.Where(p => p.TenantId != caller.Tenant))
            {
                rejections.Add($"Access profile '{profile.Name}': tenant '{profile.TenantId.Value}' is not the caller's tenant; only a ClusterAdmin may import into another tenant.");
            }
        }

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
                if (!IsTenantAllowed(tableId, caller))
                {
                    rejections.Add($"Table '{tableId}': tenant '{tableId.Domain}' is not the caller's tenant; only a ClusterAdmin may import into another tenant.");
                    continue;
                }

                TableMetadata? existingMeta;
                try
                {
                    existingMeta = await _metadataRepository.GetTableMetadataAsync(tableId, ct).ConfigureAwait(false);
                }
                catch (TableMetadataUnavailableException)
                {
                    // SEC-94-01: governance import never touches (or recreates) a table whose metadata is unavailable.
                    warnings.Add($"Table '{tableId}': skipped governance update, its stored metadata is unavailable (invalid key or index definition).");
                    continue;
                }

                if (existingMeta == null)
                {
                    var allTables = await _metadataRepository.GetAllTablesAsync(ct).ConfigureAwait(false);
                    var candidates = allTables.Where(m =>
                        IsTenantAllowed(m.Identifier, caller) &&
                        string.Equals(m.Identifier.Schema, tableId.Schema, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(m.Identifier.TableName, tableId.TableName, StringComparison.OrdinalIgnoreCase)).ToList();

                    if (candidates.Count > 1)
                    {
                        rejections.Add($"Table '{tableId}': ambiguous table reference matches multiple tenants; use fully qualified identifier.");
                        continue;
                    }
                    if (candidates.Count == 1)
                    {
                        existingMeta = candidates[0];
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

                var sensitivity = existingMeta.Table.Sensitivity;
                if (item.TryGetProperty("sensitivity", out var sProp) && sProp.GetString() is string incomingSensitivity &&
                    !string.IsNullOrWhiteSpace(incomingSensitivity))
                {
                    // Fail closed: a lower table sensitivity is not applied but rejects the import (proposal workflow). An empty stored
                    // value is the implicit NORMAL (SensitivityRank ranks it like NORMAL), so it cannot be lowered either.
                    if (Table.SensitivityRank(incomingSensitivity) < Table.SensitivityRank(existingMeta.Table.Sensitivity))
                    {
                        var storedTableSensitivity = string.IsNullOrWhiteSpace(existingMeta.Table.Sensitivity) ? "NORMAL" : existingMeta.Table.Sensitivity;
                        rejections.Add($"Table '{tableId}': sensitivity '{incomingSensitivity}' would lower the stored sensitivity '{storedTableSensitivity}'; use the dbt proposal workflow.");
                    }
                    else
                    {
                        sensitivity = incomingSensitivity;
                    }
                }

                var origin = item.TryGetProperty("origin", out var oProp) ? oProp.GetString() : "dbt_sample";
                var description = item.TryGetProperty("description", out var dProp) ? dProp.GetString() : existingMeta.Table.Description;
                var historyJson = item.TryGetProperty("history", out var hProp) ? hProp.GetRawText() : null;
                var reviewJson = item.TryGetProperty("classification_review", out var crProp) ? crProp.GetRawText() : null;

                // Fail closed: required filter columns / groups may only be tightened. An omitted property keeps the stored
                // requirement, except in replace mode where it means "none" (and is rejected like any removal).
                var storedReqCols = existingMeta.RequiredFilterColumns;
                var storedReqGroups = existingMeta.RequiredFilterColumnGroups;
                var reqCols = ParseJsonRequiredFilterColumns(item) ?? (effectiveReplace ? ImmutableArray<string>.Empty : storedReqCols);
                var reqGroups = ParseJsonRequiredFilterColumnGroups(item) ?? (effectiveReplace ? ImmutableArray<ImmutableArray<string>>.Empty : storedReqGroups);
                foreach (var removedCol in RemovedRequiredFilterColumns(storedReqCols, reqCols))
                {
                    rejections.Add($"Table '{tableId}': removing required filter column '{removedCol}' requires the dbt proposal workflow.");
                }

                foreach (var relaxedGroup in RelaxedRequiredFilterColumnGroups(storedReqGroups, reqGroups))
                {
                    rejections.Add($"Table '{tableId}': {relaxedGroup} requires the dbt proposal workflow.");
                }

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
                    PhysicalDatabaseName = existingMeta.Table.PhysicalDatabaseName,
                    Location = existingMeta.Table.Location,
                    HttpEndpoint = existingMeta.Table.HttpEndpoint,
                    PluginName = existingMeta.Table.PluginName,
                    Tags = existingMeta.Table.Tags,
                    Visibility = existingMeta.Table.Visibility,
                    RlsExpression = existingMeta.Table.RlsExpression,
                    MinGroupSize = existingMeta.Table.MinGroupSize,
                    SmallGroupStrategy = existingMeta.Table.SmallGroupStrategy,
                    RequiredFilterColumns = reqCols,
                    RequiredFilterColumnGroups = reqGroups,
                    ExpectedRows = existingMeta.Table.ExpectedRows
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
                            var incomingHigh = Table.IsSensitivityHigh(cs);
                            var storedLabel = colMeta.TryGetValue("sensitivity", out var sl) ? sl : null;
                            if ((col.IsSensitive && !incomingHigh) ||
                                (!string.IsNullOrWhiteSpace(storedLabel) && Table.SensitivityRank(cs) < Table.SensitivityRank(storedLabel)))
                            {
                                // Fail closed: a lower column sensitivity is not applied but rejects the import (proposal workflow).
                                rejections.Add($"Column '{tableId}.{col.ColumnName}': sensitivity '{cs}' would lower the stored sensitivity; use the dbt proposal workflow.");
                            }
                            else
                            {
                                colSensitivity = incomingHigh;
                                if (!string.IsNullOrWhiteSpace(cs)) colMeta["sensitivity"] = cs;
                            }
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
                                // Explicit removal: a relaxation, rejected (proposal workflow)
                                RejectRuleRemoval(updatedMaskingRules, tableId, col.ColumnName);
                            }
                            else if (mrProp.ValueKind == JsonValueKind.String)
                            {
                                var rawRule = mrProp.GetString()?.Trim() ?? string.Empty;
                                if (string.Equals(rawRule, "none", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(rawRule, "null", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(rawRule, "false", StringComparison.OrdinalIgnoreCase) ||
                                    string.IsNullOrWhiteSpace(rawRule))
                                {
                                    // B-01: "none", "null", "false", or empty treated as no rule / remove (a relaxation, rejected)
                                    RejectRuleRemoval(updatedMaskingRules, tableId, col.ColumnName);
                                }
                                else
                                {
                                    var upperRule = NormalizeMaskingRuleType(rawRule);
                                    if (!ValidMaskingRuleTypes.Contains(upperRule))
                                    {
                                        // B-01: reject unknown rule type
                                        throw new ArgumentException($"Unknown masking rule '{rawRule}' on column '{col.ColumnName}'.");
                                    }

                                    var newRule = new MaskingRule
                                    {
                                        RuleType = upperRule,
                                        Replacement = upperRule == "REDACT" ? "[REDACTED]" : null
                                    };

                                    ApplyMaskingRule(updatedMaskingRules, tableId, col.ColumnName, newRule);
                                }
                            }
                            else if (mrProp.ValueKind == JsonValueKind.Object)
                            {
                                string? rawRule = null;
                                if (mrProp.TryGetProperty("rule_type", out var rtProp) ||
                                    mrProp.TryGetProperty("type", out rtProp) ||
                                    mrProp.TryGetProperty("RuleType", out rtProp))
                                {
                                    rawRule = rtProp.GetString();
                                }

                                if (string.IsNullOrWhiteSpace(rawRule))
                                {
                                    throw new ArgumentException($"Masking rule object on column '{col.ColumnName}' missing rule_type.");
                                }

                                var upperRule = NormalizeMaskingRuleType(rawRule);
                                if (!ValidMaskingRuleTypes.Contains(upperRule))
                                {
                                    throw new ArgumentException($"Unknown masking rule '{rawRule}' on column '{col.ColumnName}'.");
                                }

                                var newRule = new MaskingRule
                                {
                                    RuleType = upperRule,
                                    Replacement = upperRule == "REDACT" ? "[REDACTED]" : null
                                };

                                if (mrProp.TryGetProperty("replacement", out var repProp) || mrProp.TryGetProperty("Replacement", out repProp))
                                {
                                    newRule = newRule with { Replacement = repProp.GetString() };
                                }
                                if (mrProp.TryGetProperty("mode", out var ruleModeProp) || mrProp.TryGetProperty("Mode", out ruleModeProp))
                                {
                                    newRule = newRule with { Mode = ruleModeProp.GetString() };
                                }
                                if (mrProp.TryGetProperty("decimals", out var decProp) || mrProp.TryGetProperty("Decimals", out decProp))
                                {
                                    if (decProp.TryGetInt32(out var decVal)) newRule = newRule with { Decimals = decVal };
                                }
                                if (mrProp.TryGetProperty("radius_meters", out var radProp) || mrProp.TryGetProperty("radiusMeters", out radProp) || mrProp.TryGetProperty("RadiusMeters", out radProp))
                                {
                                    if (radProp.TryGetDouble(out var radVal)) newRule = newRule with { RadiusMeters = radVal };
                                }
                                if (mrProp.TryGetProperty("keep_prefix", out var kpProp) || mrProp.TryGetProperty("keepPrefix", out kpProp) || mrProp.TryGetProperty("KeepPrefix", out kpProp))
                                {
                                    if (kpProp.TryGetInt32(out var kpVal)) newRule = newRule with { KeepPrefix = kpVal };
                                }
                                if (mrProp.TryGetProperty("keep_suffix", out var ksProp) || mrProp.TryGetProperty("keepSuffix", out ksProp) || mrProp.TryGetProperty("KeepSuffix", out ksProp))
                                {
                                    if (ksProp.TryGetInt32(out var ksVal)) newRule = newRule with { KeepSuffix = ksVal };
                                }
                                if (mrProp.TryGetProperty("mask_char", out var mcProp) || mrProp.TryGetProperty("maskChar", out mcProp) || mrProp.TryGetProperty("MaskChar", out mcProp))
                                {
                                    var mcStr = mcProp.GetString();
                                    if (!string.IsNullOrEmpty(mcStr)) newRule = newRule with { MaskChar = mcStr[0] };
                                }
                                if (mrProp.TryGetProperty("fixed_length", out var flProp) || mrProp.TryGetProperty("fixedLength", out flProp) || mrProp.TryGetProperty("FixedLength", out flProp))
                                {
                                    if (flProp.ValueKind == JsonValueKind.True || flProp.ValueKind == JsonValueKind.False)
                                    {
                                        newRule = newRule with { FixedLength = flProp.GetBoolean() };
                                    }
                                }
                                if (mrProp.TryGetProperty("token_domain", out var tdProp) || mrProp.TryGetProperty("tokenDomain", out tdProp) || mrProp.TryGetProperty("TokenDomain", out tdProp))
                                {
                                    newRule = newRule with { TokenDomain = tdProp.GetString() };
                                }

                                if (newRule.KeepPrefix < 0 || newRule.KeepSuffix < 0 || (newRule.KeepPrefix + newRule.KeepSuffix > MaskingRule.MaxRevealedChars))
                                {
                                    throw new ArgumentException($"Masking rule on column '{col.ColumnName}' specifies invalid revealed characters ({newRule.KeepPrefix}+{newRule.KeepSuffix} > {MaskingRule.MaxRevealedChars}).");
                                }

                                ApplyMaskingRule(updatedMaskingRules, tableId, col.ColumnName, newRule);
                            }
                        }
                        else if (effectiveReplace)
                        {
                            // B-02: replace mode - column without masking_rule would lose its rule (a relaxation, rejected)
                            RejectRuleRemoval(updatedMaskingRules, tableId, col.ColumnName);
                        }

                        updatedColumns++;
                    }
                    else if (effectiveReplace)
                    {
                        // B-02: replace mode - omitted column would lose its rule (a relaxation, rejected)
                        RejectRuleRemoval(updatedMaskingRules, tableId, col.ColumnName);
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

                pendingUpserts.Add(updatedMetadata);
                updatedTables++;
            }
        }

        virtualFiltersCount = filters.Count;
        accessProfilesCount = profiles.Count;

        // Fail closed: any relaxation, sensitivity downgrade or foreign tenant rejects the whole import before anything is written.
        if (rejections.Count > 0)
        {
            _logger.LogWarning(
                "Dbt governance import rejected: {Count} change(s) would relax governance or target another tenant.",
                rejections.Count);
            const int listed = 20;
            var summary = string.Join("; ", rejections.Take(listed)) +
                          (rejections.Count > listed ? $"; ... and {rejections.Count - listed} more" : string.Empty);
            warnings.AddRange(rejections);
            return new DbtGovernanceSyncResult(
                Success: false,
                UpdatedTablesCount: 0,
                UpdatedColumnsCount: 0,
                MaskingRulesCount: 0,
                VirtualFiltersCount: 0,
                AccessProfilesCount: 0,
                Warnings: warnings,
                ErrorMessage: $"Governance import rejected: {rejections.Count} change(s) would remove or weaken masking rules, lower a sensitivity " +
                              $"or target another tenant. Nothing was applied; relaxations require the dbt proposal workflow. {summary}",
                RemovedMaskingRulesCount: removedMaskingRulesCount,
                RelaxedMaskingRulesCount: relaxedMaskingRulesCount);
        }

        if (!dryRun)
        {
            foreach (var updatedMetadata in pendingUpserts)
            {
                await _metadataRepository.UpsertTableMetadataAsync(updatedMetadata, ct).ConfigureAwait(false);
                await SeedRebacStructureTuplesAsync(updatedMetadata.Identifier, ct).ConfigureAwait(false);
            }
        }

        // R-50: virtual filters and access profiles are saved as the calling user (never as the sync), so the approval
        // workflow and the managed-resource lock of VirtualFilterAdministrationService apply.
        if (!dryRun && _virtualFilterAdmin != null)
        {
            foreach (var filter in filters)
            {
                try
                {
                    await _virtualFilterAdmin.SaveFilterAsync(filter, caller.Actor, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    warnings.Add($"Failed to import virtual filter '{filter.Name}': {ex.Message}");
                }
            }

            foreach (var profile in profiles)
            {
                try
                {
                    await _virtualFilterAdmin.SaveProfileAsync(profile, caller.Actor, ct: ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    warnings.Add($"Failed to import access profile '{profile.Name}': {ex.Message}");
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

    private static List<VirtualFilter> ParseVirtualFilters(JsonElement element, TenantId defaultTenant)
    {
        var result = new List<VirtualFilter>();
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                var filter = ParseSingleFilter(prop.Name, prop.Value, defaultTenant);
                if (filter != null) result.Add(filter);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(name)) continue;
                var filter = ParseSingleFilter(name, item, defaultTenant);
                if (filter != null) result.Add(filter);
            }
        }
        return result;
    }

    private static VirtualFilter? ParseSingleFilter(string name, JsonElement val, TenantId defaultTenant)
    {
        // A missing tenant is the caller's request tenant (never the legacy tenant); the caller checks explicit ones.
        var tenantStr = val.TryGetProperty("tenant", out var tProp) ? tProp.GetString() : null;
        var tenant = !string.IsNullOrWhiteSpace(tenantStr) ? new TenantId(tenantStr) : defaultTenant;
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

    private static List<VirtualFilterAccessProfile> ParseAccessProfiles(JsonElement element, TenantId defaultTenant)
    {
        var result = new List<VirtualFilterAccessProfile>();
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                var profile = ParseSingleProfile(prop.Name, prop.Value, defaultTenant);
                if (profile != null) result.Add(profile);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(name)) continue;
                var profile = ParseSingleProfile(name, item, defaultTenant);
                if (profile != null) result.Add(profile);
            }
        }
        return result;
    }

    private static VirtualFilterAccessProfile? ParseSingleProfile(string name, JsonElement val, TenantId defaultTenant)
    {
        // A missing tenant is the caller's request tenant (never the legacy tenant); the caller checks explicit ones.
        var tenantStr = val.TryGetProperty("tenant", out var tProp) ? tProp.GetString() : null;
        var tenant = !string.IsNullOrWhiteSpace(tenantStr) ? new TenantId(tenantStr) : defaultTenant;
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

        return new VirtualFilterAccessProfile
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
            CatalogGovernanceRatchet.IsMaskingRelaxation(existingRule, newRule))
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

    private async Task SeedRebacStructureTuplesAsync(TableIdentifier tableId, CancellationToken ct)
    {
        if (_rebacStore == null)
        {
            return;
        }

        var domainObj = RebacTableGate.DomainObjectId(tableId.Domain);
        var schemaObj = RebacTableGate.SchemaObjectId(tableId.Domain, tableId.Schema);
        var tableObj = RebacTableGate.ObjectId(tableId);

        await _rebacStore.AddTuplesAsync([
            new RebacTuple(tableId.Domain, schemaObj, "parent", tableObj),
            new RebacTuple(tableId.Domain, domainObj, "parent", schemaObj)
        ], ct).ConfigureAwait(false);
    }

    private static ImmutableArray<string>? ParseMetaRequiredFilterColumns(IReadOnlyDictionary<string, string>? meta)
    {
        if (meta == null) return null;
        if (!meta.TryGetValue("required_filter_columns", out var val) &&
            !meta.TryGetValue("requiredFilterColumns", out val))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(val)) return ImmutableArray<string>.Empty;

        try
        {
            using var doc = JsonDocument.Parse(val);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>();
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    var s = el.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
                }
                return list.ToImmutableArray();
            }
        }
        catch (JsonException)
        {
            // fallback to comma-separated
        }

        var parts = val.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.ToImmutableArray();
    }

    private static ImmutableArray<ImmutableArray<string>>? ParseMetaRequiredFilterColumnGroups(IReadOnlyDictionary<string, string>? meta)
    {
        if (meta == null) return null;
        if (!meta.TryGetValue("required_filter_column_groups", out var val) &&
            !meta.TryGetValue("requiredFilterColumnGroups", out val))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(val)) return ImmutableArray<ImmutableArray<string>>.Empty;

        try
        {
            using var doc = JsonDocument.Parse(val);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                var outer = new List<ImmutableArray<string>>();
                foreach (var groupEl in doc.RootElement.EnumerateArray())
                {
                    if (groupEl.ValueKind == JsonValueKind.Array)
                    {
                        var inner = new List<string>();
                        foreach (var el in groupEl.EnumerateArray())
                        {
                            var s = el.GetString();
                            if (!string.IsNullOrWhiteSpace(s)) inner.Add(s);
                        }
                        outer.Add(inner.ToImmutableArray());
                    }
                }
                return outer.ToImmutableArray();
            }
        }
        catch (JsonException)
        {
            // invalid format
        }

        return ImmutableArray<ImmutableArray<string>>.Empty;
    }

    /// <summary>Stored required filter columns (a conjunction) missing from the incoming set; each one is a relaxation.</summary>
    private static List<string> RemovedRequiredFilterColumns(ImmutableArray<string> stored, ImmutableArray<string> incoming)
    {
        var incomingSet = new HashSet<string>(incoming.IsDefault ? ImmutableArray<string>.Empty : incoming, StringComparer.OrdinalIgnoreCase);
        return (stored.IsDefault ? ImmutableArray<string>.Empty : stored).Where(c => !incomingSet.Contains(c)).ToList();
    }

    /// <summary>
    /// Required filter column groups are alternatives (any group satisfies the requirement). Relaxations: a stored group that is
    /// missing from the incoming groups (also when only its columns changed), and, when groups are stored, an incoming group that
    /// is not at least as strict as (a superset of) a stored group, because it adds a weaker alternative.
    /// </summary>
    private static List<string> RelaxedRequiredFilterColumnGroups(ImmutableArray<ImmutableArray<string>> stored, ImmutableArray<ImmutableArray<string>> incoming)
    {
        static List<(string Text, HashSet<string> Set)> ToSets(ImmutableArray<ImmutableArray<string>> groups) =>
            (groups.IsDefault ? ImmutableArray<ImmutableArray<string>>.Empty : groups)
                .Select(g => g.IsDefault ? ImmutableArray<string>.Empty : g)
                .Select(g => (string.Join(", ", g), new HashSet<string>(g, StringComparer.OrdinalIgnoreCase)))
                .ToList();

        var storedSets = ToSets(stored);
        var incomingSets = ToSets(incoming);
        var result = new List<string>();
        foreach (var (text, set) in storedSets)
        {
            if (!incomingSets.Any(i => i.Set.SetEquals(set)))
            {
                result.Add($"removing required filter column group [{text}]");
            }
        }

        if (storedSets.Count > 0)
        {
            foreach (var (text, set) in incomingSets)
            {
                if (!storedSets.Any(s => set.IsSupersetOf(s.Set)))
                {
                    result.Add($"adding the weaker required filter column group [{text}]");
                }
            }
        }

        return result;
    }

    private static ImmutableArray<string>? ParseJsonRequiredFilterColumns(JsonElement item)
    {
        if (!item.TryGetProperty("required_filter_columns", out var prop) &&
            !item.TryGetProperty("requiredFilterColumns", out prop))
        {
            return null;
        }

        if (prop.ValueKind == JsonValueKind.Array)
        {
            var list = new List<string>();
            foreach (var el in prop.EnumerateArray())
            {
                var s = el.GetString();
                if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
            }
            return list.ToImmutableArray();
        }

        if (prop.ValueKind == JsonValueKind.String)
        {
            var val = prop.GetString();
            if (string.IsNullOrWhiteSpace(val)) return ImmutableArray<string>.Empty;
            return val.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToImmutableArray();
        }

        return ImmutableArray<string>.Empty;
    }

    private static ImmutableArray<ImmutableArray<string>>? ParseJsonRequiredFilterColumnGroups(JsonElement item)
    {
        if (!item.TryGetProperty("required_filter_column_groups", out var prop) &&
            !item.TryGetProperty("requiredFilterColumnGroups", out prop))
        {
            return null;
        }

        if (prop.ValueKind == JsonValueKind.Array)
        {
            var outer = new List<ImmutableArray<string>>();
            foreach (var groupEl in prop.EnumerateArray())
            {
                if (groupEl.ValueKind == JsonValueKind.Array)
                {
                    var inner = new List<string>();
                    foreach (var el in groupEl.EnumerateArray())
                    {
                        var s = el.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) inner.Add(s);
                    }
                    outer.Add(inner.ToImmutableArray());
                }
            }
            return outer.ToImmutableArray();
        }

        return ImmutableArray<ImmutableArray<string>>.Empty;
    }
}
