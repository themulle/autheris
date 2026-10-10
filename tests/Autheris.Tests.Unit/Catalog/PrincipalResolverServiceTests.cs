namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

public sealed class PrincipalResolverServiceTests
{
    private static PrincipalResolverService CreateService(List<BasicAuthUserConfig> users)
    {
        var gatewayOptions = new GatewayOptions();
        gatewayOptions.Authentication.BasicAuth.Users.AddRange(users);
        var options = Options.Create(gatewayOptions);

        return new PrincipalResolverService(
            options,
            NullLogger<PrincipalResolverService>.Instance);
    }

    [Fact]
    public async Task ResolvePrincipalAsync_ExactMatch_ReturnsSingleExactMatch()
    {
        // Arrange
        var tenant = new TenantId("tenant-a");
        var users = new List<BasicAuthUserConfig>
        {
            new()
            {
                Username = "david",
                Sid = "S-1-5-21-david",
                TenantId = "tenant-a",
                GroupSids = ["S-1-5-32-operators"]
            },
            new()
            {
                Username = "sarah",
                Sid = "S-1-5-21-sarah",
                TenantId = "tenant-a",
                GroupSids = ["S-1-5-32-analysts"]
            }
        };

        var service = CreateService(users);
        var context = new RequestContext(tenant, new Sid("user:admin"));

        // Act
        var results = await service.ResolvePrincipalAsync("david", context);

        // Assert
        results.ShouldNotBeNull();
        results.Count.ShouldBe(1);
        var item = results[0];
        item.Sid.ShouldBe("S-1-5-21-david");
        item.DisplayName.ShouldBe("david");
        item.ExactMatch.ShouldBeTrue();
        item.Groups.ShouldContain("S-1-5-32-operators");
    }

    [Fact]
    public async Task ResolvePrincipalAsync_AmbiguousMatches_ReturnsAllSuggestionsWithExactMatchFalse()
    {
        // Arrange
        var tenant = new TenantId("tenant-a");
        var users = new List<BasicAuthUserConfig>
        {
            new()
            {
                Username = "philipp_dev",
                Sid = "S-1-5-21-philipp-dev",
                TenantId = "tenant-a",
                GroupSids = ["S-1-5-32-developers"]
            },
            new()
            {
                Username = "philipp_ops",
                Sid = "S-1-5-21-philipp-ops",
                TenantId = "tenant-a",
                GroupSids = ["S-1-5-32-operators"]
            }
        };

        var service = CreateService(users);
        var context = new RequestContext(tenant, new Sid("user:admin"));

        // Act
        var results = await service.ResolvePrincipalAsync("philipp", context);

        // Assert
        results.ShouldNotBeNull();
        results.Count.ShouldBe(2);
        results.All(r => !r.ExactMatch).ShouldBeTrue();
        results.Select(r => r.DisplayName).ShouldContain("philipp_dev");
        results.Select(r => r.DisplayName).ShouldContain("philipp_ops");
    }

    [Fact]
    public async Task ResolvePrincipalAsync_FuzzyMatchTypo_ReturnsSuggestionsWithExactMatchFalse()
    {
        // Arrange
        var tenant = new TenantId("tenant-a");
        var users = new List<BasicAuthUserConfig>
        {
            new()
            {
                Username = "philipp",
                Sid = "S-1-5-21-philipp",
                TenantId = "tenant-a",
                GroupSids = ["S-1-5-32-operators"]
            }
        };

        var service = CreateService(users);
        var context = new RequestContext(tenant, new Sid("user:admin"));

        // Act - query has typo "phillip" (two l's)
        var results = await service.ResolvePrincipalAsync("phillip", context);

        // Assert
        results.ShouldNotBeNull();
        results.Count.ShouldBe(1);
        results[0].DisplayName.ShouldBe("philipp");
        results[0].ExactMatch.ShouldBeFalse();
    }

    [Fact]
    public async Task ResolvePrincipalAsync_CrossTenantIsolation_OnlyReturnsPrincipalsInCallerTenant()
    {
        // Arrange
        var tenantA = new TenantId("tenant-a");
        var users = new List<BasicAuthUserConfig>
        {
            new()
            {
                Username = "david",
                Sid = "S-1-5-21-david-a",
                TenantId = "tenant-a"
            },
            new()
            {
                Username = "david",
                Sid = "S-1-5-21-david-b",
                TenantId = "tenant-b"
            }
        };

        var service = CreateService(users);
        var context = new RequestContext(tenantA, new Sid("user:admin"));

        // Act
        var results = await service.ResolvePrincipalAsync("david", context);

        // Assert
        results.ShouldNotBeNull();
        results.Count.ShouldBe(1);
        results[0].Sid.ShouldBe("S-1-5-21-david-a");
    }
}
