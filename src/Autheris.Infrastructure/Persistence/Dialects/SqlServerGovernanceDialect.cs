namespace Autheris.Infrastructure.Persistence.Dialects;

using System;
using System.Data;
using System.Data.Common;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Interfaces;
using Microsoft.Data.SqlClient;

/// <summary>
/// SQL Server implementation of the governance SQL dialect (WP-SIMPL-20).
/// </summary>
internal sealed class SqlServerGovernanceDialect : IGovernanceSqlDialect
{
    private readonly Func<DbConnection> _connectionFactory;

    public SqlServerGovernanceDialect(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionFactory = () => new QuotedIdentifierSqlConnection(new SqlConnection(connectionString));
    }

    public SqlServerGovernanceDialect(Func<DbConnection> connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = () => new QuotedIdentifierSqlConnection(connectionFactory());
    }

    public string LockRowsForUpdate => "WITH (UPDLOCK, ROWLOCK)";

    public string UtcNow => "SYSUTCDATETIME()";

    public IParameterBudgetProvider ParameterBudget => DatabaseParameterBudgetProvider.Instance;

    public DbConnection CreateConnection() => _connectionFactory();

    public string InsertReturning(string column)
    {
        GovernanceSqlInput.Identifier(column, nameof(column));
        return "OUTPUT inserted." + Quote(column);
    }

    public string JsonValue(string column, string path)
    {
        GovernanceSqlInput.Identifier(column, nameof(column));
        GovernanceSqlInput.Identifier(path, nameof(path));
        return "JSON_VALUE(" + Quote(column) + ", '$." + path + "')";
    }

    public string StringAggregate(string expr, string separator)
    {
        GovernanceSqlInput.Identifier(expr, nameof(expr));
        return "STRING_AGG(" + expr + ", '" + GovernanceSqlInput.Separator(separator) + "')";
    }

    public string LimitWithoutOrder(int n)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(n);
        return "ORDER BY (SELECT NULL) OFFSET 0 ROWS FETCH NEXT " + n + " ROWS ONLY";
    }

    public bool IsUniqueViolation(DbException ex)
    {
        return ex is SqlException s && (s.Number == 2627 || s.Number == 2601);
    }

    public bool IsTransient(DbException ex)
    {
        return ex is SqlException s && (s.IsTransient || s.Number is 1205 or -2 or 40613 or 40197 or 40501 or 49918 or 49919 or 49920);
    }

    public string Upsert(GovernanceTable table, ReadOnlySpan<string> keyColumns, ReadOnlySpan<string> valueColumns)
    {
        if (keyColumns.IsEmpty)
        {
            throw new ArgumentException("Key columns cannot be empty for upsert.", nameof(keyColumns));
        }

        string tableName = Quote(table.ToSqlName());
        var sb = new StringBuilder();
        sb.Append("MERGE INTO ").Append(tableName).Append(" WITH (HOLDLOCK) AS target USING (VALUES (");

        for (int i = 0; i < keyColumns.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append('@').Append(keyColumns[i]);
        }

        for (int i = 0; i < valueColumns.Length; i++)
        {
            sb.Append(", @").Append(valueColumns[i]);
        }

        sb.Append(")) AS source (");

        for (int i = 0; i < keyColumns.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(Quote(keyColumns[i]));
        }

        for (int i = 0; i < valueColumns.Length; i++)
        {
            sb.Append(", ").Append(Quote(valueColumns[i]));
        }

        sb.Append(") ON (");

        for (int i = 0; i < keyColumns.Length; i++)
        {
            if (i > 0) sb.Append(" AND ");
            string col = Quote(keyColumns[i]);
            sb.Append("target.").Append(col).Append(" = source.").Append(col);
        }

        sb.Append(')');

        if (!valueColumns.IsEmpty)
        {
            sb.Append(" WHEN MATCHED THEN UPDATE SET ");
            for (int i = 0; i < valueColumns.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                string col = Quote(valueColumns[i]);
                sb.Append("target.").Append(col).Append(" = source.").Append(col);
            }
        }

        sb.Append(" WHEN NOT MATCHED THEN INSERT (");
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
            sb.Append("source.").Append(Quote(keyColumns[i]));
        }
        for (int i = 0; i < valueColumns.Length; i++)
        {
            sb.Append(", source.").Append(Quote(valueColumns[i]));
        }
        sb.Append(");");

        return sb.ToString();
    }

    internal static void AssertQuotedIdentifier(DbConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SET QUOTED_IDENTIFIER ON; SELECT SESSIONPROPERTY('QUOTED_IDENTIFIER');";
        var res = cmd.ExecuteScalar();
        if (res is null || Convert.ToInt32(res) != 1)
        {
            throw new InvalidOperationException("QUOTED_IDENTIFIER must be ON for SQL Server governance connection.");
        }
    }

    internal static async Task AssertQuotedIdentifierAsync(DbConnection connection, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SET QUOTED_IDENTIFIER ON; SELECT SESSIONPROPERTY('QUOTED_IDENTIFIER');";
        var res = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (res is null || Convert.ToInt32(res) != 1)
        {
            throw new InvalidOperationException("QUOTED_IDENTIFIER must be ON for SQL Server governance connection.");
        }
    }

    private static string Quote(string identifier) => "[" + GovernanceSqlInput.Identifier(identifier, nameof(identifier)) + "]";

    private sealed class QuotedIdentifierSqlConnection : DbConnection
    {
        private readonly DbConnection _inner;

        public QuotedIdentifierSqlConnection(DbConnection inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString
        {
            get => _inner.ConnectionString;
            set => _inner.ConnectionString = value;
        }

        public override string Database => _inner.Database;

        public override string DataSource => _inner.DataSource;

        public override string ServerVersion => _inner.ServerVersion;

        public override ConnectionState State => _inner.State;

        public override void ChangeDatabase(string databaseName) => _inner.ChangeDatabase(databaseName);

        public override void Close() => _inner.Close();

        public override void Open()
        {
            _inner.Open();
            AssertQuotedIdentifier(_inner);
        }

        public override async Task OpenAsync(CancellationToken cancellationToken)
        {
            await _inner.OpenAsync(cancellationToken).ConfigureAwait(false);
            await AssertQuotedIdentifierAsync(_inner, cancellationToken).ConfigureAwait(false);
        }

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
            _inner.BeginTransaction(isolationLevel);

        protected override DbCommand CreateDbCommand() => _inner.CreateCommand();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
