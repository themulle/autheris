using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Paging;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Policy.Services;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Messaging;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Autheris.Benchmarks;

/// <summary>
/// Benchmarks <see cref="AccessDecisionPoint"/> across cached and uncached <see cref="AccessDecisionPoint.DecideAsync"/>
/// and <see cref="AccessDecisionPoint.ExplainAsync"/> evaluations with an active consent and row filter.
/// </summary>
[MemoryDiagnoser]
public class AccessDecisionPointBenchmark : IDisposable
{
    private const double CachedP95BudgetMs = 5.0;

    private MemoryCache _memoryCache = null!;
    private InProcessChannelEventBus _eventBus = null!;
    private ConsentCacheService _cacheService = null!;
    private AccessDecisionPoint _cachedPdp = null!;
    private AccessDecisionPoint _uncachedPdp = null!;
    private AccessRequest _request = null!;

    [GlobalSetup]
    public void Setup()
    {
        var tenant = new TenantId("bench-tenant");
        var userSid = new Sid("S-1-5-21-BENCH-1001");
        var tableId = new TableIdentifier("sales", "dbo", "orders");
        var metadata = new TableMetadata
        {
            Identifier = tableId,
            Table = new Table
            {
                Id = Guid.NewGuid(),
                SourceName = "sales",
                SchemaName = "dbo",
                TableName = "orders",
                SourceType = "SqlServer"
            },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "region", DataType = "nvarchar" }
            ]
        };

        var consent = new Consent
        {
            Id = Guid.NewGuid(),
            TableId = metadata.Table.Id,
            TableIdentifier = tableId,
            TenantId = tenant,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = userSid,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(30),
            RowFilters =
            [
                new ConsentRowFilter
                {
                    ColumnName = "region",
                    Operator = "EQ",
                    ValueType = "string",
                    ValueJson = "[\"EMEA\"]"
                }
            ]
        };

        var repo = new InMemoryBenchmarkConsentRepository([consent]);
        var resolution = new ConsentResolutionService(new RowFilterSqlBuilder());
        var options = Options.Create(new GatewayOptions());

        _memoryCache = new MemoryCache(new MemoryCacheOptions());
        _eventBus = new InProcessChannelEventBus();
        _cacheService = new ConsentCacheService(_memoryCache, new FastEpochValidationService(), _eventBus, options);

        _cachedPdp = new AccessDecisionPoint(options, resolution, repo, _cacheService, memoryCache: _memoryCache);
        _uncachedPdp = new AccessDecisionPoint(options, resolution, repo, NullConsentCacheService.Instance);

        var subject = new SecurityPrincipalContext
        {
            UserSid = userSid,
            TenantId = tenant,
            GroupSids = new HashSet<Sid>(),
            GroupResolutionStatus = GroupResolutionStatus.Complete,
            TenantRoles = new HashSet<string>(),
            ClusterRoles = new HashSet<string>(),
            AuthenticationScheme = "Bearer",
            IsAuthenticated = true
        };

        _request = new AccessRequest(subject, metadata, AccessOperation.Read, AccessTransport.GraphQl);
        _cachedPdp.DecideAsync(_request).GetAwaiter().GetResult();
    }

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    [Benchmark]
    public ValueTask<TableAccessDecision> DecideCachedAsync() => _cachedPdp.DecideAsync(_request);

    [Benchmark]
    public ValueTask<TableAccessDecision> DecideUncachedAsync() => _uncachedPdp.DecideAsync(_request);

    [Benchmark]
    public ValueTask<AccessExplanation> ExplainCachedAsync() => _cachedPdp.ExplainAsync(_request);

    [Benchmark]
    public ValueTask<AccessExplanation> ExplainUncachedAsync() => _uncachedPdp.ExplainAsync(_request);

    public static async Task RunAsync()
    {
        Console.WriteLine("--- [Benchmark] AccessDecisionPoint DecideAsync & ExplainAsync ---");
        using var bench = new AccessDecisionPointBenchmark();
        bench.Setup();

        for (int i = 0; i < 200; i++)
        {
            await bench.DecideCachedAsync().ConfigureAwait(false);
        }

        const int iterations = 2_000;
        var samplesMs = new double[iterations];
        for (int i = 0; i < iterations; i++)
        {
            var start = Stopwatch.GetTimestamp();
            var decision = await bench.DecideCachedAsync().ConfigureAwait(false);
            if (!decision.IsAllowed)
            {
                throw new InvalidOperationException("Benchmark request must be allowed so the active consent and row filter path is measured.");
            }

            samplesMs[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }

        Array.Sort(samplesMs);
        double p95Ms = samplesMs[(int)(iterations * 0.95) - 1];
        double meanMs = samplesMs.Average();
        Console.WriteLine($"DecideCachedAsync   mean={meanMs:F4} ms  p95={p95Ms:F4} ms (budget < {CachedP95BudgetMs:F1} ms)");
        if (p95Ms >= CachedP95BudgetMs)
        {
            Console.Error.WriteLine($"AccessDecisionPoint cached p95 ({p95Ms:F4} ms) exceeded {CachedP95BudgetMs:F1} ms.");
            Environment.ExitCode = 1;
        }
    }

    public void Dispose()
    {
        _cacheService?.Dispose();
        _eventBus?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _memoryCache?.Dispose();
    }

    private sealed class InMemoryBenchmarkConsentRepository(IReadOnlyList<Consent> consents) : IConsentRepository
    {
        public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, CancellationToken ct = default)
            => Task.FromResult(consents);

        public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, TenantId? tenantId, CancellationToken ct = default)
            => Task.FromResult(consents);

        public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, IEnumerable<string>? roles = null, DateTimeOffset? atTime = null, CancellationToken ct = default)
            => Task.FromResult(consents);

        public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, IEnumerable<string>? roles, DateTimeOffset? atTime, TenantId? tenantId, CancellationToken ct = default)
            => Task.FromResult(consents);

        public Task<Consent> CreateConsentAsync(Consent consent, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Consent?> GetConsentByIdAsync(Guid consentId, CancellationToken ct = default) => Task.FromResult<Consent?>(null);
        public Task RevokeConsentAsync(Guid consentId, Sid revokedBySid, string reason, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<Consent>> GetExpiringConsentsAsync(DateTimeOffset threshold, int maxResults = 1000, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task ExtendConsentExpiryAsync(Guid consentId, DateTimeOffset newValidTo, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<Consent>> GetActiveConsentsByConsentRequestIdAsync(Guid consentRequestId, DateTimeOffset atTime, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<bool> RevokeSystemConsentAsync(Guid consentId, Guid consentRequestId, Sid revokedBySid, string reason, CancellationToken ct = default) => Task.FromResult(false);
        public Task ReplaceConsentsAsync(IReadOnlyList<Guid> revokeConsentIds, IReadOnlyList<Consent> create, Sid actor, string reason, IReadOnlyList<AuditLogEntry>? auditEntries = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<ConsentPage> GetConsentPageAsync(ConsentPageQuery query, CancellationToken ct = default) => Task.FromResult(new ConsentPage(consents, null));
    }
}
