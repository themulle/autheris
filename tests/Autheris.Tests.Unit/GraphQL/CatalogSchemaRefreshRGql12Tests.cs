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
}
