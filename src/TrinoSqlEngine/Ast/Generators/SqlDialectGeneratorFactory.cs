namespace TrinoSqlEngine.Ast.Generators;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using TrinoSqlEngine;

/// <summary>
/// Factory providing singleton instances of SQL dialect generators.
/// </summary>
public static class SqlDialectGeneratorFactory
{
    private static readonly FrozenDictionary<TargetSqlDialect, ISqlDialectGenerator> Generators =
        new Dictionary<TargetSqlDialect, ISqlDialectGenerator>
        {
            [TargetSqlDialect.Ansi] = new AnsiDialectGenerator(),
            [TargetSqlDialect.SqlServer] = new SqlServerDialectGenerator(),
            [TargetSqlDialect.PostgreSql] = new PostgreSqlDialectGenerator(),
            [TargetSqlDialect.Sqlite] = new SqliteDialectGenerator(),
            [TargetSqlDialect.DuckDb] = new DuckDbDialectGenerator(),
            [TargetSqlDialect.Snowflake] = new SnowflakeDialectGenerator(),
            [TargetSqlDialect.Oracle] = new OracleDialectGenerator()
        }.ToFrozenDictionary();

    public static ISqlDialectGenerator GetGenerator(TargetSqlDialect dialect)
    {
        if (Generators.TryGetValue(dialect, out var generator))
        {
            return generator;
        }

        return Generators[TargetSqlDialect.Ansi];
    }
}
