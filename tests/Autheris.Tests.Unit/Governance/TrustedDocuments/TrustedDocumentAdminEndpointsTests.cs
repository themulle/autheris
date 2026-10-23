namespace Autheris.Tests.Unit.Governance.TrustedDocuments;

using System.Security.Claims;
using System.Text;
using Autheris.Api.Endpoints;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Api.Security;
using Autheris.Application.Audit;
using Autheris.Application.Governance.TrustedDocuments;
using Autheris.Domain.Audit;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Issue #281 WP3: admin API (SEC-281-5, -6, -8, -9).</summary>
[Trait("Category", "Security")]
public sealed class TrustedDocumentAdminEndpointsTests
{
    private static readonly string H1 = new('a', 64);
    private const string PutBody = "{\"hashes\":[\"" + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" + "\"],\"allowedOrigins\":[\"https://app.example.com\"]}";

    private readonly ITrustedDocumentRepository _repo = Substitute.For<ITrustedDocumentRepository>();
    private readonly ITrustedDocumentAppRegistry _registry = Substitute.For<ITrustedDocumentAppRegistry>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();

    public TrustedDocumentAdminEndpointsTests()
    {
        _repo.ReplaceTrustedDocumentVersionAsync(default!, default!, default!, default!, default, default, default)
            .ReturnsForAnyArgs(Task.FromResult(new TrustedDocumentSetDelta([H1], [], 0, true, true)));
    }

    private TrustedDocumentAdministrationService Service() => new(_repo, _registry, _audit, Options.Create(new GatewayOptions()));

