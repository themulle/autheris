namespace Autheris.Tests.Integration;

using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;
using Shouldly;
using Autheris.Domain.Common;
using System.Linq;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Autheris.Tests.Governance;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Npgsql;

/// <summary>Issue #111 persistence contract of dataset tags and the catalog tier on PostgreSQL.</summary>
public sealed class PostgreSqlCatalogSettingsRepositoryContractTests : CatalogSettingsRepositoryContractTestsBase
{
    private PostgreSqlTestDatabase? _db;
    private readonly List<PostgreSqlGovernanceRepository> _repos = [];

    private PostgreSqlGovernanceRepository Create(DatasetVisibility newTableDefault)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Development");
        var options = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions { Provider = "PostgreSql", ConnectionString = _db!.ConnectionString, SeedDemoData = false },
            Catalog = new DataCatalogOptions { DefaultVisibility = newTableDefault }
        });
        var repo = GovernanceStoreFactory.CreatePostgreSql(Substitute.For<IEpochValidationService>(), options, env);
        _repos.Add(repo);
        return repo;
    }

    protected override async Task<IGovernanceRepository?> CreateRepositoryAsync()
    {
        _db = await PostgreSqlTestDatabase.CreateAsync();
        return _db.IsAvailable ? Create(DatasetVisibility.Hidden) : null;
    }

    protected override Task<IGovernanceRepository?> CreateAdditionalRepositoryAsync(DatasetVisibility newTableDefault = DatasetVisibility.Hidden) =>
        Task.FromResult<IGovernanceRepository?>(Create(newTableDefault));

    protected override async Task ExecuteRawAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(_db!.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    protected override async Task DisposeRepositoryAsync()
    {
        foreach (var repo in _repos)
        {
            await repo.DisposeAsync();
        }

        if (_db != null)
        {
            await _db.DisposeAsync();
        }
    }

    [Fact]
    public async Task T_R2_6_FullTier_NewDenyMaskingRule_AppliesOnlyAfterTheCachedEntryIsInvalidated_ResidualPinned()
    {
        if (!Available)
        {
            return;
        }

        var admin = new Sid("S-1-5-21-admin");
        var id = new TableIdentifier("Sales", "Crm", "deny_" + Guid.NewGuid().ToString("N")[..8]);
        var table = new Table { Id = Guid.NewGuid(), SourceName = id.Domain, SchemaName = id.Schema, TableName = id.TableName, DisplayName = id.TableName, IsActive = true };
        var columns = new[]
        {
            new TableColumn { TableId = table.Id, ColumnName = "id", DataType = "INT" },
            new TableColumn { TableId = table.Id, ColumnName = "ssn", DataType = "TEXT" }
        };
        await Repo.UpsertTableMetadataAsync(new TableMetadata { Identifier = id, Table = table, Columns = columns });
        await Repo.SetDatasetVisibilityAsync(id, DatasetVisibility.Full, admin);
        var warm = (await Repo.GetTableMetadataAsync(id))!; // structural entry (incl. masking rules) is now cached
        var ssn = warm.Columns.Single(c => c.ColumnName == "ssn");

        await ExecuteRawAsync(
            $"INSERT INTO COLUMN_MASKING_RULES (id, table_column_id, rule_type) VALUES ('{Guid.NewGuid()}', '{ssn.Id}', 'DENY')");

        var shared = new[] { id.ToString() };
        IReadOnlyList<string> Names(TableMetadata m) => Autheris.Application.Services.CatalogVisibility
            .FilterForSubject([m], [], new TenantId("t"), null, Autheris.Application.Services.CatalogSurface.Discovery, shared)
            .Single().Columns.Select(c => c.ColumnName).ToList();

        // Accepted residual (A-6): within the 30 s cache TTL the freshly denied column name is still part of the Full view of the cached entry.
        Names((await Repo.GetTableMetadataAsync(id))!).ShouldBe(["id", "ssn"]);

        ((PostgreSqlGovernanceRepository)Repo).InvalidateMetadataCacheForTesting();
        Names((await Repo.GetTableMetadataAsync(id))!).ShouldBe(["id"]);
    }
}
