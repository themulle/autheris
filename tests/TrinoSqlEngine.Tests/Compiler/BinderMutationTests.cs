using System.Collections;
using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using System.Security;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using Xunit;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>
/// Mutation-gate tests (M-12) for the shared binder logic: exact provider parameter shape (type, size, precision, scale, value) of every
/// binder, plus the refusals and their messages. The recording command exposes provider-style enum properties
/// (<c>OracleDbType</c>, <c>NpgsqlDbType</c>) so the reflection paths are exercised without a provider package.
/// </summary>
[Trait("Category", "SqlGate")]
public class BinderMutationTests
{
    public enum FakeOracleDbType { Varchar2 = 0, TimeStamp = 1 }
    public enum FakeNpgsqlDbType { Text = 0, Unknown = 1 }
    public enum FakeOtherEnum { Alpha = 0 }

    internal class RecParameter : DbParameter, IDbDataParameter
    {
        private DbType _dbType = DbType.Object;
        public override DbType DbType { get => _dbType; set => _dbType = value; }
        public bool DbTypeAssigned { get; private set; }
        public override ParameterDirection Direction { get; set; }
        public override bool IsNullable { get; set; }
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ParameterName { get; set; } = string.Empty;
        public override int Size { get; set; }
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string SourceColumn { get; set; } = string.Empty;
        public override bool SourceColumnNullMapping { get; set; }
        public override object? Value { get; set; }
        public override byte Precision { get; set; }
        public override byte Scale { get; set; }
        byte IDbDataParameter.Precision { get => Precision; set => Precision = value; }
        byte IDbDataParameter.Scale { get => Scale; set => Scale = value; }
        public override void ResetDbType() => _dbType = DbType.Object;
    }

    private sealed class OracleParameter : RecParameter
    {
        public FakeOracleDbType OracleDbType { get; set; } = FakeOracleDbType.Varchar2;
    }

    private sealed class NonEnumOracleParameter : RecParameter
    {
        public int OracleDbType { get; set; }
    }

    private sealed class ForeignEnumOracleParameter : RecParameter
    {
        public FakeOtherEnum OracleDbType { get; set; }
    }

    private sealed class NpgsqlParameter : RecParameter
    {
        public FakeNpgsqlDbType NpgsqlDbType { get; set; } = FakeNpgsqlDbType.Text;
    }