    private static DefaultHttpContext Http(string? body = null, params string[] scopes)
    {
        var ctx = new DefaultHttpContext();
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "S-1-5-21-1"), new Claim("scp", string.Join(' ', scopes))], "test");
        ctx.User = new ClaimsPrincipal(identity);
        if (body is not null)
        {
            ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
            ctx.Request.ContentType = "application/json";
        }

        return ctx;
    }

    private static async Task<int> StatusAsync(IResult result)
    {
        var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        ctx.Response.Body = new MemoryStream();
        await result.ExecuteAsync(ctx);
        return ctx.Response.StatusCode;
    }

    [Fact]
    public async Task Admin_MissingScope_403()
    {
        (await StatusAsync(await TrustedDocumentAdminEndpoints.PutAsync("shop", "1", Http(PutBody), Service()))).ShouldBe(403);
        (await StatusAsync(await TrustedDocumentAdminEndpoints.RetireAsync("shop", "1", Http(), Service()))).ShouldBe(403);
        (await StatusAsync(await TrustedDocumentAdminEndpoints.GetAsync("shop", Http(), Service()))).ShouldBe(403);
        await _repo.DidNotReceiveWithAnyArgs().ReplaceTrustedDocumentVersionAsync(default!, default!, default!, default!, default, default, default);
    }

    [Fact]
    public async Task Admin_ReadScopeCannotWrite()
    {
        (await StatusAsync(await TrustedDocumentAdminEndpoints.PutAsync("shop", "1", Http(PutBody, "trusted-documents:read"), Service()))).ShouldBe(403);
        (await StatusAsync(await TrustedDocumentAdminEndpoints.RetireAsync("shop", "1", Http(null, "trusted-documents:read"), Service()))).ShouldBe(403);
    }

    [Fact]
    public async Task Get_ReadScope_200_And_WriteScope_200_And_Unknown_404()
    {
        _repo.GetTrustedDocumentAppAsync("shop", Arg.Any<CancellationToken>()).Returns(Task.FromResult<TrustedDocumentAppView?>(
            new TrustedDocumentAppView("shop", ["https://app.example.com"], [])));
        (await StatusAsync(await TrustedDocumentAdminEndpoints.GetAsync("shop", Http(null, "trusted-documents:read"), Service()))).ShouldBe(200);
        (await StatusAsync(await TrustedDocumentAdminEndpoints.GetAsync("shop", Http(null, "trusted-documents:write"), Service()))).ShouldBe(200);
        (await StatusAsync(await TrustedDocumentAdminEndpoints.GetAsync("nope", Http(null, "trusted-documents:read"), Service()))).ShouldBe(404);
    }

    [Fact]
    public async Task Put_WithWriteScope_Succeeds_Invalidates_And_Audits()
    {
        (await StatusAsync(await TrustedDocumentAdminEndpoints.PutAsync("shop", "1", Http(PutBody, "trusted-documents:write"), Service()))).ShouldBe(200);
        _registry.Received(1).Invalidate();
        await _audit.Received(1).RecordRequiredAsync(Arg.Is<AuditLogEntry>(e => e.EventType == AuditEventTypes.TrustedDocumentsReplaced), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("Shop", "1")]
    [InlineData("shop", "1/2")]
    [InlineData("shop", "-1")]
    public async Task Admin_Put_InvalidRouteValues_400_NoEcho(string appId, string version)
    {
        var result = await TrustedDocumentAdminEndpoints.PutAsync(appId, version, Http(PutBody, "trusted-documents:write"), Service());
        (await StatusAsync(result)).ShouldBe(400);
        await _repo.DidNotReceiveWithAnyArgs().ReplaceTrustedDocumentVersionAsync(default!, default!, default!, default!, default, default, default);
    }

    [Theory]
    [InlineData("{\"hashes\":[\"zz\"],\"allowedOrigins\":[\"https://app.example.com\"]}")]
    [InlineData("{\"hashes\":[],\"allowedOrigins\":[\"https://app.example.com\"]}")]
    [InlineData("{\"hashes\":[\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"],\"allowedOrigins\":[]}")]
    [InlineData("{\"hashes\":[\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"],\"allowedOrigins\":[\"ftp://x\"]}")]
    [InlineData("not json")]
    public async Task Admin_Put_OverBounds_400(string body)
    {
        (await StatusAsync(await TrustedDocumentAdminEndpoints.PutAsync("shop", "1", Http(body, "trusted-documents:write"), Service()))).ShouldBe(400);
    }

    [Fact]
    public async Task Admin_Put_BodySizeCap_413()
    {
        var big = "{\"hashes\":[],\"pad\":\"" + new string('x', (int)TrustedDocumentAdminEndpoints.MaxPutBodyBytes) + "\"}";
        (await StatusAsync(await TrustedDocumentAdminEndpoints.PutAsync("shop", "1", Http(big, "trusted-documents:write"), Service()))).ShouldBe(413);
    }

    [Fact]
    public async Task Admin_Put_RetiredVersion_409()
    {
        _repo.ReplaceTrustedDocumentVersionAsync(default!, default!, default!, default!, default, default, default)
            .ReturnsForAnyArgs<Task<TrustedDocumentSetDelta>>(_ => throw new TrustedDocumentVersionRetiredException());
        (await StatusAsync(await TrustedDocumentAdminEndpoints.PutAsync("shop", "1", Http(PutBody, "trusted-documents:write"), Service()))).ShouldBe(409);
    }

    [Fact]
    public async Task Admin_Audit_Failure_Surfaces_And_Cache_Invalidated()
    {
        _audit.RecordRequiredAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>()).Returns<Task>(_ => throw new InvalidOperationException("audit down"));
        await Should.ThrowAsync<InvalidOperationException>(() => TrustedDocumentAdminEndpoints.PutAsync("shop", "1", Http(PutBody, "trusted-documents:write"), Service()));
        _registry.Received(1).Invalidate();
    }

    [Fact]
    public async Task Retire_Outcomes_Map_To_204_And_404()
    {
        _repo.RetireTrustedDocumentVersionAsync("shop", "1", Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(TrustedDocumentRetireOutcome.Retired));
        _repo.RetireTrustedDocumentVersionAsync("shop", "2", Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(TrustedDocumentRetireOutcome.NotFound));
        (await StatusAsync(await TrustedDocumentAdminEndpoints.RetireAsync("shop", "1", Http(null, "trusted-documents:write"), Service()))).ShouldBe(204);
        (await StatusAsync(await TrustedDocumentAdminEndpoints.RetireAsync("shop", "2", Http(null, "trusted-documents:write"), Service()))).ShouldBe(404);
    }

    private List<RouteEndpoint> Mapped(bool enabled)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddRouting();
        builder.Services.AddSingleton(Service());
        builder.Services.AddAuthorization();
        var app = builder.Build();
        app.MapTrustedDocumentAdminEndpointsWhenEnabled(new GatewayOptions
        {
            GraphQL = new GraphQLOptions { AppScopedTrustedDocuments = new AppScopedTrustedDocumentsOptions { Enabled = enabled } }
        });
        return ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>().ToList();
    }

    [Fact]
    public void Admin_NotMapped_WhenDisabled() => Mapped(enabled: false).ShouldBeEmpty();

    [Fact]
    public void Admin_Unauthenticated_401_RoutesRequireClusterAdmin_NeverAnonymous()
    {
        var endpoints = Mapped(enabled: true);
        endpoints.Count.ShouldBe(3);
        foreach (var endpoint in endpoints)
        {
            endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().ShouldContain(a => a.Policy == GatewayPolicies.ClusterAdmin);
            endpoint.Metadata.GetMetadata<IAllowAnonymous>().ShouldBeNull();
        }
    }

    [Fact]
    public void Cors_AppHeader_Allowed_Only_When_Enabled()
    {
        var off = GatewayCoreServiceExtensions.DeveloperCorsHeaders(new GatewayOptions());
        off.ShouldNotContain("X-Autheris-App");
        var on = GatewayCoreServiceExtensions.DeveloperCorsHeaders(new GatewayOptions
        {
            GraphQL = new GraphQLOptions { AppScopedTrustedDocuments = new AppScopedTrustedDocumentsOptions { Enabled = true } }
        });
        on.ShouldContain("X-Autheris-App");
    }
}
