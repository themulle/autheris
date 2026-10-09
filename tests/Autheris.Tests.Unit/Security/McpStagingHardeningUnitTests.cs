namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Autheris.Api.Extensions;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class McpStagingHardeningUnitTests
{
    [Fact]
    public void McpOptions_HasDeveloperCorsPropertiesWithSafeDefaults()
    {
        var options = new McpOptions();
        options.EnableDeveloperCors.ShouldBeFalse();
        options.DeveloperCorsOrigins.ShouldNotBeNull();
        options.DeveloperCorsOrigins.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Testing")]
    public void ValidateGatewayOptions_WhenDeveloperCorsEnabledOutsideDevelopment_ThrowsValidationException(string environmentName)
    {
        var env = new HostingEnvironment { EnvironmentName = environmentName };
        var options = new GatewayOptions
        {
            Mcp = new McpOptions
            {
                Enabled = true,
                EnableDeveloperCors = true
            }
        };

        var ex = Should.Throw<ValidationException>(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, env));

        ex.Message.ShouldContain("Mcp.EnableDeveloperCors is strictly prohibited outside Development");
    }

    [Fact]
    public void ValidateGatewayOptions_WhenDeveloperCorsEnabledInDevelopment_DoesNotThrow()
    {
        var env = new HostingEnvironment { EnvironmentName = Environments.Development };
        var options = new GatewayOptions
        {
            Mcp = new McpOptions
            {
                Enabled = true,
                EnableDeveloperCors = true
            }
        };

        Should.NotThrow(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, env));
    }
}
