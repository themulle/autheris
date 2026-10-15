namespace Autheris.Tests.Integration;

using System;
using System.Threading.Tasks;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence.Migrations;
using Microsoft.Data.SqlClient;
using Shouldly;
using Xunit;

/// <summary>SEC-94-09 on SQL Server: the audit trigger must be enabled and the truncate guard foreign key must exist and be enabled, also in Verify mode.</summary>
public sealed class SqlServerGovernancePostConditionTests : IClassFixture<MsSqlContainerFixture>
{
    private readonly string _connectionString;

    public SqlServerGovernancePostConditionTests(MsSqlContainerFixture fixture)
    {
        _connectionString = fixture.ConnectionString;
    }

    private async Task ExecAsync(string sql)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task AssertVerifyFailsThenRecoversAsync(string breakSql, string repairSql, string expectedMessage)
    {
        await GovernanceSchemaTestSupport.RunSqlServerAsync(_connectionString, GovernanceMigrationMode.Apply);
        await GovernanceSchemaTestSupport.RunSqlServerAsync(_connectionString, GovernanceMigrationMode.Verify);
        await ExecAsync(breakSql);
        try
        {
            var verify = await Should.ThrowAsync<GovernanceSchemaDriftException>(
                () => GovernanceSchemaTestSupport.RunSqlServerAsync(_connectionString, GovernanceMigrationMode.Verify));
            verify.Message.ShouldContain(expectedMessage);
            await Should.ThrowAsync<GovernanceSchemaDriftException>(
                () => GovernanceSchemaTestSupport.RunSqlServerAsync(_connectionString, GovernanceMigrationMode.Apply));
        }
        finally
        {
            await ExecAsync(repairSql);
        }

        await GovernanceSchemaTestSupport.RunSqlServerAsync(_connectionString, GovernanceMigrationMode.Verify);
    }

    [Fact]
    public Task DisabledAuditTrigger_FailsVerify() =>
        AssertVerifyFailsThenRecoversAsync(
            "DISABLE TRIGGER dbo.trg_audit_append_only ON dbo.AUDIT_LOG_ENTRIES",
            "ENABLE TRIGGER dbo.trg_audit_append_only ON dbo.AUDIT_LOG_ENTRIES",
            "append-only audit trigger");

    [Fact]
    public Task DisabledTruncateGuardForeignKey_FailsVerify() =>
        AssertVerifyFailsThenRecoversAsync(
            "ALTER TABLE dbo.AUDIT_LOG_TRUNCATE_GUARD NOCHECK CONSTRAINT fk_audit_truncate_guard",
            "ALTER TABLE dbo.AUDIT_LOG_TRUNCATE_GUARD WITH CHECK CHECK CONSTRAINT fk_audit_truncate_guard",
            "truncate guard");

    [Fact]
    public Task MissingTruncateGuardForeignKey_FailsVerify() =>
        AssertVerifyFailsThenRecoversAsync(
            "ALTER TABLE dbo.AUDIT_LOG_TRUNCATE_GUARD DROP CONSTRAINT fk_audit_truncate_guard",
            "ALTER TABLE dbo.AUDIT_LOG_TRUNCATE_GUARD ADD CONSTRAINT fk_audit_truncate_guard FOREIGN KEY (seq) REFERENCES dbo.AUDIT_LOG_ENTRIES (seq)",
            "truncate guard");
}
