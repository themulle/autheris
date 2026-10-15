namespace Autheris.Tests.Unit.Streaming;

using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Events.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class CdcWebhookSubscriptionGuardTests
{
    private static DefaultHttpContext Ctx(bool enabled, string[] roles, ICloudEventSubscriptionStore? store = null)
    {
        var claims = roles.Select(r => new Claim(ClaimTypes.Role, r)).ToList();
        claims.Add(new Claim("tenant_id", "tenant-a"));
        var services = new ServiceCollection();
        services.AddSingleton<IOptions<CdcWebhooksOptions>>(Options.Create(new CdcWebhooksOptions { Enabled = enabled }));
        if (store != null)
        {
            services.AddSingleton(store);
        }

        return new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test", ClaimTypes.Name, ClaimTypes.Role)),
            RequestServices = services.BuildServiceProvider()
        };
    }

    private static CloudEventWebhookSubscription Sub() =>
        new("x", "t", "https://example.com/hook", "dbo.t", [], "secret");

    private static int? Status(IResult result) => result.ShouldBeAssignableTo<IStatusCodeHttpResult>()!.StatusCode;

    [Fact]
    public async Task Post_Subscription_WhenCdcWebhooksDisabled_Returns501_AndStoresNothing()
    {
        var store = Substitute.For<ICloudEventSubscriptionStore>();
        var ctx = Ctx(false, ["GovernanceAdmin"], store);

        var result = await StreamingCdcEndpoints.CreateSubscriptionAsync(ctx, Sub());

        Status(result).ShouldBe(StatusCodes.Status501NotImplemented);
        await store.DidNotReceiveWithAnyArgs().RegisterSubscriptionAsync(default!, default(int), default);
    }

    [Fact]
    public async Task Get_WhenDisabled_Returns501_WithoutStoreRegistered()
    {
        var ctx = Ctx(false, ["GovernanceAdmin"]);

        var result = await StreamingCdcEndpoints.ListSubscriptionsAsync(ctx);

        Status(result).ShouldBe(StatusCodes.Status501NotImplemented);
    }

    [Fact]
    public async Task Delete_WhenDisabled_Returns501()
    {
        var ctx = Ctx(false, ["GovernanceAdmin"]);

        var result = await StreamingCdcEndpoints.DeleteSubscriptionAsync("id-1", ctx);

        Status(result).ShouldBe(StatusCodes.Status501NotImplemented);
    }

    [Fact]
    public async Task Post_WhenOptionsMissing_FailsClosedWith501()
    {
        var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };

        var result = await StreamingCdcEndpoints.ListSubscriptionsAsync(ctx);

        Status(result).ShouldBe(StatusCodes.Status501NotImplemented);
    }

    [Fact]
    public async Task Post_WhenEnabled_StillRequiresGovernanceOrTenantAdmin()
    {
        var store = Substitute.For<ICloudEventSubscriptionStore>();
        var ctx = Ctx(true, ["Reader"], store);

        var result = await StreamingCdcEndpoints.CreateSubscriptionAsync(ctx, Sub());

        Status(result).ShouldBe(StatusCodes.Status403Forbidden);
        await store.DidNotReceiveWithAnyArgs().RegisterSubscriptionAsync(default!, default(int), default);
    }

    [Fact]
    public async Task Delete_WhenEnabled_AsAdmin_RemovesSubscription()
    {
        var store = Substitute.For<ICloudEventSubscriptionStore>();
        store.RemoveSubscriptionAsync(Arg.Any<string>(), "id-1", Arg.Any<CancellationToken>()).Returns(true);
        var ctx = Ctx(true, ["TenantAdmin"], store);

        var result = await StreamingCdcEndpoints.DeleteSubscriptionAsync("id-1", ctx);

        Status(result).ShouldBe(StatusCodes.Status204NoContent);
    }
}
