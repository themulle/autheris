namespace Autheris.Tests.Unit;

using System;
using Autheris.Api.Extensions;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

public sealed class SqlEndpointLoaderStartupTests
{
    [Fact]
    public void LoadSqlEndpoints_WhenEnabledAndLoaderNotRegistered_FailsStartup()
    {
        using var sp = new ServiceCollection().BuildServiceProvider();
        var options = new GatewayOptions { SqlEndpoints = new SqlEndpointsOptions { Enabled = true } };

        Should.Throw<InvalidOperationException>(() => GatewayApplicationBuilderExtensions.LoadSqlEndpoints(sp, options));
    }

    [Fact]
    public void LoadSqlEndpoints_WhenDisabled_DoesNotRequireLoader()
    {
        using var sp = new ServiceCollection().BuildServiceProvider();
        var options = new GatewayOptions { SqlEndpoints = new SqlEndpointsOptions { Enabled = false } };

        Should.NotThrow(() => GatewayApplicationBuilderExtensions.LoadSqlEndpoints(sp, options));
    }
}
