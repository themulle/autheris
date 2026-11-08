namespace Autheris.Tests.Integration;

using System.Data.Common;
using System.Threading.Tasks;
using Xunit;
using Shouldly;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Autheris.Tests.Governance;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using NSubstitute;

/// <summary>Issue #151 composite-key relation persistence contract on PostgreSql (see <see cref="RelationRepositoryContractTestsBase"/>).</summary>
public sealed class PostgreSqlRelationRepositoryContractTests : RelationRepositoryContractTestsBase
{
    private PostgreSqlTestDatabase? _db;
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
        _repo = GovernanceStoreFactory.CreatePostgreSql(Substitute.For<IEpochValidationService>(), options, env);
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

    protected override DbConnection OpenRaw()
    {
        var conn = new NpgsqlConnection(_db!.ConnectionString);
        conn.Open();
        return conn;
    }

    protected override string InstallKeyInsertFailureSql =>
        "CREATE FUNCTION trg_fail_key_insert_fn() RETURNS trigger AS $$ BEGIN RAISE EXCEPTION 'forced key insert failure'; END $$ LANGUAGE plpgsql; " +
        "CREATE TRIGGER trg_fail_key_insert BEFORE INSERT ON TABLE_RELATION_KEYS FOR EACH ROW WHEN (NEW.key_ordinal = 2) EXECUTE FUNCTION trg_fail_key_insert_fn()";

    protected override string RemoveKeyInsertFailureSql => "DROP TRIGGER IF EXISTS trg_fail_key_insert ON TABLE_RELATION_KEYS; DROP FUNCTION IF EXISTS trg_fail_key_insert_fn()";

    [Fact]
    public async Task PostgreSql_CreateRelation_ResolvesTableIdsFromIdentifiers()
    {
        if (_repo is null)
        {
            return;
        }

        // AC-151-4: the repository no longer trusts relation.ParentTableId/ChildTableId (the helper passes random ids).
        var parent = NewTable("parent");
        var child = NewTable("child");
        await SeedTableAsync(parent);
        await SeedTableAsync(child);

        var result = await Repo.CreateRelationAsync(Rel(parent, child, ["a"], ["b"]));

        var stored = Scalar("SELECT parent_table_id || '|' || child_table_id FROM TABLE_RELATIONS WHERE id = @id", ("@id", result.StoredId.ToString()));
        stored.ShouldBe($"{TableId(parent)}|{TableId(child)}");
    }
}
