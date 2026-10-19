using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Serialization;
using MemoryPack;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using StackExchange.Redis;
using Xunit;

namespace Autheris.Tests.Unit.Cache;

/// <summary>Issue #143 (SEC-GOV143-04): the L2 envelope carries the scope clearance and a restriction fingerprint; foreign or legacy entries are misses.</summary>
public sealed class CachedConsentEnvelopeTests
{
    private static readonly TableIdentifier Table = new("corp", "hr", "employees");
    private static readonly Sid User = new("S-1-5-21-143");
    private static readonly string DefaultFingerprint = RestrictedScopePolicy.Default.Fingerprint;

    private static TableAccessDecision Restricted() =>
        TableAccessDecision.Allowed(Table, new Dictionary<string, ColumnAccessLevel> { ["salary"] = ColumnAccessLevel.Clear, ["name"] = ColumnAccessLevel.Clear }) with
        {
            ScopeClearance = new RestrictedScopeClearance(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Agent.Read" },
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "name" })
        };

    [Fact]
    public void RoundTrip_KeepsClearance()
    {
        var env = MemoryPackSerializer.Deserialize<CachedConsentEnvelope>(MemoryPackSerializer.Serialize(CachedConsentEnvelope.FromDecision(Restricted(), epoch: 5)))!;

        var decision = env.ToDecision(DefaultFingerprint);

        decision.ShouldNotBeNull();
        decision!.ScopeClearance.ShouldNotBeNull();
        decision.ScopeClearance!.RestrictedScopesHeld.ShouldBe(["Agent.Read"]);
        decision.ScopeClearance.ClearedColumns.ShouldBe(["name"]);
        decision.ScopeClearance.Permits("NAME").ShouldBeTrue();
        decision.ScopeClearance.Permits("salary").ShouldBeFalse();
    }

    [Fact]
    public void UnrestrictedDecision_RoundTrips_WithoutClearance()
    {
        var plain = TableAccessDecision.Allowed(Table, new Dictionary<string, ColumnAccessLevel> { ["name"] = ColumnAccessLevel.Clear });

        CachedConsentEnvelope.FromDecision(plain, 1).ToDecision(DefaultFingerprint)!.ScopeClearance.ShouldBeNull();
    }

    [Fact]
    public void LegacyLayout_IsMiss()
    {
        var legacy = new CachedConsentEnvelope { Domain = "corp", Schema = "hr", TableName = "employees", IsAllowed = true, HasUnconstrainedColumnAllow = true };

        legacy.RestrictionLayout.ShouldBe(0);
        legacy.ToDecision(DefaultFingerprint).ShouldBeNull();
    }

    [Fact]
    public void FingerprintMismatch_IsMiss()
    {
        var env = CachedConsentEnvelope.FromDecision(Restricted(), 1, DefaultFingerprint);

        env.ToDecision("another-fingerprint").ShouldBeNull();
        env.ToDecision(DefaultFingerprint).ShouldNotBeNull();
    }

    private static (ConsentCacheService Cache, IDatabase Db) Create(IRestrictedScopePolicy? policy)
    {
        var epochs = Substitute.For<IEpochValidationService>();
        epochs.IsEpochValidAsync(Arg.Any<TableIdentifier>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(true);
        epochs.GetCurrentEpochAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns(1L);
        epochs.AreEpochsValidAsync(Arg.Any<IReadOnlyList<EpochCheck>>(), Arg.Any<CancellationToken>())
            .Returns(ci => (IReadOnlyDictionary<TableIdentifier, bool>)ci.Arg<IReadOnlyList<EpochCheck>>().ToDictionary(c => c.Table, _ => true));
        var db = Substitute.For<IDatabase>();
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);
        var secrets = Substitute.For<IKeyVaultSecretProvider>();
        secrets.GetSecretBytes(Arg.Any<string>()).Returns(System.Text.Encoding.UTF8.GetBytes("cluster-master-key"));
        var cache = new ConsentCacheService(
            new MemoryCache(new MemoryCacheOptions()), epochs, Substitute.For<IEventBus>(), Options.Create(new GatewayOptions()),
            new MemoryPackCacheSerializer(), mux, secrets, null, policy);
        return (cache, db);
    }

    private static void ServeL2(ConsentCacheService cache, IDatabase db, CachedConsentEnvelope envelope)
    {
        var payload = new MemoryPackCacheSerializer().Serialize(envelope);
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(ci =>
            Task.FromResult((RedisValue)cache.ProtectL2Payload(ci.Arg<RedisKey>().ToString(), payload, DateTimeOffset.UtcNow.AddMinutes(5))));
    }

    private static CachedConsentEnvelope LegacyEnvelope() => new()
    {
        Domain = "corp", Schema = "hr", TableName = "employees", IsAllowed = true, Epoch = 1, HasUnconstrainedColumnAllow = true
    };

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_GOV143_07_Envelope_LegacyLayoutAndForeignFingerprint_AreMiss_L1AndL2Paths()
    {
        // Positive control: a current-layout entry with the matching fingerprint is served on both read paths.
        var (okCache, okDb) = Create(null);
        ServeL2(okCache, okDb, CachedConsentEnvelope.FromDecision(Restricted(), 1, DefaultFingerprint));
        (await okCache.GetCachedDecisionAsync(new TenantId("t1"), User, Table)).ShouldNotBeNull();
        (await okCache.GetCachedDecisionsAsync(new TenantId("t1"), User, [Table]))[Table].ShouldNotBeNull();

        // Legacy layout (written before the restriction fields existed), both paths.
        var (cache, db) = Create(null);
        ServeL2(cache, db, LegacyEnvelope());
        (await cache.GetCachedDecisionAsync(new TenantId("t1"), User, Table)).ShouldBeNull();
        (await cache.GetCachedDecisionsAsync(new TenantId("t1"), User, [Table]))[Table].ShouldBeNull();

        // Entry written by an instance with another restricted list (rolling deploy), both paths.
        var (foreign, foreignDb) = Create(RestrictedScopePolicy.FromOptions(new RestrictedScopeOptions { Scopes = ["Data.Restricted"] }));
        ServeL2(foreign, foreignDb, CachedConsentEnvelope.FromDecision(Restricted(), 1, DefaultFingerprint));
        (await foreign.GetCachedDecisionAsync(new TenantId("t1"), User, Table)).ShouldBeNull();
        (await foreign.GetCachedDecisionsAsync(new TenantId("t1"), User, [Table]))[Table].ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_GOV143_08_Envelope_RestrictedKeyNeverServedToScopelessCaller()
    {
        var (cache, _) = Create(null);
        var restrictedHash = IConsentCacheService.ComputeSubjectContextHash(null, null, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Agent.Read" });
        var scopelessHash = IConsentCacheService.ComputeSubjectContextHash(null, null, null);
        restrictedHash.ShouldNotBe(scopelessHash);

        await cache.SetCachedDecisionAsync(new TenantId("t1"), User, Table, Restricted(), TimeSpan.FromMinutes(1), restrictedHash);

        (await cache.GetCachedDecisionAsync(new TenantId("t1"), User, Table, scopelessHash)).ShouldBeNull();
        (await cache.GetCachedDecisionAsync(new TenantId("t1"), User, Table, restrictedHash))!.ScopeClearance.ShouldNotBeNull();
    }
}
