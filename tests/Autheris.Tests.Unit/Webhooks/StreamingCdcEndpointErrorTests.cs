using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Events.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Webhooks;

[Trait("Category", "Security")]
public sealed class StreamingCdcEndpointErrorTests
{
    private static DefaultHttpContext CreateAdminContext(ICloudEventSubscriptionStore store)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOptions<CdcWebhooksOptions>>(Options.Create(new CdcWebhooksOptions { Enabled = true }));
        services.AddSingleton(store);
        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        context.Items[SecurityPrincipalContext.ItemKey] = new SecurityPrincipalContext
        {
            UserSid = new Sid("S-1-5-21-ADMIN"),
            TenantId = new TenantId("tenant-a"),
            GroupSids = new HashSet<Sid>(),
            TenantRoles = new HashSet<string>(),
            ClusterRoles = new HashSet<string> { "GovernanceAdmin" },
            AuthenticationScheme = "Test"
        };
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "S-1-5-21-ADMIN"),
            new Claim(ClaimTypes.Name, "admin"),
            new Claim("tenant", "tenant-a"),
            new Claim(ClaimTypes.Role, "GovernanceAdmin")
        ], "Test"));
        return context;
    }

    private static CloudEventWebhookSubscription Subscription(string url) =>
        new("client-id", "tenant-a", url, "public.orders", [], "secret");

    [Fact]
    public async Task CdcSubscription_PrivateAddressTarget_ResponseDoesNotContainResolverDetails()
    {
        var store = Substitute.For<ICloudEventSubscriptionStore>();

        var privateResult = await StreamingCdcEndpoints.CreateSubscriptionAsync(
            CreateAdminContext(store), Subscription("http://127.0.0.1/hook"));
        var unresolvableResult = await StreamingCdcEndpoints.CreateSubscriptionAsync(
            CreateAdminContext(store), Subscription("http://does-not-resolve.invalid/hook"));

        var privateProblem = privateResult.ShouldBeOfType<ProblemHttpResult>();
        var unresolvableProblem = unresolvableResult.ShouldBeOfType<ProblemHttpResult>();
        privateProblem.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        privateProblem.ProblemDetails.Detail.ShouldNotContain("SSRF");
        privateProblem.ProblemDetails.Detail.ShouldNotContain("127.0.0.1");
        privateProblem.ProblemDetails.Extensions["code"].ShouldBe("CDC_TARGET_URL_NOT_ALLOWED");
        // Identical body for both failure classes: the endpoint must not act as a resolver/network oracle.
        unresolvableProblem.ProblemDetails.Detail.ShouldBe(privateProblem.ProblemDetails.Detail);
        unresolvableProblem.ProblemDetails.Extensions["code"].ShouldBe(privateProblem.ProblemDetails.Extensions["code"]);
        await store.DidNotReceiveWithAnyArgs().RegisterSubscriptionAsync(default!, default);
    }

    [Fact]
    public async Task CdcSubscription_QuotaExceeded_ReturnsFixedTooManyRequests()
    {
        const string internalDetail = "INTERNAL subscription limit of 7 for tenant tenant-a";
        var store = Substitute.For<ICloudEventSubscriptionStore>();
        store.RegisterSubscriptionAsync(Arg.Any<CloudEventWebhookSubscription>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask>(_ => throw new InvalidOperationException(internalDetail));

        // A public literal address passes the egress policy without DNS resolution.
        var result = await StreamingCdcEndpoints.CreateSubscriptionAsync(
            CreateAdminContext(store), Subscription("https://93.184.216.34/hook"));

        var problem = result.ShouldBeOfType<ProblemHttpResult>();
        problem.StatusCode.ShouldBe(StatusCodes.Status429TooManyRequests);
        problem.ProblemDetails.Detail.ShouldNotContain(internalDetail);
        problem.ProblemDetails.Extensions["code"].ShouldBe("CDC_SUBSCRIPTION_QUOTA_EXCEEDED");
    }
}
