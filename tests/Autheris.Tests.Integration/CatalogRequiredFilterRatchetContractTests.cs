namespace Autheris.Tests.Integration;

using System;
using System.Collections.Immutable;
using System.Threading.Tasks;
using Autheris.Application.DataCatalog.Services;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// EXT-11: required-filter columns persisted by a governance store survive the catalog governance ratchet merge
/// (a catalog sync that does not know the field) on a real PostgreSQL / SQL Server. Without Docker the tests return early.
/// </summary>
public abstract class CatalogRequiredFilterRatchetContractTestsBase
{
    protected abstract bool Available { get; }
    protected abstract IGovernanceRepository Repo { get; }
    protected abstract void InvalidateCache();

    private static TableMetadata Meta(TableIdentifier id, string description, ImmutableArray<string> requiredFilters) => new()
    {
        Identifier = id,
        Table = new Table
        {
            SourceType = "sql",
            SourceName = id.Domain,
            SchemaName = id.Schema,
            TableName = id.TableName,
            DisplayName = id.TableName,
            Description = description,
            RequiredFilterColumns = requiredFilters
        },
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "integer" },
            new TableColumn { ColumnName = "tenant_id", DataType = "varchar" }
        ]
    };

    [Fact]
    [Trait("Category", "Security")]
    public async Task EXT_11_CatalogMerge_KeepsRequiredFilterColumns_AcrossPersistenceRoundTrip()
    {
        if (!Available) return;
        var id = new TableIdentifier("sales", "dbo", $"rf_{Guid.NewGuid():N}");
        await Repo.UpsertTableMetadataAsync(Meta(id, "original", ImmutableArray.Create("tenant_id")));
        InvalidateCache();

        var existing = await Repo.GetTableMetadataAsync(id);
        existing.ShouldNotBeNull();
        existing.RequiredFilterColumns.ShouldBe(["tenant_id"]);

        // A catalog sync builds the incoming table without any required-filter columns.
        var incoming = Meta(id, "updated by catalog", ImmutableArray<string>.Empty);
        var merged = CatalogGovernanceRatchet.Merge(incoming, existing);
        await Repo.UpsertTableMetadataAsync(merged);
        InvalidateCache();

        var read = await Repo.GetTableMetadataAsync(id);
        read.ShouldNotBeNull();
        read.RequiredFilterColumns.ShouldBe(["tenant_id"]);
        read.Table.Description.ShouldBe("updated by catalog");
    }
}

public sealed class PostgreSqlCatalogRequiredFilterRatchetContractTests : CatalogRequiredFilterRatchetContractTestsBase, IClassFixture<PostgreSqlCatalogRequiredFilterRatchetContractTests.Fixture>
{
    private readonly Fixture _fixture;

    public PostgreSqlCatalogRequiredFilterRatchetContractTests(Fixture fixture) => _fixture = fixture;

    protected override bool Available => _fixture.Available;
    protected override IGovernanceRepository Repo => _fixture.Repository!;
    protected override void InvalidateCache() => _fixture.Repository!.InvalidateMetadataCacheForTesting();

    public sealed class Fixture : IAsyncLifetime
    {
        private PostgreSqlTestDatabase? _db;
        public bool Available { get; private set; }
        public PostgreSqlGovernanceRepository? Repository { get; private set; }

        public async Task InitializeAsync()
        {
            try
            {
                _db = await PostgreSqlTestDatabase.CreateAsync();
                if (!_db.IsAvailable)
                {
                    return;
                }

                var env = Substitute.For<IHostEnvironment>();
                env.EnvironmentName.Returns("Development");
                var options = Options.Create(new GatewayOptions
                {
                    GovernanceDb = new GovernanceDbOptions { Provider = "PostgreSql", ConnectionString = _db.ConnectionString, EnableOutboxProcessor = false, SeedDemoData = false }
                });
                Repository = GovernanceStoreFactory.CreatePostgreSql(Substitute.For<IEpochValidationService>(), options, env);
                Available = true;
            }
            catch (Exception ex)
            {
                Available = false;
                ContainerStartup.FailIfRequired(ex, "The PostgreSQL test container");
            }
        }

        public async Task DisposeAsync()
        {
            Repository?.Dispose();
            if (_db != null)
            {
                await _db.DisposeAsync();
            }
        }
    }
}

public sealed class SqlServerCatalogRequiredFilterRatchetContractTests : CatalogRequiredFilterRatchetContractTestsBase, IClassFixture<SqlServerCatalogRequiredFilterRatchetContractTests.Fixture>
{
    private readonly Fixture _fixture;

    public SqlServerCatalogRequiredFilterRatchetContractTests(Fixture fixture) => _fixture = fixture;

    protected override bool Available => _fixture.Available;
    protected override IGovernanceRepository Repo => _fixture.Repository!;
    protected override void InvalidateCache() => _fixture.Repository!.InvalidateMetadataCacheForTesting();

    public sealed class Fixture : IAsyncLifetime
    {
        private SqlServerTestDatabase? _db;
        public bool Available { get; private set; }
        public SqlServerGovernanceRepository? Repository { get; private set; }

        public async Task InitializeAsync()
        {
            _db = await SqlServerTestDatabase.CreateAsync();
            if (!_db.IsAvailable)
            {
                return;
            }

            var env = Substitute.For<IHostEnvironment>();
            env.EnvironmentName.Returns("Development");
            var options = Options.Create(new GatewayOptions
            {
                GovernanceDb = new GovernanceDbOptions { Provider = "SqlServer", ConnectionString = _db.ConnectionString, EnableOutboxProcessor = false, SeedDemoData = false }
            });
            Repository = GovernanceStoreFactory.CreateSqlServer(Substitute.For<IEpochValidationService>(), options, env);
            Available = true;
        }

        public async Task DisposeAsync()
        {
            Repository?.Dispose();
            if (_db != null)
            {
                await _db.DisposeAsync();
            }
        }
    }
}
