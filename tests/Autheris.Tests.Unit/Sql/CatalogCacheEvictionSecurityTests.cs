using Autheris.Application.Common.Interfaces;
using Autheris.Application.Sql.Catalog;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence.Catalog;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;

namespace Autheris.Tests.Unit.Sql;

/// <summary>Issue #678: eviction never weakens the governance validation; a stale entry is never served.</summary>
public sealed class CatalogCacheEvictionSecurityTests
{
    private const string Source = "ds";
    private static readonly TableIdentifier Orders = new("sales", "dbo", "orders");

    private static (GovernedCatalogProvider Sut, Func<string, string> SetColumn) Build(int maxEntries)
    {
        var column = "id";
        var repo = Substitute.For<IGovernanceRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var id = call.Arg<TableIdentifier>();
            return new TableMetadata
            {
                Table = new Table { SourceType = "SqlServer", SourceName = Source, SchemaName = id.Schema, TableName = id.TableName, DataSourceType = DataSourceType.Sql },
                Identifier = id,
                Columns = [new TableColumn { ColumnName = column, DataType = "int" }]
            };
        });
        var gateway = new GatewayOptions
        {
            Catalog = new DataCatalogOptions { Cache = new CatalogCacheOptions { ProviderMaxEntries = maxEntries } },
            DataSources = new SqlDataSourceOptions
            {
                Connections = { [Source] = new DataSourceConnectionOptions { Provider = "SqlServer", ConnectionString = "x" } }
            }
        };
        var sut = new GovernedCatalogProvider(repo, Options.Create(gateway), new GovernedCatalogOptions());
        return (sut, c => column = c);
    }

    private static async Task<string[]> Columns(GovernedCatalogProvider sut, TableIdentifier table)
    {
        var catalog = await sut.GetCatalogAsync(Source, [table], CancellationToken.None);
        var entry = catalog.Resolve(new SqlQualifiedName([new SqlIdentifier(table.Schema), new SqlIdentifier(table.TableName)]));
        return entry is null ? [] : [.. entry.Columns.Select(c => c.Name)];
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task EvictedEntry_NeverServedWithStaleEpoch()
    {
        var (sut, setColumn) = Build(3);
        (await Columns(sut, Orders)).ShouldBe(["id"]);
        for (var i = 0; i < 10; i++)
        {
            await Columns(sut, new TableIdentifier("sales", "dbo", $"filler{i}"));
        }

        setColumn("renamed");

        (await Columns(sut, Orders)).ShouldBe(["renamed"]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task StaleEpochAfterRemoteWrite_IsReloaded()
    {
        var (sut, setColumn) = Build(100);
        (await Columns(sut, Orders)).ShouldBe(["id"]);

        // A write by another node changes the governance record; the still-cached entry must fail its fingerprint check.
        setColumn("renamed");

        (await Columns(sut, Orders)).ShouldBe(["renamed"]);
    }
}
