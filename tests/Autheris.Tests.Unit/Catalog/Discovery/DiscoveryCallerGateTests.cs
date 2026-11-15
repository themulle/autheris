namespace Autheris.Tests.Unit.Catalog.Discovery;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Catalog.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Autheris.Tests.Common;
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
}
