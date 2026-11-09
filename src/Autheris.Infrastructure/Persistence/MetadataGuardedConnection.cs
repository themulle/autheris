using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Autheris.Application.Sql;
using Autheris.Domain.Common;

namespace Autheris.Infrastructure.Persistence;

/// <summary>
/// <see cref="SqlConnectionFactory"/> returns this decorator for every data-source connection. Each command is checked by
/// <see cref="SourceMetadataGuard"/> before the inner command runs; a violation throws, reports the rule id only (never the
/// statement) and never reaches the driver. The raw connection never leaves the factory and there is no way to unwrap it
/// outside this namespace.
/// </summary>
[SuppressMessage("Design", "CA1010", Justification = "Thin delegating wrapper.")]
internal sealed class MetadataGuardedConnection : DbConnection
{
    private readonly DbConnection _inner;

    public MetadataGuardedConnection(DbConnection inner, DatabaseDialect dialect, Action<string>? onViolation = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        Dialect = dialect;
        OnViolation = onViolation;
        _inner.StateChange += (_, e) => OnStateChange(e);
    }

    internal DbConnection Inner => _inner;

    internal DatabaseDialect Dialect { get; }

    internal Action<string>? OnViolation { get; }

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
        new MetadataGuardedTransaction(_inner.BeginTransaction(isolationLevel), this);

    protected override DbCommand CreateDbCommand() => new MetadataGuardedCommand(_inner.CreateCommand(), this);

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

/// <summary>A command that checks its text (and its behavior flags) before every execution path.</summary>
internal sealed class MetadataGuardedCommand : DbCommand
{
    private readonly DbCommand _inner;
    private MetadataGuardedConnection? _connection;
    private MetadataGuardedTransaction? _transaction;

    public MetadataGuardedCommand(DbCommand inner, MetadataGuardedConnection connection)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _connection = connection;
    }

    /// <summary>
    /// Part of the contract that the Oracle compiled-SQL binder and OracleBindByName look up by reflection (BindByName read and write).
    /// </summary>
    public bool BindByName
    {
        get
        {
            var prop = _inner.GetType().GetProperty("BindByName");
            return prop?.GetValue(_inner) as bool? ?? false;
        }
        set
        {
            var prop = _inner.GetType().GetProperty("BindByName");
            if (prop is not null)
            {
                try
                {
                    prop.SetValue(_inner, value);
                }
                catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is not null)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                }
            }
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

            _connection = value as MetadataGuardedConnection
                ?? throw new System.Security.SecurityException("A data-source command can only be attached to a connection of the gateway connection factory.");
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

            _transaction = value as MetadataGuardedTransaction
                ?? throw new System.Security.SecurityException("A data-source command can only join a transaction of the gateway connection factory.");
            _inner.Transaction = _transaction.Inner;
        }
    }

    public override void Cancel() => _inner.Cancel();

    public override void Prepare()
    {
        Check();
        _inner.Prepare();
    }

    protected override DbParameter CreateDbParameter() => _inner.CreateParameter();

    public override int ExecuteNonQuery()
    {
        Check();
        return _inner.ExecuteNonQuery();
    }

    public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
    {
        Check();
        return _inner.ExecuteNonQueryAsync(cancellationToken);
    }

    public override object? ExecuteScalar()
    {
        Check();
        return _inner.ExecuteScalar();
    }

    public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
    {
        Check();
        return _inner.ExecuteScalarAsync(cancellationToken);
    }

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        Check(behavior);
        return _inner.ExecuteReader(behavior);
    }

    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
    {
        Check(behavior);
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

    private void Check(CommandBehavior behavior = CommandBehavior.Default)
    {
        var connection = _connection ?? throw new InvalidOperationException("The command is not attached to a connection.");
        if (SourceMetadataGuard.IsSuppressed)
        {
            return;
        }

        string? rule = null;
        if ((behavior & (CommandBehavior.SchemaOnly | CommandBehavior.KeyInfo)) != 0)
        {
            rule = "R7";
        }
        else if (!SourceMetadataGuard.TryFindViolation(_inner.CommandText, connection.Dialect, out rule))
        {
            return;
        }

        connection.OnViolation?.Invoke(rule);
        throw new SourceMetadataAccessDeniedException(rule);
    }
}

/// <summary>Wraps the driver transaction so that <see cref="Connection"/> is the guarded connection.</summary>
internal sealed class MetadataGuardedTransaction(DbTransaction inner, MetadataGuardedConnection connection) : DbTransaction
{
    internal DbTransaction Inner => inner;

    public override IsolationLevel IsolationLevel => inner.IsolationLevel;

    protected override DbConnection? DbConnection => connection;

    public override void Commit() => inner.Commit();

    public override void Rollback() => inner.Rollback();

    public override Task CommitAsync(CancellationToken cancellationToken = default) => inner.CommitAsync(cancellationToken);

    public override Task RollbackAsync(CancellationToken cancellationToken = default) => inner.RollbackAsync(cancellationToken);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
