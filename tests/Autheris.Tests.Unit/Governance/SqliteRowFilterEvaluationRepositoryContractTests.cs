using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence;
using Autheris.Tests.Governance;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance;

/// <summary>Issue #107 persistence contract on SQLite (see <see cref="RowFilterEvaluationRepositoryContractTestsBase"/>).</summary>
public sealed class SqliteRowFilterEvaluationRepositoryContractTests : RowFilterEvaluationRepositoryContractTestsBase
{
    private readonly IEpochValidationService _epochs = Substitute.For<IEpochValidationService>();
    private SqliteGovernanceRepository? _repo;

    protected override Task<IGovernanceRepository?> CreateRepositoryAsync()
    {
        _repo = GovernanceStoreFactory.CreateSqlite(_epochs);
        return Task.FromResult<IGovernanceRepository?>(_repo);
    }

    protected override Task DisposeRepositoryAsync()
    {
        _repo?.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ObjectChange_InvalidatesTheEpochAfterCommit()
    {
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
