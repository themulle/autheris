namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Analytics;
using Autheris.Application.Audit;
using Autheris.Application.Catalog.Semantic;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Tests.Unit.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// The table-serving consumers decide reads through the access decision point itself (no adapter): transport per consumer, required
/// dependency, fail-closed on errors, request parity, value-free denial logging and a decision cache that respects supplemental
/// consents and the policy epoch.
/// </summary>
[Trait("Category", "Security")]
public sealed class AccessDecisionPointMigrationSecurityTests
{
    private const string TenantValue = "tenant_a";
    private const string UserSidValue = "S-1-5-21-USER";
    private static readonly TenantId Tenant = new(TenantValue);
    private static readonly TableIdentifier Orders = new("sales", "main", "orders");

    private static TableMetadata Meta() => new()
    {
        Identifier = Orders,
        Table = new Table { Id = Guid.NewGuid(), SourceName = "sales", SchemaName = "main", TableName = "orders", SourceType = "PostgreSql", IsActive = true },
        Columns = [new TableColumn { ColumnName = "id", DataType = "INTEGER" }, new TableColumn { ColumnName = "ssn", DataType = "TEXT" }]
    };

    private static ClaimsPrincipal User() =>
        PipelinePrincipals.MarkComplete(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, UserSidValue), new Claim("tenant_id", TenantValue)], "Bearer")));

    private static SecurityPrincipalContext Subject() => SecurityPrincipalContext.FromPrincipal(User(), Tenant);

    private static TableAccessDecision Allowed() =>
        TableAccessDecision.Allowed(Orders, new Dictionary<string, ColumnAccessLevel> { ["id"] = ColumnAccessLevel.Clear }, hasUnconstrainedColumnAllow: true);

    private static Consent AllowConsent() => new()
    {
        Id = Guid.NewGuid(),
        TableIdentifier = Orders,
        TenantId = Tenant,
        Effect = ConsentEffect.Allow,
        GranteeType = GranteeType.User,
        GranteeSid = new Sid(UserSidValue),
        ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
        ValidTo = DateTimeOffset.UtcNow.AddDays(1),
        ColumnRules = [new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear }]
    };

    private static ITableMetadataRepository MetaRepo() => Substitute.For<ITableMetadataRepository>();

    private static IAuditLogRepository AuditRepo() => Substitute.For<IAuditLogRepository>();

    private static ISemanticCatalogService Catalog() => Substitute.For<ISemanticCatalogService>();

    private static IGovernedAggregationService Aggregation() => Substitute.For<IGovernedAggregationService>();

    private static IConsentRepository ConsentsReturning(Func<IReadOnlyList<Consent>> consents)
    {
        var repo = Substitute.For<IConsentRepository>();
        repo.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(consents()));
        return repo;
    }

    private static IAccessDecisionPoint RecordingPdp(out List<AccessRequest> seen)
    {
        var requests = new List<AccessRequest>();
        seen = requests;
        return AccessDecisionPointStubs.Returning(r =>
        {
            requests.Add(r);
            return Allowed();
        });
    }

    private static async Task<TableAccessDecision> InvokeAggregationDecisionAsync(IAccessDecisionPoint pdp, AggregationQuery? query = null)
    {
        var service = new GovernedAggregationService(MetaRepo(), AuditRepo(), pdp);
        var task = (Task<TableAccessDecision>)typeof(GovernedAggregationService)
            .GetMethod("ResolveDecisionAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(service, [User(), new Sid(UserSidValue), Tenant, Meta(), query ?? new AggregationQuery(Orders), CancellationToken.None])!;
        return await task;
    }

    private static async Task<(TableAccessDecision Decision, bool HasMetricConsent)> InvokeMetricDecisionAsync(IAccessDecisionPoint pdp)
    {
        var service = new SemanticMetricQueryService(
            Catalog(),
            Aggregation(),
            MetaRepo(),
            pdp);
        var metric = new DbtMetric("mrr", "simple", null, null, null, "{ \"measure\": \"total\" }", [], ["total"],
            new Dictionary<string, string> { ["model"] = "m" });
        var task = (Task<(TableAccessDecision Decision, bool HasMetricConsent)>)typeof(SemanticMetricQueryService)
            .GetMethod("ResolveMetricAndTableDecisionAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(service, [User(), new Sid(UserSidValue), Tenant, Meta(), new List<DbtMetric> { metric }, CancellationToken.None])!;
        return await task;
    }

    [Fact]
    public async Task Security_MigratedConsumers_UseAccessDecisionPointWithExpectedTransport()
    {
        // REST reads: the shared authorization stage used by the stream endpoint, data query, change detection and relations.
        var restPdp = RecordingPdp(out var restSeen);
        await GovernedQueryStages.AuthorizeTableAsync(restPdp, Orders, Meta(), User(), Tenant, clientIp: null, requestedColumns: ["id"], CancellationToken.None);
        var rest = restSeen.ShouldHaveSingleItem();
        rest.Transport.ShouldBe(AccessTransport.Rest);
        rest.Operation.ShouldBe(AccessOperation.Read);

        // Analytics reads: aggregation and semantic metrics.
        var aggregationPdp = RecordingPdp(out var aggregationSeen);
        await InvokeAggregationDecisionAsync(aggregationPdp);
        var aggregation = aggregationSeen.ShouldHaveSingleItem();
        aggregation.Transport.ShouldBe(AccessTransport.Analytics);
        aggregation.Operation.ShouldBe(AccessOperation.Read);

        var metricPdp = RecordingPdp(out var metricSeen);
        await InvokeMetricDecisionAsync(metricPdp);
        var metric = metricSeen.ShouldHaveSingleItem();
        metric.Transport.ShouldBe(AccessTransport.Analytics);
        metric.Operation.ShouldBe(AccessOperation.Read);
    }

    [Fact]
    public void Security_AggregationAndMetricServices_RequireDecisionPoint_FailClosed()
    {
        Should.Throw<ArgumentNullException>(() => new GovernedAggregationService(
            MetaRepo(), AuditRepo(), pdp: null!));
        Should.Throw<ArgumentNullException>(() => new GovernedAggregationService(
            MetaRepo(), pdp: null!));
        Should.Throw<ArgumentNullException>(() => new SemanticMetricQueryService(
            Catalog(), Aggregation(), MetaRepo(),
            AuditRepo(), pdp: null!));
        Should.Throw<ArgumentNullException>(() => new SemanticMetricQueryService(
            Catalog(), Aggregation(), MetaRepo(),
            pdp: null!));

        foreach (var type in new[] { typeof(GovernedAggregationService), typeof(SemanticMetricQueryService) })
        {
            foreach (var ctor in type.GetConstructors())
            {
                var pdp = ctor.GetParameters().Single(p => p.ParameterType == typeof(IAccessDecisionPoint));
                pdp.IsOptional.ShouldBeFalse($"{type.Name}: the decision point must be a required constructor argument.");
            }
        }
    }

    [Fact]
    public async Task Security_DecisionPointThrows_ConsumerDeniesAndNeverReturnsData()
    {
        var throwing = AccessDecisionPointStubs.Throwing(() => new InvalidOperationException("decision point unavailable"));

        await Should.ThrowAsync<InvalidOperationException>(() =>
            GovernedQueryStages.AuthorizeTableAsync(throwing, Orders, Meta(), User(), Tenant, null, null, CancellationToken.None));
        await Should.ThrowAsync<InvalidOperationException>(() => InvokeAggregationDecisionAsync(throwing));
        await Should.ThrowAsync<InvalidOperationException>(() => InvokeMetricDecisionAsync(throwing));

        // Cancellation stays a cancellation; it is never turned into a decision.
        var cancelled = AccessDecisionPointStubs.Throwing(() => new OperationCanceledException());
        await Should.ThrowAsync<OperationCanceledException>(() =>
            GovernedQueryStages.AuthorizeTableAsync(cancelled, Orders, Meta(), User(), Tenant, null, null, CancellationToken.None));
    }

    [Fact]
    public async Task Security_SupplementalConsents_ForwardedToAccessRequest()
    {
        var pdp = RecordingPdp(out var seen);
        IReadOnlyList<Consent> supplemental = [AllowConsent()];
        IReadOnlyList<string> columns = ["id"];

        await pdp.DecideReadAsync(Meta(), Subject(), AccessTransport.Analytics, columns, logger: null, CancellationToken.None, supplementalConsents: supplemental);
        await pdp.DecideReadAsync(Meta(), Subject(), AccessTransport.Rest, requestedColumns: null, logger: null, CancellationToken.None);

        seen[0].SupplementalConsents.ShouldBeSameAs(supplemental);
        seen[0].Columns.ShouldBeSameAs(columns);
        seen[0].Operation.ShouldBe(AccessOperation.Read);
        seen[1].SupplementalConsents.ShouldBeNull();
        seen[1].Columns.ShouldBeNull("A null column list stays null; it is never turned into an empty list.");
    }

    [Fact]
    public async Task Security_DecisionCache_DoesNotReuseDecisionAcrossSupplementalConsents()
    {
        var cache = new EpochConsentCache();
        var repo = ConsentsReturning(() => []);
        var pdp = new AccessDecisionPoint(Options.Create(new GatewayOptions()), new ConsentResolutionService(), repo, cache);
        IReadOnlyList<Consent> supplemental = [AllowConsent()];

        var withSupplemental = await pdp.DecideReadAsync(Meta(), Subject(), AccessTransport.Analytics, null, null, CancellationToken.None, supplementalConsents: supplemental);
        withSupplemental.IsAllowed.ShouldBeTrue();
        cache.Entries.ShouldBeEmpty("A decision computed with supplemental consents is never written to the decision cache.");
        cache.Reads.ShouldBe(0, "A request with supplemental consents is never answered from the decision cache.");

        var without = await pdp.DecideReadAsync(Meta(), Subject(), AccessTransport.Analytics, null, null, CancellationToken.None);
        without.IsAllowed.ShouldBeFalse();

        // And the other way round: a cached decision without supplemental consents does not answer a request that carries them.
        cache.Seed(Orders, Allowed());
        var readsBefore = cache.Reads;
        var again = await pdp.DecideReadAsync(Meta(), Subject(), AccessTransport.Analytics, null, null, CancellationToken.None, supplementalConsents: supplemental);
        again.IsAllowed.ShouldBeTrue();
        cache.Reads.ShouldBe(readsBefore);
    }

    [Fact]
    public async Task Security_DecisionCache_InvalidatedByPolicyEpochBump()
    {
        var cache = new EpochConsentCache();
        var consents = new List<Consent> { AllowConsent() };
        var repo = ConsentsReturning(() => consents.ToList());
        var pdp = new AccessDecisionPoint(Options.Create(new GatewayOptions()), new ConsentResolutionService(), repo, cache);

        (await pdp.DecideReadAsync(Meta(), Subject(), AccessTransport.Rest, null, null, CancellationToken.None)).IsAllowed.ShouldBeTrue();
        cache.Entries.Count.ShouldBe(1);

        // Revocation: the epoch bump (eviction) must stop the cached allow from being served.
        consents.Clear();
        await cache.EvictTableDecisionsAsync(Orders);
        (await pdp.DecideReadAsync(Meta(), Subject(), AccessTransport.Rest, null, null, CancellationToken.None)).IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Security_DecisionCache_EpochBumpedWhileLoading_IsNotCached()
    {
        var cache = new EpochConsentCache();
        var repo = ConsentsReturning(() =>
        {
            cache.BumpEpoch(Orders);
            return [AllowConsent()];
        });
        var pdp = new AccessDecisionPoint(Options.Create(new GatewayOptions()), new ConsentResolutionService(), repo, cache);

        (await pdp.DecideReadAsync(Meta(), Subject(), AccessTransport.Rest, null, null, CancellationToken.None)).IsAllowed.ShouldBeTrue();

        cache.Entries.ShouldBeEmpty("A decision loaded across an epoch bump must not be cached.");
    }

    [Fact]
    public async Task Security_Denial_IsLoggedWithoutValues_AndAuditedOnce()
    {
        var logger = new CapturingLogger();
        var audit = Substitute.For<IAuditWriter>();
        var repo = ConsentsReturning(() => []);
        var pdp = new AccessDecisionPoint(
            Options.Create(new GatewayOptions()), new ConsentResolutionService(), repo, new EpochConsentCache(), auditWriter: audit);

        var decision = await pdp.DecideReadAsync(Meta(), Subject(), AccessTransport.Rest, ["ssn"], logger, CancellationToken.None);

        decision.IsAllowed.ShouldBeFalse();
        var warning = logger.Messages.ShouldHaveSingleItem();
        warning.ShouldContain("orders");
        warning.ShouldContain(UserSidValue);
        warning.ShouldNotContain("ssn", Case.Insensitive);
        warning.ShouldNotContain("Bearer");

        // The decision point audits at most once per decision; the consumer helper never writes a second row.
        audit.ReceivedCalls().Count().ShouldBeLessThanOrEqualTo(1);
    }

    [Fact]
    public async Task Security_PreEvaluatedDecision_WithoutPdpProvenance_IsReevaluated()
    {
        var denying = AccessDecisionPointStubs.Denying();
        var forged = Allowed();
        var query = new AggregationQuery(Orders, PreEvaluatedDecision: forged, BypassTableConsent: true);

        var untrusted = await InvokeAggregationDecisionAsync(denying, query);
        var trusted = await InvokeAggregationDecisionAsync(denying, query with { PreEvaluatedDecision = forged with { EvaluatedByPdp = true } });

        untrusted.IsAllowed.ShouldBeFalse();
        trusted.IsAllowed.ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task PreEvaluatedDecision_WithDefinerProvenance_IsReEvaluated()
    {
        var pdpCalls = 0;
        var denyingPdp = AccessDecisionPointStubs.Returning(r =>
        {
            pdpCalls++;
            return TableAccessDecision.Denied(r.Object.Identifier, "Denied by PDP.");
        });
        var definerDecision = Allowed() with { EvaluatedAsProductDefiner = true };
        var query = new AggregationQuery(Orders, PreEvaluatedDecision: definerDecision, BypassTableConsent: true);

        definerDecision.EvaluatedAsProductDefiner.ShouldBeTrue();
        definerDecision.EvaluatedByPdp.ShouldBeFalse();

        var resolved = await InvokeAggregationDecisionAsync(denyingPdp, query);

        resolved.IsAllowed.ShouldBeFalse();
        resolved.EvaluatedAsProductDefiner.ShouldBeFalse();
        pdpCalls.ShouldBe(1);
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
            {
                Messages.Add(formatter(state, exception));
            }
        }
    }

    /// <summary>Decision cache with the real epoch contract: eviction bumps the table epoch and a stale compare-and-set write is dropped.</summary>
    private sealed class EpochConsentCache : IConsentCacheService
    {
        private readonly Dictionary<string, TableAccessDecision> _entries = [];
        private readonly Dictionary<TableIdentifier, long> _epochs = [];

        public IReadOnlyCollection<string> Entries => _entries.Keys;

        public int Reads { get; private set; }

        public void Seed(TableIdentifier table, TableAccessDecision decision) =>
            _entries[Key(Tenant, new Sid(UserSidValue), table, null)] = decision;

        public void BumpEpoch(TableIdentifier table) => _epochs[table] = _epochs.GetValueOrDefault(table) + 1;

        // The contextHash is part of the real key; the fake keys on tenant, subject and table and ignores the hash on purpose so a
        // seeded entry would be served if the policy asked for it.
        private static string Key(TenantId tenant, Sid sid, TableIdentifier table, string? hash) => $"{tenant.Value}|{sid.Value}|{table}";

        public Task<TableAccessDecision?> GetCachedDecisionAsync(TenantId tenant, Sid userSid, TableIdentifier table, string? contextHash = null, CancellationToken ct = default)
        {
            Reads++;
            return Task.FromResult(_entries.GetValueOrDefault(Key(tenant, userSid, table, contextHash)));
        }

        public async Task<IReadOnlyDictionary<TableIdentifier, TableAccessDecision?>> GetCachedDecisionsAsync(
            TenantId tenant, Sid userSid, IReadOnlyList<TableIdentifier> tables, string? contextHash = null, CancellationToken ct = default)
        {
            var result = new Dictionary<TableIdentifier, TableAccessDecision?>();
            foreach (var table in tables)
            {
                result[table] = await GetCachedDecisionAsync(tenant, userSid, table, contextHash, ct);
            }

            return result;
        }

        public Task SetCachedDecisionAsync(TenantId tenant, Sid userSid, TableIdentifier table, TableAccessDecision decision, TimeSpan ttl, string? contextHash = null, CancellationToken ct = default)
        {
            _entries[Key(tenant, userSid, table, contextHash)] = decision;
            return Task.CompletedTask;
        }

        public Task<long?> GetEpochSnapshotAsync(TableIdentifier table, CancellationToken ct = default) =>
            Task.FromResult<long?>(_epochs.GetValueOrDefault(table));

        public Task SetCachedDecisionAsync(TenantId tenant, Sid userSid, TableIdentifier table, TableAccessDecision decision, TimeSpan ttl, string? contextHash, long? epochAtLoad, CancellationToken ct = default)
        {
            if (epochAtLoad is { } snapshot && snapshot != _epochs.GetValueOrDefault(table))
            {
                return Task.CompletedTask;
            }

            return SetCachedDecisionAsync(tenant, userSid, table, decision, ttl, contextHash, ct);
        }

        public Task EvictTableDecisionsAsync(TableIdentifier table, CancellationToken ct = default)
        {
            BumpEpoch(table);
            foreach (var key in _entries.Keys.Where(k => k.EndsWith($"|{table}", StringComparison.Ordinal)).ToList())
            {
                _entries.Remove(key);
            }

            return Task.CompletedTask;
        }

        public Task ClearL1CacheAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
