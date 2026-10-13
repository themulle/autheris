namespace Autheris.Tests.Unit;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Sql.Catalog;
using Autheris.Application.Sql.Masking;
using Autheris.Application.Sql.Policy;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine.Governance;

/// <summary>
/// X1: the governed pipeline compiles against a typed catalog and typed policies. Unit tests that construct the service directly
/// get them from here: the catalog is derived from the table metadata a test already supplies (in production it comes from the
/// database, <c>GovernedCatalogProvider</c>), and the typed policy factories are the production ones.
/// </summary>
internal static class GovernedTestSupport
{
    /// <summary>The execution guard of unit tests that run no provider statement under a timeout (the host registers the real guard; the service requires one).</summary>
    public static Autheris.Application.Sql.Interfaces.IProviderExecutionGuard Guard => Autheris.Application.Sql.Interfaces.PassThroughExecutionGuard.Instance;

    public static GovernancePolicyFactory PolicyFactory(ITableMetadataRepository? repository, IOptions<GatewayOptions>? options = null) =>
        new(new MetadataCatalogProvider(repository), repository ?? Substitute.For<ITableMetadataRepository>(), options ?? Options.Create(new GatewayOptions()));

    /// <param name="options">The gateway options of the service under test (the HMAC key reference lives in them).</param>
    /// <param name="secrets">The secret provider of the service under test; without it an HMAC mask degrades to Redact (fail closed).</param>
    public static TypedPolicySources TypedPolicies(IOptions<GatewayOptions>? options = null, IKeyVaultSecretProvider? secrets = null) =>
        new(new RowPolicyFactory(new PolicyPredicateRenderer()),
            new ColumnMaskPolicyFactory(new SqlDataMaskingProvider(options ?? Options.Create(new GatewayOptions()), secrets)));

    /// <summary>The compiled SQL text (markers only) of a statement, the successor of the former string-returning rewrite.</summary>
    public static async Task<string> RewriteSqlAsync(this GovernedSqlExecutionService service, string rawSql, ClaimsPrincipal user, TenantId tenantId, CancellationToken ct = default) =>
        (await service.CompileAsync(rawSql, user, tenantId, ct).ConfigureAwait(false)).Sql;

    /// <summary>Test catalog: the governance columns with their governance data types, the governance tenant column.</summary>
    private sealed class MetadataCatalogProvider(ITableMetadataRepository? repository) : IGovernedCatalogProvider
    {
        public async ValueTask<ITableCatalog> GetCatalogAsync(string dataSourceName, IReadOnlyCollection<TableIdentifier> tables, CancellationToken ct)
        {
            var entries = new List<TableCatalogEntry>();
            if (repository is not null)
            {
                foreach (var table in tables)
                {
                    var meta = await repository.GetTableMetadataAsync(table, ct).ConfigureAwait(false);
                    if (meta is null || meta.Columns.Count == 0)
                    {
                        continue;
                    }

                    entries.Add(new TableCatalogEntry(
                        new TableIdentity(meta.Table.SchemaName, meta.Table.TableName),
                        meta.Columns.Select(c => new CatalogColumn(c.ColumnName, c.DataType)).ToImmutableArray(),
                        meta.TenantColumnName,
                        1));
                }
            }

            string schema = entries.Count > 0 && entries.All(e => e.Identity.Schema == entries[0].Identity.Schema) ? entries[0].Identity.Schema : string.Empty;
            return new InMemoryTableCatalog(entries, schema);
        }
    }
}

/// <summary>A mocked command that owns real parameters, so that the binder (which owns every parameter of a governed command) can run.</summary>
internal static class DbCommandTestExtensions
{
    public static System.Data.Common.DbCommand WireParameters(this System.Data.Common.DbCommand command)
    {
        var inner = new Microsoft.Data.Sqlite.SqliteCommand();
        command.Parameters.Returns(inner.Parameters);
        command.CreateParameter().Returns(_ => inner.CreateParameter());
        return command;
    }

    public static IReadOnlyList<System.Data.Common.DbParameter> BoundParameters(this System.Data.Common.DbCommand command) =>
        command.Parameters.Cast<System.Data.Common.DbParameter>().ToList();

    public static IEnumerable<object?> BoundValues(this System.Data.Common.DbCommand command) => command.BoundParameters().Select(p => p.Value);

    /// <summary>The integer-valued binds (row limits and numeric literals are bound since X1).</summary>
    public static IEnumerable<long> BoundIntegers(this System.Data.Common.DbCommand command) =>
        command.BoundValues().Where(v => v is int or long or short or byte).Select(v => Convert.ToInt64(v));
}

/// <summary>Assertions on the bound values of a compiled statement: since X1 no value is part of the SQL text.</summary>
internal static class CompiledSqlTestExtensions
{
    public static IEnumerable<object?> ValuesOf(this TrinoSqlEngine.Ast.Emit.CompiledSql compiled, TrinoSqlEngine.Ast.Emit.ParameterOrigin origin) =>
        compiled.Parameters.Where(p => p.Origin == origin).Select(p => p.Value);

    public static int CountOfValue(this TrinoSqlEngine.Ast.Emit.CompiledSql compiled, TrinoSqlEngine.Ast.Emit.ParameterOrigin origin, object? value) =>
        compiled.Parameters.Count(p => p.Origin == origin && Equals(p.Value, value));

    /// <summary>The tenant is bound (once, deduplicated) and never inlined.</summary>
    public static void ShouldBindTenant(this TrinoSqlEngine.Ast.Emit.CompiledSql compiled, string tenant)
    {
        compiled.ValuesOf(TrinoSqlEngine.Ast.Emit.ParameterOrigin.Tenant).ShouldContain(tenant);
        compiled.Sql.ShouldNotContain($"'{tenant}'");
    }
}

