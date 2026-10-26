namespace Autheris.Tests.Unit.Identity.Attributes;

using Autheris.Tests.Common;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Identity.Attributes;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine.Ast.Emit;
using Xunit;
using static CallerAttributeTestSupport;

/// <summary>PLAN-GOV-129 tests 9-11 and ST-129-07..09, ST-129-11, ST-129-20 (BC-129-07, BC-129-08, BC-129-12).</summary>
[Trait("Category", "Security")]
public sealed class CallerAttributeResolverTests : IDisposable
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 17, 8, 0, 0, TimeSpan.Zero));
    private readonly List<CallerAttributeResolver> _resolvers = [];

    public void Dispose() => _resolvers.ForEach(r => r.Dispose());

    internal sealed class FakeProvider(string kind = "claims") : ICallerAttributeProvider
    {
        public int Calls;
        public Func<CallerSubject, CallerAttributeDefinition, CancellationToken, Task<CallerAttributeResult>> Behavior { get; set; } =
            (_, _, _) => Task.FromResult(Raw("100"));

        public string Kind => kind;

        public async ValueTask<CallerAttributeResult> ResolveAsync(CallerSubject subject, CallerAttributeDefinition definition, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return await Behavior(subject, definition, ct);
        }
    }

    internal sealed class NonCachingProvider : ICallerAttributeProvider, INonCachingCallerAttributeProvider
    {
        public int Calls;
        public string Kind => "claims";

        public ValueTask<CallerAttributeResult> ResolveAsync(CallerSubject subject, CallerAttributeDefinition definition, CancellationToken ct) =>
            ValueTask.FromResult(Raw($"v{Interlocked.Increment(ref Calls)}"));
    }

    internal static CallerAttributeResult Raw(params string[] values) =>
        CallerAttributeResult.Resolved(new CallerAttributeValue(values.Cast<object>().ToList(), SqlParameterType.String));

    private static string[] Strings(CallerAttributeResult r) => Enumerable.Range(0, r.Value!.Count).Select(i => r.Value[i].ToString()!).ToArray();

    private CallerAttributeResolver Create(CallerAttributeOptions? options, ICallerAttributeRegistry registry, params ICallerAttributeProvider[] providers)
    {
        var resolver = new CallerAttributeResolver(registry, providers, Microsoft.Extensions.Options.Options.Create(new GatewayOptions { CallerAttributes = options ?? Options() }), _time);
        _resolvers.Add(resolver);
        return resolver;
    }

    private static ICallerAttributeRegistry Registry(params CallerAttributeDefinition[] definitions)
    {
        var registry = Substitute.For<ICallerAttributeRegistry>();
        registry.GetAsync(Arg.Any<CancellationToken>()).Returns(new CallerAttributeSnapshot(1, definitions, []));
        return registry;
    }

    private static CallerSubject User(string oid = "11111111-1111-1111-1111-111111111111") =>
        CallerSubjectFactory.Create(Principal(Bearer, [("oid", oid)]), Tenant, Options());

    private static CallerAttributeDefinition Def(string kind = "claims", int ttl = 60, CallerAttributeType type = CallerAttributeType.Text, CallerAttributeCardinality card = CallerAttributeCardinality.List, int max = 50) =>
        Definition("cost_center", kind, type, card, max, ttl);

    private static async Task<CallerAttributeResult> Resolve(CallerAttributeResolver resolver, CallerSubject subject, string name = "cost_center", CancellationToken ct = default)
    {
        var set = await resolver.ResolveAsync(subject, [name], ct);
        set.TryGet(name, out var result);
        return result;
    }

    [Fact]
    public async Task Disabled_ResolvesNothing_AndNeverCallsAProvider()
    {
        var provider = new FakeProvider();
        var resolver = Create(Options(enabled: false), Registry(Def()), provider);
        resolver.IsEnabled.ShouldBeFalse();
        (await Resolve(resolver, User())).Outcome.ShouldBe(CallerAttributeOutcome.UnknownAttribute);
        provider.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Resolved_ValuesAreTyped_AccordingToTheDefinition()
    {
        var provider = new FakeProvider { Behavior = (_, _, _) => Task.FromResult(Raw("100", "200", "100")) };
        var resolver = Create(null, Registry(Def(type: CallerAttributeType.Whole)), provider);
        var result = await Resolve(resolver, User());
        result.Outcome.ShouldBe(CallerAttributeOutcome.Resolved);
        result.Value!.ElementType.ShouldBe(SqlParameterType.Int64);
        result.Value.Count.ShouldBe(2);   // de-duplicated
        result.Value[0].ShouldBe(100L);
    }

    [Fact]
    public async Task ST_129_20_Guid_IsCanonicalLowerCase_AndAnInvalidGuid_IsFailedNotNormalizable()
    {
        var good = new FakeProvider { Behavior = (_, _, _) => Task.FromResult(Raw("AAAAAAAA-0000-0000-0000-000000000001")) };
        var r1 = await Resolve(Create(null, Registry(Def(type: CallerAttributeType.Uuid)), good), User());
        r1.Value![0].ShouldBe("aaaaaaaa-0000-0000-0000-000000000001");

        var bad = new FakeProvider { Behavior = (_, _, _) => Task.FromResult(Raw("not-a-guid")) };
        var r2 = await Resolve(Create(null, Registry(Def(type: CallerAttributeType.Uuid)), bad), User());
        r2.Outcome.ShouldBe(CallerAttributeOutcome.Failed);
        r2.ReasonCode.ShouldBe("NotNormalizable");
    }

    [Fact]
    public async Task MixedTypes_AndNonNumbers_AreFailedNotNormalizable()
    {
        var provider = new FakeProvider { Behavior = (_, _, _) => Task.FromResult(Raw("1", "x")) };
        var result = await Resolve(Create(null, Registry(Def(type: CallerAttributeType.Whole)), provider), User());
        result.ReasonCode.ShouldBe("NotNormalizable");
    }

    [Fact]
    public async Task UnknownAttribute_AndAnAttributeOfAnotherTenant_AreUnknown()
    {
        var provider = new FakeProvider();
        var otherTenant = Def() with { Tenant = new TenantId("tenant_b") };
        var resolver = Create(null, Registry(otherTenant), provider);
        (await Resolve(resolver, User())).Outcome.ShouldBe(CallerAttributeOutcome.UnknownAttribute);
        (await Resolve(resolver, User(), "does_not_exist")).Outcome.ShouldBe(CallerAttributeOutcome.UnknownAttribute);
        provider.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task NoSubject_IsNoSubject()
    {
        var provider = new FakeProvider();
        var result = await Resolve(Create(null, Registry(Def()), provider), CallerSubject.NoSubject(Tenant));
        result.Outcome.ShouldBe(CallerAttributeOutcome.NoSubject);
        provider.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task ST_129_02_ApplicationSubject_UsesTheServiceSetProvider_AndUsersNeverDo()
    {
        var claims = new FakeProvider("claims") { Behavior = (_, _, _) => Task.FromResult(Raw("user-value")) };
        var sets = new FakeProvider("service-set") { Behavior = (_, _, _) => Task.FromResult(Raw("app-value")) };
        var resolver = Create(null, Registry(Def("claims")), claims, sets);
        var app = CallerSubjectFactory.Create(Principal(Bearer, [("idtyp", "app"), ("appid", "aaaaaaaa-0000-0000-0000-000000000001")]), Tenant, Options());

        (await Resolve(resolver, app)).Value![0].ShouldBe("app-value");
        claims.Calls.ShouldBe(0);

        var resolver2 = Create(null, Registry(Def("service-set")), claims, sets);
        var before = sets.Calls;
        (await Resolve(resolver2, User())).Outcome.ShouldBe(CallerAttributeOutcome.Missing);
        sets.Calls.ShouldBe(before);
    }

    [Fact]
    public async Task EmptyList_IsMissing()
    {
        var provider = new FakeProvider { Behavior = (_, _, _) => Task.FromResult(CallerAttributeResult.Resolved(new CallerAttributeValue([], SqlParameterType.String))) };
        (await Resolve(Create(null, Registry(Def()), provider), User())).Outcome.ShouldBe(CallerAttributeOutcome.Missing);
    }

    [Fact]
    public async Task OverTheCap_IsOverCardinality_NeverTruncated()
    {
        var provider = new FakeProvider { Behavior = (_, _, _) => Task.FromResult(Raw(Enumerable.Range(0, 6).Select(i => $"v{i}").ToArray())) };
        var result = await Resolve(Create(null, Registry(Def(max: 5)), provider), User());
        result.Outcome.ShouldBe(CallerAttributeOutcome.OverCardinality);
        result.Value.ShouldBeNull();
    }

    [Fact]
    public async Task ScalarWithTwoDistinctValues_IsFailedAmbiguous()
    {
        var provider = new FakeProvider { Behavior = (_, _, _) => Task.FromResult(Raw("a", "b")) };
        var result = await Resolve(Create(null, Registry(Def(card: CallerAttributeCardinality.Scalar, max: 1)), provider), User());
        result.ReasonCode.ShouldBe("Ambiguous");
    }

    [Fact]
    public async Task Value_IsCached_UntilTheTtl_AndNeverLongerThan300Seconds()
    {
        var provider = new FakeProvider();
        var resolver = Create(null, Registry(Def(ttl: 900)), provider);   // the definition asks for 900 s: clamped to the 300 s maximum
        await Resolve(resolver, User());
        await Resolve(resolver, User());
        provider.Calls.ShouldBe(1);

        _time.Advance(TimeSpan.FromSeconds(299));
        await Resolve(resolver, User());
        provider.Calls.ShouldBe(1);

        _time.Advance(TimeSpan.FromSeconds(2));
        await Resolve(resolver, User());
        provider.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task DefinitionTtl_ShorterThanTheMaximum_Applies()
    {
        var provider = new FakeProvider();
        var resolver = Create(null, Registry(Def(ttl: 10)), provider);
        await Resolve(resolver, User());
        _time.Advance(TimeSpan.FromSeconds(11));
        await Resolve(resolver, User());
        provider.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task Missing_IsCachedForTheNegativeTtlOnly()
    {
        var provider = new FakeProvider { Behavior = (_, _, _) => Task.FromResult(CallerAttributeResult.Missing()) };
        var resolver = Create(null, Registry(Def()), provider);
        await Resolve(resolver, User());
        await Resolve(resolver, User());
        provider.Calls.ShouldBe(1);
        _time.Advance(TimeSpan.FromSeconds(16));
        await Resolve(resolver, User());
        provider.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task Failures_AreNeverCached()
    {
        int n = 0;
        var provider = new FakeProvider { Behavior = (_, _, _) => ++n == 1 ? throw new InvalidOperationException("secret-detail") : Task.FromResult(Raw("100")) };
        var resolver = Create(null, Registry(Def()), provider);
        var first = await Resolve(resolver, User());
        first.Outcome.ShouldBe(CallerAttributeOutcome.Failed);
        first.ReasonCode!.ShouldNotContain("secret");
        (await Resolve(resolver, User())).Outcome.ShouldBe(CallerAttributeOutcome.Resolved);
    }

    [Fact]
    public async Task ST_129_09_SingleFlight_OneProviderCall_For100ConcurrentCallersOfOneSubject()
    {
        var gate = new TaskCompletionSource();
        var provider = new FakeProvider { Behavior = async (_, _, _) => { await gate.Task; return Raw("100"); } };
        var resolver = Create(null, Registry(Def()), provider);
        var calls = Enumerable.Range(0, 100).Select(_ => Resolve(resolver, User())).ToArray();
        gate.SetResult();
        var results = await Task.WhenAll(calls);
        results.ShouldAllBe(r => r.Outcome == CallerAttributeOutcome.Resolved);
        provider.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task ST_129_09_DistinctSubjects_AreDistinctLoads()
    {
        var provider = new FakeProvider();
        var resolver = Create(null, Registry(Def()), provider);
        for (int i = 0; i < 5; i++)
        {
            await Resolve(resolver, User($"11111111-1111-1111-1111-11111111111{i}"));
        }

        provider.Calls.ShouldBe(5);
    }

    [Fact]
    public async Task ST_129_09_CallerCancellation_DoesNotFailAnotherWaiterOfTheSameLoad()
    {
        var gate = new TaskCompletionSource();
        var provider = new FakeProvider { Behavior = async (_, _, _) => { await gate.Task; return Raw("100"); } };
        var resolver = Create(null, Registry(Def()), provider);
        using var cancelA = new CancellationTokenSource();
        var a = Resolve(resolver, User(), ct: cancelA.Token);
        var b = Resolve(resolver, User());
        await WaitHelper.WaitUntil(() => Volatile.Read(ref provider.Calls) >= 1, WaitHelper.HangGuard, "the provider was never invoked");
        cancelA.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => a);
        gate.SetResult();
        (await b).Outcome.ShouldBe(CallerAttributeOutcome.Resolved);
        provider.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task ST_129_07_UserKeyEqualToAnApplicationId_DoesNotCollide()
    {
        var claims = new FakeProvider("claims") { Behavior = (_, _, _) => Task.FromResult(Raw("person")) };
        var sets = new FakeProvider("service-set") { Behavior = (_, _, _) => Task.FromResult(Raw("application")) };
        var resolver = Create(null, Registry(Def("claims")), claims, sets);
        var guid = "aaaaaaaa-0000-0000-0000-000000000001";
        var person = User(guid);
        var app = CallerSubjectFactory.Create(Principal(Bearer, [("idtyp", "app"), ("appid", guid)]), Tenant, Options());

        (await Resolve(resolver, person)).Value![0].ShouldBe("person");
        (await Resolve(resolver, app)).Value![0].ShouldBe("application");
        (await Resolve(resolver, person)).Value![0].ShouldBe("person");
    }

    [Fact]
    public async Task ST_129_07_SameKeyInTwoTenants_AreSeparateEntries()
    {
        var provider = new FakeProvider { Behavior = (s, _, _) => Task.FromResult(Raw(s.Tenant.Value)) };
        var defA = Def();
        var defB = defA with { Tenant = new TenantId("tenant_b") };
        var resolver = Create(null, Registry(defA, defB), provider);
        var a = CallerSubjectFactory.Create(Principal(Bearer, [("oid", UserOid)]), Tenant, Options());
        var b = CallerSubjectFactory.Create(Principal(Bearer, [("oid", UserOid)]), new TenantId("tenant_b"), Options());

        (await Resolve(resolver, a)).Value![0].ShouldBe("tenant_a");
        (await Resolve(resolver, b)).Value![0].ShouldBe("tenant_b");
    }

    [Fact]
    public async Task DefinitionChange_DropsStaleEntries_ThroughTheHashInTheKey()
    {
        var provider = new FakeProvider();
        var registry = Substitute.For<ICallerAttributeRegistry>();
        var v1 = Def();
        var v2 = v1 with { Description = "changed" };
        registry.GetAsync(Arg.Any<CancellationToken>()).Returns(new CallerAttributeSnapshot(1, [v1], []), new CallerAttributeSnapshot(2, [v2], []));
        var resolver = Create(null, registry, provider);

        await Resolve(resolver, User());
        await Resolve(resolver, User());
        provider.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task InvalidateAttribute_AndInvalidateSubject_ForceANewLoad()
    {
        var provider = new FakeProvider();
        var resolver = Create(null, Registry(Def()), provider);
        await Resolve(resolver, User());
        resolver.InvalidateAttribute(Tenant, "cost_center");
        await Resolve(resolver, User());
        provider.Calls.ShouldBe(2);

        resolver.InvalidateSubject(Tenant, UserOid);
        await Resolve(resolver, User());
        provider.Calls.ShouldBe(3);
    }

    [Fact]
    public async Task ST_129_11_BreakerOpens_AfterFiveInfrastructureFailures_AndClosesAfterTenSeconds()
    {
        var provider = new FakeProvider { Behavior = (_, _, _) => throw new InvalidOperationException("down") };
        var resolver = Create(null, Registry(Def()), provider);
        for (int i = 0; i < 5; i++)
        {
            (await Resolve(resolver, User($"11111111-1111-1111-1111-11111111111{i}"))).Outcome.ShouldBe(CallerAttributeOutcome.Failed);
        }

        provider.Calls.ShouldBe(5);
        var open = await Resolve(resolver, User("11111111-1111-1111-1111-111111111119"));
        open.Outcome.ShouldBe(CallerAttributeOutcome.Failed);
        provider.Calls.ShouldBe(5);   // circuit open: no call

        _time.Advance(TimeSpan.FromSeconds(11));
        provider.Behavior = (_, _, _) => Task.FromResult(Raw("100"));
        (await Resolve(resolver, User("11111111-1111-1111-1111-111111111119"))).Outcome.ShouldBe(CallerAttributeOutcome.Resolved);
        (await Resolve(resolver, User("11111111-1111-1111-1111-111111111118"))).Outcome.ShouldBe(CallerAttributeOutcome.Resolved);
    }

    [Fact]
    public async Task ST_129_11_OverCardinalityOfOneSubject_NeverOpensTheBreakerForOthers()
    {
        var provider = new FakeProvider
        {
            Behavior = (s, _, _) => Task.FromResult(s.KeyFor(SubjectKeyKind.EntraObjectId) == OtherOid
                ? Raw(Enumerable.Range(0, 60).Select(i => $"v{i}").ToArray())
                : Raw("100"))
        };
        var resolver = Create(null, Registry(Def(max: 50)), provider);
        var greedy = User(OtherOid);
        for (int i = 0; i < 40; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(301));   // defeat the cache so that every request reaches the provider
            (await Resolve(resolver, greedy)).Outcome.ShouldBe(CallerAttributeOutcome.OverCardinality);
        }

        (await Resolve(resolver, User())).Outcome.ShouldBe(CallerAttributeOutcome.Resolved);
    }

    [Fact]
    public async Task ST_129_11_MissingAndNotNormalizable_NeverOpenTheBreaker()
    {
        var provider = new FakeProvider { Behavior = (_, _, _) => Task.FromResult(CallerAttributeResult.Missing()) };
        var resolver = Create(null, Registry(Def(type: CallerAttributeType.Whole)), provider);
        for (int i = 0; i < 10; i++)
        {
            await Resolve(resolver, User($"11111111-1111-1111-1111-11111111111{i % 10}"));
        }

        provider.Behavior = (_, _, _) => Task.FromResult(Raw("x"));   // not a number
        for (int i = 0; i < 10; i++)
        {
            (await Resolve(resolver, User($"22222222-2222-2222-2222-22222222222{i % 10}"))).ReasonCode.ShouldBe("NotNormalizable");
        }

        provider.Behavior = (_, _, _) => Task.FromResult(Raw("7"));
        (await Resolve(resolver, User("33333333-3333-3333-3333-333333333333"))).Outcome.ShouldBe(CallerAttributeOutcome.Resolved);
    }

    [Fact]
    public async Task ProviderTimeout_IsFailed_AndCountsAsAnInfrastructureFailure()
    {
        var provider = new FakeProvider { Behavior = async (_, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return Raw("x"); } };
        var resolver = Create(null, Registry(Def()), provider);
        var pending = Resolve(resolver, User());
        await WaitHelper.WaitUntil(() => Volatile.Read(ref provider.Calls) >= 1, WaitHelper.HangGuard, "the provider was never invoked");
        _time.Advance(TimeSpan.FromMilliseconds(2100));
        var result = await pending;
        result.Outcome.ShouldBe(CallerAttributeOutcome.Failed);
        result.ReasonCode.ShouldBe("Timeout");
    }

    [Fact]
    public async Task ST_129_09_Bulkhead_RejectsAboveTheConcurrencyLimit_AsFailed()
    {
        var gate = new TaskCompletionSource();
        var provider = new FakeProvider { Behavior = async (_, _, _) => { await gate.Task; return Raw("100"); } };
        var options = new CallerAttributeOptions { Enabled = true, ProviderMaxConcurrency = 2, Claims = Options().Claims };
        var resolver = Create(options, Registry(Def()), provider);
        var running = new[]
        {
            Resolve(resolver, User("11111111-1111-1111-1111-111111111111")),
            Resolve(resolver, User("11111111-1111-1111-1111-111111111112"))
        };
        await WaitHelper.WaitUntil(() => Volatile.Read(ref provider.Calls) >= 2, WaitHelper.HangGuard, "the provider was never invoked");
        var rejected = await Resolve(resolver, User("11111111-1111-1111-1111-111111111113"));
        rejected.Outcome.ShouldBe(CallerAttributeOutcome.Failed);
        rejected.ReasonCode.ShouldBe("Bulkhead");
        gate.SetResult();
        (await Task.WhenAll(running)).ShouldAllBe(r => r.Outcome == CallerAttributeOutcome.Resolved);
    }

    [Fact]
    public async Task RegistryFailure_IsFailed_NotAnException()
    {
        var registry = Substitute.For<ICallerAttributeRegistry>();
        registry.GetAsync(Arg.Any<CancellationToken>()).Returns<ValueTask<CallerAttributeSnapshot>>(_ => throw new InvalidOperationException("db down"));
        var result = await Resolve(Create(null, registry, new FakeProvider()), User());
        result.Outcome.ShouldBe(CallerAttributeOutcome.Failed);
        result.ReasonCode.ShouldBe("RegistryUnavailable");
    }

    [Fact]
    public async Task ST_129_08_RequestMemo_GivesOneResult_PerRequest_EvenWhenTheProviderChanges()
    {
        var provider = new NonCachingProvider();
        var resolver = Create(null, Registry(Def()), provider);
        var principal = Principal(Bearer, [("oid", UserOid)]);

        var s1 = resolver.CreateSubject(principal, Tenant);
        var s2 = resolver.CreateSubject(principal, Tenant);   // a nested read of the same request
        var first = await Resolve(resolver, s1);
        var second = await Resolve(resolver, s2);

        second.Value![0].ShouldBe(first.Value![0]);
        provider.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task ST_129_08_AnotherPrincipalObject_IsAnotherRequest()
    {
        var provider = new NonCachingProvider();
        var resolver = Create(null, Registry(Def()), provider);
        var a = await Resolve(resolver, resolver.CreateSubject(Principal(Bearer, [("oid", UserOid)]), Tenant));
        var b = await Resolve(resolver, resolver.CreateSubject(Principal(Bearer, [("oid", UserOid)]), Tenant));
        b.Value![0].ShouldNotBe(a.Value![0]);
    }

    [Fact]
    public async Task ST_129_08_APrincipalObjectReusedAfterThe30SecondBound_GetsFreshValues()
    {
        var provider = new NonCachingProvider();
        var resolver = Create(null, Registry(Def()), provider);
        var principal = Principal(Bearer, [("oid", UserOid)]);
        var first = await Resolve(resolver, resolver.CreateSubject(principal, Tenant));
        _time.Advance(TimeSpan.FromSeconds(31));
        var second = await Resolve(resolver, resolver.CreateSubject(principal, Tenant));
        second.Value![0].ShouldNotBe(first.Value![0]);
    }

    [Fact]
    public async Task ST_129_08_RequestMemo_IsBoundedByTheDefinitionTtl_WhenShorter()
    {
        var provider = new NonCachingProvider();
        var resolver = Create(null, Registry(Def(ttl: 5)), provider);
        var principal = Principal(Bearer, [("oid", UserOid)]);
        await Resolve(resolver, resolver.CreateSubject(principal, Tenant));
        _time.Advance(TimeSpan.FromSeconds(6));
        await Resolve(resolver, resolver.CreateSubject(principal, Tenant));
        provider.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task ST_129_08_LongLivedSubjects_HaveNoMemo()
    {
        var provider = new NonCachingProvider();
        var resolver = Create(null, Registry(Def()), provider);
        var principal = Principal(Bearer, [("oid", UserOid)]);
        var a = await Resolve(resolver, resolver.CreateSubject(principal, Tenant, longLived: true));
        var b = await Resolve(resolver, resolver.CreateSubject(principal, Tenant, longLived: true));
        b.Value![0].ShouldNotBe(a.Value![0]);
    }

    [Fact]
    public async Task ST_129_08_RequestMemo_IsDiscarded_WhenTheDefinitionHashChanges()
    {
        var provider = new NonCachingProvider();
        var registry = Substitute.For<ICallerAttributeRegistry>();
        var v1 = Def();
        registry.GetAsync(Arg.Any<CancellationToken>()).Returns(new CallerAttributeSnapshot(1, [v1], []), new CallerAttributeSnapshot(2, [v1 with { Description = "x" }], []));
        var resolver = Create(null, registry, provider);
        var principal = Principal(Bearer, [("oid", UserOid)]);
        await Resolve(resolver, resolver.CreateSubject(principal, Tenant));
        await Resolve(resolver, resolver.CreateSubject(principal, Tenant));
        provider.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task ST_129_15_BindReservation_IsPerRequest_AndRefusesTheExcess()
    {
        var resolver = Create(null, Registry(Def()), new FakeProvider());
        var principal = Principal(Bearer, [("oid", UserOid)]);
        var a = resolver.CreateSubject(principal, Tenant);
        var b = resolver.CreateSubject(principal, Tenant);
        a.TryReserveBinds(300, 400).ShouldBeTrue();
        b.TryReserveBinds(100, 400).ShouldBeTrue();
        b.TryReserveBinds(1, 400).ShouldBeFalse();
        resolver.CreateSubject(Principal(Bearer, [("oid", UserOid)]), Tenant).TryReserveBinds(400, 400).ShouldBeTrue();   // another request
    }

    [Fact]
    public async Task ST_129_15_BindReservation_IsReleased_WithTheMemoWindow()
    {
        var resolver = Create(null, Registry(Def()), new FakeProvider());
        var subject = resolver.CreateSubject(Principal(Bearer, [("oid", UserOid)]), Tenant);
        subject.TryReserveBinds(400, 400).ShouldBeTrue();
        subject.TryReserveBinds(1, 400).ShouldBeFalse();
        _time.Advance(TimeSpan.FromSeconds(Options().RequestMemoSeconds + 1));
        subject.TryReserveBinds(400, 400).ShouldBeTrue();
    }

    [Fact]
    public void ST_129_15_LongLivedSubject_HasThePerDecisionCap()
    {
        var resolver = Create(null, Registry(Def()), new FakeProvider());
        var subject = resolver.CreateSubject(Principal(Bearer, [("oid", UserOid)]), Tenant, longLived: true);
        subject.TryReserveBinds(400, 400).ShouldBeTrue();
        subject.TryReserveBinds(400, 400).ShouldBeTrue();   // another decision: nothing accumulates
        subject.TryReserveBinds(401, 400).ShouldBeFalse();
    }

    [Fact]
    public async Task ST_129_09_FirstCallerCancellation_DoesNotFailAnotherWaiterOfTheSharedMemoTask()
    {
        var gate = new TaskCompletionSource();
        var provider = new FakeProvider { Behavior = async (_, _, _) => { await gate.Task; return Raw("100"); } };
        var resolver = Create(null, Registry(Def()), provider);
        var principal = Principal(Bearer, [("oid", UserOid)]);
        using var cancelA = new CancellationTokenSource();
        var a = Resolve(resolver, resolver.CreateSubject(principal, Tenant), ct: cancelA.Token);
        var b = Resolve(resolver, resolver.CreateSubject(principal, Tenant));
        await WaitHelper.WaitUntil(() => Volatile.Read(ref provider.Calls) >= 1, WaitHelper.HangGuard, "the provider was never invoked");
        cancelA.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => a);
        gate.SetResult();
        (await b).Outcome.ShouldBe(CallerAttributeOutcome.Resolved);
        provider.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task PlugInSeam_AFakeScimProvider_IsUsedForAnAttributeBoundToIt()
    {
        var scim = new FakeProvider("scim") { Behavior = (_, _, _) => Task.FromResult(Raw("from-scim")) };
        var resolver = Create(null, Registry(Def("scim")), new FakeProvider("claims"), scim);
        (await Resolve(resolver, User())).Value![0].ShouldBe("from-scim");
        scim.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task UnknownProviderKind_IsFailed()
    {
        var result = await Resolve(Create(null, Registry(Def("ldaps")), new FakeProvider()), User());
        result.Outcome.ShouldBe(CallerAttributeOutcome.Failed);
    }

    [Fact]
    public async Task ST_129_22_ResultTypes_PrintNoValues()
    {
        var provider = new FakeProvider { Behavior = (_, _, _) => Task.FromResult(Raw("top-secret-value")) };
        var resolver = Create(null, Registry(Def()), provider);
        var set = await resolver.ResolveAsync(User(), ["cost_center"], default);
        set.TryGet("cost_center", out var result).ShouldBeTrue();
        set.ToString().ShouldNotContain("top-secret");
        result.ToString().ShouldNotContain("top-secret");
        result.Value!.ToString()!.ShouldNotContain("top-secret");
        string.Join(";", set.Outcomes.Select(o => o.ToString())).ShouldNotContain("top-secret");
    }
}
