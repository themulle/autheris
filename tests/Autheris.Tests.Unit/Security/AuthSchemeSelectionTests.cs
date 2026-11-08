using System.Text.Encodings.Web;
using Autheris.Api.Middleware;
using Autheris.Api.Security;
using Autheris.Application.Audit;
using Autheris.Domain.Audit;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Common;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;
using AuthenticationOptions = Autheris.Domain.Options.AuthenticationOptions;

namespace Autheris.Tests.Unit.Security;

[Trait("Category", "Security")]
public sealed class AuthSchemeSelectionTests
{
    private static GatewayOptions Options(AuthenticationMode mode, ForwardAuthOptions? forward = null) => new()
    {
        Authentication = new AuthenticationOptions
        {
            Mode = mode,
            RequireKerberosOnly = false,
            EnableKerberos = true,
            BasicAuth = new BasicAuthOptions { Enabled = true },
            ForwardAuth = forward ?? new ForwardAuthOptions { Enabled = mode != AuthenticationMode.Direct && mode != AuthenticationMode.Unset, UserSidHeader = "X-Forwarded-Sid" }
        }
    };

    private static string Select(GatewayOptions options, string path = "/graphql", params (string Name, string Value)[] headers)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        foreach (var (name, value) in headers)
        {
            context.Request.Headers[name] = value;
        }