    private sealed class RecCollection : DbParameterCollection
    {
        private readonly List<DbParameter> _items = new();
        public override int Count => _items.Count;
        public override object SyncRoot => _items;
        public override int Add(object value) { _items.Add((DbParameter)value); return _items.Count - 1; }
        public override void AddRange(Array values) { foreach (var v in values) Add(v); }
        public override void Clear() => _items.Clear();
        public override bool Contains(object value) => _items.Contains((DbParameter)value);
        public override bool Contains(string value) => _items.Any(p => p.ParameterName == value);
        public override void CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);
        public override IEnumerator GetEnumerator() => _items.GetEnumerator();
        public override int IndexOf(object value) => _items.IndexOf((DbParameter)value);
        public override int IndexOf(string parameterName) => _items.FindIndex(p => p.ParameterName == parameterName);
        public override void Insert(int index, object value) => _items.Insert(index, (DbParameter)value);
        public override void Remove(object value) => _items.Remove((DbParameter)value);
        public override void RemoveAt(int index) => _items.RemoveAt(index);
        public override void RemoveAt(string parameterName) => _items.RemoveAt(IndexOf(parameterName));
        protected override DbParameter GetParameter(int index) => _items[index];
        protected override DbParameter GetParameter(string parameterName) => _items[IndexOf(parameterName)];
        protected override void SetParameter(int index, DbParameter value) => _items[index] = value;
        protected override void SetParameter(string parameterName, DbParameter value) => _items[IndexOf(parameterName)] = value;
    }

    internal class RecCommand(Func<RecParameter> factory) : DbCommand
    {
        private readonly RecCollection _parameters = new();
        public RecCommand() : this(() => new RecParameter()) { }
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string CommandText { get; set; } = string.Empty;
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; } = CommandType.StoredProcedure;
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; }
        protected override DbParameterCollection DbParameterCollection => _parameters;
        protected override DbTransaction? DbTransaction { get; set; }
        public override void Cancel() { }
        public override int ExecuteNonQuery() => throw new NotSupportedException();
        public override object? ExecuteScalar() => throw new NotSupportedException();
        public override void Prepare() { }
        protected override DbParameter CreateDbParameter() => factory();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotSupportedException();
        public RecParameter P(int i) => (RecParameter)_parameters[i];
    }

    private sealed class OracleCommandStub() : RecCommand(() => new OracleParameter())
    {
        public bool BindByName { get; set; }
    }

    private sealed class StuckBindByNameCommand : RecCommand
    {
        public bool BindByName { get => false; set { } }
    }

    private sealed class WrongTypeBindByNameCommand : RecCommand
    {
        public int BindByName { get; set; }
    }

    private sealed class ReadOnlyBindByNameCommand : RecCommand
    {
        public bool BindByName => true;
    }

    private sealed class WriteOnlyBindByNameCommand : RecCommand
    {
        public bool BindByName { set { } }
    }

    private static readonly IReadOnlyDictionary<string, object?> NoClient = new Dictionary<string, object?>();

    private static BoundParameter Bp(object? value, SqlParameterType type, ParameterOrigin origin = ParameterOrigin.QueryLiteral, string? columnType = null, string? source = null) =>
        new("?1", "p1", 0, value, type, origin, source, columnType);

    private static CompiledSql Sql(TargetSqlDialect dialect, params BoundParameter[] ps) =>
        new("SELECT 1", ps.ToImmutableArray(), dialect, SqlStatementClass.Select, ImmutableArray<SecurityPredicateId>.Empty, "v");

    private static RecParameter Bind(DbCommandCompiledSqlBinder binder, TargetSqlDialect dialect, BoundParameter p, RecCommand? command = null)
    {
        command ??= new RecCommand();
        binder.Bind(command, Sql(dialect, p), NoClient);
        return command.P(0);
    }

    private static RecParameter SqlServer(BoundParameter p) => Bind(new SqlServerCompiledSqlBinder(), TargetSqlDialect.SqlServer, p);

    // ---- string / binary sizes ----

    [Theory]
    [InlineData(0, 4000)]
    [InlineData(4000, 4000)]
    [InlineData(4001, -1)]
    public void SqlServer_StringSize_IsFixedUpToTheDefaultThenMax(int length, int expected)
    {
        var p = SqlServer(Bp(new string('a', length), SqlParameterType.String));
        Assert.Equal(DbType.String, p.DbType);
        Assert.Equal(expected, p.Size);
    }

    [Fact]
    public void NonSizingBinders_LeaveSizeAndPrecisionUntouched()
    {
        foreach (var (binder, dialect) in new (DbCommandCompiledSqlBinder, TargetSqlDialect)[]
                 {
                     (new DuckDbCompiledSqlBinder(), TargetSqlDialect.DuckDb), (new PostgreSqlCompiledSqlBinder(), TargetSqlDialect.PostgreSql),
                     (new DatabricksCompiledSqlBinder(), TargetSqlDialect.Databricks)
                 })
        {
            Assert.Equal(0, Bind(binder, dialect, Bp("abc", SqlParameterType.String)).Size);
            Assert.Equal(0, Bind(binder, dialect, Bp(new byte[] { 1 }, SqlParameterType.Binary)).Size);
            var d = Bind(binder, dialect, Bp(1.25m, SqlParameterType.Decimal));
            Assert.Equal(0, d.Precision);
            Assert.Equal(0, d.Scale);
            Assert.Equal(DbType.Decimal, d.DbType);
        }
    }

    [Fact]
    public void Oracle_DoesNotSizeStringsOrBinary()
    {
        var oracle = new OracleCompiledSqlBinder();
        using var c1 = new OracleCommandStub();
        oracle.Bind(c1, Sql(TargetSqlDialect.Oracle, Bp("abc", SqlParameterType.String)), NoClient);
        Assert.Equal(0, c1.P(0).Size);
        using var c2 = new OracleCommandStub();
        oracle.Bind(c2, Sql(TargetSqlDialect.Oracle, Bp(new byte[] { 1 }, SqlParameterType.Binary)), NoClient);
        Assert.Equal(0, c2.P(0).Size);
    }

    [Theory]
    [InlineData(0, 8000)]
    [InlineData(8000, 8000)]
    [InlineData(8001, -1)]
    public void SqlServer_BinarySize(int length, int expected)
    {
        var p = SqlServer(Bp(new byte[length], SqlParameterType.Binary));
        Assert.Equal(DbType.Binary, p.DbType);
        Assert.Equal(expected, p.Size);
    }

    [Theory]
    [InlineData("varchar(10)", 3, 10)]
    [InlineData("varchar(10)", 10, 10)]
    [InlineData("varchar(10)", 11, 8000)]
    [InlineData("varchar(10)", 8000, 8000)]
    [InlineData("varchar(10)", 8001, -1)]
    [InlineData("varchar(max)", 3, 8000)]
    [InlineData("varchar(max)", 0, 8000)]
    [InlineData("varchar(10)", 0, 10)]
    public void SqlServer_AnsiTenantBind_NeverTruncates(string columnType, int length, int expectedSize)
    {
        var p = SqlServer(Bp(new string('a', length), SqlParameterType.String, ParameterOrigin.Tenant, columnType, "t"));
        Assert.Equal(DbType.AnsiString, p.DbType);
        Assert.Equal(expectedSize, p.Size);
        Assert.Equal(new string('a', length), p.Value);
    }

    [Fact]
    public void AnsiBind_OnABinderWithoutSizing_KeepsTheProviderSize()
    {
        // Oracle never produces AnsiString; the SQL Server style is the only producer, so use a SQL Server column style value on DuckDb: unchanged.
        var p = Bind(new DuckDbCompiledSqlBinder(), TargetSqlDialect.DuckDb, Bp("abc", SqlParameterType.String, ParameterOrigin.Tenant, "varchar(10)", "t"));
        Assert.Equal(DbType.String, p.DbType);
        Assert.Equal(0, p.Size);
    }

    [Fact]
    public void SqlServer_DecimalPrecisionAndScale_FollowTheValue()
    {
        var p = SqlServer(Bp(1.50m, SqlParameterType.Decimal));
        Assert.Equal(DbType.Decimal, p.DbType);
        Assert.Equal(38, p.Precision);
        Assert.Equal(2, p.Scale);
        Assert.Equal(1.50m, p.Value);
        Assert.Equal(6, SqlServer(Bp(12.345678m, SqlParameterType.Decimal)).Scale);
        Assert.Equal(0, SqlServer(Bp(7m, SqlParameterType.Decimal)).Scale);
        Assert.Equal(2, SqlServer(Bp(-1.50m, SqlParameterType.Decimal)).Scale);
        Assert.Equal(28, SqlServer(Bp(0.0000000000000000000000000001m, SqlParameterType.Decimal)).Scale);
    }

    // ---- boolean / timestamp ----

    [Fact]
    public void Boolean_IsABooleanExceptOnOracleWhichGetsZeroOrOne()
    {
        var pg = Bind(new PostgreSqlCompiledSqlBinder(), TargetSqlDialect.PostgreSql, Bp(true, SqlParameterType.Boolean));
        Assert.Equal(DbType.Boolean, pg.DbType);
        Assert.Equal(true, pg.Value);
        Assert.Equal(false, Bind(new PostgreSqlCompiledSqlBinder(), TargetSqlDialect.PostgreSql, Bp(false, SqlParameterType.Boolean)).Value);

        var oracle = new OracleCompiledSqlBinder();
        using var c1 = new OracleCommandStub();
        oracle.Bind(c1, Sql(TargetSqlDialect.Oracle, Bp(true, SqlParameterType.Boolean)), NoClient);
        Assert.Equal(DbType.Int32, c1.P(0).DbType);
        Assert.Equal(1, c1.P(0).Value);
        using var c2 = new OracleCommandStub();
        oracle.Bind(c2, Sql(TargetSqlDialect.Oracle, Bp(false, SqlParameterType.Boolean)), NoClient);
        Assert.Equal(0, c2.P(0).Value);
    }

    [Fact]
    public void Timestamp_UsesTheProviderTypeWhenTheProviderHasOne_ElseTheDbType()
    {
        var ts = new DateTime(2026, 1, 2, 3, 4, 5, 678);
        var oracle = new OracleCompiledSqlBinder();
        using var withProvider = new OracleCommandStub();
        oracle.Bind(withProvider, Sql(TargetSqlDialect.Oracle, Bp(ts, SqlParameterType.Timestamp)), NoClient);
        var op = Assert.IsType<OracleParameter>(withProvider.P(0));
        Assert.Equal(FakeOracleDbType.TimeStamp, op.OracleDbType);
        Assert.Equal(DbType.Object, op.DbType);   // not overwritten with DateTime
        Assert.Equal(ts, op.Value);

        // The provider exposes OracleDbType, but not as an enum: fall back to the DbType.
        using var nonEnum = new OracleCommandStubWith(() => new NonEnumOracleParameter());
        oracle.Bind(nonEnum, Sql(TargetSqlDialect.Oracle, Bp(ts, SqlParameterType.Timestamp)), NoClient);
        Assert.Equal(DbType.DateTime, nonEnum.P(0).DbType);

        // The enum has no member of that name: fall back to the DbType.
        using var foreign = new OracleCommandStubWith(() => new ForeignEnumOracleParameter());
        oracle.Bind(foreign, Sql(TargetSqlDialect.Oracle, Bp(ts, SqlParameterType.Timestamp)), NoClient);
        Assert.Equal(DbType.DateTime, foreign.P(0).DbType);

        // No provider property at all.
        using var plain = new OracleCommandStubWith(() => new RecParameter());
        oracle.Bind(plain, Sql(TargetSqlDialect.Oracle, Bp(ts, SqlParameterType.Timestamp)), NoClient);
        Assert.Equal(DbType.DateTime, plain.P(0).DbType);

        Assert.Equal(DbType.DateTime2, SqlServer(Bp(ts, SqlParameterType.Timestamp)).DbType);
        Assert.Equal(DbType.DateTime2, Bind(new PostgreSqlCompiledSqlBinder(), TargetSqlDialect.PostgreSql, Bp(ts, SqlParameterType.Timestamp)).DbType);
        Assert.Equal(DbType.DateTime, Bind(new DuckDbCompiledSqlBinder(), TargetSqlDialect.DuckDb, Bp(ts, SqlParameterType.Timestamp)).DbType);
    }

    private sealed class OracleCommandStubWith(Func<RecParameter> factory) : RecCommand(factory)
    {
        public bool BindByName { get; set; }
    }

    // ---- server-inferred type (PostgreSQL) ----

    [Fact]
    public void PostgreSql_LiteralAgainstNonTextColumn_IsSentUntyped_ButTenantAndTextColumnsKeepTheTextBind()
    {
        var pg = new PostgreSqlCompiledSqlBinder();
        RecParameter Run(BoundParameter p)
        {
            var cmd = new RecCommand(() => new NpgsqlParameter());
            pg.Bind(cmd, Sql(TargetSqlDialect.PostgreSql, p), NoClient);
            return cmd.P(0);
        }

        var inferred = Assert.IsType<NpgsqlParameter>(Run(Bp("5", SqlParameterType.String, ParameterOrigin.QueryLiteral, "integer")));
        Assert.Equal(FakeNpgsqlDbType.Unknown, inferred.NpgsqlDbType);
        Assert.Equal(string.Empty, inferred.ParameterName);

        Assert.Equal(FakeNpgsqlDbType.Text, ((NpgsqlParameter)Run(Bp("5", SqlParameterType.String, ParameterOrigin.QueryLiteral, "varchar(10)"))).NpgsqlDbType);
        Assert.Equal(FakeNpgsqlDbType.Text, ((NpgsqlParameter)Run(Bp("5", SqlParameterType.String, ParameterOrigin.Tenant, "integer", "t"))).NpgsqlDbType);
        Assert.Equal(FakeNpgsqlDbType.Text, ((NpgsqlParameter)Run(Bp("5", SqlParameterType.String, ParameterOrigin.Policy, "integer", "t"))).NpgsqlDbType);
        Assert.Equal(FakeNpgsqlDbType.Text, ((NpgsqlParameter)Run(Bp("5", SqlParameterType.String, ParameterOrigin.QueryLiteral, null))).NpgsqlDbType);
    }

    [Fact]
    public void PostgreSql_ProviderWithoutNpgsqlDbType_FallsBackToTheTextBind()
    {
        var cmd = new RecCommand(() => new RecParameter());
        new PostgreSqlCompiledSqlBinder().Bind(cmd, Sql(TargetSqlDialect.PostgreSql, Bp("5", SqlParameterType.String, ParameterOrigin.QueryLiteral, "integer")), NoClient);
        Assert.Equal(DbType.String, cmd.P(0).DbType);
        Assert.Equal("5", cmd.P(0).Value);
    }

    // ---- refusals and messages ----

    [Fact]
    public void MissingClientParameter_MessageNamesTheParameter()
    {
        var ex = Assert.Throws<MissingClientParameterException>(() =>
            new SqliteCompiledSqlBinder().Bind(new RecCommand(), Sql(TargetSqlDialect.Sqlite, Bp(null, SqlParameterType.String, ParameterOrigin.ClientNamed, null, "who")), NoClient));
        Assert.Equal("No value was supplied for client parameter 'who'.", ex.Message);

        var noName = Assert.Throws<MissingClientParameterException>(() =>
            new SqliteCompiledSqlBinder().Bind(new RecCommand(), Sql(TargetSqlDialect.Sqlite, Bp(null, SqlParameterType.String, ParameterOrigin.ClientNamed)), NoClient));
        Assert.Equal("No value was supplied for client parameter '?'.", noName.Message);
    }

    [Fact]
    public void PositionalClientParameter_IsRefused()
    {
        var ex = Assert.Throws<SecurityException>(() =>
            new SqliteCompiledSqlBinder().Bind(new RecCommand(), Sql(TargetSqlDialect.Sqlite, Bp(null, SqlParameterType.String, ParameterOrigin.ClientPositional)), NoClient));
        Assert.Equal("Positional client parameters are not supported.", ex.Message);
    }

    [Fact]
    public void ClientValueOfAnUnsupportedType_IsRefusedByTypeName()
    {
        var ex = Assert.Throws<SecurityException>(() =>
            new SqliteCompiledSqlBinder().Bind(new RecCommand(), Sql(TargetSqlDialect.Sqlite, Bp(null, SqlParameterType.String, ParameterOrigin.ClientNamed, null, "x")),
                new Dictionary<string, object?> { ["x"] = new Uri("http://x") }));
        Assert.Equal("Unsupported client parameter value type 'Uri'.", ex.Message);
    }

    [Fact]
    public void UnsupportedParameterType_IsRefused()
    {
        var ex = Assert.Throws<SecurityException>(() =>
            new SqliteCompiledSqlBinder().Bind(new RecCommand(), Sql(TargetSqlDialect.Sqlite, Bp("x", (SqlParameterType)999)), NoClient));
        Assert.Equal("Unsupported parameter type '999'.", ex.Message);
    }

    [Fact]
    public void NullTypedParameter_IsBoundAsDbNull_EvenWithAValue()
    {
        var p = Bind(new SqliteCompiledSqlBinder(), TargetSqlDialect.Sqlite, Bp("x", SqlParameterType.Null));
        Assert.Equal(DbType.String, p.DbType);
        Assert.Same(DBNull.Value, p.Value);
        var nullValue = Bind(new SqliteCompiledSqlBinder(), TargetSqlDialect.Sqlite, Bp(null, SqlParameterType.Int32));
        Assert.Same(DBNull.Value, nullValue.Value);
        var dbNull = Bind(new SqliteCompiledSqlBinder(), TargetSqlDialect.Sqlite, Bp(DBNull.Value, SqlParameterType.Int32));
        Assert.Same(DBNull.Value, dbNull.Value);
    }

    [Fact]
    public void BinderArguments_AreNullChecked()
    {
        var binder = new SqliteCompiledSqlBinder();
        var compiled = Sql(TargetSqlDialect.Sqlite);
        Assert.Equal("compiled", Assert.Throws<ArgumentNullException>(() => binder.Bind(new RecCommand(), null!, NoClient)).ParamName);
        Assert.Equal("command", Assert.Throws<ArgumentNullException>(() => binder.Bind(null!, compiled, NoClient)).ParamName);
        Assert.Equal("clientParameterValues", Assert.Throws<ArgumentNullException>(() => binder.Bind(new RecCommand(), compiled, null!)).ParamName);
    }

    [Fact]
    public void BindForCheckedExecution_ChecksItsArguments()
    {
        var binder = new SqliteCompiledSqlBinder();
        Assert.Equal("compiled", Assert.Throws<ArgumentNullException>(() => binder.BindForCheckedExecution(new RecCommand(), null!, NoClient)).ParamName);
    }

    [Fact]
    public void CompiledStatementForAnotherDialect_IsRefused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new SqlServerCompiledSqlBinder().Bind(new RecCommand(), Sql(TargetSqlDialect.Sqlite), NoClient));
        Assert.Equal("The compiled SQL was generated for another dialect.", ex.Message);
    }

    [Fact]
    public void CommandWithExistingParameters_IsRefused()
    {
        var binder = new SqliteCompiledSqlBinder();
        var cmd = new RecCommand();
        binder.Bind(cmd, Sql(TargetSqlDialect.Sqlite, Bp(1, SqlParameterType.Int32)), NoClient);
        var ex = Assert.Throws<InvalidOperationException>(() => binder.Bind(cmd, Sql(TargetSqlDialect.Sqlite), NoClient));
        Assert.Equal("The command already has parameters; the binder owns all parameters of a governed command.", ex.Message);
    }

    [Fact]
    public void Bind_SetsTheCommandTextAndTextType()
    {
        var cmd = new RecCommand();
        new SqliteCompiledSqlBinder().Bind(cmd, Sql(TargetSqlDialect.Sqlite), NoClient);
        Assert.Equal("SELECT 1", cmd.CommandText);
        Assert.Equal(CommandType.Text, cmd.CommandType);
    }

    [Theory]
    [InlineData(TargetSqlDialect.DuckDb, true)]
    [InlineData(TargetSqlDialect.PostgreSql, true)]
    [InlineData(TargetSqlDialect.SqlServer, false)]
    [InlineData(TargetSqlDialect.Sqlite, false)]
    [InlineData(TargetSqlDialect.Databricks, false)]
    public void EmbeddedNul_IsRejectedOnlyWhereTheProviderTruncates(TargetSqlDialect dialect, bool rejects)
    {
        var binder = CompiledSqlBinders.For(dialect);
        var compiled = Sql(dialect, Bp("a\0b", SqlParameterType.String));
        if (rejects)
        {
            var ex = Assert.Throws<SecurityException>(() => binder.Bind(new RecCommand(), compiled, NoClient));
            Assert.Equal("A string parameter contains an embedded NUL character, which the provider would truncate.", ex.Message);
        }
        else
        {
            binder.Bind(new RecCommand(), compiled, NoClient);
        }
    }

    [Fact]
    public void Oracle_EmbeddedNul_IsAccepted()
    {
        using var cmd = new OracleCommandStub();
        new OracleCompiledSqlBinder().Bind(cmd, Sql(TargetSqlDialect.Oracle, Bp("a\0b", SqlParameterType.String)), NoClient);
        Assert.Equal("a\0b", cmd.P(0).Value);
    }

    [Fact]
    public void Oracle_BindByNameFailures_FailClosedWithTheirOwnMessage()
    {
        var oracle = new OracleCompiledSqlBinder();
        var compiled = Sql(TargetSqlDialect.Oracle);
        Assert.Equal("The Oracle command does not expose BindByName; positional binding is not permitted.",
            Assert.Throws<SecurityException>(() => oracle.Bind(new RecCommand(), compiled, NoClient)).Message);
        Assert.Equal("The Oracle command does not expose BindByName; positional binding is not permitted.",
            Assert.Throws<SecurityException>(() => oracle.Bind(new WrongTypeBindByNameCommand(), compiled, NoClient)).Message);
        Assert.Equal("The Oracle command does not expose BindByName; positional binding is not permitted.",
            Assert.Throws<SecurityException>(() => oracle.Bind(new ReadOnlyBindByNameCommand(), compiled, NoClient)).Message);
        Assert.Equal("The Oracle command does not expose BindByName; positional binding is not permitted.",
            Assert.Throws<SecurityException>(() => oracle.Bind(new WriteOnlyBindByNameCommand(), compiled, NoClient)).Message);
        Assert.Equal("BindByName could not be enabled on the Oracle command.",
            Assert.Throws<SecurityException>(() => oracle.Bind(new StuckBindByNameCommand(), compiled, NoClient)).Message);
    }

    [Theory]
    [InlineData(ParameterOrigin.Tenant)]
    [InlineData(ParameterOrigin.Policy)]
    public void Oracle_EmptyTenantOrPolicyString_MessageIsStable(ParameterOrigin origin)
    {
        using var cmd = new OracleCommandStub();
        var ex = Assert.Throws<SecurityException>(() =>
            new OracleCompiledSqlBinder().Bind(cmd, Sql(TargetSqlDialect.Oracle, Bp("", SqlParameterType.String, origin, null, "t")), NoClient));
        Assert.Equal("An empty tenant or policy string is rejected on Oracle (it would be NULL).", ex.Message);
    }

    [Theory]
    [InlineData(ParameterOrigin.QueryLiteral)]
    [InlineData(ParameterOrigin.Mask)]
    public void Oracle_EmptyStringFromOtherOrigins_IsAllowed(ParameterOrigin origin)
    {
        using var cmd = new OracleCommandStub();
        new OracleCompiledSqlBinder().Bind(cmd, Sql(TargetSqlDialect.Oracle, Bp("", SqlParameterType.String, origin)), NoClient);
        Assert.Equal(string.Empty, cmd.P(0).Value);
    }

    [Theory]
    [InlineData(TargetSqlDialect.Snowflake)]
    [InlineData(TargetSqlDialect.Ansi)]
    public void ForAndCheckedExecutorFor_RefuseDialectsWithoutABinder_WithTheDialectInTheMessage(TargetSqlDialect dialect)
    {
        var expected = $"Not yet supported by the governed SQL compiler: Dialect (no binder for dialect {dialect}).";
        var ex = Assert.Throws<SqlCompileNotSupportedException>(() => CompiledSqlBinders.For(dialect));
        Assert.Equal(expected, ex.Message);
        Assert.Equal(SqlCompileNotSupportedReason.Dialect, ex.Reason);
        Assert.Equal(expected, Assert.Throws<SqlCompileNotSupportedException>(() => CompiledSqlBinders.CheckedExecutorFor(dialect)).Message);
    }

