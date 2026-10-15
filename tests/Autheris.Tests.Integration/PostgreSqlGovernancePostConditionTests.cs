namespace Autheris.Tests.Integration;

using System;
using System.Threading.Tasks;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence.Migrations;
using Npgsql;
using Shouldly;
using Xunit;

/// <summary>SEC-94-09 on PostgreSQL: the append-only audit trigger post-condition checks table, function, timing, events and enabled state, also in Verify mode.</summary>
public sealed class PostgreSqlGovernancePostConditionTests : IAsyncLifetime
{
    private PostgreSqlTestDatabase _db = null!;

    public async Task InitializeAsync()
    {
        _db = await PostgreSqlTestDatabase.CreateAsync();
        await GovernanceSchemaTestSupport.RunPostgreSqlAsync(_db.ConnectionString, GovernanceMigrationMode.Apply);
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    private async Task ExecAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(_db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task AssertVerifyFailsThenRecoversAsync(string breakSql, string repairSql)
    {
        await GovernanceSchemaTestSupport.RunPostgreSqlAsync(_db.ConnectionString, GovernanceMigrationMode.Verify);
        await ExecAsync(breakSql);
        try
        {
            var verify = await Should.ThrowAsync<GovernanceSchemaDriftException>(
                () => GovernanceSchemaTestSupport.RunPostgreSqlAsync(_db.ConnectionString, GovernanceMigrationMode.Verify));
            verify.Message.ShouldContain("append-only audit triggers");
            await Should.ThrowAsync<GovernanceSchemaDriftException>(
                () => GovernanceSchemaTestSupport.RunPostgreSqlAsync(_db.ConnectionString, GovernanceMigrationMode.Apply));
        }
        finally
        {
            await ExecAsync(repairSql);
        }

        await GovernanceSchemaTestSupport.RunPostgreSqlAsync(_db.ConnectionString, GovernanceMigrationMode.Verify);
    }

    [Fact]
    public Task DisabledOrMisplacedAuditTrigger_FailsVerify() =>
        AssertVerifyFailsThenRecoversAsync(
            "ALTER TABLE audit_log_entries DISABLE TRIGGER trg_audit_append_only_row",
            "ALTER TABLE audit_log_entries ENABLE TRIGGER trg_audit_append_only_row");

    [Fact]
    public Task DisabledOrMisplacedAuditTrigger_FailsVerify_TruncateTriggerDisabled() =>
        AssertVerifyFailsThenRecoversAsync(
            "ALTER TABLE audit_log_entries DISABLE TRIGGER trg_audit_append_only_trunc",
            "ALTER TABLE audit_log_entries ENABLE TRIGGER trg_audit_append_only_trunc");

    [Fact]
    public Task DisabledOrMisplacedAuditTrigger_FailsVerify_ReplicaOnly() =>
        AssertVerifyFailsThenRecoversAsync(
            "ALTER TABLE audit_log_entries ENABLE REPLICA TRIGGER trg_audit_append_only_row",
            "ALTER TABLE audit_log_entries ENABLE TRIGGER trg_audit_append_only_row");

    [Fact]
    public Task DisabledOrMisplacedAuditTrigger_FailsVerify_Misplaced() =>
        AssertVerifyFailsThenRecoversAsync(
            "CREATE TABLE decoy (id INT); DROP TRIGGER trg_audit_append_only_row ON audit_log_entries; " +
            "CREATE TRIGGER trg_audit_append_only_row BEFORE UPDATE OR DELETE ON decoy FOR EACH ROW EXECUTE FUNCTION autheris_audit_append_only();",
            "DROP TABLE decoy; CREATE TRIGGER trg_audit_append_only_row BEFORE UPDATE OR DELETE ON audit_log_entries FOR EACH ROW EXECUTE FUNCTION autheris_audit_append_only();");

    [Fact]
    public Task DisabledOrMisplacedAuditTrigger_FailsVerify_WrongEventsOrFunction() =>
        AssertVerifyFailsThenRecoversAsync(
            "CREATE FUNCTION noop_trg() RETURNS trigger AS $f$ BEGIN RETURN NEW; END; $f$ LANGUAGE plpgsql; " +
            "DROP TRIGGER trg_audit_append_only_trunc ON audit_log_entries; " +
            "CREATE TRIGGER trg_audit_append_only_trunc BEFORE TRUNCATE ON audit_log_entries FOR EACH STATEMENT EXECUTE FUNCTION noop_trg();",
            "DROP TRIGGER trg_audit_append_only_trunc ON audit_log_entries; " +
            "CREATE TRIGGER trg_audit_append_only_trunc BEFORE TRUNCATE ON audit_log_entries FOR EACH STATEMENT EXECUTE FUNCTION autheris_audit_append_only(); DROP FUNCTION noop_trg();");
}
