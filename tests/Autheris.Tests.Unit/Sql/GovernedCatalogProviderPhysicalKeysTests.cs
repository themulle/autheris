using System.Data.Common;
using System.Runtime.CompilerServices;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql.Catalog;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence.Catalog;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Governance;

namespace Autheris.Tests.Unit.Sql;

/// <summary>I403 WP-403-2: the physical key snapshot of the governed catalog provider (D-3), fail closed.</summary>
public sealed class GovernedCatalogProviderPhysicalKeysTests
{
    private const string Source = "ds";
    private static readonly TableIdentifier Orders = new("sales", "dbo", "orders");

    private sealed class FakeKeyReader(TargetSqlDialect dialect) : IKeyIndexMetadataReader
    {
        public TargetSqlDialect Dialect { get; } = dialect;
        public List<DbKeyIndexColumn> Rows { get; set; } = new();
        public Exception? Failure { get; set; }
        public int RowsToStream { get; set; } = -1;
        public bool Hang { get; set; }
        public int Calls { get; private set; }

        public async IAsyncEnumerable<DbKeyIndexColumn> ReadAsync(DbConnection connection, TableIdentity table, [EnumeratorCancellation] CancellationToken ct)
        {
            Calls++;
            if (Failure is not null) throw Failure;
            if (Hang) await new TaskCompletionSource().Task.WaitAsync(ct);
            if (RowsToStream >= 0)
            {
                for (var i = 0; i < RowsToStream; i++) yield return new DbKeyIndexColumn("ix", false, false, 1, "c" + i, false);
                yield break;
            }

            foreach (var r in Rows.ToArray()) yield return r;
            await Task.CompletedTask;
        }
    }

