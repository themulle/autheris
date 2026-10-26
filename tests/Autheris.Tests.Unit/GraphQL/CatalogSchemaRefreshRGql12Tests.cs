using Autheris.Tests.Common;
namespace Autheris.Tests.Unit.GraphQL;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.GraphQL.Catalog;
using HotChocolate;
using HotChocolate.Execution;
using HotChocolate.Types;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

/// <summary>
/// R-GQL-12: catalog changes reach the GraphQL schema without a restart, and an unreachable governance database at
/// schema build time no longer takes down the GraphQL endpoint.
/// </summary>
public sealed class CatalogSchemaRefreshRGql12Tests
{
    public sealed class PingQuery
    {
        public string Ping() => "pong";
    }

    private static TableMetadata Table(string name) => new()
    {
        Identifier = new TableIdentifier("sales", "public", name),
        Table = new Table { TableName = name, SchemaName = "public", SourceType = "PostgreSql", IsActive = true },
        Columns = [new TableColumn { ColumnName = "id", DataType = "int" }]
    };

    private static (ITableMetadataRepository Meta, ITableRelationRepository Rel) Repos()
    {
        var meta = Substitute.For<ITableMetadataRepository>();
        var rel = Substitute.For<ITableRelationRepository>();
        rel.GetRelationsForTableAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns([]);
        return (meta, rel);
    }

    [Fact]
    public async Task CatalogChange_RaisesTypesChanged_UnchangedCatalog_DoesNot()
    {
        var (meta, rel) = Repos();
        meta.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(new List<TableMetadata> { Table("orders") });
        using var module = new CatalogGraphQlTypeModule(meta, rel);
        var raised = 0;
        module.TypesChanged += (_, _) => raised++;

        await new ServiceCollection().AddGraphQLServer().AddQueryType<PingQuery>().AddTypeModule(_ => module).BuildRequestExecutorAsync();

        (await module.CheckForCatalogChangesAsync()).ShouldBeFalse();
        meta.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(new List<TableMetadata> { Table("orders"), Table("invoices") });
        (await module.CheckForCatalogChangesAsync()).ShouldBeTrue();
        raised.ShouldBe(1);
    }

    [Fact]
    public async Task UnreachableCatalog_ServesSchemaWithoutCatalogTables_AndRecovers()
    {
        var (meta, rel) = Repos();
        meta.GetAllTablesAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new TimeoutException("governance db down"));
        using var module = new CatalogGraphQlTypeModule(meta, rel);

        var executor = await new ServiceCollection().AddGraphQLServer().AddQueryType<PingQuery>().AddTypeModule(_ => module).BuildRequestExecutorAsync();
        await using var result = await executor.ExecuteAsync("{ ping }");
        result.ToJson().ShouldContain("pong");

