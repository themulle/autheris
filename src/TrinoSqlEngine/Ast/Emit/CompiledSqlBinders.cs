namespace TrinoSqlEngine.Ast.Emit;

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
#if DEBUG
        if (CompilerTestSeams.IsDisabled(SecurityControl.BinderFactoryRefusal))
        {
            return dialect switch
            {
                TargetSqlDialect.SqlServer => new SqlServerCompiledSqlBinder { NeverRefuses = true },
                TargetSqlDialect.DuckDb => new DuckDbCompiledSqlBinder { NeverRefuses = true },
                TargetSqlDialect.PostgreSql => new PostgreSqlCompiledSqlBinder { NeverRefuses = true },
                TargetSqlDialect.Oracle => new OracleCompiledSqlBinder { NeverRefuses = true },
                TargetSqlDialect.Databricks => new DatabricksCompiledSqlBinder { NeverRefuses = true },
                TargetSqlDialect.Sqlite => new SqliteCompiledSqlBinder { NeverRefuses = true },
                _ => throw NoBinder(dialect)
            };
        }
#endif
        return Resolve(dialect);
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
