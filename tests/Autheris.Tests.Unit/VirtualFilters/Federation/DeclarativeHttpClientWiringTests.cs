namespace Autheris.Tests.Unit.VirtualFilters.Federation;

using System;
using System.Net;
using System.IO;
using System.Net.Http;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Application.Services;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// F-GOV-14 CR-03 (ST-31, C-10): the <c>DeclarativeHttp</c> client of the production registration carries the SSRF
/// handler and the response size cap. Only the primary (network) handler is replaced by a stub, so removing either
/// handler registration fails these tests.
/// </summary>
public sealed class DeclarativeHttpClientWiringTests
{
    private const long Cap = 1024;

    private sealed class StubPrimaryHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(respond(request));
        }
    }

    /// <summary>A body without a declared length (chunked), as a hostile upstream would send it.</summary>
    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes, 0, bytes.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private static ServiceProvider Build(StubPrimaryHandler primary)
    {
        var options = new GatewayOptions
        {
            // CR-09: the cap is a global HTTP data source option, not a WebSQL cross-source setting.
            DataSources = new SqlDataSourceOptions { Http = new HttpDataSourceOptions { MaxResponseBytes = Cap } }
        };
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Production");
        var services = new ServiceCollection();
        services.AddSingleton(env);
        services.AddSingleton(Options.Create(options));
        services.AddLogging();
        services.AddAutherisSqlEngine(options);
        services.AddHttpClient(DeclarativeHttpDataSourceExecutor.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => primary);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task OversizedResponse_IsRejected_RowLimitExceeded()
    {
        var primary = new StubPrimaryHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            // No Content-Length: the stream cap has to stop it while it is read.
            Content = new UnknownLengthContent(Encoding.UTF8.GetBytes(new string('x', (int)Cap * 4)))
        });
        await using var provider = Build(primary);
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(DeclarativeHttpDataSourceExecutor.HttpClientName);

        using var response = await client.GetAsync("https://93.184.216.34/customers", HttpCompletionOption.ResponseHeadersRead);
        var ex = await Should.ThrowAsync<GatewaySecurityException>(() => response.Content.ReadAsStringAsync());

        ex.ErrorCode.ShouldBe("ROW_LIMIT_EXCEEDED");
    }

    [Fact]
    public async Task ResponseWithADeclaredOversizedLength_IsRejectedBeforeAnythingIsRead()
    {
        var primary = new StubPrimaryHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', (int)Cap * 4)) });
        await using var provider = Build(primary);
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(DeclarativeHttpDataSourceExecutor.HttpClientName);

        var ex = await Should.ThrowAsync<GatewaySecurityException>(() => client.GetAsync("https://93.184.216.34/customers"));

        ex.ErrorCode.ShouldBe("ROW_LIMIT_EXCEEDED");
    }

    [Fact]
    public async Task TheCrossSourceStagingBudget_DoesNotConfigureTheClientCap()
    {
        var options = new GatewayOptions { WebSql = new WebSqlOptions { CrossSource = new CrossSourceOptions { MaxStagedBytesPerTable = 100 } } };
        var primary = new StubPrimaryHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', 5000)) });
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Production");
        var services = new ServiceCollection();
        services.AddSingleton(env);
        services.AddSingleton(Options.Create(options));
        services.AddLogging();
        services.AddAutherisSqlEngine(options);
        services.AddHttpClient(DeclarativeHttpDataSourceExecutor.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => primary);
        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(DeclarativeHttpDataSourceExecutor.HttpClientName);

        (await client.GetStringAsync("https://93.184.216.34/customers")).Length.ShouldBe(5000);
    }

    [Fact]
    public async Task ResponseWithinTheCap_PassesThrough()
    {
        var primary = new StubPrimaryHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") });
        await using var provider = Build(primary);
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(DeclarativeHttpDataSourceExecutor.HttpClientName);

        (await client.GetStringAsync("https://93.184.216.34/customers")).ShouldBe("[]");
    }

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("https://127.0.0.1/customers")]
    public async Task RequestToAForbiddenDestination_IsBlockedBySsrfProtection_NeverReachesTheNetworkHandler(string url)
    {
        var primary = new StubPrimaryHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        await using var provider = Build(primary);
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(DeclarativeHttpDataSourceExecutor.HttpClientName);

        await Should.ThrowAsync<SecurityException>(() => client.GetAsync(url));

        primary.Calls.ShouldBe(0);
    }
}
