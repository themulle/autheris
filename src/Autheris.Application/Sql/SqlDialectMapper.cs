namespace Autheris.Application.Sql;

using System;
using Autheris.Domain.Common;
using TrinoSqlEngine;

public static class SqlDialectMapper
{
    public static bool TryToTargetDialect(DatabaseDialect dialect, out TargetSqlDialect target)
    {
        switch (dialect)
        {
            case DatabaseDialect.SqlServer:
                target = TargetSqlDialect.SqlServer;
                return true;
            case DatabaseDialect.PostgreSql:
                target = TargetSqlDialect.PostgreSql;
                return true;
            case DatabaseDialect.Sqlite:
                target = TargetSqlDialect.Sqlite;
                return true;
            case DatabaseDialect.Oracle:
                target = TargetSqlDialect.Oracle;
                return true;
            default:
                target = default;
                return false;
        }
    }

    public static TargetSqlDialect ToTargetDialect(DatabaseDialect dialect) => dialect switch
    {
        DatabaseDialect.SqlServer => TargetSqlDialect.SqlServer,
        DatabaseDialect.PostgreSql => TargetSqlDialect.PostgreSql,
        DatabaseDialect.Sqlite => TargetSqlDialect.Sqlite,
        DatabaseDialect.Oracle => TargetSqlDialect.Oracle,
        _ => throw new NotSupportedException($"Target AST generation not supported for dialect '{dialect}'.")
    };

    public static bool IsExecutable(DatabaseDialect dialect) =>
        dialect is DatabaseDialect.SqlServer or DatabaseDialect.PostgreSql or DatabaseDialect.Sqlite;
}
