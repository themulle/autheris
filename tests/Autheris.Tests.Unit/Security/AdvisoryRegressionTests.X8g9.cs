#pragma warning disable CA2012 // NSubstitute .Returns(...) configures a ValueTask member; the instance is not consumed here.
namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Identity.Attributes;
using Autheris.Application.Policy.Services;
using Autheris.Application.State;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Unit.Identity.Attributes;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;
using static Autheris.Tests.Unit.Identity.Attributes.CallerAttributeTestSupport;

[Trait("Category", "Security")]
public sealed partial class AdvisoryRegressionTests
{
    // GHSA-x8g9-cm3h-p6jq (details: private advisory).
    [Theory]
    [InlineData("case_01")]
    [InlineData("case_02")]
    [InlineData("case_03")]
    [InlineData("case_04")]
    [InlineData("case_05")]
    [InlineData("case_06")]
    [InlineData("case_07")]
    public async Task Ghsa_x8g9_p6jq_regression(string bullet)
    {
        switch (bullet)
        {
            case "case_01":
                await CacheScenarioAsync(epochAfterFlush: 1);
                break;
            case "case_02":
                await CacheScenarioAsync(epochAfterFlush: 0);
                break;
            case "case_03":
            {
                using var fixture = new AttributeFixture();
                await fixture.ResolveAsync();
                await fixture.ResolveAsync();
                fixture.Provider.Calls.ShouldBe(1);
                fixture.Resolver.InvalidateAttribute(Tenant, "cost_center");
                await fixture.ResolveAsync();
                fixture.Provider.Calls.ShouldBe(2);
                fixture.Resolver.InvalidateSubject(Tenant, UserOid);
                await fixture.ResolveAsync();
                fixture.Provider.Calls.ShouldBe(3);
                break;
            }
            case "case_04":
            {
                var bus = Substitute.For<IEventBus>();
                Func<string, Task>? handler = null;
                bus.Subscribe<string>(CallerAttributeResolver.InvalidationChannel, Arg.Do<Func<string, Task>>(h => handler = h)).Returns(Substitute.For<IDisposable>());
                using var fixture = new AttributeFixture(bus);
                await fixture.ResolveAsync();
                handler.ShouldNotBeNull();
                await handler($"A|{Tenant.Value}|cost_center");
                await fixture.ResolveAsync();
                fixture.Provider.Calls.ShouldBe(2);
                await handler("garbage");
                await handler($"A|{Tenant.Value}|");
                await fixture.ResolveAsync();
                fixture.Provider.Calls.ShouldBe(2);
                fixture.Resolver.InvalidateAttribute(Tenant, "cost_center");
                await bus.Received().PublishAsync(CallerAttributeResolver.InvalidationChannel, $"A|{Tenant.Value}|cost_center", Arg.Any<CancellationToken>());
                break;
            }
            case "case_05":
            {
                var bus = Substitute.For<IEventBus>();
                using var fixture = new AttributeFixture(bus);
                await fixture.ResolveAsync();
                bus.ConnectionRestored += Raise.Event<Action>();
                await fixture.ResolveAsync();
                fixture.Provider.Calls.ShouldBe(2);
                break;
            }
            case "case_06":
            {
                var repository = Substitute.For<ICallerAttributeRepository>();
                repository.SaveServiceSetAsync(default, default!, default!, default!, default!, default, default).ReturnsForAnyArgs(CallerAttributeChangeResult.Applied);
                repository.LoadCallerAttributeSnapshotAsync(default).ReturnsForAnyArgs(CallerAttributeStoreSnapshot.Empty);
                var registry = Substitute.For<ICallerAttributeRegistry>();
                registry.GetAsync(default).ReturnsForAnyArgs(new CallerAttributeSnapshot(1, [ClaimsDefinition()], []));
                var invalidator = Substitute.For<ICallerAttributeCacheInvalidator>();
                var service = new CallerAttributeAdministrationService(
                    repository, registry, Substitute.For<Autheris.Application.Common.Interfaces.IAuditLogRepository>(),
                    Microsoft.Extensions.Options.Options.Create(new GatewayOptions { CallerAttributes = Options() }), TimeProvider.System, [], invalidator);
                await service.SaveServiceSetAsync(Tenant, "app-1", "cost_center", ["a"], new CallerAttributeActor("oid-bob", HoldsApproverRole: true));
                invalidator.Received(1).InvalidateAttribute(Tenant, "cost_center");
                break;
            }
            case "case_07":
            {
                var bus = Substitute.For<IEventBus>();
                Func<string, Task>? handler = null;
                bus.Subscribe<string>(CallerAttributeResolver.InvalidationChannel, Arg.Do<Func<string, Task>>(h => handler = h)).Returns(Substitute.For<IDisposable>());
                using var fixture = new AttributeFixture(bus);
                await fixture.ResolveAsync();
                handler.ShouldNotBeNull();
                foreach (var message in new string?[] { null, "", "A|bad tenant!|x", "S|_reserved|x", "A|" + new string('t', 80) + "|x", "Z|t|x" })
                {
                    await handler(message!);
                }

                await fixture.ResolveAsync();
                fixture.Provider.Calls.ShouldBe(1);
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(bullet), bullet, null);
        }
    }

