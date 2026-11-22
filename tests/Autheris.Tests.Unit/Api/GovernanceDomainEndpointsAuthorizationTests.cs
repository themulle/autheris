using System.Security.Claims;
using Autheris.Api.Endpoints;
using Autheris.Api.Security;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Domains;
using Autheris.Application.Governance.Policy;
using Autheris.Application.Security;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Tests.Common.Fakes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Api;

[Trait("Category", "Security")]
public sealed class GovernanceDomainEndpointsAuthorizationTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly Sid GovSid = new("S-1-5-21-1001-10");
    private static readonly Sid ItSid = new("S-1-5-21-1001-20");
    private static readonly Sid OwnerSid = new("S-1-5-21-1001-30");
    private static readonly Sid OwnerGroupSid = new("S-1-5-21-1001-512");
    private static readonly DateTimeOffset T0 = new(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);

    private static HttpContext HttpFor(string sid, string role, IReadOnlySet<Sid>? groups = null, string? ifMatch = null)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, sid),
            new("tenant_id", TenantA.Value),
            new(ClaimTypes.Role, role)
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
        var context = new DefaultHttpContext { User = principal };
        var sec = SecurityPrincipalContext.FromPrincipal(principal, overrideTenant: TenantA) with
        {
            GroupSids = groups ?? new HashSet<Sid>(),
            GroupResolutionStatus = GroupResolutionStatus.Complete
        };
        context.Items[SecurityPrincipalContext.ItemKey] = sec;
        context.Items[SecurityPrincipalContext.TenantIdItemKey] = TenantA;
        if (ifMatch is not null)
        {
            context.Request.Headers.IfMatch = ifMatch;
        }

        return context;
    }

    private sealed class EmptyPolicyRepo : IAccessPolicySettingsRepository
    {
        public Task<StoredTenantAccessPolicy?> GetAsync(TenantId tenant, CancellationToken ct = default) =>
            Task.FromResult<StoredTenantAccessPolicy?>(null);
        public Task<AccessPolicyWriteResult> UpsertAsync(TenantId tenant, TenantAccessPolicyUpdate settings, int expectedVersion, Sid actor, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> DeleteIfVersionAsync(TenantId tenant, int expectedVersion, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private static (GovernanceDomainService Service, IGovernanceDomainRepository Domains, InMemoryTableMetadataRepository Tables) BuildService(
        int maxTablesPerAssignment = 1000)
    {
        var domains = Substitute.For<IGovernanceDomainRepository>();
        var tables = new InMemoryTableMetadataRepository();
        var consents = Substitute.For<IConsentRepository>();
        var audit = new InMemoryAuditWriter();
        var policyProvider = new AccessPolicySettingsProvider(
            new EmptyPolicyRepo(),
            Options.Create(new AccessPolicyOptions { MaxTablesPerAssignment = maxTablesPerAssignment }),
            audit,
            new FakeTimeProvider(T0));

        var caps = new GovernanceCapabilities(domainRepository: domains);
        var service = new GovernanceDomainService(domains, tables, consents, audit, caps, policyProvider, timeProvider: new FakeTimeProvider(T0));
        return (service, domains, tables);
    }

    [Fact]
    public async Task Table_list_bounded()
    {
        var (service, domains, _) = BuildService(maxTablesPerAssignment: 3);
        var domainId = Guid.NewGuid();
        domains.GetAsync(TenantA, domainId, Arg.Any<CancellationToken>())
            .Returns(new GovernanceDomain(domainId, TenantA, "Sales", null, null, null, ProtectionClass.Confidential, CatalogVisibilityMode.OnRequest, false, 1, T0, T0));

        var overCap = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToList();
        var res = await GovernanceDomainEndpoints.AssignTablesAsync(
            HttpFor(GovSid.Value, "GovernanceAdmin"),
            domainId,
            new GovernanceDomainEndpoints.AssignTablesRequest(overCap),
            service,
            CancellationToken.None);

        (res as IStatusCodeHttpResult)?.StatusCode.ShouldBe(StatusCodes.Status422UnprocessableEntity);
        await domains.DidNotReceiveWithAnyArgs().AssignTablesAsync(default, default, default!, default, default);
    }

    [Fact]
    public async Task It_refused_and_unassigned_protected_and_owner_allowed_on_protection()
    {
        var (service, domains, tables) = BuildService();
        var domainId = Guid.NewGuid();
        var tableId = Guid.NewGuid();
        var tableRef = new TableIdentifier("crm", "public", "customers");

        domains.GetAsync(TenantA, domainId, Arg.Any<CancellationToken>())
            .Returns(new GovernanceDomain(domainId, TenantA, "CRM", null, null, OwnerGroupSid, ProtectionClass.Confidential, CatalogVisibilityMode.OnRequest, false, 1, T0, T0));
        domains.GetContainerForTableAsync(TenantA, tableId, Arg.Any<CancellationToken>())
            .Returns(new ContainerDefaults(new ContainerRef(ContainerKind.Domain, domainId), OwnerGroupSid, ProtectionClass.Confidential, CatalogVisibilityMode.OnRequest, IsValid: true));
        domains.GetProtectionAsync(TenantA, tableId, Arg.Any<CancellationToken>())
            .Returns((null, new Dictionary<string, ObjectProtection>()));

        tables.Add(new TableMetadata
        {
            Identifier = tableRef,
            OwnerTenantId = TenantA.Value,
            Table = new Table { Id = tableId, SourceName = "crm", SchemaName = "public", TableName = "customers", IsActive = true },
            Columns = [new TableColumn { ColumnName = "email" }]
        });

        var itHttp = HttpFor(ItSid.Value, "TenantAdmin", ifMatch: "\"1\"");
        (await GovernanceDomainEndpoints.CreateDomainAsync(itHttp, new GovernanceDomainEndpoints.CreateDomainRequest("Finance"), service, CancellationToken.None) as IStatusCodeHttpResult)
            ?.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        (await GovernanceDomainEndpoints.AssignOwnerGroupAsync(itHttp, domainId, new GovernanceDomainEndpoints.AssignOwnerGroupRequest(OwnerGroupSid.Value), service, CancellationToken.None) as IStatusCodeHttpResult)
            ?.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        (await GovernanceDomainEndpoints.SetTableProtectionAsync(itHttp, tableRef.ToString(), new GovernanceDomainEndpoints.SetProtectionRequest("INTERNAL"), service, CancellationToken.None) as IStatusCodeHttpResult)
            ?.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        (await GovernanceDomainEndpoints.SetColumnProtectionAsync(itHttp, tableRef.ToString(), "email", new GovernanceDomainEndpoints.SetProtectionRequest("SECRET", "PII"), service, CancellationToken.None) as IStatusCodeHttpResult)
            ?.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);

        var govHttp = HttpFor(GovSid.Value, "GovernanceAdmin", ifMatch: "\"1\"");
        (await GovernanceDomainEndpoints.DeleteDomainAsync(govHttp, Guid.Empty, service, CancellationToken.None) as IStatusCodeHttpResult)
            ?.StatusCode.ShouldBe(StatusCodes.Status422UnprocessableEntity);
        (await GovernanceDomainEndpoints.UpdateDomainAsync(HttpFor(GovSid.Value, "GovernanceAdmin"), domainId, new GovernanceDomainEndpoints.UpdateDomainRequest("X"), service, CancellationToken.None) as IStatusCodeHttpResult)
            ?.StatusCode.ShouldBe(StatusCodes.Status428PreconditionRequired);

        var ownerHttp = HttpFor(OwnerSid.Value, "Consumer", new HashSet<Sid> { OwnerGroupSid });
        (await GovernanceDomainEndpoints.CreateDomainAsync(ownerHttp, new GovernanceDomainEndpoints.CreateDomainRequest("New"), service, CancellationToken.None) as IStatusCodeHttpResult)
            ?.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        (await GovernanceDomainEndpoints.SetColumnProtectionAsync(ownerHttp, tableRef.ToString(), "email", new GovernanceDomainEndpoints.SetProtectionRequest("SECRET", "PII_EMAIL"), service, CancellationToken.None) as IStatusCodeHttpResult)
            ?.StatusCode.ShouldBe(StatusCodes.Status200OK);
    }

    [Fact]
    public void All_domain_and_protection_routes_carry_auth_and_audit_metadata()
    {
        using var routes = new TestEndpointRouteBuilder(permissiveServices: true);
        routes.MapGovernanceDomainEndpoints();

        var endpoints = routes.DataSources
            .SelectMany(d => d.Endpoints)
            .OfType<RouteEndpoint>()
            .ToList();

        endpoints.Count.ShouldBe(9);
        endpoints.ShouldAllBe(e => e.Metadata.GetMetadata<IAuthorizeData>() != null);
        endpoints.ShouldAllBe(e => e.Metadata.GetMetadata<AuditPolicy>() != null);
    }
}
