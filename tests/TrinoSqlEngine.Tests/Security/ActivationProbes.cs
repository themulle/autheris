using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Security;
using Autheris.Application.Interfaces;
using Autheris.Application.Security;
using Autheris.Application.Sql.Catalog;
using Autheris.Application.Sql.Errors;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Catalog;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using NSubstitute;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Security;
using TrinoSqlEngine.Governance;
using Xunit;

namespace TrinoSqlEngine.Tests.Security;

/// <summary>
/// Probes of the controls that the WP-X0v amendment listed as pending (X0a, X0b, X0c, X0j, X0k, X0p). Each passes on the real code
/// and fails when its mutant switches the control off. They are deterministic and need no container.
/// </summary>
internal static class ActivationProbes
{
    public static IEnumerable<SecurityProbe> Build()
    {
        // ---- X0b: the session pin read-back (SEC-X1-08) -------------------------------------------------------------------------
        yield return new("SessionPin_UnverifiedOrFailedPin_FailsClosed", "session-pin", () =>
        {
            Assert.Throws<SqlSourceUnavailableException>(() => SessionPinning.EnsurePinned(false, "search_path"));
            Assert.Throws<SqlSourceUnavailableException>(() => SessionPinning.EnsurePinned(null, "search_path"));
            SessionPinning.EnsurePinned(true, "search_path");
        });

        // ---- X0k: MaxAffectedRows, commit order and the binder factory (SEC-X1-07) --------------------------------------------
        yield return new("CheckedExecutor_InTransaction_RollsBackWhenMaxAffectedRowsIsExceeded", "checked-max-rows", () =>
        {
            using var connection = OpenTable();
            using var transaction = connection.BeginTransaction();
            var compiled = Compiled("INSERT INTO t (a) SELECT 1 UNION ALL SELECT 2", expected: null);
            var executor = CompiledSqlBinders.CheckedExecutorFor(TargetSqlDialect.DuckDb);
            Assert.ThrowsAsync<DmlAffectedRowsLimitException>(() =>
                executor.ExecuteInTransactionAsync(transaction, compiled, new Dictionary<string, object?>(), new CheckedDmlLimits(0, 1))).GetAwaiter().GetResult();
            Assert.Equal(0, Count(connection));
        });

        yield return new("CheckedExecutor_InTransaction_NeverCommitsBeforeTheCountIsChecked", "checked-commit-order", () =>
        {
            using var connection = OpenTable();
            using var transaction = connection.BeginTransaction();
            var compiled = Compiled("INSERT INTO t (a) SELECT 1 UNION ALL SELECT 2", expected: 1);   // writes 2 rows, 1 expected
            var executor = CompiledSqlBinders.CheckedExecutorFor(TargetSqlDialect.DuckDb);
            Assert.ThrowsAsync<DmlCheckOptionViolationException>(() =>
                executor.ExecuteInTransactionAsync(transaction, compiled, new Dictionary<string, object?>(), new CheckedDmlLimits(0, 0))).GetAwaiter().GetResult();
            Assert.Equal(0, Count(connection));
        });

        foreach (var dialect in new[]
        {
            TargetSqlDialect.SqlServer, TargetSqlDialect.DuckDb, TargetSqlDialect.PostgreSql,
            TargetSqlDialect.Oracle, TargetSqlDialect.Databricks, TargetSqlDialect.Sqlite
        })
        {
            var d = dialect;
            yield return new($"BinderFactory_For_RefusesACheckedStatement[{d}]", "binder-factory", () =>
            {
                using var command = new SqliteCommand();
                var compiled = Compiled("INSERT INTO t (a) SELECT 1", expected: 1, d);
                Assert.Throws<CheckedExecutionRequiredException>(() => CompiledSqlBinders.For(d).Bind(command, compiled, new Dictionary<string, object?>()));
            });
        }

        // ---- X0j: the Databricks tenant comparison never trusts the catalog collation (SEC-X1-10) --------------------------------
        yield return new("TenantComparison_Databricks_IsBinaryEvenForAUtf8BinaryCatalogCollation", "tenant-compare", () =>
        {
            var caps = DialectCapabilityTable.Default.Get(TargetSqlDialect.Databricks);
            var predicate = TenantPredicateFactory.Build(caps, "TenantId", "t", SqlParameterType.String, columnCollation: "UTF8_BINARY");
            Assert.True(predicate is BinaryExpression { Operator: BinaryOperator.Equal, Left: not ColumnReference },
                "a plain column = value comparison would follow the column collation");
        });

        // ---- X0a: the error mapper never returns a message of the exception (SEC-X1-12) -----------------------------------------
        yield return new("ErrorMapper_ReturnsTheFixedMessage_NeverTheExceptionMessage", "error-mapper", () =>
        {
            const string secret = "secret-driver-text Server=db01;Password=hunter2";
            Exception[] exceptions =
            {
                new SecurityException(secret),
                new InvalidOperationException(secret, new SecurityException(secret)),
                new UnfilteredDmlException()
            };
            foreach (var exception in exceptions)
            {
                Assert.True(SqlCompilerErrorMapper.TryMap(exception, out var mapping));
                Assert.Equal(GovernedSqlErrorCodes.FixedMessage(mapping.Code), mapping.Title);
                Assert.DoesNotContain("secret-driver-text", mapping.Title);
                Assert.DoesNotContain(exception.Message, mapping.Title);
                Assert.Equal(mapping.Title, SqlCompilerErrorMapper.FixedMessageOrDefault(exception, "fallback"));
            }
        });

        // ---- X0p: an unavailable tenant registry is never an allow (SEC-X1-13) --------------------------------------------------
        yield return new("TenantRegistryGate_StoreUnavailable_Throws_NeverRegistered", "tenant-registry", () =>
        {
            var gate = new TenantRegistryGate(new ThrowingRegistry(), new TenantRegistryGateOptions());
            Assert.ThrowsAsync<InvalidOperationException>(async () => await gate.RegisterOrGetAsync(new TenantId("acme"), "issuer", CancellationToken.None)).GetAwaiter().GetResult();
        });

        // ---- X0c: a catalog load failure denies the table (SEC-X1-09) ------------------------------------------------------------
        yield return new("Catalog_LoadFailure_DeniesTheTable", "catalog-load", () =>
        {
            var table = new TableIdentifier("sales", "dbo", "orders");
            var governance = Substitute.For<IGovernanceRepository>();
            governance.GetTableMetadataAsync(table, Arg.Any<CancellationToken>()).Returns(new TableMetadata
            {
                Table = new Table { SourceType = "SqlServer", SourceName = "ds", SchemaName = "dbo", TableName = "orders" },
                Identifier = table,
                Columns = new[] { new TableColumn { ColumnName = "id" } }
            });
            var gateway = new GatewayOptions
            {
                DataSources = new SqlDataSourceOptions { Connections = { ["ds"] = new DataSourceConnectionOptions { Provider = "SqlServer", ConnectionString = "x" } } }
            };
            var factory = Substitute.For<ISqlConnectionFactory>();
            factory.CreateOpenConnectionAsync(Arg.Any<DataSourceConnectionOptions>(), Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult<DbConnection>(new SqliteConnection()));
            var provider = new GovernedCatalogProvider(governance, Options.Create(gateway), factory,
                new IColumnMetadataReader[] { new FailingReader() }, new GovernedCatalogOptions { CacheTtlSeconds = 300 });

            var catalog = provider.GetCatalogAsync("ds", new[] { table }, CancellationToken.None).AsTask().GetAwaiter().GetResult();

            Assert.Null(catalog.Resolve(new SqlQualifiedName(new[] { new SqlIdentifier("dbo"), new SqlIdentifier("orders") })));
        });
    }

