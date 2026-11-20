namespace Autheris.Tests.Unit.Security;

using Autheris.Tests.Common;
using System.ComponentModel.DataAnnotations;
using Autheris.Api.Extensions;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
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
        IHostEnvironment mockEnv = new TestHostEnvironment("Production");

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
        IHostEnvironment mockEnv = new TestHostEnvironment("Production");

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
        IHostEnvironment mockEnv = new TestHostEnvironment("Production");

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
        IHostEnvironment mockEnv = new TestHostEnvironment("Production");

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
        IHostEnvironment mockEnv = new TestHostEnvironment("Production");

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
        IHostEnvironment mockEnv = new TestHostEnvironment("Production");

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

        IHostEnvironment prodEnv = new TestHostEnvironment(Environments.Production);

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

        IHostEnvironment prodEnv = new TestHostEnvironment(Environments.Production);

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

        IHostEnvironment env = new TestHostEnvironment("Production");

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

        IHostEnvironment prodEnv = new TestHostEnvironment(Environments.Production);

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

        IHostEnvironment prodEnv = new TestHostEnvironment(Environments.Production);

        Should.NotThrow(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, prodEnv));
    }
}