#if DEBUG
    [Theory]
    [InlineData(TargetSqlDialect.SqlServer)]
    [InlineData(TargetSqlDialect.DuckDb)]
    [InlineData(TargetSqlDialect.PostgreSql)]
    [InlineData(TargetSqlDialect.Oracle)]
    [InlineData(TargetSqlDialect.Databricks)]
    [InlineData(TargetSqlDialect.Sqlite)]
    public void FactoryRefusalSeam_ReturnsABinderOfTheDialectThatNeverRefuses(TargetSqlDialect dialect)
    {
        var compiled = new CompiledSql("INSERT INTO t (a) SELECT 1", ImmutableArray<BoundParameter>.Empty, dialect, SqlStatementClass.Insert,
            ImmutableArray<SecurityPredicateId>.Empty, "v") { ExpectedAffectedRows = 1 };
        using var seam = CompilerTestSeams.Use(new CompilerTestSeams { DisabledControls = new HashSet<SecurityControl> { SecurityControl.BinderFactoryRefusal } });
        var binder = CompiledSqlBinders.For(dialect);
        Assert.True(binder.CanBind(dialect));
        using var cmd = new OracleCommandStub();
        binder.Bind(cmd, compiled, NoClient);   // would throw CheckedExecutionRequiredException on a refusing binder
        Assert.Equal("INSERT INTO t (a) SELECT 1", cmd.CommandText);
    }

    [Fact]
    public void FactoryRefusalSeam_StillRefusesDialectsWithoutABinder()
    {
        using var seam = CompilerTestSeams.Use(new CompilerTestSeams { DisabledControls = new HashSet<SecurityControl> { SecurityControl.BinderFactoryRefusal } });
        Assert.Equal("Not yet supported by the governed SQL compiler: Dialect (no binder for dialect Snowflake).",
            Assert.Throws<SqlCompileNotSupportedException>(() => CompiledSqlBinders.For(TargetSqlDialect.Snowflake)).Message);
    }
