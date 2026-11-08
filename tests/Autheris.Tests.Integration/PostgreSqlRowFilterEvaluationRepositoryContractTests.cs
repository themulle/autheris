namespace Autheris.Tests.Integration;

using System.Threading.Tasks;
using Autheris.Domain.Model;
using Autheris.Domain.Common;
using System.Threading;
using Xunit;
using Shouldly;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Autheris.Tests.Governance;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;

/// <summary>Issue #107 persistence contract of the row filter evaluation settings on PostgreSQL.</summary>
public sealed class PostgreSqlRowFilterEvaluationRepositoryContractTests : RowFilterEvaluationRepositoryContractTestsBase
{
    private PostgreSqlTestDatabase? _db;
    private readonly IEpochValidationService _epochs = Substitute.For<IEpochValidationService>();
    private PostgreSqlGovernanceRepository? _repo;

    protected override async Task<IGovernanceRepository?> CreateRepositoryAsync()
    {
        _db = await PostgreSqlTestDatabase.CreateAsync();
        if (!_db.IsAvailable)
        {
            return null;
        }

        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Development");
        var options = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions { Provider = "PostgreSql", ConnectionString = _db.ConnectionString, SeedDemoData = false }
        });
        _repo = GovernanceStoreFactory.CreatePostgreSql(_epochs, options, env);
        return _repo;
    }

    protected override async Task DisposeRepositoryAsync()
    {
        if (_repo != null)
        {
            await _repo.DisposeAsync();
        }

        if (_db != null)
        {
            await _db.DisposeAsync();
        }
    }

    [Fact]
    public async Task ObjectChange_InvalidatesTheEpochAfterCommit()
    {
        if (_repo is null)
        {
            return;
        }

        var id = new TableIdentifier("Sales", "Crm", "epoch_obj");
        await Repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Identifier = id,
            Table = new Table { SourceName = id.Domain, SchemaName = id.Schema, TableName = id.TableName, DisplayName = id.TableName, IsActive = true },
            Columns = [new TableColumn { ColumnName = "a", DataType = "INT" }]
        });
        _epochs.ClearReceivedCalls();

        await Repo.SetRowFilterEvaluationAsync(id, new RowFilterEvaluationSettings(RowFilterEvaluationStrategy.KeySetCache, 60, 5), new Sid("S-1-5-21-admin"));

        await _epochs.Received(1).InvalidateEpochAsync(id, Arg.Any<CancellationToken>());
    }
}