        meta.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(new List<TableMetadata> { Table("orders") });
        (await module.CheckForCatalogChangesAsync()).ShouldBeTrue();
    }

    private static async Task<IRequestExecutor> WaitForRebuildAsync(IRequestExecutorProvider provider, ICatalogSchemaDiagnostics diagnostics, int expectedOmissions)
    {
        // HotChocolate evicts and rebuilds the executor asynchronously after TypesChanged.
        var executor = await provider.GetExecutorAsync();
        try
        {
            await WaitHelper.WaitUntil(
                async () =>
                {
                    if (diagnostics.OmittedTables.Count == expectedOmissions)
                    {
                        return true;
                    }

                    executor = await provider.GetExecutorAsync();
                    return diagnostics.OmittedTables.Count == expectedOmissions;
                },
                TimeSpan.FromSeconds(10),
                pollInterval: TimeSpan.FromMilliseconds(25));
        }
        catch (TimeoutException)
        {
            // fall through: the caller asserts on the returned executor
        }

        return executor;
    }

    private static TableMetadata ColumnlessTable(string name) => new()
    {
        Identifier = new TableIdentifier("sales", "public", name),
        Table = new Table { TableName = name, SchemaName = "public", SourceType = "PostgreSql", IsActive = true },
        Columns = []
    };

    [Fact]
    public async Task Refresh_ColumnlessTableAddedAtRuntime_RaisesTypesChangedAndRebuiltExecutorServesValidTable()
    {
        var (meta, rel) = Repos();
        meta.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(new List<TableMetadata> { Table("orders") });
        using var module = new CatalogGraphQlTypeModule(meta, rel);
        var raised = 0;
        module.TypesChanged += (_, _) => raised++;
        var provider = new ServiceCollection().AddGraphQLServer().AddQueryType<PingQuery>().AddTypeModule(_ => module).Services.BuildServiceProvider();
        var resolver = provider.GetRequiredService<IRequestExecutorProvider>();
        await resolver.GetExecutorAsync();
        ((ICatalogSchemaDiagnostics)module).OmittedTables.ShouldBeEmpty();

        meta.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(new List<TableMetadata> { Table("orders"), ColumnlessTable("empty") });
        (await module.CheckForCatalogChangesAsync()).ShouldBeTrue();
        raised.ShouldBe(1);

        var rebuilt = await WaitForRebuildAsync(resolver, (ICatalogSchemaDiagnostics)module, expectedOmissions: 1);
        rebuilt.Schema.Types.ContainsName("sales_public_orders").ShouldBeTrue();
        rebuilt.Schema.Types.ContainsName("sales_public_empty").ShouldBeFalse();
        var omitted = ((ICatalogSchemaDiagnostics)module).OmittedTables.ShouldHaveSingleItem();
        omitted.Reason.ShouldBe(CatalogTableOmissionReason.NoQueryableColumns);
        // Same catalog again: no further rebuild trigger (no rebuild loop).
        (await module.CheckForCatalogChangesAsync()).ShouldBeFalse();
    }

    [Fact]
    public async Task Diagnostics_AfterBuild_ReportsOmittedTables()
    {
        var (meta, rel) = Repos();
        meta.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(new List<TableMetadata> { Table("orders"), ColumnlessTable("empty") });
        using var module = new CatalogGraphQlTypeModule(meta, rel);

        await new ServiceCollection().AddGraphQLServer().AddQueryType<PingQuery>().AddTypeModule(_ => module).BuildRequestExecutorAsync();

        var omitted = ((ICatalogSchemaDiagnostics)module).OmittedTables.ShouldHaveSingleItem();
        omitted.Table.TableName.ShouldBe("empty");
        omitted.Reason.ShouldBe(CatalogTableOmissionReason.NoQueryableColumns);
    }

    [Fact]
    public async Task Diagnostics_CatalogUnreachable_ReportsEmptyList()
    {
        var (meta, rel) = Repos();
        meta.GetAllTablesAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new TimeoutException("down"));
        using var module = new CatalogGraphQlTypeModule(meta, rel);

        await new ServiceCollection().AddGraphQLServer().AddQueryType<PingQuery>().AddTypeModule(_ => module).BuildRequestExecutorAsync();

        ((ICatalogSchemaDiagnostics)module).OmittedTables.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Diagnostics_Snapshot_NotMutatedByLaterBuild()
    {
        // SEC-BUG157-07 / T-BUG157-S12
        var (meta, rel) = Repos();
        meta.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(new List<TableMetadata> { Table("orders"), ColumnlessTable("empty") });
        using var module = new CatalogGraphQlTypeModule(meta, rel);
        var provider = new ServiceCollection().AddGraphQLServer().AddQueryType<PingQuery>().AddTypeModule(_ => module).Services.BuildServiceProvider();
        var resolver = provider.GetRequiredService<IRequestExecutorProvider>();
        await resolver.GetExecutorAsync();
        var diagnostics = (ICatalogSchemaDiagnostics)module;
        var first = diagnostics.OmittedTables;
        var firstCopy = first.ToArray();

        meta.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(new List<TableMetadata> { Table("orders"), ColumnlessTable("empty"), ColumnlessTable("empty2") });
        (await module.CheckForCatalogChangesAsync()).ShouldBeTrue();
        await WaitForRebuildAsync(resolver, diagnostics, expectedOmissions: 2);

        first.ToArray().ShouldBe(firstCopy);
        first.Count.ShouldBe(1);
        diagnostics.OmittedTables.Count.ShouldBe(2);
        ReferenceEquals(first, diagnostics.OmittedTables).ShouldBeFalse();
    }
}