#endif

    // ---- ColumnBindAdapter ----

    [Fact]
    public void Adapt_NullOrDbNullValue_IsNeverAdapted()
    {
        Assert.Equal(new AdaptedBind(SqlParameterType.String, null, false, 0),
            ColumnBindAdapter.Adapt(ColumnBindStyle.PostgreSql, SqlParameterType.String, null, "integer"));
        Assert.Equal(new AdaptedBind(SqlParameterType.String, DBNull.Value, false, 0),
            ColumnBindAdapter.Adapt(ColumnBindStyle.PostgreSql, SqlParameterType.String, DBNull.Value, "integer"));
        Assert.False(ColumnBindAdapter.Adapt(ColumnBindStyle.SqlServer, SqlParameterType.String, null, "varchar(10)").AnsiString);
        Assert.False(ColumnBindAdapter.Adapt(ColumnBindStyle.SqlServer, SqlParameterType.String, DBNull.Value, "varchar(10)").AnsiString);
    }

    [Fact]
    public void Adapt_NoStyleOrNoColumnType_ReturnsTheInput()
    {
        Assert.Equal(new AdaptedBind(SqlParameterType.String, "5", false, 0), ColumnBindAdapter.Adapt(ColumnBindStyle.None, SqlParameterType.String, "5", "varchar(10)"));
        Assert.Equal(new AdaptedBind(SqlParameterType.String, "5", false, 0), ColumnBindAdapter.Adapt(ColumnBindStyle.SqlServer, SqlParameterType.String, "5", null));
        Assert.False(ColumnBindAdapter.Adapt(ColumnBindStyle.PostgreSql, SqlParameterType.String, "5", null).ServerInferredType);
    }

    [Fact]
    public void Adapt_Oracle_NumericTextAgainstNumericColumn_BecomesADecimal_OnlyForStringValuesOfStringType()
    {
        var neg = ColumnBindAdapter.Adapt(ColumnBindStyle.Oracle, SqlParameterType.String, "-1.5", "number");
        Assert.Equal(SqlParameterType.Decimal, neg.Type);
        Assert.Equal(-1.5m, neg.Value);
        Assert.False(neg.AnsiString);
        Assert.Equal(0, neg.DeclaredLength);
        Assert.Equal(SqlParameterType.Decimal, ColumnBindAdapter.Adapt(ColumnBindStyle.Oracle, SqlParameterType.String, "12", "number").Type);

        Assert.Equal(SqlParameterType.Int32, ColumnBindAdapter.Adapt(ColumnBindStyle.Oracle, SqlParameterType.Int32, "5", "number").Type);
        Assert.Equal(SqlParameterType.String, ColumnBindAdapter.Adapt(ColumnBindStyle.Oracle, SqlParameterType.String, "5", "varchar2(10)").Type);
        Assert.Equal(SqlParameterType.String, ColumnBindAdapter.Adapt(ColumnBindStyle.Oracle, SqlParameterType.String, "abc", "number").Type);
        Assert.Equal(SqlParameterType.String, ColumnBindAdapter.Adapt(ColumnBindStyle.Oracle, SqlParameterType.String, "1e5", "number").Type);
    }

    [Fact]
    public void Adapt_Oracle_NumberAgainstTextColumn_BecomesText()
    {
        var a = ColumnBindAdapter.Adapt(ColumnBindStyle.Oracle, SqlParameterType.Int32, 5, "varchar2(10)");
        Assert.Equal(new AdaptedBind(SqlParameterType.String, "5", false, 0), a);
        Assert.Equal(SqlParameterType.String, ColumnBindAdapter.Adapt(ColumnBindStyle.Oracle, SqlParameterType.Decimal, 1.5m, "nvarchar2(10)").Type);
        Assert.Equal(SqlParameterType.Int64, ColumnBindAdapter.Adapt(ColumnBindStyle.Oracle, SqlParameterType.Int64, 5L, "date").Type);
    }
}
