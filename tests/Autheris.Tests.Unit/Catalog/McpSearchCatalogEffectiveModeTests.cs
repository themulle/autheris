namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Analytics;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Search;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.DataSources.Interfaces;
using Autheris.Application.Lineage;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Domain.Tenancy;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

public sealed class McpSearchCatalogEffectiveModeTests
{
    private static readonly McpSessionContext Session = new(
        "s1",
        "agent",
        "tenant-a",
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        UserSid: "S-1-USER",
        Roles: ["GovernanceAdmin"]);

    private static McpToolDefinition SearchTool => new(
        McpDatasetTools.SearchCatalog,
        McpDatasetTools.SearchCatalog,
        "{}",
        TargetGraphQLOperation: McpDatasetTools.SearchCatalog);

    [Fact]
    public async Task SearchCatalog_OutputsEffectiveMode()
    {
        var tableId = new TableIdentifier("sales", "public", "orders");
        var hit = new CatalogSearchHit(
            TableIdentifier: tableId,
            DisplayName: "Orders",
            Description: "Customer orders",
            Domain: "sales",
            Sensitivity: "NORMAL",
            CombinedScore: 0.9,
            Bm25Score: 0.9,
            VectorScore: null,
            MatchedTerms: ["orders"],
            RelevantColumns: ["id"],
            RelatedJoinPaths: [],
            SuggestedGraphQlField: null,
            EffectiveMode: "keyword");

        var logger = new RecordingLogger<McpToolExecutionHandler>();
        var handler = new McpToolExecutionHandler(
            new UnusedSqlExecutionService(),
            new UnusedDataQueryService(),
            new StubCatalogDiscoveryService([hit]),
            new UnusedApiDispatcherService(),
            new UnusedLineageGraphStore(),
            new EmptyTableMetadataRepository(),
            searchEngine: new StubSearchEngine("keyword"),
            logger: logger);

        var json = await handler.ExecuteToolAsync(
            SearchTool,
            """{"query":"orders","mode":"hybrid"}""",
            Session,
            CancellationToken.None);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("mode", out var modeProp).ShouldBeTrue();
        modeProp.GetString().ShouldBe("keyword");

        logger.DebugEntries.ShouldContain(msg =>
            msg.Contains("hybrid", StringComparison.OrdinalIgnoreCase) &&
            msg.Contains("keyword", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SearchCatalog_EmptyHits_UsesEngineEffectiveMode()
    {
        var logger = new RecordingLogger<McpToolExecutionHandler>();
        var handler = new McpToolExecutionHandler(
            new UnusedSqlExecutionService(),
            new UnusedDataQueryService(),
            new StubCatalogDiscoveryService([]),
            new UnusedApiDispatcherService(),
            new UnusedLineageGraphStore(),
            new EmptyTableMetadataRepository(),
            searchEngine: new StubSearchEngine("hybrid"),
            logger: logger);

        var json = await handler.ExecuteToolAsync(
            SearchTool,
            """{"query":"nonexistent","mode":"hybrid"}""",
            Session,
            CancellationToken.None);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("mode", out var modeProp).ShouldBeTrue();
        modeProp.GetString().ShouldBe("hybrid");
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> DebugEntries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel == LogLevel.Debug;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Debug)
            {
                DebugEntries.Add(formatter(state, exception));
            }
        }
    }

    private sealed class StubSearchEngine(string effectiveMode) : ICatalogSearchEngine
    {
        public bool IsIndexReady => true;
        public int IndexedTableCount => 1;
        public DateTimeOffset? LastIndexedAt => DateTimeOffset.UtcNow;
        public string ResolveEffectiveMode(CatalogSearchMode requestedMode) => effectiveMode;
        public Task RebuildIndexAsync(IReadOnlyList<TableMetadata> tables, CancellationToken ct = default) => Task.CompletedTask;
        public IReadOnlyList<CatalogSearchHit> Search(CatalogSearchQuery query, Func<TableIdentifier, bool> isVisiblePredicate, Func<TableIdentifier, bool>? isColumnRestricted = null) => [];
        public Task<IReadOnlyList<CatalogSearchHit>> SearchAsync(CatalogSearchQuery query, Func<TableIdentifier, bool> isVisiblePredicate, Func<TableIdentifier, bool>? isColumnRestricted = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CatalogSearchHit>>([]);
    }

    private sealed class StubCatalogDiscoveryService(IReadOnlyList<CatalogSearchHit> hits) : ICatalogDiscoveryService
    {
        public Task<IReadOnlyList<CatalogDatasetSummary>> ListDatasetsAsync(RequestContext context, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CatalogDatasetSummary>>([]);
        public Task<Autheris.Application.Catalog.Discovery.DiscoveryFacets> ListFacetsAsync(RequestContext context, CancellationToken ct = default) =>
            Task.FromResult(new Autheris.Application.Catalog.Discovery.DiscoveryFacets(0, []));
        public Task<CatalogDatasetDetail?> GetDatasetDetailAsync(TableIdentifier table, RequestContext context, CancellationToken ct = default) =>
            Task.FromResult<CatalogDatasetDetail?>(null);
        public Task<IReadOnlyList<CatalogDatasetSummary>> SearchCatalogAsync(string query, string? domain, RequestContext context, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CatalogDatasetSummary>>([]);
        public Task<IReadOnlyList<CatalogSearchHit>> SearchCatalogDetailedAsync(CatalogSearchQuery query, RequestContext context, CancellationToken ct = default) =>
            Task.FromResult(hits);
        public Task<IReadOnlyList<CatalogDatasourceSummary>> ListDatasourcesAsync(RequestContext context, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CatalogDatasourceSummary>>([]);
        public Task<DatasourceRegistrationResult> RegisterDatasourceAsync(DatasourceRegistrationRequest request, RequestContext context, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class UnusedSqlExecutionService : IGovernedSqlExecutionService
    {
        public Task<GovernedSqlResult> ExecuteQueryBufferedAsync(GovernedSqlQueryRequest request, ClaimsPrincipal user, TenantId tenantId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task ValidateAsync(string rawSql, ClaimsPrincipal user, TenantId tenantId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class UnusedDataQueryService : IGovernedDataQueryService
    {
        public Task<DatasetQueryEnvelope> ExecuteQueryAsync(DatasetQueryRequest request, RequestContext context, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class UnusedApiDispatcherService : IApiDispatcherService
    {
        public Task<string> DescribeApiAsync(string endpoint, string? method = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<string> InvokeApiAsync(
            string endpoint,
            string method,
            IReadOnlyDictionary<string, object?>? parameters = null,
            object? body = null,
            RequestContext? context = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<string> GetOpenApiJsonAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> GetEndpointsDocumentationAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> GetMcpToolsDocumentationAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class UnusedLineageGraphStore : ILineageGraphStore
    {
        public int Count => 0;
        public LineageNode? GetNode(string nodeId) => null;
        public bool ContainsNode(string nodeId) => false;
        public void UpdateGraph(IEnumerable<LineageNode> nodes) => throw new NotSupportedException();
        public IReadOnlyCollection<LineageNode> GetAllNodes() => [];
    }

    private sealed class EmptyTableMetadataRepository : ITableMetadataRepository
    {
        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default) => Task.FromResult<TableMetadata?>(null);
        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TableMetadata>>([]);
        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default) => Task.FromResult(metadata);
        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(
            TableIdentifier table,
            IReadOnlyList<string> primaryKey,
            IReadOnlyList<TableIndexDefinition> indexes,
            IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null,
            CancellationToken ct = default) => Task.FromResult<TableKeyDefinitionChange?>(null);
    }
}