        return GatewayAuthSchemeSelector.Select(context, options, false, "session", false);
    }

    [Theory]
    [InlineData(AuthenticationMode.Direct)]
    [InlineData(AuthenticationMode.Unset)]
    public void Direct_mode_never_selects_forwardauth(AuthenticationMode mode) =>
        Select(Options(mode, new ForwardAuthOptions { Enabled = true }), "/graphql", ("X-Forwarded-User", "alice"))
            .ShouldNotBe(GatewayAuthSchemes.ForwardAuth);

    [Fact]
    [Trait("SecurityCondition", "SEC-AUTH-02a")]
    public async Task Direct_mode_strips_identity_headers()
    {
        var forward = new ForwardAuthOptions { UserHeader = "X-Custom-User", UserSidHeader = "X-Custom-Sid" };
        var context = new DefaultHttpContext();
        foreach (var name in new[] { "X-Custom-User", "X-Custom-Sid", "X-Forwarded-Email", "X-Forwarded-Groups", "X-Forwarded-Roles", "X-Forwarded-Tenant", "X-Forwarded-Secret" })
        {
            context.Request.Headers[name] = "v";
        }

        context.Request.Headers["X-Other"] = "kept";
        var middleware = new EdgeHeaderStrippingMiddleware(_ => Task.CompletedTask, Microsoft.Extensions.Options.Options.Create(Options(AuthenticationMode.Direct, forward)));

        await middleware.InvokeAsync(context);

        context.Request.Headers.Keys.ShouldBe(["X-Other"]);
    }

    [Fact]
    public async Task Upstream_mode_keeps_identity_headers_for_the_handler()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Forwarded-User"] = "alice";
        var middleware = new EdgeHeaderStrippingMiddleware(_ => Task.CompletedTask, Microsoft.Extensions.Options.Options.Create(Options(AuthenticationMode.Upstream)));

        await middleware.InvokeAsync(context);

        context.Request.Headers.ContainsKey("X-Forwarded-User").ShouldBeTrue();
    }

    [Theory]
    [InlineData("Bearer a.b.c")]
    [InlineData("Basic dTpw")]
    [InlineData("Negotiate abc")]
    [Trait("SecurityCondition", "SEC-AUTH-HDR")]
    public void Identity_headers_never_join_or_override_direct_credential(string authorization)
    {
        var both = Select(Options(AuthenticationMode.Both), "/graphql", ("Authorization", authorization), ("X-Forwarded-User", "alice"));
        both.ShouldBe(GatewayAuthSchemes.RejectScheme);

        var bothGroupsOnly = Select(Options(AuthenticationMode.Both), "/graphql", ("Authorization", authorization), ("X-Forwarded-Groups", "S-1-5-32-544"));
        bothGroupsOnly.ShouldBe(GatewayAuthSchemes.RejectScheme);

        var direct = Select(Options(AuthenticationMode.Direct), "/graphql", ("Authorization", authorization), ("X-Forwarded-User", "alice"));
        direct.ShouldNotBe(GatewayAuthSchemes.ForwardAuth);
        direct.ShouldNotBe(GatewayAuthSchemes.RejectScheme);
    }

    [Fact]
    [Trait("SecurityCondition", "SEC-AUTH-02b")]
    public void Both_mode_rejects_token_plus_identity_headers()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer a.b.c";
        context.Request.Headers["X-Forwarded-User"] = "alice-secret-marker";

        var scheme = GatewayAuthSchemeSelector.Select(context, Options(AuthenticationMode.Both), false, "session", false);

        scheme.ShouldBe(GatewayAuthSchemes.RejectScheme);
        context.Items[GatewayAuthSchemeSelector.RejectReasonItemKey].ShouldBe("auth.ambiguous_identity");
    }

    [Fact]
    [Trait("SecurityCondition", "SEC-AUTH-02b")]
    public async Task Ambiguous_identity_audit_record_carries_no_header_value()
    {
        var audit = Substitute.For<IAuditWriter>();
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().AddSingleton(audit).BuildServiceProvider() };
        context.Request.Headers.Authorization = "Bearer token-marker";
        context.Request.Headers["X-Forwarded-User"] = "alice-secret-marker";
        GatewayAuthSchemeSelector.Select(context, Options(AuthenticationMode.Both), false, "session", false);
        var handler = new RejectAuthenticationHandler(new StaticOptionsMonitor<AuthenticationSchemeOptions>(new AuthenticationSchemeOptions()), NullLoggerFactory.Instance, UrlEncoder.Default);
        await handler.InitializeAsync(new AuthenticationScheme(GatewayAuthSchemes.RejectScheme, null, typeof(RejectAuthenticationHandler)), context);

        var result = await handler.AuthenticateAsync();

        result.Succeeded.ShouldBeFalse();
        result.Failure!.Message.ShouldBe("auth.ambiguous_identity");
        var written = (AuditLogEntry)audit.ReceivedCalls().Single().GetArguments()[0]!;
        written.EventType.ShouldBe(AuditEventTypes.AuthFailed);
        written.DetailsJson.ShouldBe("{\"reason\":\"auth.ambiguous_identity\"}");
    }

    [Fact]
    [Trait("SecurityCondition", "SEC-AUTH-02c")]
    public void Upstream_mode_ignores_authorization_header()
    {
        var options = Options(AuthenticationMode.Upstream);

        Select(options, "/graphql", ("Authorization", "Bearer a.b.c"), ("X-Forwarded-User", "alice")).ShouldBe(GatewayAuthSchemes.ForwardAuth);
        Select(options, "/graphql", ("Authorization", "Basic dTpw")).ShouldBe(GatewayAuthSchemes.ForwardAuth);
        Select(options).ShouldBe(GatewayAuthSchemes.ForwardAuth);
    }

    [Fact]
    [Trait("SecurityCondition", "SEC-AUTH-02b")]
    public void Only_configured_user_header_selects_forwardauth()
    {
        var options = Options(AuthenticationMode.Both, new ForwardAuthOptions { Enabled = true, UserHeader = "X-Custom-User" });

        Select(options, "/graphql", ("X-Custom-User", "alice")).ShouldBe(GatewayAuthSchemes.ForwardAuth);
        foreach (var hardCoded in new[] { "X-Forwarded-User", "X-Auth-Request-User", "X-Forwarded-Preferred-Username" })
        {
            Select(options, "/graphql", (hardCoded, "alice")).ShouldNotBe(GatewayAuthSchemes.ForwardAuth);
        }
    }

    [Fact]
    [Trait("SecurityCondition", "SEC-AUTH-02a")]
    public void Header_names_come_only_from_options()
    {
        var forward = new ForwardAuthOptions { Enabled = true, UserHeader = "X-U", EmailHeader = "X-E", GroupsHeader = "X-G", RolesHeader = "X-R", TenantHeader = "X-T", UserSidHeader = "X-S" };

        forward.GetIdentityHeaderNames().ShouldBe(["X-U", "X-E", "X-G", "X-R", "X-T", "X-S"]);
        new ForwardAuthOptions { UserSidHeader = null, EmailHeader = " " }.GetIdentityHeaderNames().ShouldBe(["X-Forwarded-User", "X-Forwarded-Groups", "X-Forwarded-Roles", "X-Forwarded-Tenant"]);
    }

    [Theory]
    [InlineData(AuthenticationMode.Direct)]
    [InlineData(AuthenticationMode.Upstream)]
    [InlineData(AuthenticationMode.Both)]
    public void Scim_path_selects_scim_in_every_mode(AuthenticationMode mode) =>
        Select(Options(mode), "/scim/v2/Users", ("Authorization", "Bearer opaque"), ("X-Forwarded-User", "alice")).ShouldBe(GatewayAuthSchemes.Scim);
}
