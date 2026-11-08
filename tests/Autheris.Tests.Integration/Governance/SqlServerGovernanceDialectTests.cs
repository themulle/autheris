using System;
using System.Data;
using System.Data.Common;
using System.Reflection;
using Autheris.Infrastructure.Persistence.Dialects;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Governance;

[Trait("Category", "Security")]
public sealed class SqlServerGovernanceDialectTests
{
    private const string ConnStr = "Server=localhost;Database=test;Trusted_Connection=True;";
    private readonly SqlServerGovernanceDialect _dialect = new(ConnStr);

    [Fact]
    public void Upsert_emits_merge_with_holdlock()
    {
        string[] keys = ["table_id"];
        string[] values = ["epoch", "updated_at"];

        var sql = _dialect.Upsert(GovernanceTable.PolicyEpochs, keys, values);

        sql.ShouldStartWith("MERGE INTO [POLICY_EPOCHS] WITH (HOLDLOCK) AS target");
        sql.ShouldContain("USING (VALUES (@table_id, @epoch, @updated_at)) AS source");
        sql.ShouldContain("ON (target.[table_id] = source.[table_id])");
        sql.ShouldContain("WHEN MATCHED THEN UPDATE SET target.[epoch] = source.[epoch], target.[updated_at] = source.[updated_at]");
        sql.ShouldContain("WHEN NOT MATCHED THEN INSERT ([table_id], [epoch], [updated_at]) VALUES (source.[table_id], source.[epoch], source.[updated_at]);");
    }

    [Fact]
    public void Members_conform_to_IGovernanceSqlDialect_contract()
    {
        var interfaceType = typeof(IGovernanceSqlDialect);
        var methods = interfaceType.GetMethods(BindingFlags.Public | BindingFlags.Instance);
        var properties = interfaceType.GetProperties(BindingFlags.Public | BindingFlags.Instance);

        var totalMembers = methods.Length + properties.Length;
        totalMembers.ShouldBeLessThanOrEqualTo(15, "IGovernanceSqlDialect must stay bounded (<= 15 members)");
    }

    [Fact]
    public void Dialect_specific_fragments()
    {
        _dialect.LockRowsForUpdate.ShouldBe("WITH (UPDLOCK, ROWLOCK)");
        _dialect.UtcNow.ShouldBe("SYSUTCDATETIME()");
        _dialect.InsertReturning("id").ShouldBe("OUTPUT inserted.[id]");
        _dialect.LimitWithoutOrder(10).ShouldBe("ORDER BY (SELECT NULL) OFFSET 0 ROWS FETCH NEXT 10 ROWS ONLY");
        _dialect.StringAggregate("role_name", ",").ShouldBe("STRING_AGG(role_name, ',')");
        _dialect.JsonValue("details", "appId").ShouldBe("JSON_VALUE([details], '$.appId')");
        _dialect.ParameterBudget.GetMaxParameters(Autheris.Domain.Common.DatabaseDialect.SqlServer).ShouldBe(2100);
    }

    [Fact]
    public void CreateConnection_returns_QuotedIdentifierSqlConnection_when_connection_string_provided()
    {
        var dialect = new SqlServerGovernanceDialect(ConnStr);
        using var conn = dialect.CreateConnection();
        conn.ShouldNotBeNull();
        conn.GetType().Name.ShouldBe("QuotedIdentifierSqlConnection");
    }

    [Theory]
    [InlineData("x') OR 1=1--")]
    [InlineData("a b")]
    [InlineData("a;DROP TABLE T")]
    [InlineData("")]
    public void Dialect_rejects_non_identifier_input(string bad)
    {
        // SEC-SIMPL-21
        var pg = new PostgreSqlGovernanceDialect("Host=localhost");
        Should.Throw<ArgumentException>(() => _dialect.JsonValue("details", bad));
        Should.Throw<ArgumentException>(() => pg.JsonValue("details", bad));
        Should.Throw<ArgumentException>(() => _dialect.JsonValue(bad, "appId"));
        Should.Throw<ArgumentException>(() => pg.JsonValue(bad, "appId"));
        Should.Throw<ArgumentException>(() => _dialect.StringAggregate(bad, ","));
        Should.Throw<ArgumentException>(() => pg.StringAggregate(bad, ","));
        Should.Throw<ArgumentException>(() => _dialect.InsertReturning(bad));
        Should.Throw<ArgumentException>(() => pg.InsertReturning(bad));
        Should.Throw<ArgumentException>(() => _dialect.Upsert(GovernanceTable.PolicyEpochs, [bad], ["v"]));
        Should.Throw<ArgumentException>(() => pg.Upsert(GovernanceTable.PolicyEpochs, ["k"], [bad]));
    }

    [Theory]
    [InlineData("'), (SELECT 1")]
    [InlineData("x")]
    public void Dialect_rejects_non_allowlisted_separator(string separator)
    {
        // SEC-SIMPL-21
        Should.Throw<ArgumentException>(() => _dialect.StringAggregate("role_name", separator));
        Should.Throw<ArgumentException>(() => new PostgreSqlGovernanceDialect("Host=localhost").StringAggregate("role_name", separator));
        _dialect.StringAggregate("role_name", null!).ShouldBe("STRING_AGG(role_name, ',')");
    }

    [Fact]
    public void Open_fails_closed_when_QUOTED_IDENTIFIER_is_off()
    {
        // SEC-SIMPL-21: the SESSIONPROPERTY assertion runs on open and rejects 0.
        var probe = new FakeConnection(0);
        var dialect = new SqlServerGovernanceDialect(() => probe);
        using var conn = dialect.CreateConnection();
        Should.Throw<InvalidOperationException>(() => conn.Open()).Message.ShouldContain("QUOTED_IDENTIFIER");
        probe.Opened.ShouldBeTrue();
        probe.Queried.ShouldBeTrue();

        var ok = new FakeConnection(1);
        using var okConn = new SqlServerGovernanceDialect(() => ok).CreateConnection();
        okConn.Open();
        ok.Queried.ShouldBeTrue();
    }

    private sealed class FakeConnection(object scalar) : DbConnection
    {
        public bool Opened { get; private set; }
        public bool Queried { get; set; }
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString { get; set; } = "";
        public override string Database => "fake";
        public override string DataSource => "fake";
        public override string ServerVersion => "0";
        public override ConnectionState State => Opened ? ConnectionState.Open : ConnectionState.Closed;
        public override void ChangeDatabase(string databaseName) { }
        public override void Close() => Opened = false;
        public override void Open() => Opened = true;
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => new FakeCommand(this, scalar);
    }

    private sealed class FakeCommand(FakeConnection owner, object scalar) : DbCommand
    {
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string CommandText { get; set; } = "";
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; }
        protected override DbParameterCollection DbParameterCollection => throw new NotSupportedException();
        protected override DbTransaction? DbTransaction { get; set; }
        public override void Cancel() { }
        public override int ExecuteNonQuery() => 0;
        public override object? ExecuteScalar()
        {
            owner.Queried = CommandText.Contains("SESSIONPROPERTY('QUOTED_IDENTIFIER')", StringComparison.Ordinal);
            return scalar;
        }
        public override void Prepare() { }
        protected override DbParameter CreateDbParameter() => throw new NotSupportedException();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotSupportedException();
    }
}
