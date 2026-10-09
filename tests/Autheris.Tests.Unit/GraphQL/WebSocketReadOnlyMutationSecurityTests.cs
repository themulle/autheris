namespace Autheris.Tests.Unit.GraphQL;

using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.GraphQL.Interceptors;
using Autheris.GraphQL.Subscriptions;
using HotChocolate.AspNetCore.Subscriptions;
using HotChocolate.AspNetCore.Subscriptions.Protocols;
using HotChocolate.Execution;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class WebSocketReadOnlyMutationSecurityTests
{
    private readonly SymmetricSecurityKey _signingKey = new(Encoding.UTF8.GetBytes("a-very-secret-test-key-of-at-least-256-bits-length!"));

    private (JwtSocketTokenValidator Validator, GatewayOptions Options, IIdentitySubjectResolver SubjectResolver) CreateValidator(
        List<string>? readOnlyScopes = null,
        AppOnlyTokenAccess appOnlyTokens = AppOnlyTokenAccess.Deny)
    {
        var jwtOptions = new JwtBearerOptions
        {
            TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = _signingKey,
                ValidateIssuer = false,
                ValidateAudience = false,
                ValidateLifetime = false
            }
        };

        var jwtOptionsMonitor = Substitute.For<IOptionsMonitor<JwtBearerOptions>>();
        jwtOptionsMonitor.Get(Arg.Any<string>()).Returns(jwtOptions);

        var options = new GatewayOptions
        {
            Authentication = new AuthenticationOptions
            {
                EntraId = new EntraIdAuthOptions
                {
                    Enabled = true,
                    ReadOnlyScopes = readOnlyScopes ?? ["Agent.Read", "api://app/Agent.Read"],
                    AppOnlyTokens = appOnlyTokens
                }
            }
        };

        var gatewayOptions = Options.Create(options);
        var env = Substitute.For<IHostEnvironment>();
        var subjectResolver = Substitute.For<IIdentitySubjectResolver>();

        // Default: delegated user subject
        subjectResolver.ResolveSubject(Arg.Any<ClaimsPrincipal>())
            .Returns(callInfo =>
            {
                var principal = callInfo.Arg<ClaimsPrincipal>();
                var isAppOnly = !principal.HasClaim(c => c.Type == "scp" || c.Type == "http://schemas.microsoft.com/identity/claims/scope");
                return new SubjectIdentity(
                    principal.FindFirst("sub")?.Value ?? "user-1",
                    isAppOnly ? SubjectType.ServicePrincipal : SubjectType.User,
                    IdentityProviderType.EntraId,
                    "user@example.com",
                    TenantId.LegacySingleTenant);
            });

        var validator = new JwtSocketTokenValidator(
            jwtOptionsMonitor,
            gatewayOptions,
            env,
            NullLogger<JwtSocketTokenValidator>.Instance,
            subjectResolver);

        return (validator, options, subjectResolver);
    }

    private string CreateJwt(IEnumerable<Claim> claims)
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            SigningCredentials = new SigningCredentials(_signingKey, SecurityAlgorithms.HmacSha256)
        };

        var handler = new JwtSecurityTokenHandler();
        var token = handler.CreateToken(descriptor);
        return handler.WriteToken(token);
    }

    [Fact]
    public async Task JwtSocketTokenValidator_WithReadOnlyScope_MarksPrincipalAsReadOnly()
    {
        var (validator, _, _) = CreateValidator(readOnlyScopes: ["Agent.Read"]);

        var token = CreateJwt([
            new Claim("sub", "S-1-5-21-agent-001"),
            new Claim("scp", "Agent.Read")
        ]);

        var (isValid, principal) = await validator.ValidateTokenAsync(token);

        isValid.ShouldBeTrue();
        principal.ShouldNotBeNull();
        principal.IsReadOnly().ShouldBeTrue();
    }

    [Fact]
    public async Task JwtSocketTokenValidator_WithAppOnlyToken_AndAppOnlyTokensReadOnly_MarksPrincipalAsReadOnly()
    {
        var (validator, _, _) = CreateValidator(appOnlyTokens: AppOnlyTokenAccess.ReadOnly);

        var token = CreateJwt([
            new Claim("sub", "spn-app-guid-1234"),
            new Claim("idtyp", "app"),
            new Claim("roles", "Reader")
        ]);

        var (isValid, principal) = await validator.ValidateTokenAsync(token);

        isValid.ShouldBeTrue();
        principal.ShouldNotBeNull();
        principal.IsReadOnly().ShouldBeTrue();
    }

    [Fact]
    public async Task JwtSocketTokenValidator_WithAppOnlyToken_AndAppOnlyTokensDeny_RejectsToken()
    {
        var (validator, _, _) = CreateValidator(appOnlyTokens: AppOnlyTokenAccess.Deny);

        var token = CreateJwt([
            new Claim("sub", "spn-app-guid-1234"),
            new Claim("idtyp", "app")
        ]);

        var (isValid, principal) = await validator.ValidateTokenAsync(token);

        isValid.ShouldBeFalse();
        principal.ShouldBeNull();
    }

    [Fact]
    public async Task WebSocketAuthInterceptor_InheritsReadOnlyFromUpgradeHandshake()
    {
        // Setup upgrade user with ReadOnly mode
        var upgradeIdentity = new ClaimsIdentity("Bearer");
        upgradeIdentity.AddClaim(new Claim(ClaimTypes.PrimarySid, "S-1-5-21-user-1"));
        TokenAccessScope.MarkReadOnly(upgradeIdentity);
        var httpUser = new ClaimsPrincipal(upgradeIdentity);

        var httpContext = new DefaultHttpContext { User = httpUser };

        var tokenIdentity = new ClaimsIdentity("Bearer");
        tokenIdentity.AddClaim(new Claim(ClaimTypes.PrimarySid, "S-1-5-21-user-1"));
        var tokenPrincipal = new ClaimsPrincipal(tokenIdentity);
        tokenPrincipal.IsReadOnly().ShouldBeFalse(); // Token itself wasn't marked

        var validator = Substitute.For<ISocketTokenValidator>();
        validator.ValidateTokenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult((true, (ClaimsPrincipal?)tokenPrincipal)));

        var interceptor = new WebSocketAuthInterceptor(
            NullLogger<WebSocketAuthInterceptor>.Instance,
            validator);

        var session = Substitute.For<ISocketSession>();
        session.Connection.HttpContext.Returns(httpContext);

        using var doc = JsonDocument.Parse("{\"authorization\":\"Bearer token123\"}");
        var payload = Substitute.For<IOperationMessagePayload>();
        payload.Payload.Returns(doc.RootElement.Clone());

        var status = await interceptor.OnConnectAsync(session, payload);

        status.Accepted.ShouldBeTrue();
        httpContext.User.IsReadOnly().ShouldBeTrue();
    }

    [Fact]
    public async Task ReadOnlyOperationMiddleware_BlocksMutation_WhenPrincipalIsReadOnly()
    {
        var readOnlyIdentity = new ClaimsIdentity("Test");
        readOnlyIdentity.AddClaim(new Claim(ClaimTypes.NameIdentifier, "user-1"));
        TokenAccessScope.MarkReadOnly(readOnlyIdentity);
        var principal = new ClaimsPrincipal(readOnlyIdentity);

        var executor = await new ServiceCollection()
            .AddGraphQLServer()
            .AddQueryType<DummyQuery>()
            .AddMutationType<DummyMutation>()
            .UseDocumentParser()
            .UseDocumentValidation()
            .UseRequest<ReadOnlyOperationMiddleware>()
            .UseOperationResolver()
            .UseOperationVariableCoercion()
            .UseOperationExecution()
            .BuildRequestExecutorAsync();

        var request = OperationRequestBuilder.New()
            .SetDocument("mutation { touch }")
            .AddGlobalState(nameof(ClaimsPrincipal), principal)
            .Build();

        var result = (OperationResult)await executor.ExecuteAsync(request);

        result.Errors.ShouldNotBeNull();
        result.Errors.Count.ShouldBeGreaterThan(0);
        result.Errors[0].Code.ShouldBe("READ_ONLY_TOKEN");
        DummyMutation.Calls.ShouldBe(0);
    }

    public sealed class DummyQuery
    {
        public string Ping() => "pong";
    }

    public sealed class DummyMutation
    {
        public static int Calls { get; set; }
        public string Touch()
        {
            Calls++;
            return "touched";
        }
    }
}
