using System.Data.Common;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Autheris.Tests.Governance;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance;

/// <summary>Issue #151 relation persistence contract on SQLite (see <see cref="RelationRepositoryContractTestsBase"/>).</summary>
public sealed class SqliteRelationRepositoryContractTests : RelationRepositoryContractTestsBase
{
    private readonly string _connectionString = $"Data Source=relations_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
    private SqliteGovernanceRepository? _repo;

    protected override Task<IGovernanceRepository?> CreateRepositoryAsync()
    {
        var options = Options.Create(new GatewayOptions { GovernanceDb = new GovernanceDbOptions { ConnectionString = _connectionString, SeedDemoData = false } });
        _repo = GovernanceStoreFactory.CreateSqlite(Substitute.For<IEpochValidationService>(), options);
        return Task.FromResult<IGovernanceRepository?>(_repo);
    }

    protected override Task DisposeRepositoryAsync()
    {
        _repo?.Dispose();
        return Task.CompletedTask;
    }

    protected override DbConnection OpenRaw()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    protected override string InstallKeyInsertFailureSql =>
        "CREATE TRIGGER trg_fail_key_insert BEFORE INSERT ON TABLE_RELATION_KEYS WHEN NEW.key_ordinal = 2 BEGIN SELECT RAISE(ABORT, 'forced key insert failure'); END";

    protected override string RemoveKeyInsertFailureSql => "DROP TRIGGER IF EXISTS trg_fail_key_insert";

    [Fact]
    public async Task Sqlite_CreateRelation_ResolvesTableIdsFromIdentifiers()
    {
        // AC-151-4: the SQLite repository no longer trusts relation.ParentTableId/ChildTableId (the helper passes random ids).
        var parent = NewTable("parent");
        var child = NewTable("child");
        await SeedTableAsync(parent);
        await SeedTableAsync(child);

        var result = await Repo.CreateRelationAsync(Rel(parent, child, ["a"], ["b"]));

        var stored = Scalar("SELECT parent_table_id || '|' || child_table_id FROM TABLE_RELATIONS WHERE id = @id", ("@id", result.StoredId.ToString()));
        stored.ShouldBe($"{TableId(parent)}|{TableId(child)}");
    }
}
