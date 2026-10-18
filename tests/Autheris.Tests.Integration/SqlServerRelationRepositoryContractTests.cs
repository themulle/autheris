namespace Autheris.Tests.Integration;

using System.Data.Common;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Autheris.Tests.Governance;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Data.SqlClient;
using NSubstitute;

/// <summary>Issue #151 composite-key relation persistence contract on SqlServer (see <see cref="RelationRepositoryContractTestsBase"/>).</summary>
public sealed class SqlServerRelationRepositoryContractTests : RelationRepositoryContractTestsBase
{
    private SqlServerTestDatabase? _db;
    private SqlServerGovernanceRepository? _repo;

    protected override async Task<IGovernanceRepository?> CreateRepositoryAsync()
    {
        _db = await SqlServerTestDatabase.CreateAsync();
        if (!_db.IsAvailable)
        {
            return null;
        }

        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Development");
        var options = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions { Provider = "SqlServer", ConnectionString = _db.ConnectionString, EnableOutboxProcessor = false, SeedDemoData = false }
        });
        _repo = GovernanceStoreFactory.CreateSqlServer(Substitute.For<IEpochValidationService>(), options, env);
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
        var conn = new SqlConnection(_db!.ConnectionString);
        conn.Open();
        return conn;
    }

    protected override string InstallKeyInsertFailureSql =>
        "CREATE TRIGGER trg_fail_key_insert ON dbo.TABLE_RELATION_KEYS AFTER INSERT AS BEGIN IF EXISTS (SELECT 1 FROM inserted WHERE key_ordinal = 2) THROW 50000, 'forced key insert failure', 1; END";

    // The case-insensitive collation and ux_tables_natural reject case-only twins at the database; the repository check is a second line of defence.
    protected override bool AllowsCaseOnlyDuplicateTables => false;

    protected override string RemoveKeyInsertFailureSql => "IF OBJECT_ID(N'dbo.trg_fail_key_insert', N'TR') IS NOT NULL DROP TRIGGER dbo.trg_fail_key_insert";
}
