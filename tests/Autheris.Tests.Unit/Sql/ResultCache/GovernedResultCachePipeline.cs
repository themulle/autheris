namespace Autheris.Tests.Unit.Sql.ResultCache;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Common;
using Autheris.Application.Common.Quota;
using Autheris.Application.DataSources;
using Autheris.Application.Governance.Analysis;
using Autheris.Application.Sql.Cost;
using Autheris.Application.Governance.Caching;
using Autheris.Application.Policy;
using Autheris.Application.Security;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Paging;
using Autheris.Application.Sql.ResultCache;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Common;
using Autheris.Tests.Common.Fakes;
using Autheris.Tests.Unit.TestDoubles;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

/// <summary>What one request through the governed pipeline did: rows, the page outcome, the reported cache outcomes and the event order.</summary>
internal sealed class CachePipelineRun
{
    public List<string> Rows { get; } = [];

    public List<WebSqlResultCacheOutcome> Outcomes { get; } = [];

    public GovernedSqlPageOutcome? Page { get; set; }

    public WebSqlResultCacheOutcome? Last => Outcomes.Count == 0 ? null : Outcomes[^1];
}

/// <summary>
/// The real governed pipeline (rewrite, consent, compile, executor) over a fake PostgreSQL connection that serves a configurable
/// number of rows, with a real result cache bound to the same options. Counts connections and source executions.
/// </summary>
internal sealed class GovernedResultCachePipeline : IDisposable
{
    public const string Sql = "SELECT id, name FROM crm.customers ORDER BY id";

    public static readonly TenantId Tenant = new("tenant-1");

    private int _executions;
    private int _opens;

    public GovernedResultCachePipeline(
        int rows = 5,
        int maxMegabytes = 16,
        int sourceTtl = 300,
        int maxRowsPerEntry = 1000,
        long maxOffset = 10_000,
        int defaultMaxRows = 1000,
        IQueryCostQuota? costQuota = null,
        ILogger<GovernedSqlExecutionService>? logger = null,
        bool withEpochServices = true,
        ITableReadConcurrencyGate? gate = null)
    {
        Rows = rows;
        Options = new GatewayOptions
        {
            WebSql = new WebSqlOptions
            {
                Enabled = true,
                DefaultDataSourceName = "crm",
                DefaultMaxRows = defaultMaxRows,
                MaxAllowedRows = defaultMaxRows,
                Paging = new WebSqlPagingOptions { MaxOffset = maxOffset, TokenLifetimeMinutes = 15 },
                ResultCacheMaxMegabytes = maxMegabytes,
                ResultCacheMaxEntryMegabytes = 1,
                ResultCacheMaxUserMegabytes = 8,
                ResultCacheMaxRowsPerEntry = maxRowsPerEntry,
                ResultCacheMaxEntriesPerUser = 100,
            },
            DataSources = new SqlDataSourceOptions
            {
                Connections =
                {
                    ["crm"] = new DataSourceConnectionOptions
                    {
                        Provider = "PostgreSQL",
                        ConnectionString = "Server=localhost;Database=crm;User Id=postgres;Password=postgres;",
                        ResultCacheTtlSeconds = sourceTtl,
                    },
                },
            },
        };
        Monitor = new StaticOptionsMonitor<GatewayOptions>(Options);
        Time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Governance = new GovernanceEpochProvider(Store, Microsoft.Extensions.Options.Options.Create(new GovernanceCacheSettings { EpochPollSeconds = 60 }));
        Cache = new WebSqlResultCache(Monitor, withEpochServices ? Governance : null, withEpochServices ? Epochs : null, Time);
        Spy = new CountingCache(Cache);
        Source = () => ResultCacheFixtures.Rows(Rows);

        var repo = new InMemoryTableMetadataRepository(CustomersTable("crm"), CustomersTable("dbo"), CustomersTable("public"));
        var consents = new InMemoryConsentRepository();
        var resolution = new AllowAllConsentResolution();
        Audit = new CapturingAuditWriter(this);

        IHostEnvironment env = new TestHostEnvironment("Development");
        var options = Microsoft.Extensions.Options.Options.Create(Options);
        var digester = new CompiledSqlAuditDigester(options, null, env);
        var factory = new DelegatingConnectionFactory(() =>
        {
            Interlocked.Increment(ref _opens);
            return new FakeDbConnection(() =>
            {
                var command = new FakeDbCommand();
                command.OnExecuteReader = (_, _) =>
                {
                    if (command.CommandText.Contains("customers", StringComparison.OrdinalIgnoreCase))
                    {
                        Interlocked.Increment(ref _executions);
                    }

                    return Task.FromResult(Source());
                };
                return command;
            });
        });

        Service = GovernedSqlExecutionServiceFactory.Create(
            options,
            NullAuditLogRepository.Instance,
            executionGuard: GovernedTestSupport.Guard,
            consentResolution: resolution,
            tableRepository: repo,
            connectionFactory: factory,
            policyFactory: GovernedTestSupport.PolicyFactory(repo, options),
            typedPolicies: GovernedTestSupport.TypedPolicies(options),
            logger: logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<GovernedSqlExecutionService>.Instance,
            consentRepository: consents,
            auditDigester: digester,
            auditWriter: Audit,
            environment: env,
            pageTokenCodec: new WebSqlPageTokenCodec(digester),
            costQuota: costQuota,
            resultCache: Spy,
            concurrencyGate: gate);
    }

