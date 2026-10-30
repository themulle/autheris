#pragma warning disable CA2012

namespace Autheris.Tests.Unit.Governance;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Application.Services;
using Autheris.Application.Streaming.Services;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Extensions.Lakehouse.Interfaces;
using Autheris.Extensions.Lakehouse.Services;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;
using static Autheris.Tests.Unit.Mcp.ScopeLevelTestKit;

/// <summary>
/// Issue #143 (PLAN-GOV-143 addendum A-1, SEC-GOV143-02/-13/-14): the paths that resolved consents without the token scopes (Iceberg, CDC stream)
/// and the callers without claims apply the restriction too.
/// </summary>
[Trait("Category", "Security")]
public sealed class Issue143ScopeBlindPathTests
{
    private const string Restricted = "Agent.Read";
    private static readonly TableMetadata Meta = CreateTable(gradeSensitive: true);
    private static readonly ConsentColumnRule IdClear = Rule("id", ColumnAccessLevel.Clear);
    private static readonly ConsentColumnRule GradeClear = Rule("grade", ColumnAccessLevel.Clear);
    private static readonly TenantId TenantA = new(Tenant);

    // ---------------------------------------------------------------- Iceberg (T-GOV143-02)

    private static readonly TableIdentifier IcebergId = new("tenant-1", "raw", "orders");

