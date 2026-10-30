using Autheris.Infrastructure.Persistence.Migrations;
using Autheris.Infrastructure.Persistence.Migrations.Shape;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance;

/// <summary>Issue #388: step checksum bytes (I388-SC-01), step lint (I388-SC-07) and the step file loader.</summary>
[Trait("Category", "Security")]
public sealed class StepSqlLintTests
{
    private const string EmptyDelta = "{\"ops\":[]}";

    [Fact]
    public void StepChecksum_CoversExecutedBytes_AndChangesWhenSqlChanges()
    {
        var a = SqlMigrationStep.Parse("sqlite", 2, "add_x", "-- guard: SELECT 0\nCREATE TABLE X (a TEXT);", EmptyDelta);
        var b = SqlMigrationStep.Parse("sqlite", 2, "add_x", "-- guard: SELECT 0\nCREATE TABLE X (b TEXT);", EmptyDelta);
        var header = SqlMigrationStep.Parse("sqlite", 2, "add_x", "-- guard: SELECT 1\nCREATE TABLE X (a TEXT);", EmptyDelta);

        a.Checksum.ShouldBe(SqlMigrationStep.ComputeChecksum("sqlite", 2, "add_x", a.Sql));
        a.Checksum.ShouldNotBe(b.Checksum);
        a.Checksum.ShouldNotBe(header.Checksum, "the header lines are inside the hashed bytes");
        a.Checksum.ShouldBe(a.Checksum.ToLowerInvariant());
        a.Batches.ShouldHaveSingleItem().ShouldBe(a.Sql, "the executed text is the hashed text");
        SqlMigrationStep.Parse("postgresql", 2, "add_x", "CREATE TABLE X (a TEXT);", EmptyDelta).Checksum
            .ShouldNotBe(SqlMigrationStep.Parse("sqlite", 2, "add_x", "CREATE TABLE X (a TEXT);", EmptyDelta).Checksum);
    }

    [Fact]
    public void StepChecksum_IsLineEndingAndBomInvariant()
    {
        var lf = SqlMigrationStep.Parse("sqlite", 2, "add_x", "CREATE TABLE X (a TEXT);\nCREATE INDEX IX_X ON X (a);\n", EmptyDelta);
        var crlf = SqlMigrationStep.Parse("sqlite", 2, "add_x", "﻿CREATE TABLE X (a TEXT);\r\nCREATE INDEX IX_X ON X (a);\r\n", EmptyDelta);

        crlf.Checksum.ShouldBe(lf.Checksum);
        crlf.Sql.ShouldBe(lf.Sql);
    }

    [Fact]
    public void SqlServerBatches_AreSplitOnTheBatchMarker()
    {
        var step = SqlMigrationStep.Parse("sqlserver", 2, "add_x", "CREATE TABLE X (a INT);\n-- batch\nCREATE INDEX IX_X ON X (a);\n", EmptyDelta);

        step.Batches.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData("TRUNCATE TABLE X;")]
    [InlineData("truncate  table [dbo].[X];")]
    [InlineData("CREATE INDEX CONCURRENTLY IX ON X (a);")]
    [InlineData("ALTER TABLE X DISABLE TRIGGER ALL;")]
    [InlineData("DROP TRIGGER \"trg_x\";")]
    [InlineData("GRANT SELECT ON X TO app;")]
    [InlineData("REVOKE ALL ON X FROM app;")]
    [InlineData("CREATE ROLE evil;")]
    [InlineData("ALTER LOGIN [sa] DISABLE;")]
    [InlineData("ALTER TABLE X OWNER TO evil;")]
    [InlineData("SET ROLE evil;")]
    [InlineData("CREATE EXTENSION dblink;")]
    [InlineData("COPY X FROM '/etc/passwd';")]
    [InlineData("EXEC xp_cmdshell 'dir';")]
    [InlineData("EXEC('DROP TABLE X')")]
    [InlineData("EXECUTE sp_executesql N'DROP TABLE X';")]
    [InlineData("PREPARE p AS SELECT 1;")]
    [InlineData("DO $$ BEGIN PERFORM 1; END $$;")]
    [InlineData("ATTACH DATABASE 'x.db' AS evil;")]
    [InlineData("PRAGMA writable_schema = 1;")]
    [InlineData("DELETE FROM \"autheris_schema_migrations\";")]
    [InlineData("INSERT INTO [dbo].[autheris_schema_migrations] VALUES (9);")]
    [InlineData("BEGIN;\nCREATE TABLE X (a INT);\nCOMMIT;")]
    [InlineData("CREATE TABLE X (a INT); ROLLBACK;")]
    [InlineData("/* TRUNCATE */ CREATE TABLE X (a INT); TRUNCATE\n-- c\nTABLE X;")]
    [InlineData("UPDATE AUDIT_LOG_ENTRIES SET a = 1;")]
    [InlineData("-- phase3: I388-SC-06\nDELETE FROM public.\"AUDIT_LOG_ENTRIES\";")]
    [InlineData("-- phase3: I388-SC-06\nCREATE INDEX IX_A ON AUDIT_LOG_ENTRIES (a);")]
    [InlineData("-- phase3: I388-SC-06\nALTER TABLE AUDIT_LOG_ENTRIES ADD CONSTRAINT ck CHECK (a > 0);")]
    [InlineData("ALTER TABLE AUDIT_LOG_ENTRIES ADD COLUMN extra TEXT;")]
    [InlineData("DROP TABLE audit_purge_checkpoints;")]
    [InlineData("UPDATE CONSENTS SET tenant_id = 'x';")]
    [InlineData("-- phase3: not a marker!\nSELECT 1;")]
    [InlineData("DECLARE @n sysname = N'x';\nEXEC(N'ALTER TABLE dbo.AUDIT_LOG_ENTRIES DROP CONSTRAINT ' + QUOTENAME(@n));")]
    [InlineData("-- phase3: I388-SC-06\nEXEC(N'DROP TABLE dbo.CONSENTS; ALTER TABLE dbo.CONSENTS DROP CONSTRAINT ' + QUOTENAME(@n));")]
    [InlineData("-- phase3: I388-SC-06\nEXEC(N'ALTER TABLE dbo.CONSENTS ADD x INT; --' + QUOTENAME(@n));")]
    [InlineData("-- phase3: I388-SC-06\nEXEC(N'ALTER TABLE dbo.CONSENTS DROP CONSTRAINT ' + @n);")]
    [InlineData("-- phase3: I388-SC-06\nEXEC(@sql);")]
    public void StepLint_RejectsForbiddenAndDynamicSql_QuotedAndCommented(string sql)
    {
        Should.Throw<InvalidOperationException>(() => SqlMigrationStep.Parse("postgresql", 2, "bad_step", sql, EmptyDelta))
            .Message.ShouldContain("step lint");
    }

