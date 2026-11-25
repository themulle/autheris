namespace Autheris.Tests.Unit.GraphQL;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Catalog;
using Autheris.GraphQL.Federation;
using Autheris.GraphQL.Interceptors;
using Autheris.Tests.Common;
using HotChocolate;
using HotChocolate.Configuration;
using HotChocolate.Execution;
using HotChocolate.Types;
using HotChocolate.Types.Descriptors.Configurations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

[Collection("CatalogRelation")]
public sealed class CatalogSchemaBuildProgressTests
{
    public sealed class TestUser
    {
        public string Id { get; set; } = "1";
        public string SensitiveSecretEmail { get; set; } = "secret@example.com";
    }

    [GraphQLName("Query")]
    public sealed class TestQuery
    {
        public TestUser User => new();
    }

    private sealed class InspectMiddlewareInterceptor(List<(string TypeName, int MiddlewareCount)> observed) : TypeInterceptor
    {
        public override void OnAfterCompleteType(ITypeCompletionContext completionContext, TypeSystemConfiguration configuration)
        {
            if (configuration is ObjectTypeConfiguration objType &&
                !string.IsNullOrEmpty(objType.Name) &&
                !objType.Name.StartsWith("__", StringComparison.Ordinal))
            {
                var count = objType.Fields.Sum(f => f.MiddlewareConfigurations.Count(m => string.Equals(m.Key, "SubgraphFieldMasking", StringComparison.Ordinal)));
                observed.Add((objType.Name, count));
            }
        }
    }

    private sealed class TimeAdvancingInterceptor(FakeTimeProvider timeProvider) : TypeInterceptor
    {
        private bool _advancedDiscovery;
        private bool _advancedCompletion;

        public override void OnAfterDiscoverTypes()
        {
            if (!_advancedDiscovery)
            {
                _advancedDiscovery = true;
                timeProvider.Advance(TimeSpan.FromSeconds(6));
            }
        }