    private static ClaimsPrincipal Analyst(string? scp = null)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, "analyst@corp.com"), new(ClaimTypes.Role, "DataScientist") };
        if (scp != null)
        {
            claims.Add(new Claim("scp", scp));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
    }

    /// <summary>The Iceberg service over one consent; <paramref name="seen"/> records the scope set the resolver was given.</summary>
    private static IcebergRestCatalogFederationService IcebergWith(List<IReadOnlySet<string>?> seen, bool sensitive, params ConsentColumnRule[] rules)
    {
        var meta = new TableMetadata
        {
            Identifier = IcebergId,
            Table = new Table { SourceName = "tenant-1", SchemaName = "raw", TableName = "orders", DataSourceType = DataSourceType.LakehouseIceberg },
            Columns = [new TableColumn { ColumnName = "ssn", IsSensitive = sensitive }]
        };
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        metadataRepo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<TableMetadata?>(meta));
        var consent = new Consent
        {
            TableIdentifier = IcebergId,
            TenantId = new TenantId("tenant-1"),
            GranteeType = GranteeType.User,
            GranteeSid = new Sid("analyst@corp.com"),
            Effect = ConsentEffect.Allow,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1),
            ColumnRules = rules
        };
        var repo = Substitute.For<IConsentRepository>();
        repo.GetActiveConsentsForSubjectsAsync(Arg.Any<IReadOnlyList<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>([consent]));
        var real = new ConsentResolutionService();
        var recording = Substitute.For<IConsentResolutionService>();
        recording.ResolveAccess(default!, default!, default!, default, default!, default, default, default)
            .ReturnsForAnyArgs(ci =>
            {
                seen.Add(ci.ArgAt<IReadOnlySet<string>?>(7));
                return real.ResolveAccess(ci.ArgAt<Sid>(0), ci.ArgAt<IReadOnlySet<Sid>>(1), ci.ArgAt<IReadOnlySet<string>>(2), ci.ArgAt<TableIdentifier>(3),
                    ci.ArgAt<IReadOnlyList<Consent>>(4), ci.ArgAt<DatabaseDialect>(5), ci.ArgAt<IReadOnlySet<Sid>?>(6), ci.ArgAt<IReadOnlySet<string>?>(7));
            });
        return new IcebergRestCatalogFederationService(
            Substitute.For<IIcebergMetadataReader>(), metadataRepo,
            Options.Create(new GatewayOptions { Tenancy = new TenancyOptions { DefaultTenantId = "default-tenant" } }),
            NullLogger<IcebergRestCatalogFederationService>.Instance,
            global::Autheris.Tests.Unit.TestDoubles.NullAuditLogRepository.Instance, recording, repo);
    }

    private static Task LoadAsync(IcebergRestCatalogFederationService service, ClaimsPrincipal principal) =>
        service.LoadTableAsync("tenant-1", "raw", "orders", principal).AsTask();

    [Fact]
    public async Task IcebergTable_RestrictedToken_SensitiveMasked()
    {
        // Raw metadata vending releases a table only when every column is in clear; a sensitive column never is (also not for the
        // restricted token with a base Clear), whoever asks.
        var seen = new List<IReadOnlySet<string>?>();
        var sensitive = IcebergWith(seen, sensitive: true, Rule("ssn", ColumnAccessLevel.Clear), Rule("ssn", ColumnAccessLevel.Clear, Restricted));
        await Should.ThrowAsync<SecurityException>(() => LoadAsync(sensitive, Analyst(Restricted)));
        await Should.ThrowAsync<SecurityException>(() => LoadAsync(sensitive, Analyst("other.scope")));

        // A plain table is still vended to the restricted token: the restriction only touches sensitive columns.
        var plain = IcebergWith(seen, sensitive: false);
        await Should.NotThrowAsync(() => LoadAsync(plain, Analyst(Restricted)));
        await Should.NotThrowAsync(() => LoadAsync(plain, Analyst()));
    }

    [Fact]
    public async Task T_GOV143_02_Iceberg_RestrictedToken_NoRawVending_ForSensitiveColumns()
    {
        var seen = new List<IReadOnlySet<string>?>();
        var service = IcebergWith(seen, sensitive: true);

        await Should.ThrowAsync<SecurityException>(() => LoadAsync(service, Analyst(Restricted)));
        await Should.ThrowAsync<SecurityException>(() => service.VendCredentialAsync("tenant-1", "raw", "orders", Analyst(Restricted)).AsTask());
        // A prefixed foreign-resource scope is restricted too (restrictive direction).
        await Should.ThrowAsync<SecurityException>(() => LoadAsync(service, Analyst("api://app/Agent.Read")));

        // The resolver always received the token scopes (SEC-GOV143-02): the restricted scope is held, nothing is passed for a scopeless token.
        seen.ShouldAllBe(s => s != null);
        seen.ShouldAllBe(s => s!.Count == 1);
    }

    [Fact]
    public async Task T_GOV143_15_Iceberg_ScopesFromClaimsOverload_PlainScopeClaimIncluded_EmptyIsNull()
    {
        // SEC-GOV143-15: the claims overload also reads a plain 'scope' claim (GetScopes would not); an empty set maps to null.
        var seen = new List<IReadOnlySet<string>?>();
        var service = IcebergWith(seen, sensitive: false);
        var plainScope = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "analyst@corp.com"), new Claim("scope", Restricted)], "Bearer"));

        await LoadAsync(service, plainScope);
        await LoadAsync(service, Analyst());

        seen.Count.ShouldBe(2);
        seen[0].ShouldNotBeNull().ShouldContain(Restricted);
        seen[1].ShouldBeNull();
    }

    // ---------------------------------------------------------------- stream (T-GOV143-03, -19)

    private sealed class StreamFixture
    {
        public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 18, 8, 0, 0, TimeSpan.Zero));

        public List<string?> ReadHashes { get; } = [];

        public List<string?> WrittenHashes { get; } = [];

        public List<TableAccessDecision> Written { get; } = [];

        public StreamRlsPolicyEnforcer Enforcer(IReadOnlyList<Consent> consents)
        {
            var metadataRepo = Substitute.For<ITableMetadataRepository>();
            metadataRepo.GetTableMetadataAsync(Orders, Arg.Any<CancellationToken>()).Returns(Task.FromResult<TableMetadata?>(Meta));
            var consentRepo = Substitute.For<IConsentRepository>();
            consentRepo.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<Consent>>(consents));
            var masking = Substitute.For<IColumnMaskingProvider>();
            masking.MaskValue(default!, default, default!).ReturnsForAnyArgs("MASKED");
            var policy = Substitute.For<IPolicyEnforcementService>();
            policy.HasPolicies(Arg.Any<TenantId>()).Returns(false);

            // A key/value cache keyed by the context hash, like the real one: a hit of a foreign key would show up as a wrong level.
            var store = new Dictionary<string, TableAccessDecision>();
            var cache = Substitute.For<IConsentCacheService>();
            cache.GetCachedDecisionAsync(Arg.Any<TenantId>(), Arg.Any<Sid>(), Arg.Any<TableIdentifier>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
                    var hash = ci.ArgAt<string?>(3);
                    ReadHashes.Add(hash);
                    return Task.FromResult(hash != null && store.TryGetValue(hash, out var hit) ? hit : null);
                });
            cache.SetCachedDecisionAsync(Arg.Any<TenantId>(), Arg.Any<Sid>(), Arg.Any<TableIdentifier>(), Arg.Any<TableAccessDecision>(), Arg.Any<TimeSpan>(), Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
                    var hash = ci.ArgAt<string?>(5);
                    WrittenHashes.Add(hash);
                    Written.Add(ci.ArgAt<TableAccessDecision>(3));
                    if (hash != null)
                    {
                        store[hash] = ci.ArgAt<TableAccessDecision>(3);
                    }

                    return Task.CompletedTask;
                });

            return new StreamRlsPolicyEnforcer(
                policy, metadataRepo, masking, Substitute.For<IEpochValidationService>(), NullLogger<StreamRlsPolicyEnforcer>.Instance,
                consentRepo, new ConsentResolutionService(), cache, time: Time);
        }

        public ClaimsPrincipal Subscriber(string? scp, DateTimeOffset? expires = null)
        {
            var claims = new List<Claim> { new(ClaimTypes.PrimarySid, UserSid), new("tenant_id", Tenant) };
            if (scp != null)
            {
                claims.Add(new Claim("scp", scp));
            }

            if (expires != null)
            {
                claims.Add(new Claim("exp", expires.Value.ToUnixTimeSeconds().ToString()));
            }

            return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
        }
    }

    private static CdcEvent Event() => new(
        "evt", Orders, CdcOperation.Insert, Tenant, null,
        new Dictionary<string, object?> { ["id"] = 1, ["grade"] = SentinelOne }, DateTimeOffset.UtcNow);

    private static IReadOnlyList<Consent> BaseClear() => [Allow(IdClear, GradeClear)];

    [Fact]
    public async Task StreamEvent_RestrictedSubscriber_SensitiveMasked()
    {
        var f = new StreamFixture();
        var enforcer = f.Enforcer(BaseClear());

        var restricted = await enforcer.EvaluateAndMaskAsync(Event(), f.Subscriber(Restricted));
        restricted.IsAllowed.ShouldBeTrue();
        restricted.MaskedPayload!["grade"].ShouldBe("MASKED");
        restricted.MaskedPayload["id"].ShouldBe(1);

        var scopeless = await enforcer.EvaluateAndMaskAsync(Event(), f.Subscriber(null));
        scopeless.MaskedPayload!["grade"].ShouldBe(SentinelOne);

        var scopeClear = await f.Enforcer([Allow(IdClear, GradeClear, Rule("grade", ColumnAccessLevel.Clear, Restricted))])
            .EvaluateAndMaskAsync(Event(), f.Subscriber(Restricted));
        scopeClear.MaskedPayload!["grade"].ShouldBe(SentinelOne);
    }

    [Fact]
    public async Task StreamDecisionCache_RestrictedAndUnrestrictedSubscribers_NeverShare()
    {
        var f = new StreamFixture();
        var enforcer = f.Enforcer(BaseClear());

        // Whichever subscriber fills the cache first, the other never reads its entry.
        (await enforcer.EvaluateAndMaskAsync(Event(), f.Subscriber(null))).MaskedPayload!["grade"].ShouldBe(SentinelOne);
        (await enforcer.EvaluateAndMaskAsync(Event(), f.Subscriber(Restricted))).MaskedPayload!["grade"].ShouldBe("MASKED");
        (await enforcer.EvaluateAndMaskAsync(Event(), f.Subscriber(null))).MaskedPayload!["grade"].ShouldBe(SentinelOne);
        (await enforcer.EvaluateAndMaskAsync(Event(), f.Subscriber(Restricted))).MaskedPayload!["grade"].ShouldBe("MASKED");

        f.WrittenHashes.Distinct().Count().ShouldBe(2);
        f.WrittenHashes.ShouldAllBe(h => h != null);
    }

    [Fact]
    public async Task T_GOV143_03_StreamEnforcer_RestrictedSubscriber_MasksSensitive_CacheKeyHasScopes()
    {
        var f = new StreamFixture();
        var enforcer = f.Enforcer(BaseClear());
        var empty = new HashSet<Sid>();
        var noRoles = new HashSet<string>();

        var restricted = await enforcer.EvaluateAndMaskAsync(Event(), f.Subscriber(Restricted));

        restricted.MaskedPayload!["grade"].ShouldBe("MASKED");
        var expectedHash = IConsentCacheService.ComputeSubjectContextHash(empty, noRoles, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Restricted });
        f.ReadHashes.ShouldContain(expectedHash);
        f.WrittenHashes.ShouldContain(expectedHash);
        f.WrittenHashes.ShouldNotContain(IConsentCacheService.ComputeSubjectContextHash(empty, noRoles));
        f.Written.Single().ScopeClearance.ShouldNotBeNull();
    }

    [Fact]
    public async Task T_GOV143_19_StreamSubscription_ScopeSetNotUsedAfterTokenExpiry()
    {
        var f = new StreamFixture();
        var enforcer = f.Enforcer(BaseClear());
        var subscriber = f.Subscriber(Restricted, f.Time.GetUtcNow().AddMinutes(5));
        var clearSubscriber = f.Subscriber(Restricted, f.Time.GetUtcNow().AddMinutes(5));

        (await enforcer.EvaluateAndMaskAsync(Event(), subscriber)).IsAllowed.ShouldBeTrue();

        // The subscription outlives the token: the captured scope set is no longer used, so nothing is delivered.
        f.Time.Advance(TimeSpan.FromMinutes(6));
        var expired = await enforcer.EvaluateAndMaskAsync(Event(), subscriber);
        expired.IsAllowed.ShouldBeFalse();
        expired.MaskedPayload.ShouldBeNull();
        (await enforcer.EvaluateAndMaskAsync(Event(), clearSubscriber)).IsAllowed.ShouldBeFalse();

        // A subscriber without scopes keeps today's behaviour (the expiry is not part of this item).
        (await enforcer.EvaluateAndMaskAsync(Event(), f.Subscriber(null, f.Time.GetUtcNow().AddMinutes(-1)))).IsAllowed.ShouldBeTrue();
    }

    // ---------------------------------------------------------------- Unknown clearance (T-GOV143-18, -20; SEC-13/14)

    private static TableAccessPolicy Policy(
        IReadOnlyList<Consent> consents, InMemoryAccessProfileRepository? profiles = null, IConsentCacheService? cache = null,
        IPolicyEnforcementService? abac = null, IRestrictedScopePolicy? restricted = null)
    {
        var consentRepo = Substitute.For<IConsentRepository>();
        consentRepo.GetActiveConsentsForSubjectsAsync(default!, default, default, default, default)
            .ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<Consent>>(consents));
        return new TableAccessPolicy(
            consentRepo,
            new ConsentResolutionService(null, restricted),
            cache ?? Substitute.For<IConsentCacheService>(),
            abac ?? Substitute.For<IPolicyEnforcementService>(),
            Substitute.For<IRebacEvaluator>(),
            Substitute.For<IClientIpResolver>(),
            new GatewayOptions(),
            NullMandatoryRowFilterResolver.Instance,
            contractManager: null,
            accessProfileRepository: profiles,
            restrictedScopes: restricted);
    }

    private static TableAccessQuery Query(IEnumerable<Claim>? claims) =>
        new(new Sid(UserSid), TenantA, new HashSet<Sid>(), new HashSet<string>(), Meta, claims);

    private static async Task<ColumnAccessLevel> GradeAsync(TableAccessPolicy policy, IEnumerable<Claim>? claims)
    {
        var decision = await policy.DecideAsync(Query(claims), CancellationToken.None);
        decision.IsAllowed.ShouldBeTrue();
        return decision.GetEffectiveColumnAccess("grade", Meta);
    }

    [Fact]
    public async Task T_GOV143_18_Unknown_OnlyWhenClaimsNull_ClaimsWithoutScp_StayUnrestricted()
    {
        var policy = Policy(BaseClear());

        // Absent claims: unknown scopes are treated as restricted.
        (await GradeAsync(policy, null)).ShouldBe(ColumnAccessLevel.Mask);
        // Claims without scp (scopeless or app-only token): unrestricted by design.
        (await GradeAsync(policy, new List<Claim> { new(ClaimTypes.Role, "app") })).ShouldBe(ColumnAccessLevel.Clear);
        (await GradeAsync(policy, Array.Empty<Claim>())).ShouldBe(ColumnAccessLevel.Clear);
        // Claims with a restricted scp: restricted through the resolver.
        (await GradeAsync(policy, WithScopes(Restricted).Claims)).ShouldBe(ColumnAccessLevel.Mask);
        (await GradeAsync(policy, WithScopes(ScopeA).Claims)).ShouldBe(ColumnAccessLevel.Clear);
    }

    [Fact]
    public async Task ResolveWithoutClaims_PolicyEnabled_TreatedAsRestricted()
    {
        (await GradeAsync(Policy(BaseClear()), null)).ShouldBe(ColumnAccessLevel.Mask);
        // The disabled policy restricts nothing, with or without claims.
        (await GradeAsync(Policy(BaseClear(), restricted: RestrictedScopePolicy.Disabled), null)).ShouldBe(ColumnAccessLevel.Clear);
    }

    [Fact]
    public async Task T_GOV143_20_UnknownClearance_SurvivesCacheHitAndRebuilds_NeverStoredUnderScopeKey()
    {
        // The cached entry of the scopeless key is an unrestricted decision (written by a scopeless caller).
        var cached = new ConsentResolutionService().ResolveAccess(
            new Sid(UserSid), new HashSet<Sid>(), new HashSet<string>(), Orders, BaseClear());
        cached.ScopeClearance.ShouldBeNull();
        var cache = Substitute.For<IConsentCacheService>();
        cache.GetCachedDecisionAsync(Arg.Any<TenantId>(), Arg.Any<Sid>(), Arg.Any<TableIdentifier>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TableAccessDecision?>(cached));

        var hit = await Policy(BaseClear(), cache: cache).DecideAsync(Query(null), CancellationToken.None);
        hit.ScopeClearance.ShouldNotBeNull();
        hit.GetEffectiveColumnAccess("grade", Meta).ShouldBe(ColumnAccessLevel.Mask);

        // A miss: what is stored is the plain decision of the key (no clearance), the caller still gets the restricted copy.
        var writes = new List<TableAccessDecision>();
        var missCache = Substitute.For<IConsentCacheService>();
        missCache.SetCachedDecisionAsync(Arg.Any<TenantId>(), Arg.Any<Sid>(), Arg.Any<TableIdentifier>(), Arg.Any<TableAccessDecision>(), Arg.Any<TimeSpan>(), Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                writes.Add(ci.ArgAt<TableAccessDecision>(3));
                return Task.CompletedTask;
            });
        var miss = await Policy(BaseClear(), cache: missCache).DecideAsync(Query(null), CancellationToken.None);
        miss.ScopeClearance.ShouldNotBeNull();
        writes.ShouldHaveSingleItem().ScopeClearance.ShouldBeNull();
    }

    // ---------------------------------------------------------------- AC-9 / T-GOV143-17 at the TableAccessPolicy level

    private static async Task<InMemoryAccessProfileRepository> UnmaskedProfileAsync()
    {
        var repo = new InMemoryAccessProfileRepository();
        await repo.UpsertProfileAsync(new AccessProfile
        {
            ProfileId = "prof-143",
            Name = "Unmasked 143",
            TenantId = TenantA,
            MaskingMode = MaskingPolicyMode.Unmasked,
            TargetTables = ["public.*"],
            Assignments = TestAssign.Users(UserSid),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1)
        });
        return repo;
    }

    [Fact]
    public async Task UnmaskedProfile_RestrictedTokenStillMasked()
    {
        var profiles = await UnmaskedProfileAsync();
        // No column rule: grade is masked by catalog sensitivity only; the Unmasked profile writes an explicit Clear for it.
        var policy = Policy([Allow()], profiles);

        (await GradeAsync(policy, WithScopes(ScopeA).Claims)).ShouldBe(ColumnAccessLevel.Clear);
        (await GradeAsync(policy, WithScopes(Restricted).Claims)).ShouldBe(ColumnAccessLevel.Mask);
    }

    [Fact]
    public async Task T_GOV143_17_ProfileUnmasked_And_AbacMerge_RestrictedStillMasked()
    {
        var abac = Substitute.For<IPolicyEnforcementService>();
        abac.HasPolicies(Arg.Any<TenantId>()).Returns(true);
        abac.EvaluatePolicyAsync(Arg.Any<SecurityEvaluationContext>(), Arg.Any<CancellationToken>())
            .Returns(ci => TableAccessDecision.Allowed(Orders, new Dictionary<string, ColumnAccessLevel>(), rowFilterSql: "id > 0", hasUnconstrainedColumnAllow: true));
        var profiles = await UnmaskedProfileAsync();

        var withAbac = Policy([Allow()], profiles, abac: abac);
        var restricted = await withAbac.DecideAsync(Query(WithScopes(Restricted).Claims), CancellationToken.None);
        restricted.IsAllowed.ShouldBeTrue();
        restricted.CombinedRowFilterSql.ShouldNotBeNull().ShouldContain("id > 0");
        restricted.GetEffectiveColumnAccess("grade", Meta).ShouldBe(ColumnAccessLevel.Mask);

        var other = await withAbac.DecideAsync(Query(WithScopes(ScopeA).Claims), CancellationToken.None);
        other.GetEffectiveColumnAccess("grade", Meta).ShouldBe(ColumnAccessLevel.Clear);
    }
}
