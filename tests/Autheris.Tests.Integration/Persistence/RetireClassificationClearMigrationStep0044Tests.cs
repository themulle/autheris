namespace Autheris.Tests.Integration.Persistence;

using System;
using System.Data.Common;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Infrastructure.Persistence.Migrations;
using Autheris.Tests.Integration.Governance;
using Microsoft.Data.SqlClient;
using Npgsql;
using Shouldly;
using Xunit;

/// <summary>
/// Step 0044 (<c>retire_classification_clear</c>, #1009 A-3): the per-grant classification access table and the clear-routing columns
/// of the taxonomy are dropped; the taxonomy rows (code, rank, parent, active flag) survive; a re-run is a no-op.
/// </summary>
[Trait("Category", "Security")]
public sealed class RetireClassificationClearMigrationStep0044Tests
{
    private const string StepName = "retire_classification_clear";

    private static readonly string[] DroppedColumns = ["clear_grantable", "separate_approval", "approver_mode", "steward_group_sid", "max_clear_days"];

    private const string PostgreSqlPreShape = """
        CREATE SCHEMA pre_step_0044;
        SET search_path TO pre_step_0044;
        CREATE TABLE data_classifications (
            scope VARCHAR(64) NOT NULL,
            code VARCHAR(64) NOT NULL,
            display_name VARCHAR(256) NOT NULL,
            description VARCHAR(1024) NULL,
            parent_code VARCHAR(64) NULL,
            restriction_rank SMALLINT NOT NULL,
            clear_grantable BOOLEAN NOT NULL,
            separate_approval BOOLEAN NOT NULL,
            approver_mode VARCHAR(24) NOT NULL,
            steward_group_sid VARCHAR(256) NULL,
            max_clear_days INTEGER NULL,
            is_active BOOLEAN NOT NULL,
            version INTEGER NOT NULL,
            updated_by VARCHAR(256) NOT NULL,
            updated_at TIMESTAMPTZ NOT NULL,
            CONSTRAINT pk_data_classifications PRIMARY KEY (scope, code),
            CONSTRAINT ck_data_classifications_approver_mode CHECK (approver_mode IN ('DATA_OWNER', 'STEWARD', 'DATA_OWNER_AND_STEWARD')),
            CONSTRAINT ck_data_classifications_steward CHECK (approver_mode = 'DATA_OWNER' OR steward_group_sid IS NOT NULL)
        );
        CREATE TABLE consent_classification_access (
            tenant_id VARCHAR(128) NOT NULL,
            consent_id TEXT NOT NULL,
            code VARCHAR(64) NOT NULL,
            access_level SMALLINT NOT NULL,
            valid_until TIMESTAMPTZ NULL,
            approved_by_sid VARCHAR(256) NULL,
            approved_at TIMESTAMPTZ NOT NULL,
            CONSTRAINT pk_consent_classification_access PRIMARY KEY (tenant_id, consent_id, code)
        );
        INSERT INTO data_classifications VALUES
            ('*', 'PII', 'Personal Data', NULL, NULL, 40, TRUE, TRUE, 'DATA_OWNER', NULL, 90, TRUE, 1, 'seed', CURRENT_TIMESTAMP),
            ('*', 'PII_SPECIAL', 'Special PII', NULL, 'PII', 60, TRUE, TRUE, 'DATA_OWNER_AND_STEWARD', 'S-1-STEWARD', NULL, TRUE, 1, 'seed', CURRENT_TIMESTAMP);
        INSERT INTO consent_classification_access VALUES ('t1', 'c1', 'PII', 2, NULL, 'owner', CURRENT_TIMESTAMP);
        """;

