namespace Autheris.Tests.Unit.Mcp;

using System.Security.Claims;
using System.Text.Json;
using Autheris.Application.Catalog.Visibility;
using Autheris.Application.Governance.Caching;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Unit.Catalog;
using Autheris.Tests.Unit.TestSupport;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

public sealed class McpContainerCatalogTests
{
    private static readonly TenantId Tenant = Wp0.TenantA;
    private const string UserSid = "S-1-5-21-1001";

    private static ClaimsPrincipal User() => Wp0.User(UserSid, Tenant.Value);

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Tool_is_defined_as_a_read_only_catalog_tool_on_the_catalog_table()
    {
        var definition = McpDatasetTools.Definitions(null).Single(t => t.Name == McpDatasetTools.ListContainers);

        definition.TargetGraphQLOperation.ShouldBe(McpDatasetTools.ListContainers);
        McpDatasetTools.IsDatasetTool(McpDatasetTools.ListContainers).ShouldBeTrue();
        McpDatasetTools.TargetTable(McpDatasetTools.ListContainers, "{}").ShouldBe(McpDatasetTools.CatalogTable);
    }

    [Fact]
    public async Task Lists_tiles_with_access_state_and_no_table_or_column_names()
    {
        var domains = new FakeContainerDomainRepository();
        var grants = new FakeCatalogGrantRepository();
        var open = domains.Add(Tenant, "finance", CatalogVisibilityMode.Open);
        var closed = domains.Add(Tenant, "hr", CatalogVisibilityMode.OnRequest);
        var granted = domains.Add(Tenant, "legal", CatalogVisibilityMode.OnRequest);
        grants.Add(Tenant, new ContainerRef(ContainerKind.Domain, granted.Id), new Sid(UserSid));
        using var epochs = new GovernanceEpochProvider(domains, Options.Create(new GovernanceCacheSettings { EpochPollSeconds = 60 }));
        using var provider = new ContainerVisibilitySnapshotProvider(
            domains, grants, new Autheris.Tests.Unit.Governance.FakeConsentRepository(), new FakeGovernanceCapabilities(), epochs);

        var json = await McpContainerCatalog.ListAsync(new ContainerCatalogService(provider), Tenant, User(), Args("{}"));

        using var document = JsonDocument.Parse(json);
        var items = document.RootElement.GetProperty("containers").EnumerateArray().ToList();
        items.Select(i => i.GetProperty("name").GetString()).ShouldBe(["finance", "hr", "legal"]);
        items.Single(i => i.GetProperty("id").GetGuid() == open.Id).GetProperty("accessState").GetString().ShouldBe("OPEN");
        items.Single(i => i.GetProperty("id").GetGuid() == closed.Id).GetProperty("accessState").GetString().ShouldBe("NONE");
        items.Single(i => i.GetProperty("id").GetGuid() == granted.Id).GetProperty("accessState").GetString().ShouldBe("GRANTED");
        json.ShouldNotContain("columns");
        json.ShouldNotContain("tables");
    }

    [Fact]
    public async Task Kind_filter_and_limit_are_honoured_and_absent_service_answers_empty()
    {
        var domains = new FakeContainerDomainRepository();
        domains.Add(Tenant, "a", CatalogVisibilityMode.Open);
        domains.Add(Tenant, "b", CatalogVisibilityMode.Open);
        using var epochs = new GovernanceEpochProvider(domains, Options.Create(new GovernanceCacheSettings { EpochPollSeconds = 60 }));
        using var provider = new ContainerVisibilitySnapshotProvider(
            domains, new FakeCatalogGrantRepository(), new Autheris.Tests.Unit.Governance.FakeConsentRepository(), new FakeGovernanceCapabilities(), epochs);
        var service = new ContainerCatalogService(provider);

        var limited = await McpContainerCatalog.ListAsync(service, Tenant, User(), Args("""{"limit":1}"""));
        var products = await McpContainerCatalog.ListAsync(service, Tenant, User(), Args("""{"kind":"dataProduct"}"""));
        var none = await McpContainerCatalog.ListAsync(null, Tenant, User(), Args("{}"));

        JsonDocument.Parse(limited).RootElement.GetProperty("containers").GetArrayLength().ShouldBe(1);
        JsonDocument.Parse(products).RootElement.GetProperty("containers").GetArrayLength().ShouldBe(0);
        JsonDocument.Parse(none).RootElement.GetProperty("containers").GetArrayLength().ShouldBe(0);
    }
}
