namespace Autheris.Tests.Integration.Governance;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
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
/// Grant revoke authority over HTTP with the real stores (W3-11): owner group members of the grant's container, Data
/// Governance and the grantee revoke; IT only where it may decide; a grant of another tenant is never revoked (ClusterAdmin
/// included); catalog grants are revocable by owner members, governance and IT (SEC-1009-36); the capacity is audited.
/// </summary>
[Trait("Category", "Security")]
public sealed class GrantRevokeAuthorityApiIntegrationTests : IClassFixture<GrantRevokeAuthorityApiIntegrationTests.HostFixture>
{
    private const string Tenant = "tenant-default";
    private const string OwnerGroup = "S-1-5-21-9101-OWNERS";
    private const string Grantee = "S-1-5-21-9101-ALICE";

    private readonly WebApplicationFactory<Program> _factory;

    public GrantRevokeAuthorityApiIntegrationTests(HostFixture factory) => _factory = factory;

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
                ["Gateway:GovernanceDb:ConnectionString"] = GovernanceTestDatabase.Create("GrantRevokeAuthorityApiIntegrationTests"),
            });
        }
    }

    private sealed record Seeded(Guid DomainId, Consent Grant, CatalogVisibilityGrant CatalogGrant);

    private async Task<Seeded> SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var tenant = new TenantId(Tenant);
        var now = DateTimeOffset.UtcNow;
        var actor = new Sid("S-1-5-21-SEED");
        var id = new TableIdentifier("default", "sales", "orders_" + Guid.NewGuid().ToString("N")[..8]);
        var tableId = Guid.NewGuid();
        var stored = await sp.GetRequiredService<ITableMetadataRepository>().UpsertTableMetadataAsync(new TableMetadata
        {
            Identifier = id,
            Table = new Table { Id = tableId, SourceName = id.Domain, SchemaName = id.Schema, TableName = id.TableName, IsActive = true },
            Columns = [new TableColumn { TableId = tableId, ColumnName = "id", DataType = "int" }]
        });

        var domains = sp.GetRequiredService<IGovernanceDomainRepository>();
        var domain = await domains.UpsertAsync(
            new GovernanceDomain(Guid.NewGuid(), tenant, "revoke-" + Guid.NewGuid().ToString("N")[..8], null, null, new Sid(OwnerGroup), ProtectionClass.Internal, CatalogVisibilityMode.Open, false, 0, now, now),
            expectedVersion: 0,
            actor);
        await domains.AssignTablesAsync(tenant, domain.Domain!.Id, [stored.Table.Id], actor);

        var grant = await sp.GetRequiredService<IConsentRepository>().CreateConsentAsync(new Consent
        {
            TableId = stored.Table.Id,
            TableIdentifier = id,
            TenantId = tenant,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid(Grantee),
            Effect = ConsentEffect.Allow,
            ValidFrom = now.AddDays(-1),
            ValidTo = now.AddDays(30),
            CreatedBySid = actor
        });

        var catalogGrant = await sp.GetRequiredService<ICatalogVisibilityGrantRepository>().CreateAsync(new CatalogVisibilityGrant
        {
            TenantId = tenant,
            ContainerKind = ContainerKind.Domain,
            ContainerId = domain.Domain.Id,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid(Grantee),
            ValidFrom = now.AddDays(-1),
            ValidTo = now.AddDays(30),
            GrantedBySid = actor,
            ApproverCapacity = ApproverCapacity.Governance,
            CreatedAt = now
        });

        return new Seeded(domain.Domain.Id, grant, catalogGrant);
    }

    private HttpClient Client(string sid, string? groups = null, string? roles = null, string tenant = Tenant)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", sid);
        client.DefaultRequestHeaders.Add("X-Test-Tenant", tenant);
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

    private static async Task<HttpResponseMessage> RevokeAsync(HttpClient client, string route)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, route)
        {
            Content = new StringContent("{\"reason\":\"access no longer needed\"}", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        return await client.SendAsync(request);
    }

    private async Task<Consent?> ReloadAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IConsentRepository>().GetConsentByIdAsync(id);
    }

    [Fact]
    public async Task Owner_group_member_revokes_and_the_capacity_is_audited()
    {
        var seeded = await SeedAsync();
        using var owner = Client("S-1-5-21-9101-OWNER-MEMBER", OwnerGroup);

        var response = await RevokeAsync(owner, $"/api/v1/consent/grants/{seeded.Grant.Id:D}/revoke");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await ReloadAsync(seeded.Grant.Id)).ShouldNotBeNull().IsRevoked.ShouldBeTrue();

        using var scope = _factory.Services.CreateScope();
        var entries = await scope.ServiceProvider.GetRequiredService<IAuditLogRepository>()
            .QueryAuditLogsAsync(targetTable: seeded.Grant.TableIdentifier.ToString(), tenantId: new TenantId(Tenant));
        var revoked = entries.Where(e => e.EventType == AuditEventTypes.ConsentRevoked && (e.DetailsJson ?? string.Empty).Contains(seeded.Grant.Id.ToString("D"), StringComparison.OrdinalIgnoreCase)).ToList();
        revoked.ShouldNotBeEmpty();
        revoked.ShouldContain(e => e.DetailsJson!.Contains("\"Capacity\":\"ContainerOwner\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stranger_and_it_admin_cannot_revoke_a_grant_of_an_owned_container()
    {
        var seeded = await SeedAsync();
        using var stranger = Client("S-1-5-21-9101-MALLORY", "S-1-5-21-9101-NOBODY");
        using var it = Client("S-1-5-21-9101-IT", roles: "TenantAdmin");

        (await RevokeAsync(stranger, $"/api/v1/consent/grants/{seeded.Grant.Id:D}/revoke")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await RevokeAsync(it, $"/api/v1/consent/grants/{seeded.Grant.Id:D}/revoke")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ReloadAsync(seeded.Grant.Id)).ShouldNotBeNull().IsRevoked.ShouldBeFalse();
    }

    [Fact]
    public async Task Grantee_and_governance_revoke()
    {
        var first = await SeedAsync();
        var second = await SeedAsync();
        using var grantee = Client(Grantee);
        using var governance = Client("S-1-5-21-9101-DG", roles: "GovernanceAdmin");

        (await RevokeAsync(grantee, $"/api/v1/consent/grants/{first.Grant.Id:D}/revoke")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await RevokeAsync(governance, $"/api/v1/consent/grants/{second.Grant.Id:D}/revoke")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Cluster_admin_cannot_revoke_a_grant_of_another_tenant()
    {
        var seeded = await SeedAsync();
        using var root = Client("S-1-5-21-9101-ROOT", roles: "ClusterAdmin", tenant: "tenant-other");

        // The handler answers 404 (DecisionEndpointTests); a tenant gate in front of it may refuse earlier. Never 200.
        (await RevokeAsync(root, $"/api/v1/consent/grants/{seeded.Grant.Id:D}/revoke")).StatusCode
            .ShouldBeOneOf(HttpStatusCode.NotFound, HttpStatusCode.Forbidden);
        (await ReloadAsync(seeded.Grant.Id)).ShouldNotBeNull().IsRevoked.ShouldBeFalse();
    }

    [Fact]
    public async Task Catalog_grant_is_revocable_by_owner_member_but_not_by_a_stranger()
    {
        var seeded = await SeedAsync();
        using var stranger = Client("S-1-5-21-9101-MALLORY", "S-1-5-21-9101-NOBODY");
        using var owner = Client("S-1-5-21-9101-OWNER-MEMBER", OwnerGroup);
        var route = $"/api/v1/consent/catalog-grants/{seeded.CatalogGrant.Id:D}/revoke";

        (await RevokeAsync(stranger, route)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var response = await RevokeAsync(owner, route);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        (await ReloadCatalogGrantAsync(seeded.CatalogGrant.Id)).ShouldNotBeNull().IsRevoked.ShouldBeTrue();

        // The success entry is read back from the audit table, committed with the revoke.
        var entries = await AuditOfAsync(CatalogTarget(seeded));
        entries.ShouldContain(e => e.EventType == AuditEventTypes.ConsentRevoked &&
                                   e.DetailsJson!.Contains(seeded.CatalogGrant.Id.ToString("D"), StringComparison.OrdinalIgnoreCase) &&
                                   e.DetailsJson.Contains("\"Capacity\":\"ContainerOwner\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Failed_catalog_revoke_audit_write_leaves_the_grant_active()
    {
        var seeded = await SeedAsync();
        var target = "audit-failure:" + Guid.NewGuid().ToString("N");
        using var scope = _factory.Services.CreateScope();
        var writer = scope.ServiceProvider.GetRequiredService<ICatalogGrantRevocationWriter>();

        // PostgreSQL rejects a NUL character in text: the audit insert fails inside the revoke transaction.
        var broken = RevokedEntry(target, seeded.CatalogGrant.Id, traceId: "trace\0broken");
        await Should.ThrowAsync<Exception>(() => writer.RevokeWithAuditAsync(
            new TenantId(Tenant), seeded.CatalogGrant.Id, new Sid("S-1-5-21-9101-OWNER-MEMBER"), "access no longer needed", DateTimeOffset.UtcNow, broken));

        (await ReloadCatalogGrantAsync(seeded.CatalogGrant.Id)).ShouldNotBeNull().IsRevoked.ShouldBeFalse();
        (await AuditOfAsync(target)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Catalog_revoke_that_does_not_apply_writes_no_audit_entry()
    {
        var target = "not-applied:" + Guid.NewGuid().ToString("N");
        using var scope = _factory.Services.CreateScope();
        var writer = scope.ServiceProvider.GetRequiredService<ICatalogGrantRevocationWriter>();
        var missing = Guid.NewGuid();

        var revoked = await writer.RevokeWithAuditAsync(
            new TenantId(Tenant), missing, new Sid("S-1-5-21-9101-OWNER-MEMBER"), "access no longer needed", DateTimeOffset.UtcNow, RevokedEntry(target, missing));

        revoked.ShouldBeFalse();
        (await AuditOfAsync(target)).ShouldBeEmpty();
    }

    private static string CatalogTarget(Seeded seeded) => $"{ContainerKind.Domain.ToSqlString()}:{seeded.DomainId:D}";

    private static AuditLogEntry RevokedEntry(string target, Guid grantId, string? traceId = null) => new()
    {
        TenantId = new TenantId(Tenant),
        EventType = AuditEventTypes.ConsentRevoked,
        ActorSid = new Sid("S-1-5-21-9101-OWNER-MEMBER"),
        TargetTable = target,
        Decision = "REVOKED",
        TraceId = traceId ?? Guid.NewGuid().ToString("N"),
        DetailsJson = "{\"CatalogGrantId\":\"" + grantId.ToString("D") + "\",\"GrantKind\":\"CATALOG\"}"
    };

    private async Task<CatalogVisibilityGrant?> ReloadCatalogGrantAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ICatalogVisibilityGrantRepository>().GetByIdAsync(new TenantId(Tenant), id);
    }

    private async Task<IReadOnlyList<AuditLogEntry>> AuditOfAsync(string target)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IAuditLogRepository>()
            .QueryAuditLogsAsync(targetTable: target, tenantId: new TenantId(Tenant));
    }

    [Fact]
    public async Task Container_overview_lists_the_grant_and_the_catalog_grant_for_an_owner_member()
    {
        var seeded = await SeedAsync();
        using var owner = Client("S-1-5-21-9101-OWNER-MEMBER", OwnerGroup);

        var response = await owner.GetAsync($"/api/v1/governance/containers/domain/{seeded.DomainId:D}/grants");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain(seeded.Grant.Id.ToString("D"));
        body.ShouldContain(seeded.CatalogGrant.Id.ToString("D"));
        (await owner.GetAsync("/api/v1/consent/inbox?cursor=not-a-cursor")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
