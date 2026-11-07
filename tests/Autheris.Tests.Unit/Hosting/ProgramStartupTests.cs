namespace Autheris.Tests.Unit.Hosting;

using System;
using System.Linq;
using System.Net.NetworkInformation;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

public sealed class ProgramStartupTests
{
    [Fact]
    public void No_embedded_cache_server_started()
    {
        var services = new ServiceCollection();
        var options = new GatewayOptions();

        services.AddAutherisStorage(options);

        // 1. No hosted service or singleton of any Garnet type
        var descriptors = services.Where(d =>
            (d.ServiceType.FullName?.Contains("Garnet", StringComparison.OrdinalIgnoreCase) ?? false) ||
            (d.ImplementationType?.FullName?.Contains("Garnet", StringComparison.OrdinalIgnoreCase) ?? false)).ToList();

        descriptors.ShouldBeEmpty("No Garnet service or hosted service should be registered in DI.");

        // 2. No listening port for Garnet default port (3278)
        var ipGlobalProperties = IPGlobalProperties.GetIPGlobalProperties();
        var tcpListeners = ipGlobalProperties.GetActiveTcpListeners();
        tcpListeners.Any(endpoint => endpoint.Port == 3278).ShouldBeFalse("Port 3278 should not have an embedded Garnet listener.");
    }
}
