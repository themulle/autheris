using Autheris.Tests.Unit.TestSupport;
namespace Autheris.Tests.Unit.Security;

using Autheris.Tests.Common;
using Autheris.Domain.Security;
using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Extensions;
using Autheris.Api.Middleware;
using Autheris.Api.Security;
using NSubstitute.ExceptionExtensions;
using Autheris.Application.Governance;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Audit;
using Autheris.Application.Security;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Security;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Streaming.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public class GatewayAuthIssuerAudienceTests
{
    [Fact]
    public void M01_AddGatewayAuth_WithoutIdp_ValidatesIssuerAndAudienceFailClosed()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        var options = new GatewayOptions();
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(Environments.Development);

        services.AddGatewayAuth(options, env);
        var sp = services.BuildServiceProvider();

        var jwtOptions = sp.GetRequiredService<IOptionsMonitor<Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions>>()
            .Get(GatewayAuthSchemes.JwtBearer);

        // SEC M-02 (Review 2026-10-02): issuer/audience validation is always on; without configured
        // issuers/audiences no token can pass (fail-closed) instead of accepting any audience.
        jwtOptions.TokenValidationParameters.ValidateIssuer.ShouldBeTrue();
        jwtOptions.TokenValidationParameters.ValidIssuers.ShouldBeNull();
        jwtOptions.TokenValidationParameters.ValidateAudience.ShouldBeTrue();
        jwtOptions.TokenValidationParameters.ValidAudiences.ShouldBeNull();
    }

    [Fact]
    public void M01_AddGatewayAuth_WithEntraId_EnablesValidateIssuerAndAudience()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        var options = new GatewayOptions
        {
            Authentication = new Autheris.Domain.Options.AuthenticationOptions
            {
                EntraId = new EntraIdAuthOptions
                {
                    Enabled = true,
                    TenantId = "test-tenant-123",
                    Audience = "api://my-gateway"
                }
            }
        };
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(Environments.Development);

        services.AddGatewayAuth(options, env);
        var sp = services.BuildServiceProvider();

        var jwtOptions = sp.GetRequiredService<IOptionsMonitor<Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions>>()
            .Get(GatewayAuthSchemes.JwtBearer);

        jwtOptions.TokenValidationParameters.ValidateIssuer.ShouldBeTrue();
        jwtOptions.TokenValidationParameters.ValidIssuers.ShouldNotBeNull();
        jwtOptions.TokenValidationParameters.ValidIssuers.ShouldContain("https://login.microsoftonline.com/test-tenant-123/v2.0");
        jwtOptions.TokenValidationParameters.ValidateAudience.ShouldBeTrue();
        jwtOptions.TokenValidationParameters.ValidAudiences.ShouldNotBeNull();
        jwtOptions.TokenValidationParameters.ValidAudiences.ShouldContain("api://my-gateway");
    }
}
