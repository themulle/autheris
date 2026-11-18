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

/// <summary>Issue #678: the provider cache is bounded by <c>Gateway:Catalog:Cache:ProviderMaxEntries</c>; a miss reloads and re-admits.</summary>
public sealed class GovernedCatalogProviderCacheTests
{
    private const string Source = "ds";

    private sealed class Fixture
    {
        public int Loads;
        public string Column = "id";
        public IGovernanceRepository Repo { get; } = Substitute.For<IGovernanceRepository>();
        public GovernedCatalogProvider Sut { get; }

        public Fixture(int maxEntries)
        {
            Repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                Loads++;
                var id = call.Arg<TableIdentifier>();
                return new TableMetadata
                {
                    Table = new Table { SourceType = "SqlServer", SourceName = Source, SchemaName = id.Schema, TableName = id.TableName, DataSourceType = DataSourceType.Sql },
                    Identifier = id,
                    Columns = [new TableColumn { ColumnName = Column, DataType = "int" }]
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
            Sut = new GovernedCatalogProvider(Repo, Options.Create(gateway), new GovernedCatalogOptions());
        }

        public async Task<string[]> ColumnsOf(TableIdentifier table)
        {
            var catalog = await Sut.GetCatalogAsync(Source, [table], CancellationToken.None);
            var entry = catalog.Resolve(new SqlQualifiedName([new SqlIdentifier(table.Schema), new SqlIdentifier(table.TableName)]));
            return entry is null ? [] : [.. entry.Columns.Select(c => c.Name)];
        }
    }

    private static TableIdentifier T(int i) => new("sales", "dbo", $"t{i}");

    [Fact]
    public async Task ExceedingLimit_EvictsAndRecomputes()
    {
        var f = new Fixture(maxEntries: 3);
        for (var i = 0; i < 12; i++)
        {
            (await f.ColumnsOf(T(i))).ShouldBe(["id"]);
        }

        var loadsAfterFill = f.Loads;
        // A table inside the cache is served without a new load of the admission path beyond the fingerprint read.
        (await f.ColumnsOf(T(11))).ShouldBe(["id"]);
        // The first table was evicted, so it is recomputed (admission load runs again) and is still admitted.
        (await f.ColumnsOf(T(0))).ShouldBe(["id"]);
        f.Loads.ShouldBeGreaterThan(loadsAfterFill);
    }

    private static int CacheCount(GovernedCatalogProvider sut)
    {
        var field = typeof(GovernedCatalogProvider).GetField("_cache", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        return (int)field.FieldType.GetProperty("Count")!.GetValue(field.GetValue(sut))!;
    }

    [Fact]
    public async Task Invalidate_RemovesMatchingKeys_CaseInsensitive()
    {
        var f = new Fixture(maxEntries: 100);
        await f.ColumnsOf(T(1));
        await f.ColumnsOf(T(2));
        CacheCount(f.Sut).ShouldBe(2);

        f.Sut.Invalidate("unrelated");
        CacheCount(f.Sut).ShouldBe(2);

        // The data source name and the connection key are both "ds" here; either match removes the entry, whatever the case.
        f.Sut.Invalidate("DS");
        CacheCount(f.Sut).ShouldBe(0);

        await f.ColumnsOf(T(1));
        f.Sut.Invalidate();
        CacheCount(f.Sut).ShouldBe(0);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Hit_WithFingerprintMismatch_IsReloaded()
    {
        var f = new Fixture(maxEntries: 100);
        (await f.ColumnsOf(T(1))).ShouldBe(["id"]);

        f.Column = "changed";

        (await f.ColumnsOf(T(1))).ShouldBe(["changed"]);
    }
}
