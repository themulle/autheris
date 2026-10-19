using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Autheris.Tests.Governance;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance;

/// <summary>Issue #111 persistence contract on SQLite (see <see cref="CatalogSettingsRepositoryContractTestsBase"/>).</summary>
public sealed class SqliteCatalogSettingsRepositoryContractTests : CatalogSettingsRepositoryContractTestsBase
{
    private static readonly Sid Admin = new("S-1-5-21-admin");
    private readonly string _connectionString = $"Data Source=catalogsettings_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
    private readonly List<SqliteGovernanceRepository> _repos = [];

    private SqliteGovernanceRepository Create(DatasetVisibility newTableDefault)
    {
        var options = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions { ConnectionString = _connectionString, SeedDemoData = false },
            Catalog = new DataCatalogOptions { DefaultVisibility = newTableDefault }
        });
        var repo = GovernanceStoreFactory.CreateSqlite(Substitute.For<IEpochValidationService>(), options);
        _repos.Add(repo);
        return repo;
    }

    protected override Task<IGovernanceRepository?> CreateRepositoryAsync() =>
        Task.FromResult<IGovernanceRepository?>(Create(DatasetVisibility.Hidden));

    protected override Task<IGovernanceRepository?> CreateAdditionalRepositoryAsync(DatasetVisibility newTableDefault = DatasetVisibility.Hidden) =>
        Task.FromResult<IGovernanceRepository?>(Create(newTableDefault));

    protected override async Task ExecuteRawAsync(string sql)
    {
        await using var connection = await _repos[0].Connections.OpenAsync(CancellationToken.None);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    protected override Task DisposeRepositoryAsync()
    {
        foreach (var repo in _repos)
        {
            repo.Dispose();
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task T_R2_6_FullTier_NewDenyMaskingRule_AppliesOnlyAfterTheCachedEntryIsInvalidated_ResidualPinned()
    {
        var id = new TableIdentifier("Sales", "Crm", "deny_" + Guid.NewGuid().ToString("N")[..8]);
        var table = new Table { Id = Guid.NewGuid(), SourceName = id.Domain, SchemaName = id.Schema, TableName = id.TableName, DisplayName = id.TableName, IsActive = true };
        var columns = new[]
        {
            new TableColumn { TableId = table.Id, ColumnName = "id", DataType = "INT" },
            new TableColumn { TableId = table.Id, ColumnName = "ssn", DataType = "TEXT" }
        };
        await Repo.UpsertTableMetadataAsync(new TableMetadata { Identifier = id, Table = table, Columns = columns });
        await Repo.SetDatasetVisibilityAsync(id, DatasetVisibility.Full, Admin);
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

        ((SqliteGovernanceRepository)Repo).InvalidateMetadataCacheForTesting();
        Names((await Repo.GetTableMetadataAsync(id))!).ShouldBe(["id"]);
    }
}
