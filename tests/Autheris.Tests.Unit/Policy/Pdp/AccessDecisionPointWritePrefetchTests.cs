namespace Autheris.Tests.Unit.Policy.Pdp;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Policy.Services;
using Autheris.Application.Policy.WriteGrants;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Tests.Common.Fakes;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class AccessDecisionPointWritePrefetchTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TenantId TenantB = new("tenant-b");
    private static readonly Sid CallerSid = new("S-1-5-21-1056-1056-1001");
    private static readonly DateTimeOffset T0 = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task DecideManyAsync_InsertBatch_PrefetchesProtectionOnceWithoutPerTableGetAsync()
    {
        var time = new FakeTimeProvider(T0);
        var table1 = MakeTable("orders");
        var table2 = MakeTable("invoices");
        var protection = MakeProtection(ProtectionClass.Internal);
        var protectionProvider = new CountingProtectionProvider(protection);
        var pdp = CreatePdp(time, protectionProvider, allowWrite: true);
        var subject = CreateSubject(TenantA);

        var decisions = await pdp.DecideManyAsync(
            subject,
            [table1, table2],
            AccessOperation.Insert,
            AccessTransport.Discovery);

        decisions.Count.ShouldBe(2);
        decisions.ShouldAllBe(d => d.IsAllowed);
        protectionProvider.ResolveManyCalls.ShouldBe(1);
        protectionProvider.GetCalls.ShouldBe(0);
    }

    [Fact]
    public async Task DecideWriteAsync_FreshSameTenantPrefetch_SkipsGetAsync()
    {
        var time = new FakeTimeProvider(T0);
        var table = MakeTable("orders");
        var protectionProvider = new CountingProtectionProvider(MakeProtection(ProtectionClass.Internal));
        var prefetch = await ProtectionBatchPrefetch.LoadAsync(protectionProvider, TenantA, [table], T0, CancellationToken.None);
        protectionProvider.ResetCounters();

        var pdp = CreatePdp(time, protectionProvider, allowWrite: true);
        var request = new AccessRequest(CreateSubject(TenantA), table, AccessOperation.Insert, AccessTransport.WebSql, Columns: ["amount"])
        {
            ProtectionPrefetch = prefetch
        };

        var decision = await pdp.DecideAsync(request);

        decision.IsAllowed.ShouldBeTrue();
        protectionProvider.GetCalls.ShouldBe(0);
    }

    [Fact]
    public async Task DecideWriteAsync_CrossTenantOrExpiredPrefetch_FallsBackToGetAsync()
    {
        var time = new FakeTimeProvider(T0);
        var table = MakeTable("orders");
        var protectionProvider = new CountingProtectionProvider(MakeProtection(ProtectionClass.Internal));
        var otherTenantPrefetch = await ProtectionBatchPrefetch.LoadAsync(protectionProvider, TenantB, [table], T0, CancellationToken.None);
        var sameTenantPrefetch = await ProtectionBatchPrefetch.LoadAsync(protectionProvider, TenantA, [table], T0, CancellationToken.None);
        protectionProvider.ResetCounters();

        var pdp = CreatePdp(time, protectionProvider, allowWrite: true);

        var crossTenant = await pdp.DecideAsync(
            new AccessRequest(CreateSubject(TenantA), table, AccessOperation.Insert, AccessTransport.WebSql, Columns: ["amount"])
            {
                ProtectionPrefetch = otherTenantPrefetch
            });
        crossTenant.IsAllowed.ShouldBeTrue();
        protectionProvider.GetCalls.ShouldBe(1);

        time.Advance(ConsentBatchPrefetch.MaxAge + TimeSpan.FromSeconds(1));
        var expired = await pdp.DecideAsync(
            new AccessRequest(CreateSubject(TenantA), table, AccessOperation.Insert, AccessTransport.WebSql, Columns: ["amount"])
            {
                ProtectionPrefetch = sameTenantPrefetch
            });
        expired.IsAllowed.ShouldBeTrue();
        protectionProvider.GetCalls.ShouldBe(2);
    }

    [Fact]
    public async Task DecideWriteAsync_WhenProtectionProviderThrows_DeniesFailClosed()
    {
        var time = new FakeTimeProvider(T0);
        var table = MakeTable("orders");
        var protectionProvider = new CountingProtectionProvider(MakeProtection(ProtectionClass.Internal))
        {
            Failure = new InvalidOperationException("protection store offline")
        };
        var pdp = CreatePdp(time, protectionProvider, allowWrite: true);

        var decision = await pdp.DecideAsync(
            new AccessRequest(CreateSubject(TenantA), table, AccessOperation.Insert, AccessTransport.WebSql, Columns: ["amount"]));

        decision.IsAllowed.ShouldBeFalse();
        decision.DeniedReasons.ShouldContain(r => r.Contains("Protection classification unavailable", StringComparison.OrdinalIgnoreCase));
        protectionProvider.GetCalls.ShouldBe(1);
    }

    private static AccessDecisionPoint CreatePdp(
        FakeTimeProvider time,
        IProtectionSnapshotProvider protectionProvider,
        bool allowWrite) =>
        new(
            Options.Create(new GatewayOptions()),
            new ConsentResolutionService(),
            new InMemoryConsentRepository(),
            NullConsentCacheService.Instance,
            writeGrantEvaluator: new FixedWriteGrantEvaluator(allowWrite),
            timeProvider: time,
            protectionProvider: protectionProvider);

    private static SecurityPrincipalContext CreateSubject(TenantId tenant)
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, CallerSid.Value)], "Test");
        return new SecurityPrincipalContext
        {
            UserSid = CallerSid,
            ResolvedTenant = tenant,
            GroupSids = ImmutableHashSet<Sid>.Empty,
            GroupResolutionStatus = GroupResolutionStatus.Complete,
            TenantRoles = ImmutableHashSet<string>.Empty,
            ClusterRoles = ImmutableHashSet<string>.Empty,
            IsAuthenticated = true,
            AuthenticationScheme = "Test",
            Principal = new ClaimsPrincipal(identity)
        };
    }

    private static TableMetadata MakeTable(string name) =>
        new()
        {
            Identifier = new TableIdentifier("sales", "dbo", name),
            PrimaryKeyColumns = ["id"],
            Table = new Table
            {
                Id = Guid.NewGuid(),
                SourceName = "sales",
                SchemaName = "dbo",
                TableName = name,
                IsActive = true
            },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "amount", DataType = "decimal" }
            ]
        };

    private static EffectiveProtection MakeProtection(ProtectionClass tableClass) =>
        new(
            Container: new ContainerRef(ContainerKind.Domain, Guid.NewGuid()),
            OwnerGroupSid: null,
            TableClass: tableClass,
            DeclaredTableClass: tableClass,
            TableMaskCategory: null,
            Columns: new Dictionary<string, ColumnProtection>(StringComparer.OrdinalIgnoreCase),
            Visibility: CatalogVisibilityMode.Open);

    private sealed class FixedWriteGrantEvaluator(bool allow) : IWriteGrantEvaluator
    {
        public Task<WriteGrantDecision> EvaluateAsync(
            Autheris.Application.Policy.WriteGrants.WriteGrantQuery query,
            CancellationToken ct = default) =>
            Task.FromResult(allow
                ? WriteGrantDecision.Allowed(WriteRowScope.AllRows, new HashSet<string>(["amount"], StringComparer.OrdinalIgnoreCase), null, ["g1"])
                : WriteGrantDecision.Denied("no_write_grant"));
    }

    private sealed class CountingProtectionProvider(EffectiveProtection protection) : IProtectionSnapshotProvider
    {
        public int GetCalls { get; private set; }
        public int ResolveManyCalls { get; private set; }
        public Exception? Failure { get; set; }

        public void ResetCounters()
        {
            GetCalls = 0;
            ResolveManyCalls = 0;
        }

        public ValueTask<EffectiveProtection> GetAsync(TenantId tenant, TableIdentifier table, CancellationToken ct = default)
        {
            GetCalls++;
            if (Failure is not null)
            {
                return ValueTask.FromException<EffectiveProtection>(Failure);
            }

            return ValueTask.FromResult(protection);
        }

        public ValueTask<IReadOnlyDictionary<TableIdentifier, EffectiveProtection>> ResolveManyAsync(
            TenantId tenant, IReadOnlyList<TableMetadata> tables, CancellationToken ct = default)
        {
            ResolveManyCalls++;
            if (Failure is not null)
            {
                return ValueTask.FromException<IReadOnlyDictionary<TableIdentifier, EffectiveProtection>>(Failure);
            }

            var map = new Dictionary<TableIdentifier, EffectiveProtection>(tables.Count);
            foreach (var table in tables)
            {
                map[table.Identifier] = protection;
            }

            return ValueTask.FromResult<IReadOnlyDictionary<TableIdentifier, EffectiveProtection>>(map);
        }
    }
}
