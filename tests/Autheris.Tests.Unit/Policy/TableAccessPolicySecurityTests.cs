namespace Autheris.Tests.Unit.Policy;

using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Policy.Services;
using Autheris.Application.Services;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Tests.Unit.Governance;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class TableAccessPolicySecurityTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [Trait("Category", "Security")]
    public async Task SEC_36_SP3a_02_Write_decision_denies_without_write_grant(bool allowDml, bool consentBypass, bool clusterAdmin)
    {
        var options = Options.Create(new GatewayOptions
        {
            WebSql = new WebSqlOptions { AllowDml = allowDml },
            Insecure = new InsecureGettingStartedOptions { danger_bypass_consent_checks = consentBypass }
        });
        var pdp = new AccessDecisionPoint(
            options,
            new ConsentResolutionService(),
            new FakeConsentRepository(),
            NullConsentCacheService.Instance,
            mandatoryFilters: NullMandatoryRowFilterResolver.Instance);

        var claims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, "S-1-5-21-U1"),
            new(ClaimTypes.NameIdentifier, "u1"),
            new("tenant_id", "t1"),
            new(ClaimTypes.Role, "Writer")
        };
        if (clusterAdmin)
        {
            claims.Add(new Claim(ClaimTypes.Role, "ClusterAdmin"));
        }

        var user = Autheris.Tests.Unit.TestSupport.PipelinePrincipals.MarkComplete(new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")));
        var id = new TableIdentifier("default", "main", "orders");
        var metadata = new TableMetadata
        {
            Identifier = id,
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }]
        };
        var subject = SecurityPrincipalContext.FromPrincipal(user, new TenantId("t1"));

        var update = await pdp.DecideAsync(new AccessRequest(subject, metadata, AccessOperation.Update, AccessTransport.WebSql, Columns: ["id"]));
        var insert = await pdp.DecideAsync(new AccessRequest(subject, metadata, AccessOperation.Insert, AccessTransport.WebSql, Columns: ["id"]));
        var delete = await pdp.DecideAsync(new AccessRequest(subject, metadata, AccessOperation.Delete, AccessTransport.WebSql));

        update.IsAllowed.ShouldBeFalse();
        insert.IsAllowed.ShouldBeFalse();
        delete.IsAllowed.ShouldBeFalse();
    }
}
