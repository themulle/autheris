using Autheris.Application.Sql;
using Autheris.Domain.Common;
using Shouldly;

namespace Autheris.Tests.Unit.Sql;

/// <summary>SEC-SIMPL-141: the runtime guard rejects source metadata reads before execution and fails closed.</summary>
[Trait("Category", "Security")]
public sealed class SourceMetadataGuardTests
{
    private static string? Rule(string sql, DatabaseDialect dialect) =>
        SourceMetadataGuard.TryFindViolation(sql, dialect, out var rule) ? rule : null;

    [Theory]
    [InlineData(DatabaseDialect.PostgreSql)]
    [InlineData(DatabaseDialect.SqlServer)]
    [InlineData(DatabaseDialect.Oracle)]
    [InlineData(DatabaseDialect.Databricks)]
    [InlineData(DatabaseDialect.Sqlite)]
    public void SEC_SIMPL_141_Rejects_InformationSchema_AllDialects(DatabaseDialect dialect)
    {
        Rule("SELECT column_name FROM information_schema.columns", dialect).ShouldBe("R1");
        Rule("SELECT 1 FROM \"INFORMATION_SCHEMA\".\"TABLES\"", dialect).ShouldBe("R1");
    }

    [Theory]
    [InlineData("SELECT * FROM sys.tables")]
    [InlineData("SELECT * FROM [sys].[objects]")]
    [InlineData("SELECT OBJECT_ID(N'dbo.t')")]
    [InlineData("EXEC sp_help 't'")]
    [InlineData("SELECT * FROM master.dbo.sysdatabases")]
    public void SEC_SIMPL_141_Rejects_SqlServerSysAndMetadataFunctions(string sql) =>
        Rule(sql, DatabaseDialect.SqlServer).ShouldBe("R2");

    [Fact]
    public void SEC_SIMPL_141_SqlServer_SessionContextSetter_IsAllowed() =>
        Rule("EXEC sys.sp_set_session_context @key = N'k', @value = @v, @read_only = 1", DatabaseDialect.SqlServer).ShouldBeNull();

    [Theory]
    [InlineData("SELECT * FROM pg_catalog.pg_class")]
    [InlineData("SELECT * FROM \"pg_catalog\".\"pg_attribute\"")]
    [InlineData("SELECT 't'::regclass")]
    [InlineData("SELECT pg_get_viewdef(1)")]
    [InlineData("SELECT * FROM pg_stat_activity")]
    public void SEC_SIMPL_141_Rejects_PostgreSqlCatalog(string sql) =>
        Rule(sql, DatabaseDialect.PostgreSql).ShouldBe("R3");

    [Theory]
    [InlineData("SELECT * FROM ALL_TAB_COLUMNS")]
    [InlineData("SELECT * FROM v$session")]
    [InlineData("SELECT DBMS_METADATA.GET_DDL('TABLE','T') FROM dual")]
    [InlineData("SELECT * FROM sys.obj$")]
    public void SEC_SIMPL_141_Rejects_OracleDictionary(string sql) =>
        Rule(sql, DatabaseDialect.Oracle).ShouldBe("R4");

    [Fact]
    public void SEC_SIMPL_141_Allows_OracleUtlRawTenantComparison()
    {
        Rule("SELECT id FROM orders WHERE (\"SYS\".\"UTL_RAW\".\"CAST_TO_RAW\"(tenant_id) = \"SYS\".\"UTL_RAW\".\"CAST_TO_RAW\"(:p1))", DatabaseDialect.Oracle).ShouldBeNull();
        Rule("SELECT id FROM orders WHERE (SYS.UTL_RAW.CAST_TO_RAW(tenant_id) = SYS.UTL_RAW.CAST_TO_RAW(:p1))", DatabaseDialect.Oracle).ShouldBeNull();
    }

