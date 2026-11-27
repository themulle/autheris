namespace Autheris.Tests.Unit.Databricks.Provisioning;

using System.IO;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Autheris.Api.Endpoints;
using Autheris.Application.Governance.Provisioning;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Autheris.Tests.Unit.Databricks.Provisioning.UcProvisioningScenario;

/// <summary>
/// Issue #1122: the provisioning field of grants and the administrators' status endpoint. Grants on other sources keep no field;
/// other tenants and pattern grants are unsupported; the status shows counts only and only to the provisioning tenant's admins.
/// </summary>
[Trait("Category", "Security")]
public sealed class ProvisioningEndpointsTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-27T10:00:00Z");
    private readonly FakeUcStore _store = new();

    [Fact]
    public async Task Grants_on_provisioned_sources_carry_their_feedback_and_others_carry_none()
    {
        var provisioned = Grant(Now);
        var pending = Grant(Now);
        var otherSource = new Consent { TableIdentifier = new TableIdentifier("pg", "sales", "orders"), TenantId = Tenant };
        var otherTenant = new Consent { TableIdentifier = TableId(), TenantId = new TenantId("beta") };
        var pattern = new Consent { ObjectPattern = Source + ".sales.*", DataSource = Source, TenantId = Tenant };
        _store.Feedback[(Tenant, provisioned.Id)] = new UcGrantFeedback(Tenant, provisioned.Id, Source, UcGrantState.Provisioned, null, Now, Now);

        var result = await ProvisioningEndpoints.LookupAsync([provisioned, pending, otherSource, otherTenant, pattern], Options(), _store, CancellationToken.None);

        result[provisioned.Id].ShouldBe(new GrantProvisioningDto("databricks:dbx", "PROVISIONED", null, Now));
        result[pending.Id].State.ShouldBe("PENDING");
        result.ContainsKey(otherSource.Id).ShouldBeFalse();
        result[otherTenant.Id].ReasonCode.ShouldBe(UcReasonCodes.TenantNotProvisioned);
        result[pattern.Id].ReasonCode.ShouldBe(UcReasonCodes.PatternGrant);
    }

    [Fact]
    public void Grant_without_provisioning_serializes_exactly_as_before()
    {
        var dto = ConsentApiEndpoints.ToGrantDto(Grant(Now));

        JsonSerializer.Serialize(dto).ShouldNotContain("Provisioning");
        JsonSerializer.Serialize(dto with { Provisioning = new GrantProvisioningDto("databricks:dbx", "PENDING", null, null) }).ShouldContain("Provisioning");
    }

    [Fact]
    public async Task Status_shows_counts_to_the_provisioning_tenant_admin()
    {
        _store.Ledger[(Source, new UcPrivilegeKey(UcSecurableType.Table, "main.sales.orders", GroupOid, "SELECT"))] = new UcLedgerEntry
        {
            DataSource = Source, SecurableType = UcSecurableType.Table, FullName = "main.sales.orders", Principal = "analysts",
            PrincipalExternalId = GroupOid, Privilege = "SELECT", State = UcLedgerState.RevokePending, Attempts = 4, LastErrorCode = "UC_FORBIDDEN"
        };

        var (status, body) = await ExecuteAsync(await ProvisioningEndpoints.GetStatusAsync(Source, Context("acme", "GovernanceAdmin"), Monitor(), _store, CancellationToken.None));

        status.ShouldBe(StatusCodes.Status200OK);
        body.ShouldContain("\"overdueRevokes\":1");
        body.ShouldContain("REVOKE_PENDING");
        body.ShouldContain("UC_FORBIDDEN");
        body.ShouldNotContain("analysts");
        body.ShouldNotContain(GroupOid);
    }

    [Theory]
    [InlineData("beta", "GovernanceAdmin", Source, StatusCodes.Status404NotFound)]
    [InlineData("acme", "GovernanceAdmin", "unknown", StatusCodes.Status404NotFound)]
    [InlineData("acme", "Reader", Source, StatusCodes.Status403Forbidden)]
    public async Task Status_is_hidden_from_other_tenants_unknown_sources_and_non_admins(string tenant, string role, string source, int expected)
    {
        var (status, _) = await ExecuteAsync(await ProvisioningEndpoints.GetStatusAsync(source, Context(tenant, role), Monitor(), _store, CancellationToken.None));

        status.ShouldBe(expected);
    }

    private static StaticOptionsMonitor<GatewayOptions> Monitor() => new(Options());

    private static HttpContext Context(string tenant, string role)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.PrimarySid, "S-1-5-21-ADMIN"), new Claim("tenant_id", tenant), new Claim(ClaimTypes.Role, role)], "TestAuth"));
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
            User = principal,
            Response = { Body = new MemoryStream() }
        };
        context.Items[SecurityPrincipalContext.TenantIdItemKey] = new TenantId(tenant);
        context.Items[SecurityPrincipalContext.ItemKey] = SecurityPrincipalContext.FromPrincipal(principal, overrideTenant: new TenantId(tenant));
        return context;
    }

    private static async Task<(int Status, string Body)> ExecuteAsync(IResult result)
    {
        var context = Context("acme", "GovernanceAdmin");
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        return (context.Response.StatusCode, Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray()));
    }
}
