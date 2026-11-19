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
public class ProductionSecurityInvariantsTests
{
    [Fact]
    public void ValidateSecurityInvariants_InProduction_EnableTestAuthHandler_ThrowsValidationException()
    {
        // Arrange
        var mockEnv = Substitute.For<IHostEnvironment>();
        mockEnv.EnvironmentName.Returns("Production");

        var options = new GatewayOptions
        {
            Authentication = new Autheris.Domain.Options.AuthenticationOptions
            {
                EnableTestAuthHandler = true
            }
        };

        // Act & Assert
        var ex = Should.Throw<ValidationException>(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, mockEnv));
        ex.Message.ShouldContain("EnableTestAuthHandler may be true ONLY in the Development environment");
    }

    [Fact]
    public void ValidateSecurityInvariants_InProduction_EnableTestAuthHandler_EvenWithAnonymous_ThrowsValidationException()
    {
        // Arrange
        var mockEnv = Substitute.For<IHostEnvironment>();
        mockEnv.EnvironmentName.Returns("Production");

        var options = new GatewayOptions
        {
            Authentication = new Autheris.Domain.Options.AuthenticationOptions
            {
                EnableTestAuthHandler = true
            },
            Insecure = new InsecureGettingStartedOptions
            {
                danger_allow_anonymous_access = true
            }
        };

        // Act & Assert: Finding 2 remediation ensures it is unconditionally forbidden
        var ex = Should.Throw<ValidationException>(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, mockEnv));
        ex.Message.ShouldContain("EnableTestAuthHandler may be true ONLY in the Development environment");
    }

    [Fact]
    public void ValidateSecurityInvariants_InProduction_AnonymousAccess_ThrowsValidationException()
    {
        // Arrange
        var mockEnv = Substitute.For<IHostEnvironment>();
        mockEnv.EnvironmentName.Returns("Production");

        var options = new GatewayOptions
        {
            Insecure = new InsecureGettingStartedOptions
            {
                danger_allow_anonymous_access = true
            }
        };

        // Act & Assert
        var ex = Should.Throw<ValidationException>(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, mockEnv));
        ex.Message.ShouldContain("danger_allow_anonymous_access may be true ONLY in the Development environment");
    }

    [Fact]
    public void ValidateSecurityInvariants_InProduction_UntrustedCertificatesAllowed_ThrowsValidationException()
    {
        var mockEnv = Substitute.For<IHostEnvironment>();
        mockEnv.EnvironmentName.Returns("Production");

        var options = new GatewayOptions
        {
            Insecure = new InsecureGettingStartedOptions
            {
                danger_allow_untrusted_certificates = true
            }
        };

        var ex = Should.Throw<ValidationException>(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, mockEnv));
        ex.Message.ShouldContain("DANGER:danger_allow_untrusted_certificates");
    }

    [Fact]
    public void ValidateSecurityInvariants_InProduction_DisableRateLimiting_ThrowsValidationException()
    {
        var mockEnv = Substitute.For<IHostEnvironment>();
        mockEnv.EnvironmentName.Returns("Production");

        var options = new GatewayOptions
        {
            Insecure = new InsecureGettingStartedOptions
            {
                warn_disable_rate_limiting = true
            }
        };

        // warn_disable_rate_limiting is classified as DANGER (property name kept for compatibility).
        var ex = Should.Throw<ValidationException>(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, mockEnv));
        ex.Message.ShouldContain("DANGER:warn_disable_rate_limiting");
    }

    [Fact]
    public void ValidateGatewayOptions_InProduction_WarnRelaxedQueryLimits_IsPermittedAsWarning()
    {
        var mockEnv = Substitute.For<IHostEnvironment>();
        mockEnv.EnvironmentName.Returns("Production");

        var options = new GatewayOptions
        {
            Insecure = new InsecureGettingStartedOptions
            {
                warn_relaxed_query_limits = true
            },
            DataMasking = new DataMaskingOptions { HmacSecretKeyVaultRef = "vault://keys/prod-hmac" }, Audit = new AuditOptions { ChainAnchorPath = "/var/anchor/audit-anchor.json" }
        };

        // WARN entries are permitted in Production (reported, not blocking).
        options.GetActiveWarnings().ShouldContain("WARN:warn_relaxed_query_limits");
        Should.NotThrow(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, mockEnv, _ => null));
    }

    [Fact]
    public void HIGH01_UntrustedCertificatesAllowed_InProduction_ThrowsValidationException()
    {
        var options = new GatewayOptions
        {
            Insecure = new InsecureGettingStartedOptions
            {
                danger_allow_untrusted_certificates = true
            }
        };

        var prodEnv = Substitute.For<IHostEnvironment>();
        prodEnv.EnvironmentName.Returns(Environments.Production);

        var ex = Should.Throw<ValidationException>(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, prodEnv));
        ex.Message.ShouldContain("danger_allow_untrusted_certificates");
    }

    [Fact]
    public void HIGH02_WildcardCors_InProduction_ThrowsValidationException()
    {
        var options = new GatewayOptions
        {
            DataMasking = new DataMaskingOptions
            {
                HmacSecretKeyVaultRef = "vault://keys/prod-hmac"
            },
            GraphQL = new GraphQLOptions
            {
                TrustedOrigins = ["*"]
            }
        };

        var prodEnv = Substitute.For<IHostEnvironment>();
        prodEnv.EnvironmentName.Returns(Environments.Production);

        var ex = Should.Throw<ValidationException>(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, prodEnv));
        ex.Message.ShouldContain("TrustedOrigins '*' (wildcard CORS) is prohibited outside the Development environment for security reasons (CSRF protection)");
    }

    [Fact]
    public void ValidateGatewayOptions_SeedDemoData_InProduction_ThrowsValidationException()
    {
        var options = new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions
            {
                SeedDemoData = true
            }
        };

        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Production");

        var ex = Should.Throw<ValidationException>(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, env));
        ex.Message.ShouldContain("SeedDemoData");
    }

    [Theory]
    [InlineData("http://portal.corp.local")]
    [InlineData("http://evil.com")]
    [InlineData("ftp://secure.corp.local")]
    public void M03_ValidateGatewayOptions_NonHttpsTrustedOriginInProduction_ThrowsValidationException(string nonHttpsOrigin)
    {
        var options = new GatewayOptions
        {
            DataMasking = new DataMaskingOptions
            {
                HmacSecretKeyVaultRef = "vault://keys/prod-hmac"
            },
            GraphQL = new GraphQLOptions
            {
                TrustedOrigins = [nonHttpsOrigin]
            }
        };

        var prodEnv = Substitute.For<IHostEnvironment>();
        prodEnv.EnvironmentName.Returns(Environments.Production);

        var ex = Should.Throw<ValidationException>(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, prodEnv));
        ex.Message.ShouldContain("Outside Development, TrustedOrigins must contain only HTTPS URLs");
    }

    [Fact]
    public void M03_ValidateGatewayOptions_HttpsTrustedOriginInProduction_Succeeds()
    {
        var options = new GatewayOptions
        {
            DataMasking = new DataMaskingOptions
            {
                HmacSecretKeyVaultRef = "vault://keys/prod-hmac"
            },
            GraphQL = new GraphQLOptions
            {
                TrustedOrigins = ["https://portal.corp.local", "https://app.corp.local"]
            }
        };

        var prodEnv = Substitute.For<IHostEnvironment>();
        prodEnv.EnvironmentName.Returns(Environments.Production);

        Should.NotThrow(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, prodEnv));
    }
}
