namespace Autheris.Tests.Unit;

using System;
using System.Net;
using System.Net.Http;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Security;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;

/// <summary>
/// EGRESS: Allowlist for trusted on-premises integration targets (Jira/ServiceNow/OpenMetadata/OpenLineage in the
/// corporate network). Trusted hosts/networks bypass the private-address check only; metadata, loopback and link-local
/// targets stay blocked and HTTPS remains mandatory outside Development.
/// </summary>
public sealed class EgressAllowlistTests
{
    private sealed class OkHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private static IHostEnvironment Env(string name)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(name);
        return env;
    }

    private static (HttpMessageInvoker Invoker, OkHandler Inner) CreateInvoker(
        string environment,
        string[]? hosts = null,
        string[]? networks = null,
        string? integration = EgressIntegrations.Itsm)
    {
        var options = Options.Create(new GatewayOptions
        {
            Egress = new OutboundEgressOptions
            {
                TrustedInternalHosts = [.. hosts ?? []],
                TrustedInternalNetworks = [.. networks ?? []]
            }
        });

        var inner = new OkHandler();
        // SEC E-02: the allowlist only applies to handler instances created for an allowlist-capable integration.
        var handler = new SsrfProtectionHandler(Env(environment), options, integration) { InnerHandler = inner };
        return (new HttpMessageInvoker(handler), inner);
    }

    [Fact]
    public async Task EGRESS_PrivateAddressInTrustedNetwork_IsAllowedInProduction()
    {
        var (invoker, inner) = CreateInvoker(Environments.Production, networks: ["10.20.0.0/16"]);

        using var response = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://10.20.1.5/rest/api/2/issue"), CancellationToken.None);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        inner.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task EGRESS_PrivateAddressOutsideTrustedNetwork_IsStillBlocked()
    {
        var (invoker, inner) = CreateInvoker(Environments.Production, networks: ["10.20.0.0/16"]);

        await Should.ThrowAsync<SecurityException>(async () =>
            await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://192.168.1.10/api"), CancellationToken.None));
        inner.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task EGRESS_WithoutAllowlist_PrivateAddressIsBlocked()
    {
        var (invoker, inner) = CreateInvoker(Environments.Production);

        await Should.ThrowAsync<SecurityException>(async () =>
            await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://10.20.1.5/api"), CancellationToken.None));
        inner.Calls.ShouldBe(0);
    }

    [Theory]
    [InlineData("https://127.0.0.1/api", "127.0.0.0/8")]
    [InlineData("https://169.254.169.254/latest/meta-data/", "169.254.0.0/16")]
    public async Task EGRESS_LoopbackAndLinkLocal_StayBlockedEvenWhenTrusted(string url, string network)
    {
        var (invoker, inner) = CreateInvoker(Environments.Production, networks: [network]);

        await Should.ThrowAsync<SecurityException>(async () =>
            await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, url), CancellationToken.None));
        inner.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task EGRESS_TrustedInternalTarget_RequiresHttpsOutsideDevelopment()
    {
        var (invoker, inner) = CreateInvoker(Environments.Production, networks: ["10.20.0.0/16"]);

        var ex = await Should.ThrowAsync<SecurityException>(async () =>
            await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://10.20.1.5/api"), CancellationToken.None));
        ex.Message.ShouldContain("HTTP");
        inner.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task EGRESS_TrustedInternalTarget_AllowsHttpInDevelopment()
    {
        var (invoker, inner) = CreateInvoker(Environments.Development, networks: ["10.20.0.0/16"]);

        using var response = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://10.20.1.5/api"), CancellationToken.None);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        inner.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task EGRESS_TrustedHostName_MetadataHostIsNeverTrusted()
    {
        var (invoker, inner) = CreateInvoker(Environments.Production, hosts: ["metadata.google.internal"]);

        await Should.ThrowAsync<SecurityException>(async () =>
            await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://metadata.google.internal/computeMetadata/v1/"), CancellationToken.None));
        inner.Calls.ShouldBe(0);
    }

    [Fact]
    public void EGRESS_DefaultOptions_HaveEmptyAllowlist()
    {
        var options = new GatewayOptions();

        options.Egress.TrustedInternalHosts.ShouldBeEmpty();
        options.Egress.TrustedInternalNetworks.ShouldBeEmpty();
        options.Egress.TrustedIntegrations.ShouldBeNull();
        string.Join(",", options.Egress.GetEffectiveTrustedIntegrations()).ShouldBe("Itsm,Catalog,OpenMetadata,Lineage");
    }

    [Fact]
    public async Task EGRESS_HandlerWithoutIntegrationName_DoesNotUseAllowlist()
    {
        var (invoker, inner) = CreateInvoker(Environments.Production, networks: ["10.20.0.0/16"], integration: null);

        await Should.ThrowAsync<SecurityException>(async () =>
            await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://10.20.1.5/api"), CancellationToken.None));
        inner.Calls.ShouldBe(0);
    }
}