    [Theory]
    [InlineData("-- guard: UPDATE X SET a = 1")]
    [InlineData("-- guard: SELECT 1; DROP TABLE X")]
    [InlineData("-- guard: DELETE FROM X")]
    [InlineData("-- guard: SELECT 1 FROM autheris_schema_migrations")]
    public void StepLint_RejectsGuardThatIsNotASingleSelect(string header)
    {
        Should.Throw<InvalidOperationException>(() => SqlMigrationStep.Parse("sqlite", 2, "bad_guard", header + "\nCREATE TABLE X (a INT);", EmptyDelta))
            .Message.ShouldContain("step lint");
    }

    [Theory]
    [InlineData("CREATE TABLE IF NOT EXISTS X (a TEXT);\nCREATE INDEX IF NOT EXISTS IX_X ON X (a);")]
    [InlineData("-- guard: SELECT COUNT(*) FROM pragma_table_info('X') WHERE name = 'b'\nALTER TABLE X ADD COLUMN b TEXT;")]
    [InlineData("-- phase3: I388-SC-06\nALTER TABLE audit_log_entries ALTER COLUMN tenant_id DROP DEFAULT;")]
    [InlineData("-- phase3: I388-SC-06\nALTER TABLE [dbo].[AUDIT_LOG_ENTRIES] ADD note NVARCHAR(10) NULL;")]
    [InlineData("-- phase3: I388-SC-14\nUPDATE CONSENTS SET tenant_id = 'a' WHERE tenant_id = 'b';")]
    [InlineData("INSERT INTO X (a) VALUES ('copy exec truncate');")]
    [InlineData("-- phase3: I388-SC-06\nDECLARE @n sysname = N'x';\nIF @n IS NOT NULL EXEC(N'ALTER TABLE dbo.AUDIT_LOG_ENTRIES DROP CONSTRAINT ' + QUOTENAME(@n));")]
    [InlineData("DECLARE @n sysname = N'x';\nEXEC(N'ALTER TABLE dbo.CONSENTS DROP CONSTRAINT ' + QUOTENAME(@n));")]
    public void StepLint_AcceptsPlainDdlAndMarkedAuditForms(string sql)
    {
        var step = SqlMigrationStep.Parse("postgresql", 2, "ok_step", sql, EmptyDelta);

        step.Sql.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void StepLoader_FailsClosedOnMissingDeltaOrBadName()
    {
        Should.Throw<InvalidOperationException>(() => SqlMigrationStep.Load("sqlite", ["0002_a.sql"], _ => "CREATE TABLE A (x INT);"));
        Should.Throw<InvalidOperationException>(() => SqlMigrationStep.Load("sqlite", ["0002_a.shape.json"], _ => EmptyDelta));
        Should.Throw<InvalidOperationException>(() => SqlMigrationStep.Load("sqlite", ["2_a.sql", "2_a.shape.json"], f => f.EndsWith(".sql", StringComparison.Ordinal) ? "CREATE TABLE A (x INT);" : EmptyDelta));
        Should.Throw<InvalidOperationException>(() => SqlMigrationStep.Load("sqlite", ["0001_a.sql", "0001_a.shape.json"], f => f.EndsWith(".sql", StringComparison.Ordinal) ? "CREATE TABLE A (x INT);" : EmptyDelta));
        SqlMigrationStep.Load("sqlite", ["0002_a.sql", "0002_a.shape.json"], f => f.EndsWith(".sql", StringComparison.Ordinal) ? "CREATE TABLE A (x INT);" : EmptyDelta)
            .ShouldHaveSingleItem().Name.ShouldBe("a");
    }

    [Fact]
    public void ShippedCatalogues_HoldExactlySteps0002And0003()
    {
        foreach (var engine in new[] { "sqlite", "postgresql", "sqlserver" })
        {
            SqlMigrationStep.LoadEmbedded(engine).Select(s => (s.Version, s.Name)).ShouldBe([(2, "key_and_index_column_direction"), (3, "tenant_default_removal_and_request_tenant")]);
        }
    }
}