    private sealed class AttributeFixture : IDisposable
    {
        public CallerAttributeResolverTests.FakeProvider Provider { get; } = new();

        public CallerAttributeResolver Resolver { get; }

        public AttributeFixture(IEventBus? bus = null)
        {
            var registry = Substitute.For<ICallerAttributeRegistry>();
            registry.GetAsync(Arg.Any<CancellationToken>()).Returns(new CallerAttributeSnapshot(1,
                [Definition("cost_center", "claims", CallerAttributeType.Text, CallerAttributeCardinality.List, 50, 60)], []));
            Resolver = new CallerAttributeResolver(registry, [Provider],
                Microsoft.Extensions.Options.Options.Create(new GatewayOptions { CallerAttributes = Options() }), eventBus: bus);
        }

        public async Task ResolveAsync() =>
            await Resolver.ResolveAsync(CallerSubjectFactory.Create(Principal(Bearer, [("oid", UserOid)]), Tenant, Options()), ["cost_center"], CancellationToken.None);

        public void Dispose() => Resolver.Dispose();
    }

    private static async Task CacheScenarioAsync(long epochAfterFlush)
    {
        var tenant = new TenantId("tenant-replay");
        var served = new Queue<long>([5, epochAfterFlush, 5]);
        var state = Substitute.For<IDistributedClusterStateProvider>();
        state.GetAsync<long>(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => ValueTask.FromResult<long>(served.Dequeue()));
        state.IncrementAsync(default!, default, default, default).ReturnsForAnyArgs(ValueTask.FromResult<long?>(1));

        var repository = Substitute.For<IAccessProfileRepository>();
        var profile = new AccessProfile { ProfileId = "p-1", TenantId = tenant, Name = "P", MaskingMode = MaskingPolicyMode.Default, TargetTables = ["a.b.c"], CreatedBy = "admin", CreatedAt = DateTimeOffset.UtcNow };
        repository.GetProfilesForSubjectAsync(tenant, "alice", Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<AccessProfile>>([profile]));

        var options = Microsoft.Extensions.Options.Options.Create(new GatewayOptions
        {
            Caching = new CachingOptions { L1MemoryCache = new L1MemoryCacheOptions { AccessProfileEpochCacheMilliseconds = 0 } }
        });
        using var l1 = new MemoryCache(new MemoryCacheOptions { SizeLimit = 1024 * 1024 });
        using var cache = new AccessProfileCache(repository, state, l1, options);

        (await cache.GetProfilesAsync(tenant, "alice")).Count.ShouldBe(1);

        repository.GetProfilesForSubjectAsync(tenant, "alice", Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<AccessProfile>>([]));
        (await cache.GetProfilesAsync(tenant, "alice")).ShouldBeEmpty();
        (await cache.GetProfilesAsync(tenant, "alice")).ShouldBeEmpty();
    }
}
