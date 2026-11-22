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

        forward.IdentityHeaderNames.ShouldBe(["X-U", "X-E", "X-G", "X-R", "X-T", "X-S"]);
        new ForwardAuthOptions { UserSidHeader = null, EmailHeader = " " }.IdentityHeaderNames.ShouldBe(["X-Forwarded-User", "X-Forwarded-Groups", "X-Forwarded-Roles", "X-Forwarded-Tenant"]);
    }

    [Theory]
    [InlineData(AuthenticationMode.Direct)]
    [InlineData(AuthenticationMode.Upstream)]
    [InlineData(AuthenticationMode.Both)]
    public void Scim_path_selects_scim_in_every_mode(AuthenticationMode mode)
    {
        Select(Options(mode), "/scim/v2/realm-1/Users").ShouldBe(GatewayAuthSchemes.Scim);
        Select(Options(mode), "/scim/v2/realm-1/Users", ("Authorization", "Bearer opaque"), ("X-Forwarded-User", "alice")).ShouldBe(GatewayAuthSchemes.Scim);
        Select(Options(mode), "/scim/v2/realm-1/Groups", ("X-Forwarded-User", "alice")).ShouldBe(GatewayAuthSchemes.Scim);
        Select(Options(mode), "/scim/v2/realm-1/ServiceProviderConfig", ("Authorization", "Basic dTpw")).ShouldBe(GatewayAuthSchemes.Scim);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task EdgeHeaderStripping_StripsInternalHeaders_PreservesWebhookAndAppHeaders()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Autheris-Transport"] = "GraphQL";
        context.Request.Headers["X-Autheris-Domain"] = "internal";
        context.Request.Headers["X-Autheris-Principal"] = "admin";
        context.Request.Headers["X-Autheris-Tenant"] = "tenant-b";
        context.Request.Headers["X-Autheris-Decision"] = "allowed";
        context.Request.Headers["X-Autheris-Roles"] = "ClusterAdmin";
        context.Request.Headers["X-Autheris-Groups"] = "S-1-5-32-544";
        context.Request.Headers["X-Autheris-Signature"] = "forged";
        context.Request.Headers["X-Autheris-Nonce"] = "nonce-1";
        context.Request.Headers["X-Autheris-Timestamp"] = "1700000000";
        context.Request.Headers["X-Autheris-Delivery"] = "deliv-1";
        context.Request.Headers["X-Autheris-Signature-256"] = "sha256=abcd";
        context.Request.Headers["X-Autheris-App"] = "shop/1";

        var middleware = new EdgeHeaderStrippingMiddleware(_ => Task.CompletedTask, Microsoft.Extensions.Options.Options.Create(Options(AuthenticationMode.Upstream)));
        await middleware.InvokeAsync(context);

        context.Request.Headers.ContainsKey("X-Autheris-Transport").ShouldBeFalse();
        context.Request.Headers.ContainsKey("X-Autheris-Domain").ShouldBeFalse();
        context.Request.Headers.ContainsKey("X-Autheris-Principal").ShouldBeFalse();
        context.Request.Headers.ContainsKey("X-Autheris-Tenant").ShouldBeFalse();
        context.Request.Headers.ContainsKey("X-Autheris-Decision").ShouldBeFalse();
        context.Request.Headers.ContainsKey("X-Autheris-Roles").ShouldBeFalse();
        context.Request.Headers.ContainsKey("X-Autheris-Groups").ShouldBeFalse();
        context.Request.Headers.ContainsKey("X-Autheris-Signature").ShouldBeFalse();
        context.Request.Headers.ContainsKey("X-Autheris-Nonce").ShouldBeFalse();

        context.Request.Headers["X-Autheris-Timestamp"].ToString().ShouldBe("1700000000");
        context.Request.Headers["X-Autheris-Delivery"].ToString().ShouldBe("deliv-1");
        context.Request.Headers["X-Autheris-Signature-256"].ToString().ShouldBe("sha256=abcd");
        context.Request.Headers["X-Autheris-App"].ToString().ShouldBe("shop/1");
    }

    [Fact]
    public async Task EdgeHeaderStripping_StripsOnlyExplicitInternalHeaders_AllowsCustomAndWebhookHeaders()
    {
        var internalHeaders = new[]
        {
            "X-Autheris-Transport", "X-Autheris-Domain", "X-Autheris-Principal", "X-Autheris-Tenant",
            "X-Autheris-Decision", "X-Autheris-Denial-Reason", "X-Autheris-Rls-Filter", "X-Autheris-Roles",
            "X-Autheris-Groups", "X-Autheris-Signature", "X-Autheris-Nonce", "X-Autheris-Signature-Version",
            "X-Autheris-Body-Sha256", "X-Autheris-Event-Id", "X-Autheris-Event-Type", "X-Autheris-Mode",
            "X-Autheris-Truncated", "X-Autheris-Cache", "X-Autheris-Debug", "X-Autheris-Internal",
            "X-Autheris-User", "X-Autheris-Scopes", "X-Autheris-Subject", "X-Autheris-Realm"
        };

        var context = new DefaultHttpContext();
        foreach (var header in internalHeaders)
        {
            context.Request.Headers[header] = "spoofed";
        }
        context.Request.Headers["X-Autheris-Custom"] = "client-value";
        context.Request.Headers["X-Autheris-Timestamp"] = "1700000000";
        context.Request.Headers["X-Autheris-Delivery"] = "deliv-1";
        context.Request.Headers["X-Autheris-Signature-256"] = "sha256=abcd";
        context.Request.Headers["X-Autheris-App"] = "shop/1";

        var middleware = new EdgeHeaderStrippingMiddleware(_ => Task.CompletedTask, Microsoft.Extensions.Options.Options.Create(Options(AuthenticationMode.Upstream)));
        await middleware.InvokeAsync(context);

        foreach (var header in internalHeaders)
        {
            context.Request.Headers.ContainsKey(header).ShouldBeFalse($"Expected internal header '{header}' to be stripped.");
        }

        context.Request.Headers["X-Autheris-Custom"].ToString().ShouldBe("client-value");
        context.Request.Headers["X-Autheris-Timestamp"].ToString().ShouldBe("1700000000");
        context.Request.Headers["X-Autheris-Delivery"].ToString().ShouldBe("deliv-1");
        context.Request.Headers["X-Autheris-Signature-256"].ToString().ShouldBe("sha256=abcd");
        context.Request.Headers["X-Autheris-App"].ToString().ShouldBe("shop/1");

        // Verify case-insensitive stripping and Direct mode ForwardAuth stripping while preserving X-Autheris-Custom.
        var directCtx = new DefaultHttpContext();
        directCtx.Request.Headers["x-autheris-principal"] = "spoofed";
        directCtx.Request.Headers["X-Forwarded-User"] = "alice";
        directCtx.Request.Headers["X-Autheris-Custom"] = "kept-in-direct";
        var directMiddleware = new EdgeHeaderStrippingMiddleware(_ => Task.CompletedTask, Microsoft.Extensions.Options.Options.Create(Options(AuthenticationMode.Direct)));
        await directMiddleware.InvokeAsync(directCtx);

        directCtx.Request.Headers.ContainsKey("x-autheris-principal").ShouldBeFalse();
        directCtx.Request.Headers.ContainsKey("X-Forwarded-User").ShouldBeFalse();
        directCtx.Request.Headers["X-Autheris-Custom"].ToString().ShouldBe("kept-in-direct");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ProductWebhookHeaderReader_AcceptsSingleFamily_RejectsMixedOrMultiValued()
    {
        var autherisCtx = new DefaultHttpContext();
        autherisCtx.Request.Headers["X-Autheris-Timestamp"] = "1700000000";
        autherisCtx.Request.Headers["X-Autheris-Delivery"] = "d-1";
        autherisCtx.Request.Headers["X-Autheris-Signature-256"] = "sha256=aaa";
        Autheris.Api.Endpoints.ProductEndpoints.TryReadWebhookDelivery(autherisCtx.Request.Headers, out var d1).ShouldBeTrue();
        d1.DeliveryId.ShouldBe("d-1");
        d1.Signature.ShouldBe("sha256=aaa");

        var webhookCtx = new DefaultHttpContext();
        webhookCtx.Request.Headers["X-Webhook-Timestamp"] = "1700000001";
        webhookCtx.Request.Headers["X-Webhook-Delivery"] = "d-2";
        webhookCtx.Request.Headers["X-Webhook-Signature-256"] = "sha256=bbb";
        Autheris.Api.Endpoints.ProductEndpoints.TryReadWebhookDelivery(webhookCtx.Request.Headers, out var d2).ShouldBeTrue();
        d2.DeliveryId.ShouldBe("d-2");
        d2.Signature.ShouldBe("sha256=bbb");

        var mixedCtx = new DefaultHttpContext();
        mixedCtx.Request.Headers["X-Autheris-Timestamp"] = "1700000000";
        mixedCtx.Request.Headers["X-Webhook-Delivery"] = "d-2";
        mixedCtx.Request.Headers["X-Webhook-Signature-256"] = "sha256=bbb";
        Autheris.Api.Endpoints.ProductEndpoints.TryReadWebhookDelivery(mixedCtx.Request.Headers, out _).ShouldBeFalse();

        var multiCtx = new DefaultHttpContext();
        multiCtx.Request.Headers["X-Autheris-Timestamp"] = "1700000000";
        multiCtx.Request.Headers["X-Autheris-Delivery"] = "d-1";
        multiCtx.Request.Headers["X-Autheris-Signature-256"] = new Microsoft.Extensions.Primitives.StringValues(["sha256=aaa", "sha256=bbb"]);
        Autheris.Api.Endpoints.ProductEndpoints.TryReadWebhookDelivery(multiCtx.Request.Headers, out _).ShouldBeFalse();
    }
}
