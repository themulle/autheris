namespace Autheris.Tests.Unit.Policy;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance;
using Autheris.Domain.Model;
using Autheris.Domain.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Policy.Services;
using Autheris.Application.Services;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Options;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Tests.Unit.Governance;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class TableAccessPolicySecurityTests
{
    [Fact]
    [Trait("Category", "Security")]
    public void Constructor_without_policy_service_throws()
    {
        var repo = new FakeConsentRepository();
        var resolution = new ConsentResolutionService();
        var cache = NullConsentCacheService.Instance;
        var options = new GatewayOptions();
        var filters = NullMandatoryRowFilterResolver.Instance;

        var ex = Should.Throw<ArgumentNullException>(() =>
            new TableAccessPolicy(
                repo,
                resolution,
                cache,
                policyEnforcementService: null!,
                clientIpResolver: null,
                options,
                filters));

        ex.ParamName.ShouldBe("policyEnforcementService");
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, true, false)]
    [InlineData(false, false, true, true)]
    [InlineData(true, true, true, true)]
    [Trait("Category", "Security")]
    public async Task SEC_36_SP3a_02_CanWriteTable_is_false_without_write_decision(bool allowDml, bool writerRole, bool consentBypass, bool clusterAdmin)
    {
        var enforcement = new PoliciesPresentEnforcement();
        var options = new GatewayOptions
        {
            WebSql = new WebSqlOptions { AllowDml = allowDml, DmlWriterRoles = writerRole ? ["Writer"] : [] },
            Insecure = new InsecureGettingStartedOptions { danger_bypass_consent_checks = consentBypass }
        };
        var policy = new TableAccessPolicy(
            new FakeConsentRepository(), new ConsentResolutionService(), NullConsentCacheService.Instance, enforcement, clientIpResolver: null, options, NullMandatoryRowFilterResolver.Instance);
        var roles = new List<Claim> { new(ClaimTypes.NameIdentifier, "u1"), new(ClaimTypes.Role, "Writer") };
        if (clusterAdmin)
        {
            roles.Add(new Claim(ClaimTypes.Role, "ClusterAdmin"));
        }

        var user = new ClaimsPrincipal(new ClaimsIdentity(roles, "Test"));
        var id = new TableIdentifier("default", "main", "orders");

        (await policy.CanWriteTableAsync(user, new TenantId("t1"), new TableMetadata { Identifier = id })).ShouldBeFalse();
        (await policy.CanWriteTableAsync(user, new TenantId("t1"), id)).ShouldBeFalse();
    }

    private sealed class PoliciesPresentEnforcement : IPolicyEnforcementService
    {
        public ValueTask<TableAccessDecision> EvaluatePolicyAsync(SecurityEvaluationContext context, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public bool HasPolicies(TenantId tenant) => true;

        public Task ReloadPoliciesAsync(TenantId tenant, CancellationToken ct = default) => Task.CompletedTask;
    }
}
