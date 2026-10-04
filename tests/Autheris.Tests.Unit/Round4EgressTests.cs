namespace Autheris.Tests.Unit;

using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Extensions;
using Autheris.Application.Security;
using Autheris.Domain.Options;
using Autheris.Extensions.DataCatalog;
using Autheris.Extensions.Lakehouse.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Round 4 (Nachprüfung) – egress allowlist and outbound HTTP hardening:
/// E-01 startup validation / complete never-allowed ranges in the trusted path / fail-closed DNS / IDN + IPv6 host handling,
/// E-02 allowlist per integration (Lakehouse never) and S3 host regex (EX-12),
/// E-03 hardened primary handler (no redirects, connect-time check with IP pinning) for all integration clients,
/// E-04 Alation TOKEN header cannot follow a redirect.
/// </summary>
public sealed class Round4EgressTests
{
    private static readonly Func<string, string?> NoEnvironmentVariables = _ => null;

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

    private static DataMaskingOptions ProdMasking() => new() { HmacSecretKeyVaultRef = "vault://keys/prod-hmac" };

    private static OutboundEgressOptions Egress(string[]? hosts = null, string[]? networks = null, string[]? integrations = null) => new()
    {
        TrustedInternalHosts = [.. hosts ?? []],
        TrustedInternalNetworks = [.. networks ?? []],
        TrustedIntegrations = integrations is null ? null : [.. integrations]
    };

    private static Func<string, CancellationToken, Task<IPAddress[]>> Resolver(string host, params string[] addresses) =>
        (name, _) => string.Equals(name, host, StringComparison.Ordinal)
            ? Task.FromResult(addresses.Select(a => IPAddress.Parse(a)).ToArray())
            : Task.FromException<IPAddress[]>(new SocketException((int)SocketError.HostNotFound));

    private static (HttpMessageInvoker Invoker, OkHandler Inner) CreateInvoker(
        OutboundEgressOptions egress,
        string? integration = EgressIntegrations.Itsm,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolver = null,
        string environment = "Production")
    {
        var options = Options.Create(new GatewayOptions { Egress = egress });
        var inner = new OkHandler();
        var handler = new SsrfProtectionHandler(Env(environment), options, integration, resolver) { InnerHandler = inner };
        return (new HttpMessageInvoker(handler), inner);
    }

    // =========================================================================
    // E-01: startup validation
    // =========================================================================