    private const string SqlServerPreShape = """
        CREATE TABLE dbo.DATA_CLASSIFICATIONS (
            scope NVARCHAR(64) NOT NULL,
            code NVARCHAR(64) NOT NULL,
            display_name NVARCHAR(256) NOT NULL,
            description NVARCHAR(1024) NULL,
            parent_code NVARCHAR(64) NULL,
            restriction_rank SMALLINT NOT NULL,
            clear_grantable BIT NOT NULL,
            separate_approval BIT NOT NULL,
            approver_mode NVARCHAR(24) NOT NULL,
            steward_group_sid NVARCHAR(256) NULL,
            max_clear_days INT NULL,
            is_active BIT NOT NULL,
            version INT NOT NULL,
            updated_by NVARCHAR(256) NOT NULL,
            updated_at DATETIMEOFFSET NOT NULL,
            CONSTRAINT PK_DATA_CLASSIFICATIONS PRIMARY KEY (scope, code),
            CONSTRAINT CK_DATA_CLASSIFICATIONS_APPROVER_MODE CHECK (approver_mode IN (N'DATA_OWNER', N'STEWARD', N'DATA_OWNER_AND_STEWARD')),
            CONSTRAINT CK_DATA_CLASSIFICATIONS_STEWARD CHECK (approver_mode = N'DATA_OWNER' OR steward_group_sid IS NOT NULL)
        );
        CREATE TABLE dbo.CONSENT_CLASSIFICATION_ACCESS (
            tenant_id NVARCHAR(128) NOT NULL,
            consent_id NVARCHAR(64) NOT NULL,
            code NVARCHAR(64) NOT NULL,
            access_level SMALLINT NOT NULL,
            valid_until DATETIMEOFFSET NULL,
            approved_by_sid NVARCHAR(256) NULL,
            approved_at DATETIMEOFFSET NOT NULL,
            CONSTRAINT PK_CONSENT_CLASSIFICATION_ACCESS PRIMARY KEY (tenant_id, consent_id, code)
        );
        INSERT INTO dbo.DATA_CLASSIFICATIONS VALUES
            (N'*', N'PII', N'Personal Data', NULL, NULL, 40, 1, 1, N'DATA_OWNER', NULL, 90, 1, 1, N'seed', SYSDATETIMEOFFSET()),
            (N'*', N'PII_SPECIAL', N'Special PII', NULL, N'PII', 60, 1, 1, N'DATA_OWNER_AND_STEWARD', N'S-1-STEWARD', NULL, 1, 1, N'seed', SYSDATETIMEOFFSET());
        INSERT INTO dbo.CONSENT_CLASSIFICATION_ACCESS VALUES (N't1', N'c1', N'PII', 2, NULL, N'owner', SYSDATETIMEOFFSET());
        """;

    [Theory]
    [InlineData("postgresql")]
    [InlineData("sqlserver")]
    public async Task Clear_grant_table_and_routing_columns_are_dropped_and_the_taxonomy_survives(string engine)
    {
        await using var db = await OpenPreStepAsync(engine);
        if (db is null) return;
        var p = engine == "postgresql" ? "" : "dbo.";

        await RunStepAsync(db);
        await RunStepAsync(db);

        await Should.ThrowAsync<DbException>(() => CountAsync(db.Connection, $"SELECT COUNT(*) FROM {p}CONSENT_CLASSIFICATION_ACCESS"), "the clear grant table must be gone");
        foreach (var column in DroppedColumns)
        {
            await Should.ThrowAsync<DbException>(() => CountAsync(db.Connection, $"SELECT COUNT({column}) FROM {p}DATA_CLASSIFICATIONS"), $"{column} must be gone");
        }

        (await CountAsync(db.Connection, $"SELECT COUNT(*) FROM {p}DATA_CLASSIFICATIONS WHERE code = 'PII_SPECIAL' AND parent_code = 'PII' AND restriction_rank = 60")).ShouldBe(1);
        (await CountAsync(db.Connection, $"SELECT COUNT(*) FROM {p}DATA_CLASSIFICATIONS")).ShouldBe(2);
    }

    private static async Task<Db?> OpenPreStepAsync(string engine)
    {
        var step = SqlMigrationStep.LoadEmbedded(engine).Single(s => s.Version == 44 && s.Name == StepName);
        if (engine == "postgresql")
        {
            var conn = new NpgsqlConnection(await GovernanceTestDatabase.CreateAsync("retire_classification_clear_0044"));
            await conn.OpenAsync();
            await ExecAsync(conn, PostgreSqlPreShape);
            return new Db(conn, step, conn.DisposeAsync);
        }

        var database = await SqlServerTestDatabase.CreateAsync();
        if (!database.IsAvailable)
        {
            return null;
        }

        var sqlConn = new SqlConnection(database.ConnectionString);
        await sqlConn.OpenAsync();
        await ExecAsync(sqlConn, SqlServerPreShape);

        return new Db(sqlConn, step, async () =>
        {
            await sqlConn.DisposeAsync();
            await database.DisposeAsync();
        });
    }

    private static async Task RunStepAsync(Db db)
    {
        await using var tx = await db.Connection.BeginTransactionAsync();
        try
        {
            foreach (var batch in db.Step.Batches)
            {
                await ExecAsync(db.Connection, batch, tx);
            }

            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    private static async Task ExecAsync(DbConnection conn, string sql, DbTransaction? tx = null)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountAsync(DbConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private sealed record Db(DbConnection Connection, SqlMigrationStep Step, Func<ValueTask> Dispose) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Dispose();
    }
}