        public override void OnBeforeCompleteType(ITypeCompletionContext completionContext, TypeSystemConfiguration configuration)
        {
            if (!_advancedCompletion)
            {
                _advancedCompletion = true;
                timeProvider.Advance(TimeSpan.FromSeconds(11));
            }
        }
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 3)]
    public async Task SubgraphFieldMaskingTypeInterceptor_RespectsFederationEnabledOption(bool federationEnabled, int expectedTotalMiddlewareCount)
    {
        var observed = new List<(string TypeName, int MiddlewareCount)>();
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(new GatewayOptions
        {
            Federation = new FederationOptions { Enabled = federationEnabled }
        }));

        await services.AddGraphQLServer()
            .AddQueryType<TestQuery>()
            .TryAddTypeInterceptor<SubgraphFieldMaskingTypeInterceptor>()
            .TryAddTypeInterceptor(_ => new InspectMiddlewareInterceptor(observed))
            .BuildRequestExecutorAsync();

        var total = observed.Where(x => x.TypeName is "Query" or "TestUser").Sum(x => x.MiddlewareCount);
        total.ShouldBe(expectedTotalMiddlewareCount);
    }

    [Fact]
    public async Task ProgressInterceptor_DoesNotLog_WhenSchemaBuildCompletesBelowThreshold()
    {
        var time = new FakeTimeProvider();
        var log = new CapturingLogger<CatalogGraphQlTypeModule>();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton<ILogger<CatalogGraphQlTypeModule>>(log);

        await services.AddGraphQLServer()
            .AddQueryType<TestQuery>()
            .TryAddTypeInterceptor<CatalogSchemaBuildProgressTypeInterceptor>()
            .BuildRequestExecutorAsync();

        log.Messages.ShouldNotContain(m => m.Contains("GraphQL schema compilation", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProgressInterceptor_LogsThrottledProgressWithoutCatalogNames_WhenSchemaBuildExceedsThreshold()
    {
        var time = new FakeTimeProvider();
        var log = new CapturingLogger<CatalogGraphQlTypeModule>();
        var meta = MakeTable("sales", "orders", "secret_col");
        var snapshotProvider = new StaticSnapshotProvider([meta], new CatalogVersion(1, 1));
        var module = new CatalogGraphQlTypeModule(
            new CountingMetadataRepo([meta]),
            new EmptyRelationRepo(),
            logger: log,
            timeProvider: time,
            snapshotProvider: snapshotProvider);

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton<ILogger<CatalogGraphQlTypeModule>>(log);

        await services.AddGraphQLServer()
            .AddQueryType<TestQuery>()
            .AddTypeModule(_ => module)
            .TryAddTypeInterceptor(_ => new TimeAdvancingInterceptor(time))
            .TryAddTypeInterceptor<CatalogSchemaBuildProgressTypeInterceptor>()
            .BuildRequestExecutorAsync();

        var compilationLogs = log.Messages.Where(m => m.Contains("GraphQL schema compilation", StringComparison.Ordinal)).ToList();
        compilationLogs.ShouldNotBeEmpty();
        compilationLogs.ShouldContain(m => m.Contains("managedMemoryMb=", StringComparison.Ordinal) && m.Contains("gen2Collections=", StringComparison.Ordinal));
        compilationLogs.ShouldContain(m => m.Contains("completedTypes=", StringComparison.Ordinal) && m.Contains("totalTypes=", StringComparison.Ordinal));
        foreach (var msg in compilationLogs)
        {
            msg.ShouldNotContain("orders", Case.Insensitive);
            msg.ShouldNotContain("secret_col", Case.Insensitive);
            msg.ShouldNotContain("sales", Case.Insensitive);
        }
    }

    [Fact]
    public async Task TypeModule_ReusesSnapshotProviderOnStartup_AttachesRootFieldFeatures_AndReadsRepoOnForcedCheck()
    {
        var t1 = MakeTable("sales", "orders", "amount");
        var t2 = MakeTable("sales", "customers", "email");
        var repo = new CountingMetadataRepo([t1]);
        var snapshotProvider = new StaticSnapshotProvider([t1], new CatalogVersion(4, 2));
        var versionProvider = new FixedVersionProvider(new CatalogVersion(4, 2));

        using var module = new CatalogGraphQlTypeModule(
            repo,
            new EmptyRelationRepo(),
            versionProvider: versionProvider,
            timeProvider: new FakeTimeProvider(),
            snapshotProvider: snapshotProvider);

        var executor = await new ServiceCollection()
            .AddGraphQLServer()
            .AddQueryType<TestQuery>()
            .AddTypeModule(_ => module)
            .BuildRequestExecutorAsync();

        repo.WholeCatalogReads.ShouldBe(0);
        snapshotProvider.GetCalls.ShouldBe(1);

        var queryType = executor.Schema.QueryType;
        queryType.Fields.ContainsName("sales_public_orders").ShouldBeTrue();
        queryType.Fields.ContainsName("sales_public_ordersConnection").ShouldBeTrue();
        queryType.Fields.ContainsName("sales_public_orders_aggregate").ShouldBeTrue();
        foreach (var fieldName in new[] { "sales_public_orders", "sales_public_ordersConnection", "sales_public_orders_aggregate" })
        {
            var field = queryType.Fields[fieldName];
            field.Features.Get<TransportSourceFeature>().ShouldNotBeNull();
            field.Features.Get<CatalogRootFieldFeature>().ShouldNotBeNull();
        }

        repo.SetTables([t1, t2]);
        var changed = await module.CheckForCatalogChangesAsync(fromTimer: false, forceFull: true);
        changed.ShouldBeTrue();
        repo.WholeCatalogReads.ShouldBe(1);
    }

    private static TableMetadata MakeTable(string domain, string name, string col) => new()
    {
        Identifier = new TableIdentifier(domain, "public", name),
        Table = new Table
        {
            Id = Guid.NewGuid(),
            SourceName = $"{domain}_pg",
            SourceType = "PostgreSql",
            SchemaName = "public",
            TableName = name,
            IsActive = true
        },
        Columns =
        [
            new TableColumn { Id = Guid.NewGuid(), ColumnName = "id", DataType = "integer" },
            new TableColumn { Id = Guid.NewGuid(), ColumnName = col, DataType = "text" }
        ],
        PrimaryKeyColumns = ["id"]
    };

    private sealed class StaticSnapshotProvider(IReadOnlyList<TableMetadata> tables, CatalogVersion version) : ICatalogSnapshotProvider
    {
        private int _getCalls;
        public int GetCalls => Volatile.Read(ref _getCalls);
        public CatalogSnapshot CurrentSnapshot => CatalogSnapshot.Create(version, tables, []);
        public bool IsInitialized => true;
        public bool IsDegraded => false;

        public ValueTask<CatalogSnapshot> GetSnapshotAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _getCalls);
            return ValueTask.FromResult(CurrentSnapshot);
        }

        public Task RefreshAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void SignalChange() { }
    }

    private sealed class CountingMetadataRepo(IReadOnlyList<TableMetadata> initial) : ITableMetadataRepository
    {
        private IReadOnlyList<TableMetadata> _tables = initial;
        private int _wholeCatalogReads;
        public int WholeCatalogReads => Volatile.Read(ref _wholeCatalogReads);
        public void SetTables(IReadOnlyList<TableMetadata> tables) => _tables = tables;

        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _wholeCatalogReads);
            return Task.FromResult(_tables);
        }

        public Task<CatalogTablePage> QueryTablesAsync(CatalogTableQuery query, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _wholeCatalogReads);
            return Task.FromResult(new CatalogTablePage(_tables.ToList(), null));
        }

        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default)
            => Task.FromResult(_tables.FirstOrDefault(t => t.Identifier.Equals(table)));

        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default)
            => Task.FromResult(metadata);

        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(
            TableIdentifier table, IReadOnlyList<string> primaryKey, IReadOnlyList<TableIndexDefinition> indexes,
            IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class EmptyRelationRepo : ITableRelationRepository
    {
        public bool BulkRelationsAuthoritative => true;
        public Task<IReadOnlyList<TableRelation>> GetAllRelationsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TableRelation>>([]);
        public Task<IReadOnlyList<TableRelation>> GetRelationsForTableAsync(TableIdentifier parentTable, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TableRelation>>([]);
        public Task<RelationWriteResult> CreateRelationAsync(TableRelation relation, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> SetRelationCrossDataSourceOptInAsync(Guid relationId, bool allowCrossDataSource, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FixedVersionProvider(CatalogVersion version) : ICatalogVersionProvider
    {
        public ValueTask<CatalogVersion> GetCatalogVersionAsync(CancellationToken ct = default) => ValueTask.FromResult(version);
        public Task<string> GetVersionAsync(CancellationToken ct = default) => Task.FromResult(version.Formatted);
    }
}