    [Theory]
    [InlineData("0.0.0.0/0")]
    [InlineData("::/0")]
    [InlineData("16.0.0.0/4")]
    [InlineData("127.0.0.0/8")]
    [InlineData("100.0.0.0/8")]
    [InlineData("169.254.0.0/16")]
    [InlineData("224.0.0.0/8")]
    [InlineData("::ffff:0:0/96")]
    [InlineData("fe80::/10")]
    [InlineData("fd00:ec2::/32")]
    [InlineData("fd00:ec2::254/128")]
    [InlineData("100.100.100.200/32")]
    [InlineData("168.63.129.16/32")]
    [InlineData("::fff0:0:0/92")]
    [InlineData("fc00::/7")]
    [InlineData("not-a-cidr")]
    [InlineData("10.20.0.0")]
    [InlineData("10.20.0.0/40")]
    public void E01_InvalidOrTooBroadTrustedNetwork_AbortsStartup_InEveryEnvironment(string cidr)
    {
        var egress = Egress(networks: [cidr]);
        EgressAllowlist.Validate(egress).ShouldNotBeEmpty();

        foreach (var environment in new[] { Environments.Development, Environments.Production })
        {
            var options = new GatewayOptions { Egress = egress, DataMasking = ProdMasking() };
            var ex = Should.Throw<ValidationException>(() =>
                GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, Env(environment), NoEnvironmentVariables));
            ex.Message.ShouldContain("Egress");
        }
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("metadata.google.internal")]
    [InlineData("kubernetes.default.svc")]
    [InlineData("169.254.169.254")]
    [InlineData("100.100.100.200")]
    [InlineData("*.corp.local")]
    [InlineData("https://jira.corp.local")]
    [InlineData("jira.corp.local:8443")]
    public void E01_ForbiddenOrMalformedTrustedHost_IsRejected(string host)
    {
        EgressAllowlist.Validate(Egress(hosts: [host])).ShouldNotBeEmpty();
    }

    [Fact]
    public void E01_LegitimateAllowlist_PassesValidation_AndIsReportedAsWarn()
    {
        var options = new GatewayOptions
        {
            Egress = Egress(
                hosts: ["jira.corp.local", "bücher.corp.example"],
                networks: ["10.20.0.0/16", "172.16.0.0/12", "fd12:3456:789a::/48", "2001:db8:1::/48"]),
            DataMasking = ProdMasking()
        };

        EgressAllowlist.Validate(options.Egress).ShouldBeEmpty();
        Should.NotThrow(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, Env(Environments.Production), NoEnvironmentVariables));

        options.IsEgressAllowlistActive.ShouldBeTrue();
        options.GetActiveWarnings().ShouldContain(w => w.StartsWith("WARN:egress_trusted_internal_allowlist", StringComparison.Ordinal));
        options.HasAnyDangerBypassActive.ShouldBeFalse();
        new GatewayOptions().IsEgressAllowlistActive.ShouldBeFalse();
    }

    [Fact]
    public void E01_InvalidEntries_AreIgnoredByTheHandler_DefenseInDepth()
    {
        // Even if the startup validation were bypassed (e.g. options built in code), an over-broad network is not applied.
        var allowlist = EgressAllowlist.Create(Egress(networks: ["0.0.0.0/0", "::/0", "127.0.0.0/8"]), EgressIntegrations.Itsm);

        allowlist.IsEmpty.ShouldBeTrue();
    }

    // =========================================================================
    // E-01: never-allowed targets in the trusted path
    // =========================================================================

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("0.1.2.3")]
    [InlineData("100.100.100.200")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("fd00:ec2::254")]
    [InlineData("::")]
    [InlineData("168.63.129.16")]
    public async Task E01_TrustedHostResolvingToNeverAllowedAddress_IsBlocked(string address)
    {
        var (invoker, inner) = CreateInvoker(
            Egress(hosts: ["jira.corp.local"], networks: ["10.20.0.0/16"]),
            resolver: Resolver("jira.corp.local", address));

        await Should.ThrowAsync<SecurityException>(async () =>
            await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://jira.corp.local/rest/api/2/issue"), CancellationToken.None));
        inner.Calls.ShouldBe(0);
    }

    [Theory]
    [InlineData("https://0.0.0.0/api")]
    [InlineData("https://100.100.100.200/latest/meta-data/")]
    [InlineData("https://224.0.0.1/api")]
    [InlineData("https://239.255.255.250/api")]
    public async Task E01_NeverAllowedLiteral_IsBlocked_WithAndWithoutAllowlist(string url)
    {
        var (withAllowlist, inner1) = CreateInvoker(Egress(networks: ["10.20.0.0/16"]));
        var (withoutAllowlist, inner2) = CreateInvoker(Egress());

        await Should.ThrowAsync<SecurityException>(async () =>
            await withAllowlist.SendAsync(new HttpRequestMessage(HttpMethod.Get, url), CancellationToken.None));
        await Should.ThrowAsync<SecurityException>(async () =>
            await withoutAllowlist.SendAsync(new HttpRequestMessage(HttpMethod.Get, url), CancellationToken.None));
        inner1.Calls.ShouldBe(0);
        inner2.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task E01_TrustedHost_DnsFailure_FailsClosed()
    {
        var (invoker, inner) = CreateInvoker(
            Egress(hosts: ["jira.corp.local"]),
            resolver: (_, _) => Task.FromException<IPAddress[]>(new SocketException((int)SocketError.HostNotFound)));

        var ex = await Should.ThrowAsync<SecurityException>(async () =>
            await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://jira.corp.local/api"), CancellationToken.None));
        ex.Message.ShouldContain("fail-closed");
        inner.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task E01_TrustedHost_EmptyDnsAnswer_FailsClosed()
    {
        var (invoker, inner) = CreateInvoker(
            Egress(hosts: ["jira.corp.local"]),
            resolver: (_, _) => Task.FromResult(Array.Empty<IPAddress>()));

        await Should.ThrowAsync<SecurityException>(async () =>
            await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://jira.corp.local/api"), CancellationToken.None));
        inner.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task E01_TrustedHost_ResolvingToPrivateAddress_IsAllowed()
    {
        var (invoker, inner) = CreateInvoker(
            Egress(hosts: ["jira.corp.local"]),
            resolver: Resolver("jira.corp.local", "10.20.1.5"));

        using var response = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://jira.corp.local/api"), CancellationToken.None);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        inner.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task E01_IdnTrustedHost_IsComparedAsPunycode()
    {
        var (invoker, inner) = CreateInvoker(
            Egress(hosts: ["bücher.corp.example"]),
            resolver: Resolver("xn--bcher-kva.corp.example", "10.20.1.5"));

        using var response = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://bücher.corp.example/api"), CancellationToken.None);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        inner.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task E01_BracketedIpv6Literal_InTrustedUlaNetwork_IsAllowed()
    {
        var (invoker, inner) = CreateInvoker(Egress(networks: ["fd12:3456:789a::/48"]));

        using var response = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://[fd12:3456:789a::10]/api"), CancellationToken.None);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        inner.Calls.ShouldBe(1);
    }

    // =========================================================================
    // E-02: allowlist per integration, Lakehouse never; S3 host regex
    // =========================================================================

    [Fact]
    public async Task E02_LakehouseClient_BlocksPrivateTarget_DespiteAllowlist()
    {
        var egress = Egress(hosts: ["internal-lb.corp.local"], networks: ["10.0.0.0/8"]);
        var (lakehouse, lakehouseInner) = CreateInvoker(egress, EgressIntegrations.Lakehouse, Resolver("internal-lb.corp.local", "10.1.2.3"));
        var (itsm, itsmInner) = CreateInvoker(egress, EgressIntegrations.Itsm, Resolver("internal-lb.corp.local", "10.1.2.3"));

        await Should.ThrowAsync<SecurityException>(async () =>
            await lakehouse.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://10.1.2.3/bucket/key"), CancellationToken.None));
        lakehouseInner.Calls.ShouldBe(0);

        using var response = await itsm.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://10.1.2.3/api"), CancellationToken.None);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        itsmInner.Calls.ShouldBe(1);
    }

    [Fact]
    public void E02_Lakehouse_CanNeverBeConfiguredAsTrustedIntegration()
    {
        var egress = Egress(networks: ["10.0.0.0/8"], integrations: ["Lakehouse", "Itsm"]);

        EgressAllowlist.IsIntegrationPermitted(egress, EgressIntegrations.Lakehouse).ShouldBeFalse();
        EgressAllowlist.Create(egress, EgressIntegrations.Lakehouse).IsEmpty.ShouldBeTrue();
        EgressAllowlist.Validate(egress).ShouldContain(e => e.Contains("Lakehouse", StringComparison.Ordinal));
        EgressAllowlist.Validate(Egress(integrations: ["Itsmm"])).ShouldNotBeEmpty();
    }

    [Fact]
    public async Task E02_IntegrationNotListedInTrustedIntegrations_DoesNotUseAllowlist()
    {
        var egress = Egress(networks: ["10.20.0.0/16"], integrations: ["Catalog"]);
        var (itsm, itsmInner) = CreateInvoker(egress, EgressIntegrations.Itsm);
        var (catalog, catalogInner) = CreateInvoker(egress, EgressIntegrations.Catalog);

        await Should.ThrowAsync<SecurityException>(async () =>
            await itsm.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://10.20.1.5/api"), CancellationToken.None));
        itsmInner.Calls.ShouldBe(0);

        using var response = await catalog.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://10.20.1.5/api"), CancellationToken.None);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        catalogInner.Calls.ShouldBe(1);
    }

    [Theory]
    [InlineData("s3.amazonaws.com")]
    [InlineData("s3.eu-central-1.amazonaws.com")]
    [InlineData("s3-us-west-2.amazonaws.com")]
    [InlineData("my-bucket.s3.eu-central-1.amazonaws.com")]
    [InlineData("my.dotted.bucket.s3.us-east-1.amazonaws.com")]
    [InlineData("my-bucket.s3-accelerate.amazonaws.com")]
    [InlineData("my-bucket.s3-accelerate.dualstack.amazonaws.com")]
    [InlineData("s3.dualstack.ap-northeast-1.amazonaws.com")]
    [InlineData("s3-fips.us-gov-west-1.amazonaws.com")]
    [InlineData("S3.EU-WEST-1.AMAZONAWS.COM")]
    public void E02_S3HostRegex_AcceptsGenuineS3Endpoints(string host)
    {
        S3LakehouseStorageProvider.IsAmazonS3Host(host).ShouldBeTrue(host);
    }

    [Theory]
    [InlineData("internal-orders-123456.eu-central-1.elb.amazonaws.com")]
    [InlineData("s3-123456.eu-central-1.elb.amazonaws.com")]
    [InlineData("ec2-10-0-0-1.eu-central-1.compute.amazonaws.com")]
    [InlineData("ip-10-0-0-1.ec2.internal")]
    [InlineData("abc123.execute-api.eu-central-1.amazonaws.com")]
    [InlineData("my-db.abc.eu-central-1.rds.amazonaws.com")]
    [InlineData("amazonaws.com")]
    [InlineData("s3.amazonaws.com.evil.net")]
    [InlineData("evils3.amazonaws.com")]
    public void E02_S3HostRegex_RejectsOtherAwsServiceHosts(string host)
    {
        S3LakehouseStorageProvider.IsAmazonS3Host(host).ShouldBeFalse(host);
    }

    [Fact]
    public void E02_S3ManifestUrlPointingToInternalElb_IsRejected()
    {
        var options = Options.Create(new GatewayOptions());
        using var httpClient = new HttpClient(new OkHandler());
        var provider = new S3LakehouseStorageProvider(httpClient, options, NullLogger<S3LakehouseStorageProvider>.Instance);

        Should.Throw<SecurityException>(() =>
            provider.ResolveS3Uri("https://internal-orders-123456.eu-central-1.elb.amazonaws.com/bucket/key.json", out _, out _));

        // positive: virtual-hosted style S3 URL keeps working
        provider.ResolveS3Uri("https://my-bucket.s3.eu-central-1.amazonaws.com/tables/x.json", out var bucket, out var key).Host
            .ShouldBe("my-bucket.s3.eu-central-1.amazonaws.com");
        bucket.ShouldBe("my-bucket");
        key.ShouldBe("tables/x.json");
    }

    // =========================================================================
    // E-03: connect-time decision (IP pinning) and hardened primary handler
    // =========================================================================

    [Fact]
    public void E03_ConnectDecision_PublicAddress_IsPermitted()
    {
        var result = SecureOutboundHttp.SelectPermittedAddresses("api.example.com", [IPAddress.Parse("93.184.216.34")], false, EgressAllowlist.Empty);

        result.Single().ShouldBe(IPAddress.Parse("93.184.216.34"));
    }

    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData("192.168.1.1")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.100.100.200")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.251")]
    [InlineData("::ffff:10.0.0.5")]
    [InlineData("fd00:ec2::254")]
    public void E03_ConnectDecision_RebindingToRestrictedAddress_IsBlocked(string address)
    {
        // DNS answer changed between the handler check and the connect (rebinding) or a mixed answer: every candidate is checked.
        Should.Throw<SecurityException>(() => SecureOutboundHttp.SelectPermittedAddresses(
            "api.example.com",
            [IPAddress.Parse("93.184.216.34"), IPAddress.Parse(address)],
            false,
            EgressAllowlist.Empty));
    }

    [Fact]
    public void E03_ConnectDecision_PrivateAddress_OnlyWithAllowlistOfThatIntegration()
    {
        var egress = Egress(networks: ["10.20.0.0/16"]);
        var candidates = new[] { IPAddress.Parse("::ffff:10.20.1.5") };

        var permitted = SecureOutboundHttp.SelectPermittedAddresses("jira.corp.local", candidates, false, EgressAllowlist.Create(egress, EgressIntegrations.Itsm));
        permitted.Single().ShouldBe(IPAddress.Parse("10.20.1.5"));

        Should.Throw<SecurityException>(() =>
            SecureOutboundHttp.SelectPermittedAddresses("lake.corp.local", candidates, false, EgressAllowlist.Create(egress, EgressIntegrations.Lakehouse)));
        Should.Throw<SecurityException>(() =>
            SecureOutboundHttp.SelectPermittedAddresses("jira.corp.local", [IPAddress.Parse("10.30.0.1")], false, EgressAllowlist.Create(egress, EgressIntegrations.Itsm)));
    }

    [Fact]
    public void E03_ConnectDecision_Development_AllowsPrivate_ButNeverMetadataOrLoopback()
    {
        SecureOutboundHttp.SelectPermittedAddresses("openmetadata", [IPAddress.Parse("172.18.0.4")], true, EgressAllowlist.Empty)
            .Single().ShouldBe(IPAddress.Parse("172.18.0.4"));

        Should.Throw<SecurityException>(() =>
            SecureOutboundHttp.SelectPermittedAddresses("evil.example", [IPAddress.Parse("169.254.169.254")], true, EgressAllowlist.Empty));
        Should.Throw<SecurityException>(() =>
            SecureOutboundHttp.SelectPermittedAddresses("evil.example", [IPAddress.Parse("127.0.0.1")], true, EgressAllowlist.Empty));
        Should.Throw<SecurityException>(() =>
            SecureOutboundHttp.SelectPermittedAddresses("metadata.google.internal", [IPAddress.Parse("93.184.216.34")], true, EgressAllowlist.Empty));
    }

    [Fact]
    public async Task E03_ConnectResolution_DnsFailureOrEmptyAnswer_FailsClosed()
    {
        await Should.ThrowAsync<SocketException>(() => SecureOutboundHttp.ResolvePermittedAddressesAsync(
            "jira.corp.local",
            false,
            EgressAllowlist.Empty,
            (_, _) => Task.FromException<IPAddress[]>(new SocketException((int)SocketError.HostNotFound)),
            CancellationToken.None));

        await Should.ThrowAsync<SecurityException>(() => SecureOutboundHttp.ResolvePermittedAddressesAsync(
            "jira.corp.local",
            false,
            EgressAllowlist.Empty,
            (_, _) => Task.FromResult(Array.Empty<IPAddress>()),
            CancellationToken.None));
    }

    [Fact]
    public async Task E03_ConnectResolution_IpLiteral_DoesNotUseDns()
    {
        var called = false;
        var result = await SecureOutboundHttp.ResolvePermittedAddressesAsync(
            "[2001:db8::1]",
            false,
            EgressAllowlist.Empty,
            (_, _) =>
            {
                called = true;
                return Task.FromResult(Array.Empty<IPAddress>());
            },
            CancellationToken.None);

        called.ShouldBeFalse();
        result.Single().ShouldBe(IPAddress.Parse("2001:db8::1"));
    }

    [Fact]
    public void E03_PrimaryHandler_DoesNotFollowRedirects_AndPinsConnections()
    {
        using var handler = SecureOutboundHttp.CreatePrimaryHandler(false, EgressAllowlist.Empty, false, (_, _) => Task.FromResult(Array.Empty<IPAddress>()));

        handler.AllowAutoRedirect.ShouldBeFalse();
        handler.ConnectCallback.ShouldNotBeNull();
        handler.SslOptions.RemoteCertificateValidationCallback.ShouldBeNull();
    }

    [Fact]
    public void E03_SystemProxyDetection_RequestHostIsNeverTreatedAsProxy()
    {
        SecureOutboundHttp.IsSystemProxyEndpoint(new DnsEndPoint("jira.corp.local", 443), new Uri("https://jira.corp.local/api")).ShouldBeFalse();
        SecureOutboundHttp.IsSystemProxyEndpoint(new DnsEndPoint("jira.corp.local", 443), null).ShouldBeFalse();
    }

    private static ServiceProvider BuildGatewayServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Env(Environments.Production));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddLogging();
        var options = new GatewayOptions();
        services.AddSingleton(Options.Create(options));
        services.AddGatewayInfrastructure(options);
        return services.BuildServiceProvider();
    }

    private static HttpMessageHandler GetPrimaryHandler(HttpMessageHandler handler)
    {
        var current = handler;
        while (current is DelegatingHandler delegating && delegating.InnerHandler is { } inner)
        {
            current = inner;
        }

        return current;
    }

    [Theory]
    [InlineData("IOpenMetadataClient")]
    [InlineData("ServiceNowTableApiClient")]
    [InlineData("JiraCloudRestClient")]
    [InlineData("OpenLineageClient")]
    [InlineData("PurviewDataCatalogClient")]
    [InlineData("CollibraDataCatalogClient")]
    [InlineData("AlationCatalogClient")]
    [InlineData("OpenJev")]
    [InlineData("S3LakehouseStorageProvider")]
    [InlineData("AzureBlobStorageProvider")]
    [InlineData("IAuditWormExportService")]
    public void E03_AllIntegrationClients_UseHardenedPrimaryHandler(string clientName)
    {
        var provider = BuildGatewayServices();
        var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(clientName);

        var primary = GetPrimaryHandler(handler).ShouldBeOfType<SocketsHttpHandler>(clientName);
        primary.AllowAutoRedirect.ShouldBeFalse(clientName);
        primary.ConnectCallback.ShouldNotBeNull(clientName);
    }

    // =========================================================================
    // E-04: Alation TOKEN header cannot follow a redirect
    // =========================================================================

    [Fact]
    public void E04_AlationClient_DoesNotFollowRedirects()
    {
        var provider = BuildGatewayServices();
        var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(nameof(AlationCatalogClient));

        // Without automatic redirects a 3xx is returned to the Alation client (EnsureSuccessStatusCode -> error) and the
        // custom TOKEN header is never re-sent to the redirect target.
        GetPrimaryHandler(handler).ShouldBeOfType<SocketsHttpHandler>().AllowAutoRedirect.ShouldBeFalse();
    }
}
