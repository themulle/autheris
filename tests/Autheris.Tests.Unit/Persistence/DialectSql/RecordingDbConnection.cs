using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Autheris.Tests.Unit.Persistence.DialectSql;

/// <summary>One bound parameter as seen at execution time.</summary>
internal sealed record RecordedParameter(string Name, object? Value, DbType DbType, string ProviderType);

/// <summary>One executed command: its text, its parameters in binding order and whether a transaction was assigned to it.</summary>
internal sealed record RecordedCommand(string Kind, string CommandText, IReadOnlyList<RecordedParameter> Parameters, bool TransactionSet);

/// <summary>
/// ARCH-MNT-22 L2 runtime golden: a <see cref="DbConnection"/> that executes nothing and records every command. Parameters are the REAL
/// provider parameters of the dialect under test (the connection hands out the parameter collection of an unconnected provider command), so
/// their <see cref="DbType"/> and provider type can be compared with a reference built through the provider's <c>AddWithValue</c>.
/// Readers are served from <see cref="DataTable"/>s queued with <see cref="EnqueueReader"/>.
/// </summary>
internal sealed class RecordingDbConnection(Func<DbCommand> providerCommandFactory) : DbConnection
{
    private readonly Queue<DataTable> _readers = new();
    private string _connectionString = string.Empty;

    public List<RecordedCommand> Commands { get; } = [];

    public int NonQueryResult { get; set; } = 1;

    public object? ScalarResult { get; set; }

    public int TransactionSetCount => Commands.Count(c => c.TransactionSet);

    public void EnqueueReader(DataTable table) => _readers.Enqueue(table);

    internal DataTable? DequeueReader() => _readers.Count > 0 ? _readers.Dequeue() : null;

    internal DbCommand CreateProviderCommand() => providerCommandFactory();

    [AllowNull]
    public override string ConnectionString
    {
        get => _connectionString;
        set => _connectionString = value ?? string.Empty;
    }

    public override string Database => "recording";

    public override string DataSource => "recording";

    public override string ServerVersion => "0";

    public override ConnectionState State => ConnectionState.Open;

    public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

    public override void Close()
    {
    }

    public override void Open()
    {
    }

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => new RecordingDbTransaction(this);

    protected override DbCommand CreateDbCommand() => new RecordingDbCommand(this);

    internal static RecordedParameter Snapshot(DbParameter p) =>
        new(p.ParameterName, p.Value, p.DbType, ProviderTypeOf(p));

    private static string ProviderTypeOf(DbParameter p)
    {
        foreach (var propertyName in new[] { "NpgsqlDbType", "SqlDbType", "SqliteType" })
        {
            var property = p.GetType().GetProperty(propertyName);
            if (property is not null)
            {
                return p.GetType().Name + ":" + Convert.ToString(property.GetValue(p), CultureInfo.InvariantCulture);
            }
        }

        return p.GetType().Name;
    }
}

internal sealed class RecordingDbTransaction(RecordingDbConnection connection) : DbTransaction
{
    public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;

    protected override DbConnection DbConnection => connection;

    public override void Commit()
    {
    }

    public override void Rollback()
    {
    }
}

internal sealed class RecordingDbCommand : DbCommand
{
    private readonly RecordingDbConnection _connection;
    private readonly DbCommand _inner;
    private DbTransaction? _transaction;
    private bool _transactionSet;

    public RecordingDbCommand(RecordingDbConnection connection)
    {
        _connection = connection;
        _inner = connection.CreateProviderCommand();
    }

    [AllowNull]
    public override string CommandText
    {
        get => _inner.CommandText;
        set => _inner.CommandText = value;
    }

    public override int CommandTimeout { get; set; }

    public override CommandType CommandType { get; set; } = CommandType.Text;

    public override bool DesignTimeVisible { get; set; }

    public override UpdateRowSource UpdatedRowSource { get; set; }

    protected override DbConnection? DbConnection
    {
        get => _connection;
        set => throw new NotSupportedException();
    }

    protected override DbParameterCollection DbParameterCollection => _inner.Parameters;

    protected override DbTransaction? DbTransaction
    {
        get => _transaction;
        set
        {
            _transaction = value;
            _transactionSet = true;
        }
    }

    public override void Cancel()
    {
    }

    public override int ExecuteNonQuery()
    {
        Record("NonQuery");
        return _connection.NonQueryResult;
    }

    public override object? ExecuteScalar()
    {
        Record("Scalar");
        return _connection.ScalarResult;
    }

    public override void Prepare()
    {
    }

    protected override DbParameter CreateDbParameter() => _inner.CreateParameter();

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        Record("Reader");
        return (_connection.DequeueReader() ?? new DataTable()).CreateDataReader();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    private void Record(string kind)
    {
        var parameters = _inner.Parameters.Cast<DbParameter>().Select(RecordingDbConnection.Snapshot).ToList();
        _connection.Commands.Add(new RecordedCommand(kind, CommandText, parameters, _transactionSet));
    }
}
