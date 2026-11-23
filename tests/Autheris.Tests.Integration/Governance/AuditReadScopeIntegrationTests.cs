namespace Autheris.Tests.Integration.Governance;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Tests.Common.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

/// <summary>
/// Scoped audit read over the real stores: an owner-group member sees only entries of the objects of the containers they own,
/// an outsider is refused, and neither the <c>view</c> filter nor a cursor can widen the scope. Never reads another tenant.
/// </summary>
[Trait("Category", "Security")]
public sealed class AuditReadScopeIntegrationTests : IClassFixture<AuditReadScopeIntegrationTests.HostFixture>
{
    private const string Tenant = "tenant-default";
    private const string OwnerGroup = "S-1-5-21-9001-OWNERS";
    private const string OtherGroup = "S-1-5-21-9002-OTHERS";
    private const string OwnTable = "default.sales.orders";
    private const string ForeignTable = "default.hr.salaries";

    private readonly WebApplicationFactory<Program> _factory;

    public AuditReadScopeIntegrationTests(HostFixture factory)
    {
        _factory = factory;
    }

    public sealed class HostFixture : IntegrationHost<HostFixture>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            base.ConfigureWebHost(builder);
        }

        protected override void ConfigureHostConfiguration(IConfigurationBuilder config)
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Authentication:EnableTestAuthHandler"] = "true",
                ["Gateway:GovernanceDb:Provider"] = "PostgreSql",
                ["Gateway:GovernanceDb:ConnectionString"] = GovernanceTestDatabase.Create("AuditReadScopeIntegrationTests"),
            });
        }
    }

    private async Task SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var tenant = new TenantId(Tenant);
        var metadata = sp.GetRequiredService<ITableMetadataRepository>();
        var domains = sp.GetRequiredService<IGovernanceDomainRepository>();
        var audit = sp.GetRequiredService<IAuditLogRepository>();
        var now = DateTimeOffset.UtcNow;
        var actor = new Sid("S-1-5-21-SEED");

        foreach (var (name, group) in new[] { (OwnTable, OwnerGroup), (ForeignTable, OtherGroup) })
        {
            var id = TableIdentifier.Parse(name);
            var tableId = Guid.NewGuid();
            await metadata.UpsertTableMetadataAsync(new TableMetadata
            {
                Identifier = id,
                Table = new Table { Id = tableId, SourceName = id.Domain, SchemaName = id.Schema, TableName = id.TableName, IsActive = true },
                Columns = [new TableColumn { TableId = tableId, ColumnName = "id", DataType = "int" }]
            });
            var domain = await domains.UpsertAsync(
                new GovernanceDomain(Guid.NewGuid(), tenant, id.Schema + "-domain", null, null, new Sid(group), ProtectionClass.Internal, CatalogVisibilityMode.Open, false, 0, now, now),
                expectedVersion: 0,
                actor);
            await domains.AssignTablesAsync(tenant, domain.Domain!.Id, [tableId], actor);
        }

        foreach (var (table, tenantId, eventType) in new[]
        {
            (OwnTable, Tenant, AuditEventTypes.PermissionApproved),
            (OwnTable, Tenant, AuditEventTypes.SelfGrant),
            (ForeignTable, Tenant, AuditEventTypes.PermissionApproved),
            (OwnTable, "other-tenant", AuditEventTypes.PermissionApproved),
        })
        {
            await audit.RecordAuditEventAsync(new AuditLogEntry
            {
                TenantId = new TenantId(tenantId),
                TargetTable = table,
                EventType = eventType,
                ActorSid = actor,
                Decision = "ALLOW"
            });
        }
    }

    private HttpClient Client(string sid, string? groups = null, string? roles = null)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", sid);
        client.DefaultRequestHeaders.Add("X-Test-Tenant", Tenant);
        if (groups is not null)
        {
            client.DefaultRequestHeaders.Add("X-Test-Group-Sids", groups);
        }

        if (roles is not null)
        {
            client.DefaultRequestHeaders.Add("X-Test-Roles", roles);
        }

        return client;
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    [Fact]
    public async Task Owner_sees_only_rows_of_the_owned_container()
    {
        await SeedAsync();
        using var client = Client("S-1-5-21-OWNER-MEMBER", OwnerGroup);

        var response = await client.GetAsync("/api/v1/governance/audit");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var items = (await BodyAsync(response)).GetProperty("items").EnumerateArray().ToList();
        items.ShouldNotBeEmpty();
        items.ShouldAllBe(i => i.GetProperty("targetTable").GetString() == OwnTable && i.GetProperty("tenantId").GetString() == Tenant);
    }

    [Fact]
    public async Task Outsider_is_refused_and_naming_a_foreign_object_does_not_widen()
    {
        await SeedAsync();
        using var outsider = Client("S-1-5-21-OUTSIDER", "S-1-5-21-9999-NOBODY");
        using var owner = Client("S-1-5-21-OWNER-MEMBER", OwnerGroup);

        (await outsider.GetAsync("/api/v1/governance/audit")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await owner.GetAsync("/api/v1/governance/audit?targetTable=" + ForeignTable)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task View_and_cursor_cannot_widen_the_owner_scope()
    {
        await SeedAsync();
        using var owner = Client("S-1-5-21-OWNER-MEMBER", OwnerGroup);

        var decisions = await owner.GetAsync("/api/v1/governance/audit?view=decisions");
        decisions.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await BodyAsync(decisions)).GetProperty("items").EnumerateArray()
            .ShouldAllBe(i => i.GetProperty("targetTable").GetString() == OwnTable);

        var firstPage = await BodyAsync(await owner.GetAsync("/api/v1/governance/audit?pageSize=1"));
        var cursor = firstPage.GetProperty("nextCursor").GetString();
        cursor.ShouldNotBeNull();

        // a cursor minted for the owner scope is not accepted under another view
        (await owner.GetAsync($"/api/v1/governance/audit?pageSize=1&view=self-grants&cursor={Uri.EscapeDataString(cursor)}"))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // nor by another caller
        using var other = Client("S-1-5-21-OTHER-OWNER", OwnerGroup);
        (await other.GetAsync($"/api/v1/governance/audit?pageSize=1&cursor={Uri.EscapeDataString(cursor)}"))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