    [Theory]
    [InlineData("SELECT * FROM system.information_schema.tables", "R5")]
    [InlineData("SELECT * FROM system.access.audit", "R5")]
    [InlineData("SHOW TABLES", "R6")]
    [InlineData("DESCRIBE t", "R6")]
    public void SEC_SIMPL_141_Rejects_DatabricksSystemAndStatements(string sql, string rule) =>
        Rule(sql, DatabaseDialect.Databricks).ShouldBe(rule);

    [Theory]
    [InlineData("EXPLAIN SELECT 1")]
    [InlineData("SET SHOWPLAN_ALL ON")]
    [InlineData("SET STATISTICS IO ON")]
    [InlineData("DBCC CHECKDB")]
    [InlineData("SELECT 1; DESC t")]
    public void SEC_SIMPL_141_Rejects_ExplainShowplanDbcc(string sql) =>
        Rule(sql, DatabaseDialect.SqlServer).ShouldBe("R6");

    [Theory]
    [InlineData("SELECT * FROM t WHERE a = 'sys.tables'", DatabaseDialect.SqlServer)]
    [InlineData("SELECT 1 -- information_schema.columns", DatabaseDialect.PostgreSql)]
    [InlineData("SELECT 1 /* a /* nested pg_catalog.pg_class */ sys.tables */", DatabaseDialect.SqlServer)]
    [InlineData("SELECT $q$ pg_catalog.pg_class $q$ FROM t", DatabaseDialect.PostgreSql)]
    [InlineData("SELECT E'it\\'s sys.tables' FROM t", DatabaseDialect.PostgreSql)]
    [InlineData("SELECT q'[all_tables ' here]' FROM dual", DatabaseDialect.Oracle)]
    [InlineData("SELECT a FROM t ORDER BY a DESC", DatabaseDialect.PostgreSql)]
    public void SEC_SIMPL_141_Allows_LiteralsAndCommentsContainingCatalogNames(string sql, DatabaseDialect dialect) =>
        Rule(sql, dialect).ShouldBeNull();

    [Theory]
    [InlineData("SELECT * FROM USER_PROFILES", DatabaseDialect.Oracle)]
    [InlineData("SELECT * FROM orders_stats", DatabaseDialect.PostgreSql)]
    [InlineData("SELECT * FROM system_events", DatabaseDialect.Databricks)]
    [InlineData("SELECT * FROM dbo.sysadmin_notes", DatabaseDialect.SqlServer)]
    [InlineData("SELECT * FROM \"AUTH_APP\".\"ORDERS\" WHERE \"SYS\".\"UTL_RAW\".\"CAST_TO_RAW\"(\"TenantId\") = \"SYS\".\"UTL_RAW\".\"CAST_TO_RAW\"(:p1)", DatabaseDialect.Oracle)]
    public void SEC_SIMPL_141_Allows_UserTablesNamedLikeUsersOrderStats(string sql, DatabaseDialect dialect) =>
        Rule(sql, dialect).ShouldBeNull();

    [Theory]
    [InlineData("SELECT 'unterminated FROM sys.tables", DatabaseDialect.SqlServer)]
    [InlineData("SELECT 1 /* open comment", DatabaseDialect.PostgreSql)]
    [InlineData("SELECT \"open FROM t", DatabaseDialect.PostgreSql)]
    [InlineData("SELECT [open FROM t", DatabaseDialect.SqlServer)]
    [InlineData("SELECT $q$ never closed", DatabaseDialect.PostgreSql)]
    public void SEC_SIMPL_141_Rejects_UnterminatedLiteralOrComment(string sql, DatabaseDialect dialect) =>
        Rule(sql, dialect).ShouldBe("LEX");

    [Fact]
    public void SEC_SIMPL_141_Exception_CarriesRuleIdNotSql()
    {
        const string sql = "SELECT secret_marker FROM sys.tables";
        var ex = Should.Throw<SourceMetadataAccessDeniedException>(() => SourceMetadataGuard.EnsureDataOnly(sql, DatabaseDialect.SqlServer));
        ex.Rule.ShouldBe("R2");
        ex.Message.ShouldNotContain("secret_marker");
        ex.Message.ShouldNotContain("sys");
    }
}
