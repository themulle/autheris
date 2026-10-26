namespace Autheris.Tests.Integration;

using System;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Npgsql;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

/// <summary>DEF-02/03/04 on PostgreSql: see <see cref="ConsentRevokeUnificationContractTestsBase"/>.</summary>
public sealed class PostgreSqlConsentRevokeUnificationContractTests : ConsentRevokeUnificationContractTestsBase
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
            GovernanceDb = new GovernanceDbOptions
            {
                Provider = "PostgreSql", ConnectionString = _db.ConnectionString, EnableOutboxProcessor = false, SeedDemoData = false
            }
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

    protected override async Task<object?> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = new NpgsqlConnection(_db!.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    protected override Task GrantGovernanceAdminAsync(string sid) => ScalarAsync(
        @"INSERT INTO ROLES (id, role_name, description) VALUES (@rid, 'GovernanceAdmin', 'Governance Administrator')
            ON CONFLICT DO NOTHING;
        INSERT INTO ROLE_MEMBERS (id, role_id, member_type, member_sid)
            SELECT @m, id, 'User', @sid FROM ROLES WHERE role_name = 'GovernanceAdmin';",
        ("@rid", Guid.NewGuid().ToString()), ("@m", Guid.NewGuid().ToString()), ("@sid", sid));

    protected override async Task<long> BlockedSessionCountAsync() => Convert.ToInt64(await ScalarAsync("SELECT COUNT(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock'"));

    protected override async Task<ILockHolder> HoldConsentRowLockAsync(Guid consentId)
    {
        var conn = new NpgsqlConnection(_db!.ConnectionString);
        await conn.OpenAsync();
        var tx = await conn.BeginTransactionAsync();
        await using (var cmd = new NpgsqlCommand("SELECT 1 FROM CONSENTS WHERE id = @id FOR UPDATE", conn, tx))
        {
            cmd.Parameters.AddWithValue("@id", consentId.ToString());
            await cmd.ExecuteScalarAsync();
        }

        return new Holder(conn, tx);
    }

    private sealed class Holder(NpgsqlConnection conn, NpgsqlTransaction tx) : ILockHolder
    {
        private bool _done;

        public async Task ExecuteAndCommitAsync(string sql, params (string Name, object Value)[] parameters)
        {
            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            foreach (var (name, value) in parameters)
            {
                cmd.Parameters.AddWithValue(name, value);
            }

            await cmd.ExecuteNonQueryAsync();
            await tx.CommitAsync();
            _done = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (!_done)
            {
                await tx.RollbackAsync();
            }

            await tx.DisposeAsync();
            await conn.DisposeAsync();
        }
    }
}
