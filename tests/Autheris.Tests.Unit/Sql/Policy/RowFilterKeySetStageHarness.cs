namespace Autheris.Tests.Unit.Sql.Policy;

using System.Collections.Immutable;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql.Policy;
using Autheris.Application.Sql.Policy.RowFilterKeySets;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Unit.VirtualFilters.Federation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;

/// <summary>Issue #107 WP-C: fakes and a builder for the stage, factory and rewriter tests of the key-set cache.</summary>
internal sealed class MutableGatewayOptionsMonitor(GatewayOptions initial) : IOptionsMonitor<GatewayOptions>
{
    public GatewayOptions CurrentValue { get; set; } = initial;

    public GatewayOptions Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<GatewayOptions, string?> listener) => null;
}

internal sealed class FakeKeyQueryCompiler : IPolicyKeyQueryCompiler
{
    public int Calls { get; private set; }

    public List<int> RowLimits { get; } = [];

    public Exception? Failure { get; set; }

    public string KeyType { get; set; } = "bigint";

    public string ForeignKeyType { get; set; } = "bigint";

    public CompiledKeyQuery CompileKeyQuery(PolicyKeyQueryRequest request, CancellationToken ct)
    {
        Calls++;
        RowLimits.Add(request.RowLimit);
        if (Failure is not null)
        {
            throw Failure;
        }

        string fk = ForeignKeys.TryGetValue(request.Correlated, out var named) ? named : "client_id";
        return new CompiledKeyQuery(
            $"SELECT DISTINCT c.id FROM customers c /* {fk} */",
            [new BoundParameter("@t", "__tenant", 0, request.Tenant.Value, SqlParameterType.String, ParameterOrigin.Tenant)],
            KeyType, ForeignKeyType, fk, "test/1");
    }

    /// <summary>The foreign key column (and with it the compiled text, hence the cache key) per site node; default <c>client_id</c>.</summary>
    public Dictionary<TrinoSqlEngine.Ast.Nodes.ExistsExpression, string> ForeignKeys { get; } = new(ReferenceEqualityComparer.Instance);
}

internal sealed class FakeKeySetLoader(FakeTimeProvider time) : IRowFilterKeySetLoader
{
    public int Calls;

    public List<RowFilterKeySetLoadRequest> Requests { get; } = [];

    public Func<RowFilterKeySetLoadRequest, CancellationToken, Task<RowFilterKeySetEntry>>? Override { get; set; }

    public int KeyCount { get; set; } = 3;

    public Task<RowFilterKeySetEntry> LoadAsync(RowFilterKeySetLoadRequest request, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        lock (Requests) Requests.Add(request);
        if (Override is not null)
        {
            return Override(request, ct);
        }

        var entry = new RowFilterKeySetEntry(
            Enumerable.Range(1, KeyCount).Select(i => (object)(long)i).ToArray(), false, time.GetUtcNow(), request.Tenant.Value, request.DataSourceName);
        return Task.FromResult(entry);
    }
}

internal sealed class FakeEpochs : IEpochValidationService
{
    public int Calls;

    public bool Authoritative { get; set; } = true;

    public long Epoch { get; set; } = 1;

    public ValueTask<IReadOnlyDictionary<TableIdentifier, long>?> TryGetAuthoritativeEpochsAsync(IReadOnlyCollection<TableIdentifier> tables, CancellationToken ct = default)
    {
        Interlocked.Increment(ref Calls);
        IReadOnlyDictionary<TableIdentifier, long>? result = Authoritative ? tables.Distinct().ToDictionary(t => t, _ => Epoch) : null;
        return ValueTask.FromResult(result);
    }

    public Task<bool> IsEpochValidAsync(TableIdentifier table, long cachedEpoch, CancellationToken ct = default) => Task.FromResult(true);

    public Task InvalidateEpochAsync(TableIdentifier table, CancellationToken ct = default) => Task.CompletedTask;

    public Task<long> GetCurrentEpochAsync(TableIdentifier table, CancellationToken ct = default) => Task.FromResult(Epoch);

