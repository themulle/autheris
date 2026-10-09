using System.Security.Claims;
using Autheris.Api.Middleware;
using Autheris.Api.Security;
using Autheris.Application.Services;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.GraphQL.Interceptors;
using HotChocolate;
using HotChocolate.Execution;
using HotChocolate.Types;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Security;

/// <summary>
/// Finding 3.3 / R13: agent scopes and app-only tokens can be limited to reading.
/// </summary>
public class ReadOnlyTokenTests
{
    private const string MappedScopeClaimType = "http://schemas.microsoft.com/identity/claims/scope";

    private static readonly IdentitySubjectResolver Resolver = new();

    private static ClaimsPrincipal UserToken(string? scp, string scopeClaimType = "scp")
    {
        var claims = new List<Claim>
        {
            new("oid", "11111111-1111-1111-1111-111111111111"),
            new("azp", "talos-agent"),
            new("preferred_username", "anna@corp.example")
        };
        if (scp != null) claims.Add(new Claim(scopeClaimType, scp));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
    }

    private static ClaimsPrincipal AppToken() =>
        new(new ClaimsIdentity(
            [
                new Claim("oid", "22222222-2222-2222-2222-222222222222"),
                new Claim("appid", "talos-daemon"),
                new Claim("idtyp", "app"),
                new Claim("roles", "Reader")
            ],
            "Bearer"));

    private static ClaimsPrincipal ReadOnlyPrincipal()
    {
        var identity = new ClaimsIdentity([new Claim("oid", "33333333-3333-3333-3333-333333333333")], "Bearer");
        TokenAccessScope.MarkReadOnly(identity);
        return new ClaimsPrincipal(identity);
    }

    // ---------------------------------------------------------------- marker

