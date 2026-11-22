namespace Autheris.Tests.Unit.Catalog.Discovery;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Catalog.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Autheris.Infrastructure.RateLimiting;
using Autheris.Tests.Common;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

public sealed class DiscoveryCallerGateTests
{
    [Fact]
    [Trait("Category", "Security")]
    public async Task EnsureAllowed_RejectsAnonymousAndAnonymousSids()
    {
        var dOpts = new CatalogDiscoveryOptions();
        var gOpts = new GatewayOptions { Tenancy = TenancyShape.Multi.Options() };

        var unauth = await Should.ThrowAsync<DiscoveryStatusException>(async () =>
            await DiscoveryCallerGate.EnsureAllowedAsync(
                DiscoveryTestBuilder.Caller(DiscoveryTestBuilder.Anonymous()), dOpts, gOpts, rateLimiter: null));
        unauth.Kind.ShouldBe(DiscoveryFailureKind.Unauthenticated);

        var anonMcp = await Should.ThrowAsync<DiscoveryStatusException>(async () =>
            await DiscoveryCallerGate.EnsureAllowedAsync(
                DiscoveryTestBuilder.Caller(DiscoveryTestBuilder.User(DiscoveryCallerGate.AnonymousMcpClientSid, "tenant-a")), dOpts, gOpts, rateLimiter: null));
        anonMcp.Kind.ShouldBe(DiscoveryFailureKind.Unauthenticated);

        var anonSid = await Should.ThrowAsync<DiscoveryStatusException>(async () =>
            await DiscoveryCallerGate.EnsureAllowedAsync(
                DiscoveryTestBuilder.Caller(DiscoveryTestBuilder.User(DiscoveryCallerGate.AnonymousSidValue, "tenant-a")), dOpts, gOpts, rateLimiter: null));
        anonSid.Kind.ShouldBe(DiscoveryFailureKind.Unauthenticated);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task EnsureAllowed_EnforcesRequiredRolesAndScopes_WithTenantOverrides()
    {
        var dOpts = new CatalogDiscoveryOptions
        {
            Discovery = new CatalogDiscoverySettings
            {
                RequiredRoles = ["DataReader"],
                RequiredScopes = ["Catalog.Read"],
                TenantOverrides = new Dictionary<string, TenantDiscoverySettings>(StringComparer.OrdinalIgnoreCase)
                {
                    ["tenant-b"] = new() { RequiredRoles = ["TenantBAnalyst"], RequiredScopes = ["Custom.Scope"] }
                }
            }
        };
        var gOpts = new GatewayOptions { Tenancy = TenancyShape.Multi.Options() };

        var missingRole = DiscoveryTestBuilder.User("S-1", "tenant-a", roles: [], scopes: "Catalog.Read");
        var ex = await Should.ThrowAsync<DiscoveryStatusException>(async () =>
            await DiscoveryCallerGate.EnsureAllowedAsync(DiscoveryTestBuilder.Caller(missingRole), dOpts, gOpts, rateLimiter: null));
        ex.Kind.ShouldBe(DiscoveryFailureKind.Forbidden);
        ex.Code.ShouldBe("DISCOVERY_NOT_PERMITTED");

        var validTenantA = DiscoveryTestBuilder.User("S-1", "tenant-a", roles: ["DataReader"], scopes: "Catalog.Read");
        (await DiscoveryCallerGate.EnsureAllowedAsync(DiscoveryTestBuilder.Caller(validTenantA), dOpts, gOpts, rateLimiter: null)).Value.ShouldBe("S-1");

        var overrideDenied = DiscoveryTestBuilder.User("S-2", "tenant-b", roles: ["DataReader"], scopes: "Catalog.Read");
        await Should.ThrowAsync<DiscoveryStatusException>(async () =>
            await DiscoveryCallerGate.EnsureAllowedAsync(
                DiscoveryTestBuilder.Caller(overrideDenied, new TenantId("tenant-b")), dOpts, gOpts, rateLimiter: null));

        var overrideAllowed = DiscoveryTestBuilder.User("S-2", "tenant-b", roles: ["TenantBAnalyst"], scopes: "Custom.Scope");
        (await DiscoveryCallerGate.EnsureAllowedAsync(
            DiscoveryTestBuilder.Caller(overrideAllowed, new TenantId("tenant-b")), dOpts, gOpts, rateLimiter: null)).Value.ShouldBe("S-2");
    }

    [Fact]
    public async Task EnsureAllowed_EnforcesPerTenantSidRateLimit()
    {
        var limiter = new FakeDiscoveryRateLimiter();
        var dOpts = new CatalogDiscoveryOptions
        {
            Discovery = new CatalogDiscoverySettings { RequestsPerMinute = 2 }
        };
        var gOpts = new GatewayOptions { Tenancy = TenancyShape.Multi.Options() };
        var caller = DiscoveryTestBuilder.Caller(DiscoveryTestBuilder.User("S-RL", "tenant-a"));

        await DiscoveryCallerGate.EnsureAllowedAsync(caller, dOpts, gOpts, limiter);
        await DiscoveryCallerGate.EnsureAllowedAsync(caller, dOpts, gOpts, limiter);

        var ex = await Should.ThrowAsync<DiscoveryStatusException>(async () =>
            await DiscoveryCallerGate.EnsureAllowedAsync(caller, dOpts, gOpts, limiter));
        ex.Kind.ShouldBe(DiscoveryFailureKind.RateLimited);
        ex.RetryAfterSeconds.ShouldNotBeNull();
    }

    [Fact]
    public void OptionsValidator_RejectsOversizedSampleRowsOrArtifactBytes()
    {
        var validator = new CatalogDiscoveryOptionsValidator();

        validator.Validate(null, new CatalogDiscoveryOptions
        {
            Discovery = new CatalogDiscoverySettings { MaxSampleRows = CatalogDiscoverySettings.HardMaxSampleRows + 1 }
        }).Failed.ShouldBeTrue();

        validator.Validate(null, new CatalogDiscoveryOptions
        {
            Discovery = new CatalogDiscoverySettings { MaxArtifactBytes = CatalogDiscoverySettings.HardMaxArtifactBytes + 1 }
        }).Failed.ShouldBeTrue();

        validator.Validate(null, new CatalogDiscoveryOptions()).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public async Task EnsureAllowed_Sub120Rpm_RefillsAtConfiguredRate_For30And90Rpm()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
        var limiter = new InMemoryRateLimiterService(timeProvider: clock);
        var gOpts = new GatewayOptions { Tenancy = TenancyShape.Multi.Options() };

        // 30 RPM => 0.5 tokens/sec (1 token every 2 seconds).
        var opts30 = new CatalogDiscoveryOptions { Discovery = new CatalogDiscoverySettings { RequestsPerMinute = 30 } };
        var caller30 = DiscoveryTestBuilder.Caller(DiscoveryTestBuilder.User("S-RL-30", "tenant-a"));
        for (int i = 0; i < 30; i++)
        {
            await DiscoveryCallerGate.EnsureAllowedAsync(caller30, opts30, gOpts, limiter);
        }

        await Should.ThrowAsync<DiscoveryStatusException>(async () =>
            await DiscoveryCallerGate.EnsureAllowedAsync(caller30, opts30, gOpts, limiter));

        // After 1 second only 0.5 tokens accrued -> still rate limited.
        clock.Advance(TimeSpan.FromSeconds(1));
        await Should.ThrowAsync<DiscoveryStatusException>(async () =>
            await DiscoveryCallerGate.EnsureAllowedAsync(caller30, opts30, gOpts, limiter));

        // After 1 more second (2s total) -> 1.0 token accrued -> 1 allowed, next rejected.
        clock.Advance(TimeSpan.FromSeconds(1));
        await DiscoveryCallerGate.EnsureAllowedAsync(caller30, opts30, gOpts, limiter);
        await Should.ThrowAsync<DiscoveryStatusException>(async () =>
            await DiscoveryCallerGate.EnsureAllowedAsync(caller30, opts30, gOpts, limiter));

        // 90 RPM => 1.5 tokens/sec (30 tokens over 20 seconds, not 20).
        var opts90 = new CatalogDiscoveryOptions { Discovery = new CatalogDiscoverySettings { RequestsPerMinute = 90 } };
        var caller90 = DiscoveryTestBuilder.Caller(DiscoveryTestBuilder.User("S-RL-90", "tenant-a"));
        for (int i = 0; i < 90; i++)
        {
            await DiscoveryCallerGate.EnsureAllowedAsync(caller90, opts90, gOpts, limiter);
        }

        await Should.ThrowAsync<DiscoveryStatusException>(async () =>
            await DiscoveryCallerGate.EnsureAllowedAsync(caller90, opts90, gOpts, limiter));

        clock.Advance(TimeSpan.FromSeconds(20));
        for (int i = 0; i < 30; i++)
        {
            await DiscoveryCallerGate.EnsureAllowedAsync(caller90, opts90, gOpts, limiter);
        }

        await Should.ThrowAsync<DiscoveryStatusException>(async () =>
            await DiscoveryCallerGate.EnsureAllowedAsync(caller90, opts90, gOpts, limiter));
    }
}