    public Task<IReadOnlyDictionary<TableIdentifier, long>> GetCurrentEpochsAsync(IEnumerable<TableIdentifier> tables, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<TableIdentifier, long>>(tables.Distinct().ToDictionary(t => t, _ => Epoch));
}

/// <summary>A stage over fakes. <see cref="Options"/> is mutable per statement (the stage reads it through <see cref="IOptionsMonitor{T}"/>).</summary>
internal sealed class StageHarness : IDisposable
{
    public const string DataSource = "ds";

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero));

    public List<(LogLevel Level, string Message)> Logs { get; } = [];

    public FakeKeyQueryCompiler Compiler { get; } = new();

    public FakeEpochs Epochs { get; } = new();

    public FakeKeySetLoader Loader { get; }

    public RowFilterKeySetCache Cache { get; }

    public MutableGatewayOptionsMonitor Options { get; }

    public RowFilterKeySetStage Stage { get; }

    public StageHarness(
        bool allowSource = true,
        RowFilterEvaluationStrategy defaultStrategy = RowFilterEvaluationStrategy.KeySetCache,
        long cacheSizeLimit = 64L * 1024 * 1024,
        int concurrency = 4,
        int maxKeys = 2000,
        string provider = "SqlServer")
    {
        Loader = new FakeKeySetLoader(Time);
        Options = new MutableGatewayOptionsMonitor(Gateway(allowSource, defaultStrategy, maxKeys, provider));
        Cache = new RowFilterKeySetCache(cacheSizeLimit, concurrency, Time, new ListLogger<RowFilterKeySetCache>(Logs));
        Stage = new RowFilterKeySetStage(Options, Compiler, Epochs, Cache, Loader, new ListLogger<RowFilterKeySetStage>(Logs));
    }

    public static GatewayOptions Gateway(bool allowSource, RowFilterEvaluationStrategy strategy, int maxKeys = 2000, string provider = "SqlServer", TimeSpan? duration = null, string dataSource = DataSource) => new()
    {
        RowFilters = new RowFilterOptions
        {
            KeySetCache = new RowFilterKeySetCacheOptions
            {
                DefaultStrategy = strategy,
                MaxKeys = maxKeys,
                DefaultDuration = duration ?? TimeSpan.FromMinutes(5)
            }
        },
        DataSources = new SqlDataSourceOptions
        {
            Connections = new(StringComparer.OrdinalIgnoreCase)
            {
                [dataSource] = new DataSourceConnectionOptions { Provider = provider, ConnectionString = "Server=x", KeySetCacheAllowed = allowSource }
            }
        }
    };

    public void Dispose() => Cache.Dispose();

    public static readonly TableIdentity Orders = new("main", "orders");
    public static readonly TableIdentity Invoices = new("main", "invoices");

    public static TableIdentifier OrdersId => new(DataSource, "main", "orders");

    public static TableIdentifier InvoicesId => new(DataSource, "main", "invoices");

    public static TableIdentifier CustomersId => new(DataSource, "main", "customers");

    public static ITableCatalog Catalog() => new InMemoryTableCatalog(
    [
        new TableCatalogEntry(Orders, ImmutableArray.Create(new CatalogColumn("id", "bigint"), new CatalogColumn("client_id", "bigint")), "tenant_id", 1),
        new TableCatalogEntry(Invoices, ImmutableArray.Create(new CatalogColumn("id", "bigint"), new CatalogColumn("client_id", "bigint")), "tenant_id", 1)
    ], "main");

    public static RowFilterKeySetContext Context(
        IEnumerable<TableIdentity>? tables = null, string tenant = "t1", bool disabled = false, bool isRead = true, int userSqlLength = 4000,
        IReadOnlyDictionary<TableIdentity, TableMetadata?>? metadata = null, TargetSqlDialect dialect = TargetSqlDialect.SqlServer, string dataSource = DataSource)
    {
        var list = (tables ?? [Orders]).ToList();
        return new RowFilterKeySetContext(
            dataSource, new TenantId(tenant), dialect, Catalog(), new TenantBinding(RowPolicyFactory_TenantName, tenant, SqlParameterType.String),
            metadata ?? new Dictionary<TableIdentity, TableMetadata?>(),
            list.ToDictionary(t => t, t => (IReadOnlyList<TableIdentifier>)[new TableIdentifier(DataSource, t.Schema, t.Table), CustomersId]),
            disabled, isRead, userSqlLength, CompileRequestFactory.DefaultMaxExpansionFactor);
    }

    private const string RowPolicyFactory_TenantName = Autheris.Application.Policy.RowPolicyFactory.TenantParameterName;
}