    public int Rows { get; }

    public GatewayOptions Options { get; }

    public StaticOptionsMonitor<GatewayOptions> Monitor { get; }

    public FakeTimeProvider Time { get; }

    public MutableEpochStore Store { get; } = new();

    public FakeEpochService Epochs { get; } = new();

    public GovernanceEpochProvider Governance { get; }

    public WebSqlResultCache Cache { get; }

    public CountingCache Spy { get; }

    /// <summary>When set, the required ALLOW audit write fails (fail closed).</summary>
    public bool FailAudit { get; set; }

    public CapturingAuditWriter Audit { get; }

    public GovernedSqlExecutionService Service { get; }

    public Func<DbDataReader> Source { get; set; }

    public List<AuditLogEntry> AuditEntries => Audit.Entries;

    /// <summary>The order of audit and row events of all requests (a hit must audit before its first row).</summary>
    public List<string> Events { get; } = [];

    public int Executions => Volatile.Read(ref _executions);

    public int ConnectionsOpened => Volatile.Read(ref _opens);

    public static ClaimsPrincipal User(string sid = "user-1", string? extraGroup = null)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, sid),
            new("sub", sid),
            new("tenant_id", Tenant.Value),
        };
        if (extraGroup is not null)
        {
            claims.Add(new Claim(ClaimTypes.GroupSid, extraGroup, ClaimValueTypes.String, IdentityClaimIssuers.Pipeline));
        }

        return Autheris.Tests.Unit.TestSupport.PipelinePrincipals.MarkComplete(new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth")));
    }

    public async Task<CachePipelineRun> RunAsync(
        ClaimsPrincipal? user = null,
        string sql = Sql,
        SqlPageWindow? page = null,
        string? nextToken = null,
        WebSqlResultCacheMode mode = WebSqlResultCacheMode.Default,
        string format = "json",
        bool participate = true,
        Func<DbDataReader, CancellationToken, Task>? writer = null,
        CancellationToken ct = default)
    {
        var run = new CachePipelineRun();
        var request = new GovernedSqlQueryRequest(
            sql,
            DataSourceName: "crm",
            Transport: SqlTransports.WebSql,
            Page: page,
            NextToken: nextToken,
            OnPageOutcome: outcome => run.Page = outcome)
        {
            ResultCache = participate ? new WebSqlResultCacheRequest(mode, format, outcome => run.Outcomes.Add(outcome)) : null,
        };
        await Service.ExecuteGovernedQueryAsync(
            request,
            user ?? User(),
            Tenant,
            writer ?? (async (reader, token) =>
            {
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    lock (Audit.Entries)
                    {
                        if (run.Rows.Count == 0)
                        {
                            Events.Add("row");
                        }
                    }

                    run.Rows.Add($"{reader.GetValue(0)}:{reader.GetValue(1)}");
                }
            }),
            ct).ConfigureAwait(false);
        return run;
    }

    public void Dispose()
    {
        Cache.Dispose();
        Governance.Dispose();
    }

    private static TableMetadata CustomersTable(string schema) => new()
    {
        Identifier = new TableIdentifier("crm", schema, "customers"),
        Table = new Table { TableName = "customers", SchemaName = schema, SourceName = "crm", SourceType = "PostgreSQL", IsActive = true, DataSourceType = DataSourceType.Sql },
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "int" },
            new TableColumn { ColumnName = "name", DataType = "varchar" },
        ],
    };
}

/// <summary>Captures formatted log messages (message text and exception type only) of the executor.</summary>
internal sealed class CapturedLogger : ILogger<GovernedSqlExecutionService>
{
    public List<string> Messages { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (Messages)
        {
            Messages.Add(formatter(state, exception) + (exception is null ? string.Empty : "|" + exception.GetType().Name));
        }
    }
}

/// <summary>Delegates to the real cache and records what the executor asked it (lookups and the key hashes it built).</summary>
internal sealed class CountingCache(IWebSqlResultCache inner) : IWebSqlResultCache
{
    private int _lookups;

    public int Lookups => Volatile.Read(ref _lookups);

    public List<string> Hashes { get; } = [];

    public bool IsEnabled => inner.IsEnabled;

    public ResultCacheLimits Limits => inner.Limits;

