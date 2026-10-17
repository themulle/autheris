namespace TrinoSqlEngine.Ast.Emit;

using System.Collections.Immutable;
using System.Data.Common;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;

/// <summary>
/// The closed set of production binders and checked executors (stateless singletons). It is not resolved through DI, so no
/// non-refusing binder can be swapped in (SEC-X1-07); the binder base class has a <c>private protected</c> constructor, and the
/// checked executor an internal one.
/// </summary>
public static class CompiledSqlBinders
{
    private static readonly SqlServerCompiledSqlBinder SqlServer = new();
    private static readonly DuckDbCompiledSqlBinder DuckDb = new();
    private static readonly PostgreSqlCompiledSqlBinder PostgreSql = new();
    private static readonly OracleCompiledSqlBinder Oracle = new();
    private static readonly DatabricksCompiledSqlBinder Databricks = new();
    private static readonly SqliteCompiledSqlBinder Sqlite = new();

    private static readonly CheckedDmlExecutor SqlServerExecutor = new(SqlServer);
    private static readonly CheckedDmlExecutor DuckDbExecutor = new(DuckDb);
    private static readonly CheckedDmlExecutor PostgreSqlExecutor = new(PostgreSql);
    private static readonly CheckedDmlExecutor OracleExecutor = new(Oracle);
    private static readonly DatabricksCheckedDmlExecutor DatabricksExecutor = new(Databricks);
    private static readonly CheckedDmlExecutor SqliteExecutor = new(Sqlite);

    /// <exception cref="SqlCompileNotSupportedException">Reason Dialect, for Snowflake, Ansi and any dialect without a binder.</exception>
    public static DbCommandCompiledSqlBinder For(TargetSqlDialect dialect)
    {
        return Resolve(dialect);
    }

    /// <summary>
    /// Issue #107 (SEC-RF107-20, D-WPC-8): binds a <see cref="CompiledKeyQuery"/> to a provider command through the same production binder as every
    /// governed statement: <c>CommandText</c> is <see cref="CompiledKeyQuery.Sql"/> and every <see cref="CompiledKeyQuery.Parameters"/> element is added
    /// by name in array order (the SQLite row limit is a <c>QueryLiteral</c> bind). The <see cref="CompiledSql"/> wrapper is created here, inside
    /// the engine (SEC-X1-07); a key query has no client parameters, so a client origin bind is refused by the binder.
    /// </summary>
    public static void BindKeyQuery(DbCommand command, CompiledKeyQuery query, TargetSqlDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(query);
        var compiled = new CompiledSql(query.Sql, query.Parameters, dialect, SqlStatementClass.Select, ImmutableArray<SecurityPredicateId>.Empty, query.CompilerVersion);
        Resolve(dialect).Bind(command, compiled, new Dictionary<string, object?>());
    }

    private static DbCommandCompiledSqlBinder Resolve(TargetSqlDialect dialect) => dialect switch
    {
        TargetSqlDialect.SqlServer => SqlServer,
        TargetSqlDialect.DuckDb => DuckDb,
        TargetSqlDialect.PostgreSql => PostgreSql,
        TargetSqlDialect.Oracle => Oracle,
        TargetSqlDialect.Databricks => Databricks,
        TargetSqlDialect.Sqlite => Sqlite,
        _ => throw NoBinder(dialect)
    };

    /// <exception cref="SqlCompileNotSupportedException">Reason Dialect, as for <see cref="For"/>.</exception>
    public static ICheckedDmlExecutor CheckedExecutorFor(TargetSqlDialect dialect) => dialect switch
    {
        TargetSqlDialect.SqlServer => SqlServerExecutor,
        TargetSqlDialect.DuckDb => DuckDbExecutor,
        TargetSqlDialect.PostgreSql => PostgreSqlExecutor,
        TargetSqlDialect.Oracle => OracleExecutor,
        TargetSqlDialect.Databricks => DatabricksExecutor,
        TargetSqlDialect.Sqlite => SqliteExecutor,
        _ => throw NoBinder(dialect)
    };

    private static SqlCompileNotSupportedException NoBinder(TargetSqlDialect dialect) =>
        new(SqlCompileNotSupportedReason.Dialect, $"no binder for dialect {dialect}");
}