/// <summary>
/// A minimal connection that hands out <see cref="FakeDbCommand"/>s and records what the executor did with it
/// (X1.2: commands created, transactions begun and completed).
/// </summary>
internal sealed class FakeDbConnection : System.Data.Common.DbConnection
{
    private System.Data.ConnectionState _state = System.Data.ConnectionState.Open;
    private readonly Func<FakeDbCommand> _commandFactory;

    public FakeDbConnection(Func<FakeDbCommand>? commandFactory = null) => _commandFactory = commandFactory ?? (() => new FakeDbCommand());

    public List<FakeDbCommand> Commands { get; } = [];

    public List<FakeDbTransaction> Transactions { get; } = [];

    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string ConnectionString { get; set; } = string.Empty;

    public override string Database => "fake";

    public override string DataSource => "fake";

    public override string ServerVersion => "0";

    public override System.Data.ConnectionState State => _state;

    public override void ChangeDatabase(string databaseName) { }

    public override void Close() => _state = System.Data.ConnectionState.Closed;

    public override void Open() => _state = System.Data.ConnectionState.Open;

    protected override System.Data.Common.DbTransaction BeginDbTransaction(System.Data.IsolationLevel isolationLevel)
    {
        var tx = new FakeDbTransaction(this, isolationLevel);
        Transactions.Add(tx);
        return tx;
    }

    /// <summary>A transaction as the session initializer would hand it out (PostgreSQL).</summary>
    public FakeDbTransaction BeginSessionTransaction() => (FakeDbTransaction)BeginTransaction();

    protected override System.Data.Common.DbCommand CreateDbCommand()
    {
        var command = _commandFactory();
        command.AttachTo(this);
        Commands.Add(command);
        return command;
    }
}

internal sealed class FakeDbTransaction(FakeDbConnection connection, System.Data.IsolationLevel isolationLevel) : System.Data.Common.DbTransaction
{
    public int Commits { get; private set; }

    public int Rollbacks { get; private set; }

    /// <summary>Whether the cancellation token of the last commit could be cancelled (L2: a commit never takes the caller's token).</summary>
    public bool? LastCommitTokenCanBeCanceled { get; private set; }

    protected override System.Data.Common.DbConnection DbConnection => connection;

    public override System.Data.IsolationLevel IsolationLevel => isolationLevel;

    public override void Commit() => Commits++;

    public override void Rollback() => Rollbacks++;

    public override Task CommitAsync(CancellationToken cancellationToken = default)
    {
        LastCommitTokenCanBeCanceled = cancellationToken.CanBeCanceled;
        Commits++;
        return Task.CompletedTask;
    }

    public override Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        Rollbacks++;
        return Task.CompletedTask;
    }
}

/// <summary>A command with real parameters (a throwaway <see cref="Microsoft.Data.Sqlite.SqliteCommand"/> owns the collection).</summary>
internal class FakeDbCommand : System.Data.Common.DbCommand
{
    private readonly Microsoft.Data.Sqlite.SqliteCommand _parameters = new();
    private System.Data.Common.DbConnection? _connection;

    public Func<System.Data.CommandBehavior, CancellationToken, Task<System.Data.Common.DbDataReader>>? OnExecuteReader { get; set; }

    public Func<CancellationToken, Task<int>>? OnExecuteNonQuery { get; set; }

    public int NonQueryResult { get; set; } = 1;

    internal void AttachTo(System.Data.Common.DbConnection connection) => _connection = connection;

    public IEnumerable<object?> BoundValues => _parameters.Parameters.Cast<System.Data.Common.DbParameter>().Select(p => p.Value);

    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string CommandText { get; set; } = string.Empty;

    public override int CommandTimeout { get; set; }

    public override System.Data.CommandType CommandType { get; set; } = System.Data.CommandType.Text;

    public override bool DesignTimeVisible { get; set; }

    public override System.Data.UpdateRowSource UpdatedRowSource { get; set; }

    protected override System.Data.Common.DbConnection? DbConnection { get => _connection; set => _connection = value; }

    protected override System.Data.Common.DbParameterCollection DbParameterCollection => _parameters.Parameters;

    protected override System.Data.Common.DbTransaction? DbTransaction { get; set; }

    public override void Cancel() { }

    public override void Prepare() { }

    public override int ExecuteNonQuery() => NonQueryResult;

    public override object? ExecuteScalar() => null;

    public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken) =>
        OnExecuteNonQuery is { } f ? f(cancellationToken) : Task.FromResult(NonQueryResult);

    protected override System.Data.Common.DbParameter CreateDbParameter() => _parameters.CreateParameter();

    protected override System.Data.Common.DbDataReader ExecuteDbDataReader(System.Data.CommandBehavior behavior) =>
        new System.Data.DataTable().CreateDataReader();

    protected override Task<System.Data.Common.DbDataReader> ExecuteDbDataReaderAsync(System.Data.CommandBehavior behavior, CancellationToken cancellationToken) =>
        OnExecuteReader is { } f ? f(behavior, cancellationToken) : Task.FromResult<System.Data.Common.DbDataReader>(new System.Data.DataTable().CreateDataReader());
}

/// <summary>An Oracle-like command: the binder requires the <c>BindByName</c> property (SEC-ADG-03) and sets it to true.</summary>
internal sealed class FakeBindByNameCommand : FakeDbCommand
{
    public bool BindByName { get; set; }
}