    [Fact]
    public void InboundAccessModeClaim_FromTokenIssuer_IsNotTrusted()
    {
        var forged = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(TokenAccessScope.ClaimType, TokenAccessScope.ReadOnlyValue, ClaimValueTypes.String, "https://login.microsoftonline.com/t/v2.0")],
            "Bearer"));

        forged.IsReadOnly().ShouldBeFalse();
        ReadOnlyPrincipal().IsReadOnly().ShouldBeTrue();
    }

    // ---------------------------------------------------------------- Entra classification

    [Theory]
    [InlineData("Agent.Read", "scp")]
    [InlineData("Agent.Read", MappedScopeClaimType)]
    [InlineData("agent.read", "scp")]
    public void UserToken_WithOnlyReadOnlyScope_IsReadOnly(string scp, string claimType)
    {
        var principal = UserToken(scp, claimType);

        EntraTokenPolicy.Apply(principal, new EntraIdAuthOptions(), Resolver).ShouldBeNull();

        principal.IsReadOnly().ShouldBeTrue();
    }

    [Fact]
    public void ConfiguredScope_InFullApiForm_MatchesBareScp()
    {
        var principal = UserToken("Agent.Read");
        var options = new EntraIdAuthOptions { ReadOnlyScopes = ["api://autheris/Agent.Read"] };

        EntraTokenPolicy.Apply(principal, options, Resolver);

        principal.IsReadOnly().ShouldBeTrue();
    }

    [Theory]
    [InlineData("Agent.Read Data.Write")]
    [InlineData("user_impersonation")]
    public void UserToken_WithAnyOtherScope_IsNotReadOnly(string scp)
    {
        var principal = UserToken(scp);

        EntraTokenPolicy.Apply(principal, new EntraIdAuthOptions(), Resolver).ShouldBeNull();

        principal.IsReadOnly().ShouldBeFalse();
    }

    [Fact]
    public void DelegatedToken_WithoutHumanClaims_IsNotTreatedAsApp()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("oid", "44444444-4444-4444-4444-444444444444"), new Claim("azp", "spa"), new Claim("scp", "user_impersonation")],
            "Bearer"));

        EntraTokenPolicy.Apply(principal, new EntraIdAuthOptions { AppOnlyTokens = AppOnlyTokenAccess.Deny }, Resolver).ShouldBeNull();
    }

    [Theory]
    [InlineData(AppOnlyTokenAccess.ReadWrite, false)]
    [InlineData(AppOnlyTokenAccess.ReadOnly, true)]
    public void AppOnlyToken_FollowsConfiguredAccess(AppOnlyTokenAccess access, bool expectedReadOnly)
    {
        var principal = AppToken();

        EntraTokenPolicy.Apply(principal, new EntraIdAuthOptions { AppOnlyTokens = access }, Resolver).ShouldBeNull();

        principal.IsReadOnly().ShouldBe(expectedReadOnly);
    }

    [Fact]
    public void AppOnlyToken_WhenDenied_IsRejected()
    {
        EntraTokenPolicy.Apply(AppToken(), new EntraIdAuthOptions { AppOnlyTokens = AppOnlyTokenAccess.Deny }, Resolver)
            .ShouldNotBeNull();
    }

    [Fact]
    public void AppOnlyToken_DefaultSetting_IsReadOnly()
    {
        var options = new EntraIdAuthOptions();
        options.AppOnlyTokens.ShouldBe(AppOnlyTokenAccess.ReadOnly);

        var principal = AppToken();
        EntraTokenPolicy.Apply(principal, options, Resolver).ShouldBeNull();
        principal.IsReadOnly().ShouldBeTrue();
    }

    [Fact]
    public void Token_WithoutScopesAndWithoutRoles_IsRejected()
    {
        var bareToken = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("oid", "55555555-5555-5555-5555-555555555555"), new Claim("preferred_username", "bob@corp.example")],
            "Bearer"));

        var error = EntraTokenPolicy.Apply(bareToken, new EntraIdAuthOptions(), Resolver);
        error.ShouldNotBeNull();
        error.ShouldContain("without scopes ('scp') or roles ('roles')");
    }

    [Fact]
    public void IdToken_WithNonceClaim_IsRejected()
    {
        var idToken = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("oid", "66666666-6666-6666-6666-666666666666"),
                new Claim("nonce", "xyz-123-nonce"),
                new Claim("roles", "Reader")
            ],
            "Bearer"));

        var error = EntraTokenPolicy.Apply(idToken, new EntraIdAuthOptions(), Resolver);
        error.ShouldNotBeNull();
        error.ShouldContain("ID tokens are not accepted");
    }

    [Fact]
    public void AppOnlyToken_WithoutRoles_IsRejected()
    {
        var appWithoutRoles = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("oid", "77777777-7777-7777-7777-777777777777"),
                new Claim("appid", "rogue-daemon"),
                new Claim("idtyp", "app")
            ],
            "Bearer"));

        var error = EntraTokenPolicy.Apply(appWithoutRoles, new EntraIdAuthOptions(), Resolver);
        error.ShouldNotBeNull();
    }

    [Fact]
    public void AppOnlyToken_WhenAllowedClientIdsConfigured_EnforcesAllowlist()
    {
        var options = new EntraIdAuthOptions
        {
            AllowedClientIds = ["allowed-daemon-1", "allowed-daemon-2"]
        };

        // Allowed client
        var allowedPrincipal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("oid", "88888888-8888-8888-8888-888888888888"),
                new Claim("appid", "allowed-daemon-1"),
                new Claim("idtyp", "app"),
                new Claim("roles", "Reader")
            ],
            "Bearer"));

        EntraTokenPolicy.Apply(allowedPrincipal, options, Resolver).ShouldBeNull();

        // Disallowed client
        var unlistedPrincipal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("oid", "99999999-9999-9999-9999-999999999999"),
                new Claim("appid", "unlisted-daemon"),
                new Claim("idtyp", "app"),
                new Claim("roles", "Reader")
            ],
            "Bearer"));

        var error = EntraTokenPolicy.Apply(unlistedPrincipal, options, Resolver);
        error.ShouldNotBeNull();
        error.ShouldContain("unlisted-daemon");
    }

    // ---------------------------------------------------------------- HTTP

    [Theory]
    [InlineData("POST", "/api/governance/sunsetting/rules", false)]
    [InlineData("DELETE", "/api/rebac/tuples", false)]
    [InlineData("POST", "/api/v1/procedures/close_period", false)]
    [InlineData("POST", "/graphqlx", false)]
    [InlineData("POST", "/graphql", true)]
    [InlineData("POST", "/graphql/finance", true)]
    [InlineData("POST", "/mcp", true)]
    [InlineData("POST", "/api/v1/sql", true)]
    [InlineData("POST", "/api/v1/queries/open_invoices", true)]
    [InlineData("POST", "/api/v1/queries", false)]
    [InlineData("POST", "/api/v1/queries/", false)]
    [InlineData("GET", "/api/governance/sunsetting/rules", true)]
    public async Task ReadOnlyToken_HttpRequest_IsLimitedToQueryEndpoints(string method, string path, bool expectedPassed)
    {
        var (passed, status) = await InvokeMiddlewareAsync(ReadOnlyPrincipal(), method, path);

        passed.ShouldBe(expectedPassed);
        if (!expectedPassed) status.ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task RegularToken_WriteRequest_PassesThrough()
    {
        var (passed, _) = await InvokeMiddlewareAsync(UserToken("user_impersonation"), "POST", "/api/governance/sunsetting/rules");

        passed.ShouldBeTrue();
    }

    private static async Task<(bool Passed, int Status)> InvokeMiddlewareAsync(ClaimsPrincipal user, string method, string path)
    {
        var passed = false;
        var middleware = new ReadOnlyTokenMiddleware(_ => { passed = true; return Task.CompletedTask; }, "/graphql", "/mcp");
        var context = new DefaultHttpContext { User = user };
        context.Request.Method = method;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        return (passed, context.Response.StatusCode);
    }

    // ---------------------------------------------------------------- GraphQL

    [Fact]
    public async Task ReadOnlyToken_GraphQlMutation_IsRejected()
    {
        var result = await ExecuteAsync("mutation { touch }", ReadOnlyPrincipal());

        result.Errors.ShouldNotBeNull();
        result.Errors.ShouldContain(e => e.Code == "READ_ONLY_TOKEN");
        Mutation.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task ReadOnlyToken_NamedMutationInMixedDocument_IsRejected()
    {
        var result = await ExecuteAsync("query Q { ping } mutation M { touch }", ReadOnlyPrincipal(), "M");

        result.Errors.ShouldNotBeNull();
        result.Errors.ShouldContain(e => e.Code == "READ_ONLY_TOKEN");
    }

    [Fact]
    public async Task ReadOnlyToken_GraphQlQuery_IsExecuted()
    {
        var result = await ExecuteAsync("{ ping }", ReadOnlyPrincipal());

        (result.Errors ?? []).ShouldBeEmpty();
        result.Data.ShouldNotBeNull();
    }

    [Fact]
    public async Task RegularToken_GraphQlMutation_IsExecuted()
    {
        var result = await ExecuteAsync("mutation { touch }", UserToken("user_impersonation"));

        (result.Errors ?? []).ShouldBeEmpty();
        Mutation.Calls.ShouldBe(1);
    }

    private static async Task<OperationResult> ExecuteAsync(string document, ClaimsPrincipal principal, string? operationName = null)
    {
        Mutation.Calls = 0;
        var executor = await new ServiceCollection()
            .AddGraphQLServer()
            .AddQueryType<PingQuery>()
            .AddMutationType<Mutation>()
            .UseDocumentParser()
            .UseDocumentValidation()
            .UseRequest<ReadOnlyOperationMiddleware>()
            .UseOperationResolver()
            .UseOperationVariableCoercion()
            .UseOperationExecution()
            .BuildRequestExecutorAsync();

        var request = OperationRequestBuilder.New()
            .SetDocument(document)
            .SetOperationName(operationName)
            .AddGlobalState(nameof(ClaimsPrincipal), principal)
            .Build();

        return (OperationResult)await executor.ExecuteAsync(request);
    }

    public sealed class PingQuery
    {
        public string Ping() => "pong";
    }

    public sealed class Mutation
    {
        public static int Calls { get; set; }

        public bool Touch()
        {
            Calls++;
            return true;
        }
    }
}
