namespace Autheris.Infrastructure.Persistence.Dialects;

using System;
using System.Data.Common;
using System.Text;
using Autheris.Domain.Interfaces;
using Npgsql;

/// <summary>
/// PostgreSQL implementation of the governance SQL dialect.
/// </summary>
internal sealed class PostgreSqlGovernanceDialect : IGovernanceSqlDialect
{
    private readonly Func<DbConnection> _connectionFactory;

    public PostgreSqlGovernanceDialect(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionFactory = () => new NpgsqlConnection(connectionString);
    }

    public PostgreSqlGovernanceDialect(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _connectionFactory = () => dataSource.CreateConnection();
    }

    public PostgreSqlGovernanceDialect(Func<DbConnection> connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public string LockRowsForUpdate => "FOR UPDATE";

    public string UtcNow => "CURRENT_TIMESTAMP";

    public IParameterBudgetProvider ParameterBudget => DatabaseParameterBudgetProvider.Instance;

    public DbConnection CreateConnection() => _connectionFactory();

    public string InsertReturning(string column)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        return "RETURNING " + Quote(column);
    }

    public string JsonValue(string column, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Quote(column) + "->>'" + path + "'";
    }

    public string StringAggregate(string expr, string separator)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expr);
        return "STRING_AGG(" + expr + ", '" + (separator ?? ",") + "')";
    }

    public string LimitWithoutOrder(int n)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(n);
        return "LIMIT " + n;
    }

    public bool IsUniqueViolation(DbException ex)
    {
        return ex is PostgresException pg && pg.SqlState == "23505";
    }

    public bool IsTransient(DbException ex)
    {
        return ex is PostgresException pg && (pg.IsTransient || pg.SqlState is "40001" or "40P01" or "08000" or "08003" or "08006");
    }

    public string Upsert(GovernanceTable table, ReadOnlySpan<string> keyColumns, ReadOnlySpan<string> valueColumns)
    {
        if (keyColumns.IsEmpty)
        {
            throw new ArgumentException("Key columns cannot be empty for upsert.", nameof(keyColumns));
        }

        string tableName = Quote(table.ToSqlName());
        var sb = new StringBuilder();
        sb.Append("INSERT INTO ").Append(tableName).Append(" (");

        for (int i = 0; i < keyColumns.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(Quote(keyColumns[i]));
        }

        for (int i = 0; i < valueColumns.Length; i++)
        {
            sb.Append(", ").Append(Quote(valueColumns[i]));
        }

        sb.Append(") VALUES (");

        for (int i = 0; i < keyColumns.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append('@').Append(keyColumns[i]);
        }

        for (int i = 0; i < valueColumns.Length; i++)
        {
            sb.Append(", @").Append(valueColumns[i]);
        }

        sb.Append(") ON CONFLICT (");

        for (int i = 0; i < keyColumns.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(Quote(keyColumns[i]));
        }

        sb.Append(')');

        if (valueColumns.IsEmpty)
        {
            sb.Append(" DO NOTHING");
        }
        else
        {
            sb.Append(" DO UPDATE SET ");
            for (int i = 0; i < valueColumns.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                string col = Quote(valueColumns[i]);
                sb.Append(col).Append(" = EXCLUDED.").Append(col);
            }
        }

        return sb.ToString();
    }

    private static string Quote(string identifier) => "\"" + identifier + "\"";
}
