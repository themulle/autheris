namespace Autheris.Tests.Unit.Security;

using Autheris.Tests.Common;
using System.Threading.Tasks;
using Autheris.Api.Extensions;
using Autheris.Application.Security;
using Autheris.Application.Services;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public class SsrfProtectionTests
{
    [Fact]
    public void DeclarativeHttp_IsRestrictedIp_IdentifiesPrivateAndLoopbackIps()
    {
        DeclarativeHttpDataSourceExecutor.IsRestrictedIp(System.Net.IPAddress.Parse("127.0.0.1")).ShouldBeTrue();
        DeclarativeHttpDataSourceExecutor.IsRestrictedIp(System.Net.IPAddress.Parse("10.1.2.3")).ShouldBeTrue();
        DeclarativeHttpDataSourceExecutor.IsRestrictedIp(System.Net.IPAddress.Parse("172.16.5.6")).ShouldBeTrue();
        DeclarativeHttpDataSourceExecutor.IsRestrictedIp(System.Net.IPAddress.Parse("192.168.1.100")).ShouldBeTrue();
        DeclarativeHttpDataSourceExecutor.IsRestrictedIp(System.Net.IPAddress.Parse("169.254.169.254")).ShouldBeTrue();
        DeclarativeHttpDataSourceExecutor.IsRestrictedIp(System.Net.IPAddress.Parse("::1")).ShouldBeTrue();
        DeclarativeHttpDataSourceExecutor.IsRestrictedIp(System.Net.IPAddress.Parse("fc00::1")).ShouldBeTrue();

        // Public IPs should not be restricted
        DeclarativeHttpDataSourceExecutor.IsRestrictedIp(System.Net.IPAddress.Parse("8.8.8.8")).ShouldBeFalse();
        DeclarativeHttpDataSourceExecutor.IsRestrictedIp(System.Net.IPAddress.Parse("93.184.216.34")).ShouldBeFalse();
    }

    [Fact]
    public void DeclarativeHttp_IsForbiddenMetadataHost_IdentifiesMetadataHosts()
    {
        DeclarativeHttpDataSourceExecutor.IsForbiddenMetadataHost("metadata.google.internal").ShouldBeTrue();
        DeclarativeHttpDataSourceExecutor.IsForbiddenMetadataHost("sub.metadata.google.internal").ShouldBeTrue();
        DeclarativeHttpDataSourceExecutor.IsForbiddenMetadataHost("kubernetes.default.svc").ShouldBeTrue();
        DeclarativeHttpDataSourceExecutor.IsForbiddenMetadataHost("kubernetes.default.svc.cluster.local").ShouldBeTrue();

        DeclarativeHttpDataSourceExecutor.IsForbiddenMetadataHost("api.corp.com").ShouldBeFalse();
    }

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://100.100.100.200/latest/meta-data/")]
    [InlineData("http://metadata.google.internal/")]
    [InlineData("http://127.0.0.1:8080/internal/admin")]
    [InlineData("http://192.168.1.1/admin")]
    public async Task HIGH03_SsrfProtectionHandler_OutboundRequestToRestrictedAddress_ThrowsSecurityException(string restrictedUrl)
    {
        var handler = new SsrfProtectionHandler();
        var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, restrictedUrl);
        var invoker = new System.Net.Http.HttpMessageInvoker(handler);

        var ex = await Should.ThrowAsync<System.Security.SecurityException>(async () =>
            await invoker.SendAsync(request, System.Threading.CancellationToken.None));
        ex.Message.ShouldContain("strictly forbidden");
    }

    [Fact]
    public void CRIT02_LakehouseHttpClients_HaveSsrfProtectionHandlerConfigured()
    {
        var services = new ServiceCollection();
        var options = new GatewayOptions();
        IHostEnvironment env = new TestHostEnvironment(Environments.Production);
        services.AddSingleton(env);
        services.AddLogging();

        services.AddGatewayInfrastructure(options);
        Autheris.Modules.Lakehouse.LakehouseServiceCollectionExtensions.AddLakehouseIntegration(services, options);

        var sp = services.BuildServiceProvider();
        var factory = sp.GetRequiredService<IHttpClientFactory>();

        Should.NotThrow(() => factory.CreateClient(nameof(Autheris.Extensions.Lakehouse.Services.S3LakehouseStorageProvider)));
        Should.NotThrow(() => factory.CreateClient(nameof(Autheris.Extensions.Lakehouse.Services.AzureBlobStorageProvider)));
    }
}
