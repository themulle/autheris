namespace Autheris.Extensions.Tests;

using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.Lineage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class OpenLineageClientTenantFilterTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.Created);
        }
    }

    private static (OpenLineageClient Client, CapturingHandler Handler) Create(params LineageNode[] nodes) => CreateWithDefault(null, nodes);

    private static (OpenLineageClient Client, CapturingHandler Handler) CreateWithDefault(string? defaultTenant, params LineageNode[] nodes)
    {
        var handler = new CapturingHandler();
        var store = Substitute.For<ILineageGraphStore>();
        store.GetAllNodes().Returns(nodes);
        var options = Options.Create(new GatewayOptions
        {
            Tenancy = new TenancyOptions { DefaultTenantId = defaultTenant },
            Catalog = new DataCatalogOptions { OpenLineageEndpoint = "http://openlineage.internal/api/v1/lineage" }
        });
        return (new OpenLineageClient(new HttpClient(handler), store, options, NullLogger<OpenLineageClient>.Instance), handler);
    }

    private static LineageNode Node(string id, string? tenant, params string[] downstream)
        => new(id, id, LineageNodeType.Table, downstream, TenantId: tenant);

    [Fact]
    public async Task Push_OtherTenantNodes_AreExcluded()
    {
        var (client, handler) = Create(Node("a.table", "tenant-a"), Node("b.table", "tenant-b"));

        (await client.PushLineageGraphAsync(new TenantId("tenant-a"))).ShouldBeTrue();

        handler.Body.ShouldNotBeNull();
        handler.Body.ShouldContain("a.table");
        handler.Body.ShouldNotContain("b.table");
    }

    [Fact]
    public async Task Push_UntenantedNodes_ExcludedForNonLegacyTenant()
    {
        var (client, handler) = Create(Node("legacy.table", null), Node("a.table", "tenant-a"));

        await client.PushLineageGraphAsync(new TenantId("tenant-a"));

        handler.Body.ShouldNotBeNull();
        handler.Body.ShouldContain("a.table");
        handler.Body.ShouldNotContain("legacy.table");
    }

    [Fact]
    public async Task Push_UntenantedNodes_IncludedForLegacyTenant()
    {
        var (client, handler) = CreateWithDefault("default-tenant", Node("legacy.table", null), Node("b.table", "tenant-b"));

        await client.PushLineageGraphAsync(Autheris.Tests.Common.TestTenants.Default);

        handler.Body.ShouldNotBeNull();
        handler.Body.ShouldContain("legacy.table");
        handler.Body.ShouldNotContain("b.table");
    }

    [Fact]
    public async Task Push_UntenantedNodes_BelongToTheConfiguredDefaultTenantOnly()
    {
        var (client, handler) = CreateWithDefault("tenant-d", Node("legacy.table", null), Node("b.table", "tenant-b"));

        await client.PushLineageGraphAsync(new TenantId("tenant-d"));
        handler.Body.ShouldNotBeNull();
        handler.Body.ShouldContain("legacy.table");
        handler.Body.ShouldNotContain("b.table");

        await client.PushLineageGraphAsync(Autheris.Tests.Common.TestTenants.Default);
        handler.Body.ShouldNotBeNull();
        handler.Body.ShouldNotContain("legacy.table");
    }

    [Fact]
    public async Task Push_UntenantedNodes_ExcludedForEveryTenantWithoutDefaultTenant()
    {
        var (client, handler) = Create(Node("legacy.table", null), Node("a.table", "tenant-a"));

        await client.PushLineageGraphAsync(Autheris.Tests.Common.TestTenants.Default);
        handler.Body.ShouldNotBeNull();
        handler.Body.ShouldNotContain("legacy.table");
    }

    [Fact]
    public async Task Push_DownstreamReferenceToOtherTenant_DoesNotMakeNodeAnInput()
    {
        var (client, handler) = Create(Node("a.table", "tenant-a", "b.table"), Node("b.table", "tenant-b"));

        await client.PushLineageGraphAsync(new TenantId("tenant-a"));

        handler.Body.ShouldNotBeNull();
        handler.Body.ShouldContain("\"outputs\":[{");
        handler.Body.ShouldContain("\"inputs\":[]");
    }
}
