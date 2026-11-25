namespace Autheris.Modules.Dbt;

using Autheris.Application.Governance.Ownership;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Catalog.Semantic;
using Autheris.Application.Governance;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Lineage;
using Autheris.Application.Policy;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

/// <summary>
/// Owner keys in a dbt governance import (#1009, D13): per-table data owners no longer exist, Data Governance assigns owner groups to
/// containers. An import item carrying <c>owner</c>, <c>owners</c>, <c>group</c> or <c>groups</c> rejects the whole import before any write
/// (fail closed); no identifier value ever reaches an error message, a warning or a log line.
/// </summary>
public sealed partial class DbtMetadataIngestionService
{
    /// <summary>Full composition. The audit repository is mandatory here (L-5).</summary>
    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public DbtMetadataIngestionService(
        IDbtProposalRepository proposalRepository,
        ITableMetadataRepository metadataRepository,
        ILineageGraphStore lineageGraphStore,
        IPolicyEpochRepository? epochRepository,
        Microsoft.Extensions.Options.IOptions<Autheris.Domain.Options.GatewayOptions>? gatewayOptions,
        Autheris.Application.Sql.Endpoints.Services.SqlEndpointLoader? sqlEndpointLoader,
        ITableRelationRepository? relationRepository,
        VirtualFilterAdministrationService? virtualFilterAdmin,
        Autheris.Application.Governance.Interfaces.IDatasetTagImportService? tagImport,
        Autheris.Application.Catalog.Discovery.IDiscoveryArtifactStore? artifactStore,
        IDbtTestTargetMap? testTargetMap,
        ILogger<DbtMetadataIngestionService> logger,
        IAuditLogRepository auditLog,
        IAuditWriter? auditWriter = null,
        IContainerOwnership? ownershipRepo = null,
        Autheris.Application.Catalog.ICatalogWriteScopeFactory? catalogWriteScopeFactory = null,
        IClassificationRepository? classificationRepository = null,
        Microsoft.Extensions.Options.IOptions<Autheris.Application.Catalog.Services.CatalogDiscoveryOptions>? discoveryOptions = null,
        TimeProvider? timeProvider = null,
        IGoldenQueryRepository? goldenQueryRepository = null,
        Autheris.Application.Governance.Notifications.IGovernanceNotificationDispatcher? notificationDispatcher = null)
        : this(proposalRepository, metadataRepository, lineageGraphStore, epochRepository, gatewayOptions, sqlEndpointLoader, relationRepository,
            virtualFilterAdmin, tagImport, artifactStore, testTargetMap, auditWriter, logger)
    {
        _goldenQueryRepository = goldenQueryRepository;
        _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
        _ownershipRepo = ownershipRepo;
        _catalogWriteScopes = catalogWriteScopeFactory;
        _classificationRepository = classificationRepository;
        _discoveryOptions = discoveryOptions;
        _notifications = notificationDispatcher;
        if (timeProvider is not null)
        {
            _timeProvider = timeProvider;
        }
    }

    public DbtMetadataIngestionService(
        IDbtProposalRepository proposalRepository,
        ITableMetadataRepository metadataRepository,
        ILineageGraphStore lineageGraphStore,
        IPolicyEpochRepository? epochRepository,
        Microsoft.Extensions.Options.IOptions<Autheris.Domain.Options.GatewayOptions>? gatewayOptions,
        Autheris.Application.Sql.Endpoints.Services.SqlEndpointLoader? sqlEndpointLoader,
        ITableRelationRepository? relationRepository,
        VirtualFilterAdministrationService? virtualFilterAdmin,
        Autheris.Application.Governance.Interfaces.IDatasetTagImportService? tagImport,
        Autheris.Application.Catalog.Discovery.IDiscoveryArtifactStore? artifactStore,
        IDbtTestTargetMap? testTargetMap,
        Autheris.Application.Audit.IAuditWriter? auditWriter,
        ILogger<DbtMetadataIngestionService> logger)
    {
        _tagImport = tagImport;
        _artifactStore = artifactStore;
        _testTargetMap = testTargetMap;
        _proposalRepository = proposalRepository ?? throw new ArgumentNullException(nameof(proposalRepository));
        _metadataRepository = metadataRepository ?? throw new ArgumentNullException(nameof(metadataRepository));
        _lineageGraphStore = lineageGraphStore ?? throw new ArgumentNullException(nameof(lineageGraphStore));
        _epochRepository = epochRepository;
        _gatewayOptions = gatewayOptions;
        _sqlEndpointLoader = sqlEndpointLoader;
        _relationRepository = relationRepository;
        _virtualFilterAdmin = virtualFilterAdmin;
        _auditWriter = auditWriter;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Governed rejection code of an import that carries owner keys.</summary>
    internal const string OwnerImportRetiredCode = "dbt_owner_import_disabled";

    private static bool AnyOwnerKeys(JsonElement modelsArray)
    {
        if (modelsArray.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var item in modelsArray.EnumerateArray())
        {
            if (DbtOwnerSpecValidator.HasOwnerKeys(item))
            {
                return true;
            }
        }

        return false;
    }

    private static DbtGovernanceSyncResult OwnerFailure(string code, List<string> warnings) =>
        new(
            Success: false,
            UpdatedTablesCount: 0,
            UpdatedColumnsCount: 0,
            MaskingRulesCount: 0,
            VirtualFiltersCount: 0,
            AccessProfilesCount: 0,
            Warnings: warnings,
            ErrorMessage: $"Governance import rejected: owner keys are not supported ({code}); Data Governance assigns owner groups to containers. " +
                          "Nothing was applied.");
}