    private sealed class CollectingLogger : ILogger<GovernedCatalogProvider>
    {
        public List<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception) + (exception is null ? string.Empty : "|EX:" + exception.Message));
    }

    private static TableMetadata Table(string sourceName = Source, DataSourceType type = DataSourceType.Sql, string sourceType = "SqlServer") => new()
    {
        Identifier = Orders,
        Table = new Table { SourceType = sourceType, SourceName = sourceName, SchemaName = "dbo", TableName = "orders", DataSourceType = type }
    };

    private static (GovernedCatalogProvider Sut, FakeKeyReader Reader, ISqlConnectionFactory Factory, CollectingLogger Log) Build(
        string provider = "SqlServer", TargetSqlDialect dialect = TargetSqlDialect.SqlServer, bool registerReader = true, TimeSpan? timeout = null)
    {
        var reader = new FakeKeyReader(dialect);
        var gateway = new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions { Connections = { [Source] = new DataSourceConnectionOptions { Provider = provider, ConnectionString = "x" } } }
        };
        var factory = Substitute.For<ISqlConnectionFactory>();
        factory.CreateOpenConnectionAsync(Arg.Any<DataSourceConnectionOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<DbConnection>(new SqliteConnection()));
        var log = new CollectingLogger();
        var sut = new GovernedCatalogProvider(Substitute.For<IGovernanceRepository>(), Options.Create(gateway), factory,
            Array.Empty<IColumnMetadataReader>(), new GovernedCatalogOptions { CacheTtlSeconds = 60 }, TimeProvider.System, log,
            registerReader ? new IKeyIndexMetadataReader[] { reader } : Array.Empty<IKeyIndexMetadataReader>())
        {
            PhysicalKeyReadTimeout = timeout ?? TimeSpan.FromSeconds(30)
        };
        return (sut, reader, factory, log);
    }

    private static DbKeyIndexColumn Row(string index, int ordinal, string column, bool pk = false, bool clustered = false, bool desc = false) =>
        new(index, pk, clustered, ordinal, column, desc);

    [Fact]
    public async Task Read_BuildsKeysWithDirections()
    {
        var (sut, reader, _, _) = Build();
        reader.Rows = [Row("pk", 1, "id", pk: true, clustered: true), Row("pk", 2, "ts", pk: true, clustered: true, desc: true), Row("uq", 1, "code")];

        var snapshot = await sut.ReadAsync(Table(), CancellationToken.None);

        snapshot.State.ShouldBe(PhysicalKeySnapshotState.Known);
        snapshot.Dialect.ShouldBe(TargetSqlDialect.SqlServer);
        snapshot.Keys!.Select(k => (k.IsPrimaryKey, string.Join(",", k.Columns), string.Join(",", k.Directions))).ToArray().ShouldBe(new[]
        {
            (true, "id,ts", "Ascending,Descending"), (false, "code", "Ascending")
        });
    }

    [Fact]
    public async Task LoginBreakerOpen_Unavailable()
    {
        // I403-T-21
        var (sut, reader, factory, _) = Build();
        factory.CreateOpenConnectionAsync(Arg.Any<DataSourceConnectionOptions>(), Arg.Any<CancellationToken>())
            .Returns<Task<DbConnection>>(_ => throw new SqlSourceLoginBlockedException(TimeSpan.FromSeconds(30), "x"));

        (await sut.ReadAsync(Table(), CancellationToken.None)).State.ShouldBe(PhysicalKeySnapshotState.Unavailable);
        reader.Calls.ShouldBe(0);
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "I403-SC-03")]
    public async Task PhysicalRead_UsesGovernedProviderPathAndBreaker_NoSecondConnection()
    {
        // I403-T-S02: the single connection factory is the only way in, called once per read; an open breaker means no reader call
        var (sut, reader, factory, _) = Build();
        reader.Rows = [Row("pk", 1, "id", pk: true)];

        await sut.ReadAsync(Table(), CancellationToken.None);
        await factory.Received(1).CreateOpenConnectionAsync(Arg.Any<DataSourceConnectionOptions>(), Arg.Any<CancellationToken>());

        factory.ClearReceivedCalls();
        factory.CreateOpenConnectionAsync(Arg.Any<DataSourceConnectionOptions>(), Arg.Any<CancellationToken>())
            .Returns<Task<DbConnection>>(_ => throw new SqlSourceLoginBlockedException(TimeSpan.FromSeconds(30), "x"));
        var before = reader.Calls;
        (await sut.ReadAsync(Table(), CancellationToken.None)).State.ShouldBe(PhysicalKeySnapshotState.Unavailable);
        reader.Calls.ShouldBe(before);
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "I403-SC-01")]
    public async Task DialectMismatch_Unavailable()
    {
        // I403-T-22: the table says PostgreSql, the configured connection is SqlServer
        var (sut, reader, factory, _) = Build();

        (await sut.ReadAsync(Table(sourceType: "PostgreSql"), CancellationToken.None)).State.ShouldBe(PhysicalKeySnapshotState.Unavailable);
        reader.Calls.ShouldBe(0);
        await factory.DidNotReceiveWithAnyArgs().CreateOpenConnectionAsync(default!, default);
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "I403-SC-11")]
    public async Task AdmissionDeniedTable_NoPhysicalRead_NoFinding()
    {
        // I403-T-S11: unknown data source and non-SQL tables are never read
        var (sut, reader, factory, _) = Build();

        (await sut.ReadAsync(Table(sourceName: "other"), CancellationToken.None)).State.ShouldBe(PhysicalKeySnapshotState.Unavailable);
        (await sut.ReadAsync(Table(type: DataSourceType.HttpDeclarative), CancellationToken.None)).State.ShouldBe(PhysicalKeySnapshotState.Unsupported);
        reader.Calls.ShouldBe(0);
        await factory.DidNotReceiveWithAnyArgs().CreateOpenConnectionAsync(default!, default);
    }

    [Fact]
    public async Task NoReaderForDialect_Unsupported()
    {
        var (sut, reader, _, _) = Build(registerReader: false);

        (await sut.ReadAsync(Table(), CancellationToken.None)).State.ShouldBe(PhysicalKeySnapshotState.Unsupported);
        reader.Calls.ShouldBe(0);
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "I403-SC-13")]
    public async Task EmptyResultWithReadableColumns_Unavailable()
    {
        // I403-T-23
        var (sut, _, _, _) = Build();

        (await sut.ReadAsync(Table(), CancellationToken.None)).State.ShouldBe(PhysicalKeySnapshotState.Unavailable);
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "I403-SC-10")]
    public async Task CapExceeded_Unavailable_NoPartialSnapshot()
    {
        // I403-T-24 / I403-T-S10 (row cap plus one aborts while streaming)
        var (sut, reader, _, _) = Build();
        reader.RowsToStream = TableKeyDefinitionValidator.MaxIndexesPerTable * TableKeyDefinitionValidator.MaxColumnsPerKey + 1;

        var snapshot = await sut.ReadAsync(Table(), CancellationToken.None);

        snapshot.State.ShouldBe(PhysicalKeySnapshotState.Unavailable);
        snapshot.Keys.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "I403-SC-10")]
    public async Task CommandTimeout_Unavailable_NoPartialUpdate()
    {
        // I403-T-S10 (timeout half)
        var (sut, reader, _, _) = Build(timeout: TimeSpan.FromMilliseconds(50));
        reader.Hang = true;

        var snapshot = await sut.ReadAsync(Table(), CancellationToken.None);

        snapshot.State.ShouldBe(PhysicalKeySnapshotState.Unavailable);
        snapshot.Keys.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "I403-SC-01")]
    public async Task OrdinalGapDuplicateOrDuplicateColumn_Unavailable()
    {
        // I403-T-S13
        DbKeyIndexColumn[][] bad =
        [
            [Row("pk", 1, "a", pk: true), Row("pk", 3, "b", pk: true)],              // gap
            [Row("pk", 1, "a", pk: true), Row("pk", 1, "b", pk: true)],              // duplicate ordinal
            [Row("pk", 1, "a", pk: true), Row("pk", 2, "A", pk: true)],              // duplicate column
            [Row("pk", 2, "a", pk: true)],                                           // does not start at 1
            [Row("a", 1, "x", pk: true), Row("b", 1, "y", pk: true)],                // two primary keys
            [Row("u", 1, "x"), Row("v", 1, "y"), Row("u", 2, "z")]                   // key split into two runs
        ];
        foreach (var rows in bad)
        {
            var (sut, reader, _, _) = Build();
            reader.Rows = rows.ToList();
            (await sut.ReadAsync(Table(), CancellationToken.None)).State.ShouldBe(PhysicalKeySnapshotState.Unavailable);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "I403-SC-09")]
    public async Task Exception_LoggedWithoutSqlOrNames()
    {
        // I403-T-25
        var (sut, reader, _, log) = Build();
        reader.Failure = new InvalidOperationException("SENTINEL-SQL select * from secret_table; Password=hunter2");

        (await sut.ReadAsync(Table(), CancellationToken.None)).State.ShouldBe(PhysicalKeySnapshotState.Unavailable);

        log.Messages.ShouldNotBeEmpty();
        log.Messages.ShouldAllBe(m => !m.Contains("SENTINEL", StringComparison.Ordinal) && !m.Contains("hunter2", StringComparison.Ordinal) &&
                                      !m.Contains("select", StringComparison.OrdinalIgnoreCase) && !m.Contains("EX:", StringComparison.Ordinal));
        log.Messages.ShouldContain(m => m.Contains(nameof(InvalidOperationException), StringComparison.Ordinal));
    }

    [Fact]
    public async Task CallerCancellation_Propagates()
    {
        var (sut, reader, _, _) = Build();
        reader.Hang = true;
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(50);

        await Should.ThrowAsync<OperationCanceledException>(async () => await sut.ReadAsync(Table(), cts.Token));
    }
}
