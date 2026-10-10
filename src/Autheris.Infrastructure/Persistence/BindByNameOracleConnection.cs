using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Security;
using Oracle.ManagedDataAccess.Client;

namespace Autheris.Infrastructure.Persistence;

/// <summary>
/// SEC-ADG-03 / CR-ADG-07: ODP.NET binds by POSITION unless <c>OracleCommand.BindByName</c> is true. With reused markers and
/// out-of-order parameters a positional bind attaches a tenant value to a user slot. <see cref="SqlConnectionFactory"/> returns
/// this wrapper for every Oracle connection, so no consumer can obtain a raw <see cref="OracleCommand"/>:
/// every command it creates has <c>BindByName = true</c>, setting it to false throws, and every execute method asserts it again.
/// An architecture test forbids the driver types outside this namespace.
/// </summary>
[SuppressMessage("Design", "CA1010", Justification = "Thin delegating wrapper.")]
internal sealed class BindByNameOracleConnection : DbConnection
{
    private readonly OracleConnection _inner;

    public BindByNameOracleConnection(OracleConnection inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _inner.StateChange += (_, e) => OnStateChange(e);
    }

    [AllowNull]
    public override string ConnectionString
    {
        get => _inner.ConnectionString;
        set => _inner.ConnectionString = value;
    }

    public override string Database => _inner.Database;

    public override string DataSource => _inner.DataSource;

    public override string ServerVersion => _inner.ServerVersion;

    public override ConnectionState State => _inner.State;

    public override int ConnectionTimeout => _inner.ConnectionTimeout;

    public override void ChangeDatabase(string databaseName) => _inner.ChangeDatabase(databaseName);

    public override void Open() => _inner.Open();

    public override Task OpenAsync(CancellationToken cancellationToken) => _inner.OpenAsync(cancellationToken);

    public override void Close() => _inner.Close();

    public override Task CloseAsync() => _inner.CloseAsync();

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
        new BindByNameOracleTransaction(_inner.BeginTransaction(isolationLevel), this);

    protected override DbCommand CreateDbCommand() => new BindByNameOracleCommand(_inner.CreateCommand(), this);

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

    internal OracleConnection Inner => _inner;
}

/// <summary>An Oracle command that always binds by name and refuses to run otherwise (CR-ADG-07).</summary>
internal sealed class BindByNameOracleCommand : DbCommand
{
    private readonly OracleCommand _inner;
    private BindByNameOracleConnection? _connection;
    private BindByNameOracleTransaction? _transaction;

    public BindByNameOracleCommand(OracleCommand inner, BindByNameOracleConnection connection)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _connection = connection;
        _inner.BindByName = true;
    }

    /// <summary>
    /// Part of the contract that the Oracle compiled-SQL binder looks up by reflection (<c>BindByName</c> read and write).
    /// It can be enabled, never disabled.
    /// </summary>
    public bool BindByName
    {
        get => _inner.BindByName;
        set
        {
            if (!value)
            {
                throw new SecurityException("Positional binding is not permitted on Oracle commands; BindByName cannot be disabled.");
            }

            _inner.BindByName = true;
        }
    }

    [AllowNull]
    public override string CommandText
    {
        get => _inner.CommandText;
        set => _inner.CommandText = value;
    }

    public override int CommandTimeout
    {
        get => _inner.CommandTimeout;
        set => _inner.CommandTimeout = value;
    }

    public override CommandType CommandType
    {
        get => _inner.CommandType;
        set => _inner.CommandType = value;
    }

    public override bool DesignTimeVisible
    {
        get => _inner.DesignTimeVisible;
        set => _inner.DesignTimeVisible = value;
    }

    public override UpdateRowSource UpdatedRowSource
    {
        get => _inner.UpdatedRowSource;
        set => _inner.UpdatedRowSource = value;
    }

    protected override DbConnection? DbConnection
    {
        get => _connection;
        set
        {
            if (value is null)
            {
                _connection = null;
                _inner.Connection = null;
                return;
            }

            _connection = value as BindByNameOracleConnection
                ?? throw new SecurityException("An Oracle command can only be attached to a connection of the gateway connection factory.");
            _inner.Connection = _connection.Inner;
        }
    }

    protected override DbParameterCollection DbParameterCollection => _inner.Parameters;

    protected override DbTransaction? DbTransaction
    {
        get => _transaction;
        set
        {
            if (value is null)
            {
                _transaction = null;
                _inner.Transaction = null;
                return;
            }

            _transaction = value as BindByNameOracleTransaction
                ?? throw new SecurityException("An Oracle command can only join a transaction of the gateway connection factory.");
            _inner.Transaction = (OracleTransaction)_transaction.Inner;
        }
    }

    public override void Cancel() => _inner.Cancel();

    public override void Prepare()
    {
        AssertBindByName();
        _inner.Prepare();
    }

    protected override DbParameter CreateDbParameter() => _inner.CreateParameter();

    public override int ExecuteNonQuery()
    {
        AssertBindByName();
        return _inner.ExecuteNonQuery();
    }

    public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
    {
        AssertBindByName();
        return _inner.ExecuteNonQueryAsync(cancellationToken);
    }

    public override object? ExecuteScalar()
    {
        AssertBindByName();
        return _inner.ExecuteScalar();
    }

    public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
    {
        AssertBindByName();
        return _inner.ExecuteScalarAsync(cancellationToken);
    }

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        AssertBindByName();
        return _inner.ExecuteReader(behavior);
    }

    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
    {
        AssertBindByName();
        return await _inner.ExecuteReaderAsync(behavior, cancellationToken).ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    private void AssertBindByName()
    {
        if (!_inner.BindByName)
        {
            throw new SecurityException("An Oracle command reached execution with BindByName = false; positional binding is not permitted.");
        }
    }
}

/// <summary>
/// CR-ADG-28: wraps the driver transaction so that <see cref="Connection"/> is the gateway connection; a command created from
/// <c>transaction.Connection</c> therefore also binds by name.
/// </summary>
internal sealed class BindByNameOracleTransaction : DbTransaction
{
    private readonly DbTransaction _inner;
    private readonly BindByNameOracleConnection _connection;

    public BindByNameOracleTransaction(DbTransaction inner, BindByNameOracleConnection connection)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    }

    internal DbTransaction Inner => _inner;

    public override IsolationLevel IsolationLevel => _inner.IsolationLevel;

    protected override DbConnection? DbConnection => _connection;

    public override void Commit() => _inner.Commit();

    public override void Rollback() => _inner.Rollback();

    public override Task CommitAsync(CancellationToken cancellationToken = default) => _inner.CommitAsync(cancellationToken);

    public override Task RollbackAsync(CancellationToken cancellationToken = default) => _inner.RollbackAsync(cancellationToken);

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