    private sealed class ThrowingRegistry : ITenantRegistry
    {
        public ValueTask<TenantRegistration> RegisterOrGetAsync(TenantId tenant, string? issuer, CancellationToken ct) =>
            throw new InvalidOperationException("registry store down");

        public ValueTask PreseedAsync(IEnumerable<string> configuredTenantIds, CancellationToken ct) => ValueTask.CompletedTask;
    }

    private sealed class FailingReader : IColumnMetadataReader
    {
        public TargetSqlDialect Dialect => TargetSqlDialect.SqlServer;

        public async IAsyncEnumerable<DbColumnMetadata> ReadAsync(DbConnection connection, TableIdentity table, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            throw new InvalidOperationException("metadata store down");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    private static CompiledSql Compiled(string sql, int? expected, TargetSqlDialect dialect = TargetSqlDialect.DuckDb) =>
        new(sql, ImmutableArray<BoundParameter>.Empty, dialect, SqlStatementClass.Insert, ImmutableArray<SecurityPredicateId>.Empty, "test")
        {
            ExpectedAffectedRows = expected
        };

    private static SqliteConnection OpenTable()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var create = connection.CreateCommand();
        create.CommandText = "CREATE TABLE t (a INTEGER)";
        create.ExecuteNonQuery();
        return connection;
    }

    private static long Count(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM t";
        return (long)command.ExecuteScalar()!;
    }
}
