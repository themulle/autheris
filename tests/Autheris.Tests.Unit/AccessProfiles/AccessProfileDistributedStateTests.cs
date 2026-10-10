namespace Autheris.Tests.Unit.AccessProfiles;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Policy.Exceptions;
using Autheris.Application.Policy.Interfaces;
using Autheris.Application.Policy.Services;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Application.State;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.State;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class AccessProfileDistributedStateTests
{
    private static readonly TenantId TenantTest = new("tenant-dist");

    private sealed class TestTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan span) => _now = _now.Add(span);
    }

    private static TableMetadata CreateTableMetadata()
    {
        return new TableMetadata
        {
            Identifier = new TableIdentifier("telemetry", "tem", "crane"),
            Table = new Table
            {
                SourceName = "telemetry",
                SchemaName = "tem",
                TableName = "crane",
                IsActive = true
            },
            Columns =
            [
                new TableColumn { ColumnName = "crane_id", DataType = "VARCHAR(64)", IsSensitive = false },
                new TableColumn { ColumnName = "status", DataType = "VARCHAR(32)", IsSensitive = false }
            ]
        };
    }

    private static TableAccessPolicy CreatePolicyWithCache(
        IAccessProfileCache profileCache,
        IAccessProfileRepository? profileRepo = null,
        IMemoryCache? memoryCache = null)
    {
        var consentRepo = Substitute.For<IConsentRepository>();
        var resolutionService = Substitute.For<IConsentResolutionService>();
        var cacheService = Substitute.For<IConsentCacheService>();
        var policyEnforcementService = Substitute.For<IPolicyEnforcementService>();
        var rebacEvaluator = Substitute.For<IRebacEvaluator>();
        var clientIpResolver = Substitute.For<IClientIpResolver>();
        var mandatoryFilters = NullMandatoryRowFilterResolver.Instance;
        var options = new GatewayOptions();

        return new TableAccessPolicy(
            consentRepo,
            resolutionService,
            cacheService,
            policyEnforcementService,
            rebacEvaluator,
            clientIpResolver,
            options,
            mandatoryFilters,
            contractManager: null,
            accessProfileRepository: profileRepo,
            memoryCache: memoryCache,
            accessProfileCache: profileCache);
    }

    [Fact]
    public async Task ProfileChange_OnNodeA_IsVisibleOnNodeB_WithoutBusMessage()
    {
        // AR-01: Two TableAccessPolicy instances with separate IMemoryCache, shared InMemoryClusterStateProvider, NO event bus.
        // After InvalidateTenantAsync on Node A, Node B delivers the updated profile after epoch cache expiration.
        var sharedStore = new InMemoryClusterStateProvider();
        var repo = new InMemoryAccessProfileRepository();
        var fakeTime = new TestTimeProvider();

        var l1A = new MemoryCache(new MemoryCacheOptions());
        var l1B = new MemoryCache(new MemoryCacheOptions());

        var options = Options.Create(new GatewayOptions
        {
            Caching = new CachingOptions
            {
                L1MemoryCache = new L1MemoryCacheOptions
                {
                    AccessProfileEpochCacheMilliseconds = 1000
                }
            }
        });

        var cacheA = new AccessProfileCache(repo, sharedStore, l1A, options, eventBus: null, timeProvider: fakeTime, logger: NullLogger<AccessProfileCache>.Instance);
        var cacheB = new AccessProfileCache(repo, sharedStore, l1B, options, eventBus: null, timeProvider: fakeTime, logger: NullLogger<AccessProfileCache>.Instance);

        var policyA = CreatePolicyWithCache(cacheA, repo, l1A);
        var policyB = CreatePolicyWithCache(cacheB, repo, l1B);

        var subject = "alice";
        var initialProfile = new AccessProfile
        {
            ProfileId = "prof-init",
            TenantId = TenantTest,
            Name = "InitialProfile",
            TargetTables = ["tem.*"],
            RowFilterPredicate = "status = 'INITIAL'",
            AssignedSubjects = [subject],
            ValidTo = DateTimeOffset.UtcNow.AddDays(1)
        };
        await repo.UpsertProfileAsync(initialProfile);

        var meta = CreateTableMetadata();
        var query = new TableAccessQuery(new Sid(subject), TenantTest, new HashSet<Sid>(), new HashSet<string>(), meta);

        // Node A and Node B both load and cache initial profile
        var decisionA1 = await policyA.DecideAsync(query, CancellationToken.None);
        decisionA1.CombinedRowFilterSql.ShouldBe("status = 'INITIAL'");

        var decisionB1 = await policyB.DecideAsync(query, CancellationToken.None);
        decisionB1.CombinedRowFilterSql.ShouldBe("status = 'INITIAL'");

        // DB update with fresh profile
        var updatedProfile = new AccessProfile
        {
            ProfileId = initialProfile.ProfileId,
            TenantId = TenantTest,
            Name = "UpdatedProfile",
            TargetTables = ["tem.*"],
            RowFilterPredicate = "status = 'UPDATED'",
            AssignedSubjects = [subject],
            ValidTo = DateTimeOffset.UtcNow.AddDays(1)
        };
        await repo.UpsertProfileAsync(updatedProfile);

        // Node A invalidates tenant
        await cacheA.InvalidateTenantAsync(TenantTest, CancellationToken.None);

        // Advance TestTimeProvider past 1000ms epoch cache window
        fakeTime.Advance(TimeSpan.FromMilliseconds(1500));

        // Node B now sees the new epoch from shared store, bypassing old L1 entry, and serves fresh DB profile!
        var decisionB2 = await policyB.DecideAsync(query, CancellationToken.None);
        decisionB2.CombinedRowFilterSql.ShouldBe("status = 'UPDATED'");
    }

    [Fact]
    public async Task StaleLoad_AfterInvalidate_IsNotServed()
    {
        // AR-01: DB-Load blocked via TaskCompletionSource, invalidation runs in between, follow-up request reads fresh.
        var sharedStore = new InMemoryClusterStateProvider();
        var l1 = new MemoryCache(new MemoryCacheOptions());
        var fakeTime = new TestTimeProvider();
        var mockRepo = Substitute.For<IAccessProfileRepository>();

        var slowTcs = new TaskCompletionSource<IReadOnlyList<AccessProfile>>();
        var subject = "bob";

        var staleProfile = new AccessProfile
        {
            ProfileId = "prof-stale",
            TenantId = TenantTest,
            Name = "StaleProfile",
            TargetTables = ["tem.*"],
            RowFilterPredicate = "status = 'STALE'",
            AssignedSubjects = [subject],
            ValidTo = DateTimeOffset.UtcNow.AddDays(1)
        };

        var freshProfile = new AccessProfile
        {
            ProfileId = "prof-fresh",
            TenantId = TenantTest,
            Name = "FreshProfile",
            TargetTables = ["tem.*"],
            RowFilterPredicate = "status = 'FRESH'",
            AssignedSubjects = [subject],
            ValidTo = DateTimeOffset.UtcNow.AddDays(1)
        };

        // First call to DB blocks
        int callCount = 0;
        mockRepo.GetProfilesForSubjectAsync(TenantTest, subject, Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                if (Interlocked.Increment(ref callCount) == 1)
                {
                    return await slowTcs.Task;
                }
                return new List<AccessProfile> { freshProfile };
            });

        var options = Options.Create(new GatewayOptions());
        var cache = new AccessProfileCache(mockRepo, sharedStore, l1, options, eventBus: null, timeProvider: fakeTime, logger: NullLogger<AccessProfileCache>.Instance);

        // Request 1 starts loading from DB (blocked)
        var pendingTask = cache.GetProfilesAsync(TenantTest, subject, CancellationToken.None).AsTask();

        // Invalidation runs concurrently while Request 1 is waiting on DB
        fakeTime.Advance(TimeSpan.FromSeconds(2));
        await cache.InvalidateTenantAsync(TenantTest, CancellationToken.None);

        // Now Request 1 DB completes with stale data
        slowTcs.SetResult([staleProfile]);
        var result1 = await pendingTask;

        // Advance epoch cache window
        fakeTime.Advance(TimeSpan.FromSeconds(2));

        // Follow-up Request 2 MUST NOT be served the stale result
        var result2 = await cache.GetProfilesAsync(TenantTest, subject, CancellationToken.None);
        result2.Count.ShouldBe(1);
        result2[0].RowFilterPredicate.ShouldBe("status = 'FRESH'");
    }

    [Fact]
    public async Task EpochStoreUnavailable_BypassesCache_AndDbUnavailable_Denies()
    {
        // AR-01: When cluster store is unavailable, L1 is bypassed. If DB is also unavailable, throw AccessProfileSourceUnavailableException -> TableAccessPolicy Denies (fail-closed).
        var failingStore = Substitute.For<IDistributedClusterStateProvider>();
        failingStore.IncrementAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult<long?>(null));
        failingStore.GetAsync<long>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromException<long>(new InvalidOperationException("Redis down")));

        var failingRepo = Substitute.For<IAccessProfileRepository>();
        failingRepo.GetProfilesForSubjectAsync(Arg.Any<TenantId>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<AccessProfile>>(new IOException("Database connection timeout")));

        var l1 = new MemoryCache(new MemoryCacheOptions());
        var options = Options.Create(new GatewayOptions());
        var cache = new AccessProfileCache(failingRepo, failingStore, l1, options, eventBus: null, logger: NullLogger<AccessProfileCache>.Instance);

        var policy = CreatePolicyWithCache(cache, failingRepo, l1);

        var meta = CreateTableMetadata();
        var query = new TableAccessQuery(new Sid("charlie"), TenantTest, new HashSet<Sid>(), new HashSet<string>(), meta);

        // Cache lookup directly throws AccessProfileSourceUnavailableException
        await Should.ThrowAsync<AccessProfileSourceUnavailableException>(async () =>
        {
            await cache.GetProfilesAsync(TenantTest, "charlie", CancellationToken.None);
        });

        // Policy evaluation must catch AccessProfileSourceUnavailableException and return Denied (fail-closed)
        var decision = await policy.DecideAsync(query, CancellationToken.None);
        decision.IsAllowed.ShouldBeFalse();
        decision.DeniedReasons.ShouldContain(r => r.Contains("fail-closed"));
    }

    [Fact]
    public async Task InvalidationFailure_Returns503_AndWritesAudit()
    {
        // AR-01: GovernanceEndpoints.CreateBulkConsentAsync or RevokeAccessProfileAsync returns 503 and records PROFILE_INVALIDATION_FAILED audit when invalidation fails.
        var failingCache = Substitute.For<IAccessProfileCache>();
        failingCache.InvalidateTenantAsync(Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("Cluster state provider connection lost")));

        var auditRepo = Substitute.For<IAuditLogRepository>();
        var profileRepo = new InMemoryAccessProfileRepository();

        var services = new ServiceCollection();
        services.AddSingleton(failingCache);
        services.AddSingleton(auditRepo);
        services.AddSingleton<IAccessProfileRepository>(profileRepo);
        var serviceProvider = services.BuildServiceProvider();

        var httpContext = new DefaultHttpContext
        {
            RequestServices = serviceProvider,
            Response = { Body = new MemoryStream() }
        };
        httpContext.Items[SecurityPrincipalContext.ItemKey] = new SecurityPrincipalContext
        {
            UserSid = new Sid("S-1-5-21-ADMIN"),
            TenantId = TenantTest,
            GroupSids = new HashSet<Sid>(),
            TenantRoles = new HashSet<string>(),
            ClusterRoles = new HashSet<string> { "GovernanceAdmin" },
            AuthenticationScheme = "Test"
        };
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "admin"),
            new("sub", "S-1-5-21-ADMIN"),
            new("role", "GovernanceAdmin")
        };
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));

        var request = new BulkConsentRequest
        {
            Subject = "david",
            MaskingMode = "Unmasked",
            Tables = ["tem.*"],
            RowFilter = "1=1",
            Justification = "Maintenance exception approved by governance",
            ValidDays = 30
        };

        var result = await GovernanceEndpoints.CreateBulkConsentAsync(request, httpContext, profileRepo, CancellationToken.None);

        var statusCodeResult = result as Microsoft.AspNetCore.Http.IStatusCodeHttpResult;
        statusCodeResult.ShouldNotBeNull();
        statusCodeResult.StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);

        // Verify audit event recorded
        await auditRepo.Received(1).RecordAuditEventAsync(
            Arg.Is<AuditLogEntry>(e =>
                e.EventType == "PROFILE_INVALIDATION_FAILED" &&
                e.Decision == "DENY" &&
                e.TenantId == TenantTest),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void TableAccessPolicy_HasNoStaticCacheFields()
    {
        // AR-01 Architecture Test: TableAccessPolicy must not have static fields of type IMemoryCache or ConcurrentDictionary.
        var staticFields = typeof(TableAccessPolicy).GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        foreach (var field in staticFields)
        {
            typeof(IMemoryCache).IsAssignableFrom(field.FieldType).ShouldBeFalse(
                $"TableAccessPolicy has static IMemoryCache field: {field.Name}");
            (field.FieldType.IsGenericType && field.FieldType.GetGenericTypeDefinition() == typeof(ConcurrentDictionary<,>)).ShouldBeFalse(
                $"TableAccessPolicy has static ConcurrentDictionary field: {field.Name}");
        }
    }
}
