namespace TrinoSqlEngine;

/// <summary>The SQL dialect a statement is compiled for. Which of them is executable is decided by the capability table.</summary>
public enum TargetSqlDialect
{
    Ansi,
    PostgreSql,
    SqlServer,
    Sqlite,
    DuckDb,
    Snowflake,
    Oracle,
    Databricks
}
