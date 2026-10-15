namespace Autheris.Tests.Unit.Streaming;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Events.Interfaces;
using Autheris.Application.Events.Services;
using Autheris.Application.Webhooks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class CdcSubscriptionEndpointTests
{
    private const string GoodUrl = "https://93.184.216.34/hook";
    private static readonly string GoodSecret = new('s', 40);

    private sealed record Harness(DefaultHttpContext Ctx, ICloudEventSubscriptionStore Store, IWebhookSecretProtector Protector);

    private static IWebhookSecretProtector AvailableProtector()
    {
        var protector = Substitute.For<IWebhookSecretProtector>();
        protector.IsAvailable.Returns(true);
        protector.Protect(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(c => "enc:v1:" + c.ArgAt<string>(2).Length);
        return protector;
    }

    private static Harness Create(
        ICloudEventSubscriptionStore? store = null,
        IWebhookSecretProtector? protector = null,
        bool protectorRegistered = true,
        string environment = "Production",
        string tenant = "tenant-a",
        string? sid = "S-1-5-21-ADMIN",
        string[]? groups = null,
        CdcWebhooksOptions? options = null)
    {
        store ??= new InMemoryCloudEventSubscriptionStore();
        protector ??= AvailableProtector();
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(environment);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOptions<CdcWebhooksOptions>>(Options.Create(options ?? new CdcWebhooksOptions { Enabled = true }));
        services.AddSingleton(store);
        services.AddSingleton(env);
        if (protectorRegistered)
        {
            services.AddSingleton(protector);
        }

        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        context.Items[SecurityPrincipalContext.ItemKey] = new SecurityPrincipalContext
        {
            UserSid = new Sid(sid ?? "none"),
            TenantId = new TenantId(tenant),
            GroupSids = new HashSet<Sid>(),
            TenantRoles = new HashSet<string>(),
            ClusterRoles = new HashSet<string> { "GovernanceAdmin" },
            AuthenticationScheme = "Test"
        };
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, "admin"),
            new("tenant", tenant),
            new(ClaimTypes.Role, "GovernanceAdmin")
        };
        if (sid is not null)
        {
            claims.Add(new Claim(ClaimTypes.PrimarySid, sid));
        }

        claims.AddRange((groups ?? ["G-1", "G-2"]).Select(g => new Claim(ClaimTypes.GroupSid, g)));
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
        return new Harness(context, store, protector);
    }

    private static CloudEventWebhookSubscription Body(string url = GoodUrl, string? secret = null, string filter = "public.orders") =>
        new("client-id", "other-tenant", url, filter, [], secret ?? GoodSecret);

    private static ProblemHttpResult Problem(IResult r, int status, string code)
    {
        var p = r.ShouldBeOfType<ProblemHttpResult>();
        p.StatusCode.ShouldBe(status);
        p.ProblemDetails.Extensions["code"].ShouldBe(code);
        return p;
    }

    private static async Task<CloudEventWebhookSubscription> SingleStored(Harness h, string tenant = "tenant-a") =>
        (await h.Store.ListSubscriptionsAsync(tenant)).ShouldHaveSingleItem();

    [Fact]
    public async Task Post_Subscription_WhenSecretVaultUnsupported_ReturnsFailClosed()
    {
        var store = Substitute.For<ICloudEventSubscriptionStore>();
        var protector = Substitute.For<IWebhookSecretProtector>();
        protector.IsAvailable.Returns(false);
        var h = Create(store, protector);

        var result = await StreamingCdcEndpoints.CreateSubscriptionAsync(h.Ctx, Body());

        Problem(result, StatusCodes.Status503ServiceUnavailable, "CDC_SECRET_STORE_UNAVAILABLE");
        await store.DidNotReceiveWithAnyArgs().RegisterSubscriptionAsync(default!, default(int), default);
        protector.DidNotReceiveWithAnyArgs().Protect(default!, default!, default!);
    }

    [Fact]
    public async Task Post_WhenNoProtectorRegistered_ReturnsFailClosed()
    {
        var store = Substitute.For<ICloudEventSubscriptionStore>();
        var h = Create(store, protectorRegistered: false);

        var result = await StreamingCdcEndpoints.CreateSubscriptionAsync(h.Ctx, Body());

        Problem(result, StatusCodes.Status503ServiceUnavailable, "CDC_SECRET_STORE_UNAVAILABLE");
        await store.DidNotReceiveWithAnyArgs().RegisterSubscriptionAsync(default!, default(int), default);
    }

    [Fact]
    public async Task Post_OutsideDevelopment_WithoutSecret_Returns400()
    {
        var h = Create();

        var result = await StreamingCdcEndpoints.CreateSubscriptionAsync(h.Ctx, Body() with { HmacSecret = string.Empty });

        Problem(result, StatusCodes.Status400BadRequest, "CDC_SECRET_REQUIRED");
        (await h.Store.ListSubscriptionsAsync("tenant-a")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Post_OutsideDevelopment_ShortSecret_Returns400()
    {
        var h = Create();

        var result = await StreamingCdcEndpoints.CreateSubscriptionAsync(h.Ctx, Body(secret: "short"));

        Problem(result, StatusCodes.Status400BadRequest, "CDC_SECRET_REQUIRED");
    }

    [Fact]
    public async Task Post_OverlongSecret_Returns400()
    {
        var h = Create(environment: "Development");

        var result = await StreamingCdcEndpoints.CreateSubscriptionAsync(h.Ctx, Body(secret: new string('x', 513)));

        Problem(result, StatusCodes.Status400BadRequest, "CDC_SECRET_REQUIRED");
    }

    [Fact]
    public async Task Post_InDevelopment_WithoutSecretAndKek_CreatesUnsignedSubscription()
    {
        var protector = Substitute.For<IWebhookSecretProtector>();
        protector.IsAvailable.Returns(false);
        var h = Create(protector: protector, environment: "Development");

        var result = await StreamingCdcEndpoints.CreateSubscriptionAsync(h.Ctx, Body() with { HmacSecret = string.Empty });

        result.ShouldBeOfType<Created<CdcSubscriptionView>>();
        (await SingleStored(h)).HmacSecret.ShouldBeEmpty();
    }

    [Fact]
    public async Task Post_StoresOnlyTheProtectedSecret_NeverThePlaintext()
    {
        var h = Create();

        await StreamingCdcEndpoints.CreateSubscriptionAsync(h.Ctx, Body());

        var stored = await SingleStored(h);
        stored.HmacSecret.ShouldStartWith("enc:v1:");
        stored.HmacSecret.ShouldNotContain(GoodSecret);
        h.Protector.Received(1).Protect("tenant-a", stored.Id, GoodSecret);
    }

    [Fact]
    public async Task Post_IgnoresClientSuppliedCreatorSnapshot()
    {
        var h = Create(sid: "S-1-5-21-REAL", groups: ["G-REAL"]);
        var forged = Body() with
        {
            CreatedBySid = "S-1-5-21-FORGED", CreatedByGroupSids = ["G-FORGED"], CreatedByRoles = ["PlatformAdmin"],
            CreatedAt = DateTimeOffset.UtcNow.AddYears(10), TenantId = "other-tenant", Id = "chosen-id", IsEnabled = true
        };

        await StreamingCdcEndpoints.CreateSubscriptionAsync(h.Ctx, forged);

        var stored = await SingleStored(h);
        stored.CreatedBySid.ShouldBe("S-1-5-21-REAL");
        stored.CreatedByGroupSids.ShouldBe(["G-REAL"]);
        stored.CreatedByRoles.ShouldBe(["GovernanceAdmin"]);
        stored.CreatedAt!.Value.ShouldBeLessThan(DateTimeOffset.UtcNow.AddMinutes(1));
        stored.TenantId.ShouldBe("tenant-a");
        stored.Id.ShouldNotBe("chosen-id");
        (await h.Store.ListSubscriptionsAsync("other-tenant")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Post_CapturesSnapshotFromAuthenticatedAdmin()
    {
        var h = Create(sid: "S-1-5-21-ADMIN", groups: ["G-1", "G-2"]);

        await StreamingCdcEndpoints.CreateSubscriptionAsync(h.Ctx, Body());

        var stored = await SingleStored(h);
        stored.CreatedBySid.ShouldBe("S-1-5-21-ADMIN");
        stored.CreatedByGroupSids.ShouldBe(["G-1", "G-2"], ignoreOrder: true);
        stored.CreatedByRoles.ShouldBe(["GovernanceAdmin"]);
        stored.CreatedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Post_Without_Sid_Returns403()
    {
        var h = Create(sid: null);

        var result = await StreamingCdcEndpoints.CreateSubscriptionAsync(h.Ctx, Body());

        result.ShouldBeAssignableTo<IStatusCodeHttpResult>()!.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        (await h.Store.ListSubscriptionsAsync("tenant-a")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Post_TooManyGroups_Returns400_AndStoresNothing()
    {
        var h = Create(groups: ["1", "2", "3"], options: new CdcWebhooksOptions { Enabled = true, MaxSnapshotGroups = 2 });

        var result = await StreamingCdcEndpoints.CreateSubscriptionAsync(h.Ctx, Body());

        Problem(result, StatusCodes.Status400BadRequest, "CDC_SNAPSHOT_TOO_LARGE");
        (await h.Store.ListSubscriptionsAsync("tenant-a")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Post_QuotaExceeded_Returns429_UsingConfiguredQuota()
    {
        var h = Create(options: new CdcWebhooksOptions { Enabled = true, MaxSubscriptionsPerTenant = 2 });

        await StreamingCdcEndpoints.CreateSubscriptionAsync(h.Ctx, Body());
        await StreamingCdcEndpoints.CreateSubscriptionAsync(h.Ctx, Body());
        var third = await StreamingCdcEndpoints.CreateSubscriptionAsync(h.Ctx, Body());

        Problem(third, StatusCodes.Status429TooManyRequests, "CDC_SUBSCRIPTION_QUOTA_EXCEEDED");
    }

    [Theory]
    [InlineData("Host")]
    [InlineData("X-Forwarded-For")]
    [InlineData("ce-id")]
    [InlineData("x-autheris-signature-256")]
    public async Task Post_ForbiddenCustomHeader_Returns400(string header)
    {
        var h = Create();

        var result = await StreamingCdcEndpoints.CreateSubscriptionAsync(
            h.Ctx, Body() with { CustomHeaders = new Dictionary<string, string> { [header] = "v" } });

        Problem(result, StatusCodes.Status400BadRequest, "CDC_CUSTOM_HEADER_NOT_ALLOWED");
        (await h.Store.ListSubscriptionsAsync("tenant-a")).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("a b; drop")]
    [InlineData("a.b.c.d")]
    public async Task Post_InvalidTableFilter_Returns400(string filter)
    {
        var h = Create();

        var result = await StreamingCdcEndpoints.CreateSubscriptionAsync(h.Ctx, Body(filter: filter));

        Problem(result, StatusCodes.Status400BadRequest, "CDC_FILTER_INVALID");
    }

    [Fact]
    public async Task Post_UndefinedOperation_Returns400()
    {
        var h = Create();

        var result = await StreamingCdcEndpoints.CreateSubscriptionAsync(h.Ctx, Body() with { FilterOperations = [(CdcOperation)99] });

        Problem(result, StatusCodes.Status400BadRequest, "CDC_FILTER_INVALID");
    }

    [Fact]
    public async Task Post_PrivateTarget_IsAllowedOnlyInDevelopment()
    {
        var prod = Create();
        var dev = Create(environment: "Development");

        var prodResult = await StreamingCdcEndpoints.CreateSubscriptionAsync(prod.Ctx, Body(url: "http://10.0.0.5:9/hook"));
        var devResult = await StreamingCdcEndpoints.CreateSubscriptionAsync(dev.Ctx, Body(url: "http://10.0.0.5:9/hook"));

        Problem(prodResult, StatusCodes.Status400BadRequest, "CDC_TARGET_URL_NOT_ALLOWED");
        devResult.ShouldBeOfType<Created<CdcSubscriptionView>>();
    }

    [Fact]
    public async Task Post_Response_RedactsSecretAndGroups()
    {
        var h = Create();

        var result = await StreamingCdcEndpoints.CreateSubscriptionAsync(h.Ctx, Body());

        var view = result.ShouldBeOfType<Created<CdcSubscriptionView>>().Value!;
        view.HmacSecret.ShouldBe("[REDACTED]");
        view.CreatedByRoleCount.ShouldBe(1);
        System.Text.Json.JsonSerializer.Serialize(view).ShouldNotContain("G-1");
    }

    [Fact]
    public async Task Get_RedactsSecretAndGroups()
    {
        var h = Create();
        await StreamingCdcEndpoints.CreateSubscriptionAsync(h.Ctx, Body());

        var result = await StreamingCdcEndpoints.ListSubscriptionsAsync(h.Ctx);

        var views = result.ShouldBeOfType<Ok<IEnumerable<CdcSubscriptionView>>>().Value!.ToList();
        var json = System.Text.Json.JsonSerializer.Serialize(views);
        views.ShouldHaveSingleItem().HmacSecret.ShouldBe("[REDACTED]");
        json.ShouldNotContain("G-1");
        json.ShouldNotContain("enc:v1:");
    }

    [Fact]
    public async Task Delete_OtherTenantsSubscription_Returns404()
    {
        var owner = Create(tenant: "tenant-a");
        await StreamingCdcEndpoints.CreateSubscriptionAsync(owner.Ctx, Body());
        var id = (await SingleStored(owner)).Id;
        var intruder = Create(owner.Store, owner.Protector, tenant: "tenant-b");

        var result = await StreamingCdcEndpoints.DeleteSubscriptionAsync(id, intruder.Ctx);

        result.ShouldBeOfType<NotFound>();
        (await owner.Store.GetSubscriptionAsync("tenant-a", id)).ShouldNotBeNull();
    }

    [Fact]
    public async Task Post_CustomHeaders_AreProtectedAtRest_AndRedactedInResponses()
    {
        var h = Create();
        var headers = new Dictionary<string, string> { ["X-Api-Key"] = "super-token" };

        var result = await StreamingCdcEndpoints.CreateSubscriptionAsync(h.Ctx, Body() with { CustomHeaders = headers });

        var stored = await SingleStored(h);
        stored.CustomHeaders!.ShouldContainKey(CdcCustomHeaders.ProtectedKey);
        stored.CustomHeaders!.Values.ShouldAllBe(v => !v.Contains("super-token"));
        h.Protector.Received().Protect("tenant-a", stored.Id, Arg.Is<string>(j => j.Contains("super-token")));
        var json = JsonSerializer.Serialize(result.ShouldBeOfType<Created<CdcSubscriptionView>>().Value);
        json.ShouldNotContain("super-token");
        json.ShouldNotContain("enc:v1:");
        var list = await StreamingCdcEndpoints.ListSubscriptionsAsync(h.Ctx);
        JsonSerializer.Serialize(list.ShouldBeOfType<Ok<IEnumerable<CdcSubscriptionView>>>().Value).ShouldNotContain("super-token");
    }

    [Fact]
    public async Task Post_TooManyOrTooLargeCustomHeaders_Returns400()
    {
        var h = Create();
        var many = Enumerable.Range(0, 21).ToDictionary(i => "X-H" + i, _ => "v");
        var big = new Dictionary<string, string> { ["X-Big"] = new string('v', 2049) };

        Problem(await StreamingCdcEndpoints.CreateSubscriptionAsync(h.Ctx, Body() with { CustomHeaders = many }), 400, "CDC_CUSTOM_HEADER_NOT_ALLOWED");
        Problem(await StreamingCdcEndpoints.CreateSubscriptionAsync(h.Ctx, Body() with { CustomHeaders = big }), 400, "CDC_CUSTOM_HEADER_NOT_ALLOWED");
    }

    [Fact]
    public async Task Post_StoreBusy_Returns503()
    {
        var store = Substitute.For<ICloudEventSubscriptionStore>();
        store.RegisterSubscriptionAsync(Arg.Any<CloudEventWebhookSubscription>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask>(_ => throw new CloudEventSubscriptionStoreBusyException());
        var h = Create(store);

        Problem(await StreamingCdcEndpoints.CreateSubscriptionAsync(h.Ctx, Body()), 503, "CDC_STORE_BUSY");
    }
}