    public ValueTask<WebSqlResultCacheLookup> LookupAsync(WebSqlResultCacheKey key, bool bypass, TimeSpan followerWait, CancellationToken ct)
    {
        Interlocked.Increment(ref _lookups);
        lock (Hashes)
        {
            Hashes.Add(key.Hash);
        }

        return inner.LookupAsync(key, bypass, followerWait, ct);
    }

    public bool IsMarkedTooLarge(string hash) => inner.IsMarkedTooLarge(hash);

    public ValueTask<bool> TryCommitAsync(
        WebSqlResultCacheLookup fill, CachedWebSqlResult result, TimeSpan ttl, string dataSource, string? traceId, CancellationToken ct,
        IResultCacheReservation? reservation = null) =>
        inner.TryCommitAsync(fill, result, ttl, dataSource, traceId, ct, reservation);

    public void MarkTooLarge(WebSqlResultCacheLookup fill, TimeSpan ttl) => inner.MarkTooLarge(fill, ttl);

    public IResultCacheReservation? TryReserve(WebSqlResultCacheKey key) => inner.TryReserve(key);
}

/// <summary>A read concurrency gate that can be switched to refuse and that counts the leases still held.</summary>
internal sealed class SwitchableReadGate : ITableReadConcurrencyGate
{
    private int _active;

    public bool Deny { get; set; }

    public int Active => Volatile.Read(ref _active);

    public IDisposable? TryEnter(string key, int maxConcurrent)
    {
        if (Deny)
        {
            return null;
        }

        Interlocked.Increment(ref _active);
        return new Lease(this);
    }

    private sealed class Lease(SwitchableReadGate gate) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Interlocked.Decrement(ref gate._active);
            }
        }
    }
}

/// <summary>Allows every table with every column (consent is not what these tests exercise).</summary>
internal sealed class AllowAllConsentResolution : IConsentResolutionService
{
    public TableAccessDecision ResolveAccess(
        Sid userSid,
        IReadOnlySet<Sid> subjectGroupSids,
        IReadOnlySet<string> userRoles,
        TableIdentifier table,
        IReadOnlyList<Consent> activeConsents,
        DatabaseDialect dialect = DatabaseDialect.SqlServer,
        IReadOnlySet<Sid>? allUserSids = null,
        IReadOnlySet<string>? callerScopes = null) =>
        TableAccessDecision.Allowed(table, new Dictionary<string, ColumnAccessLevel>(), null, hasUnconstrainedColumnAllow: true);
}

internal sealed class DelegatingConnectionFactory(Func<DbConnection> create) : ISqlConnectionFactory
{
    public Task<DbConnection> CreateOpenConnectionAsync(DataSourceConnectionOptions options, CancellationToken ct = default) =>
        Task.FromResult(create());
}

/// <summary>Records ALLOW query audits in order (also into the shared event list) and can fail them closed.</summary>
internal sealed class CapturingAuditWriter(GovernedResultCachePipeline? pipeline = null) : IAuditWriter
{
    public List<AuditLogEntry> Entries { get; } = [];

    public bool Fail { get; set; }

    public Task RecordRequiredAsync(AuditLogEntry entry, CancellationToken ct = default) => RecordAsync(entry, ct);

    public Task RecordBestEffortAsync(AuditLogEntry entry, CancellationToken ct = default) => RecordAsync(entry, ct);

    public Task RecordAsync(AuditLogEntry entry, CancellationToken ct = default)
    {
        if ((pipeline?.FailAudit ?? false) || Fail)
        {
            throw new AuditUnavailableException();
        }

        lock (Entries)
        {
            Entries.Add(entry);
            if (pipeline is not null && entry.EventType == AuditEventTypes.WebSqlQuery)
            {
                pipeline.Events.Add("audit");
            }
        }

        return Task.CompletedTask;
    }
}

/// <summary>Admits with a scripted outcome and records every admission request and settlement.</summary>
internal sealed class ScriptedCostQuota(Func<QueryCostRequest, QueryCostOutcome>? outcome = null) : IQueryCostQuota
{
    public List<int> Estimates { get; } = [];

    public List<int> Settled { get; } = [];

    public Task<QueryCostAdmission> AdmitAsync(QueryCostRequest request, CancellationToken ct)
    {
        lock (Estimates)
        {
            Estimates.Add(request.EstimatedCost);
        }

        return Task.FromResult(new QueryCostAdmission(
            outcome?.Invoke(request) ?? QueryCostOutcome.Admitted, "user-1", ClientQuotaPolicy.ForTier(ClientTier.Standard), request.EstimatedCost, 2000, 500, 0, null));
    }

    public Task<CostQuotaResult?> SettleAsync(QueryCostAdmission admission, int actualCost, CancellationToken ct)
    {
        lock (Settled)
        {
            Settled.Add(actualCost);
        }

        return Task.FromResult<CostQuotaResult?>(null);
    }

    public Task ChargeFailureFeeAsync(ClaimsPrincipal principal, CancellationToken ct) => Task.CompletedTask;
}
