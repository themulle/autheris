namespace Autheris.Tests.Unit.Configuration;

using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Options;
using Autheris.Domain.Options.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

/// <summary>
/// ARCH-MNT-06-T-03 and T-04: the security options deleted as dead (no reader) never governed behaviour, so removing them changes nothing.
/// MCP masking and MCP authentication are on by default without them; Casbin is governed by Casbin:Enabled alone.
/// </summary>
[Trait("Category", "Security")]
public sealed class DeadSecurityOptionsRemovedTests
{
    private static bool Has(Type type, string name) =>
        type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase) is not null;

    [Fact]
    public async Task ARCH_MNT_06_T_03_McpMasking_AppliedByDefault_WithoutFlag()
    {
        Has(typeof(McpOptions), "RequirePiiMasking").ShouldBeFalse();
        new GatewayOptions().IsMcpUnmaskedAllowed.ShouldBeFalse();

        var options = Options.Create(new GatewayOptions { Mcp = new McpOptions { Enabled = true } });
        var guardrail = new AiDataGuardrailService(
            new McpToolRegistry(), options, NullLogger<AiDataGuardrailService>.Instance,
            global::Autheris.Tests.Unit.TestDoubles.NullAuditLogRepository.Instance);
        var session = new Autheris.Domain.Model.McpSessionContext("sess-1", "agent-principal", "tenant-alpha", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var result = await guardrail.ExecuteToolWithGuardrailAsync(new Autheris.Domain.Model.McpToolCallRequest("query_customers", "{}"), session);

        result.IsSuccess.ShouldBeTrue();
        result.IsMasked.ShouldBeTrue();
        result.ContentJson.ShouldNotContain("erika.mustermann@acme-corp.com");
    }

    [Fact]
    public void ARCH_MNT_06_T_03_McpAuth_RequiredByDefault()
    {
        Has(typeof(McpOptions), "AllowAnonymousDiscovery").ShouldBeFalse();
        var defaults = new GatewayOptions();
        defaults.IsMcpAuthBypassed.ShouldBeFalse();
        defaults.IsAnonymousAccessAllowed.ShouldBeFalse();
        defaults.Insecure.danger_bypass_mcp_auth.ShouldBeFalse();
    }

    [Fact]
    public void ARCH_MNT_06_T_04_CasbinEnforcement_AppliedWithoutEnforceInQueryPipelineKey()
    {
        Has(typeof(CasbinOptions), "EnforceInQueryPipeline").ShouldBeFalse();
        // Casbin:Enabled is the only switch; the guardrail and the query path read it (see McpDatasetToolWiringTests.Guardrail_AsksCasbin_OnlyWhenCasbinIsEnabled).
        typeof(CasbinOptions).GetProperties().Select(p => p.Name).ShouldContain("Enabled");
        new CasbinOptions().Enabled.ShouldBeFalse();
    }

    [Theory]
    [InlineData(typeof(ScimOptions), "EnableMassChangeProtection")]
    [InlineData(typeof(ScimOptions), "MassChangeThresholdPercentage")]
    [InlineData(typeof(EntraIdAuthOptions), "SidClaimType")]
    [InlineData(typeof(EntraIdAuthOptions), "GroupsClaimType")]
    [InlineData(typeof(AdfsAuthOptions), "SidClaimType")]
    [InlineData(typeof(AdfsAuthOptions), "GroupSidClaimType")]
    public void ARCH_MNT_06_T_07_DeletedOptionProperties_AreGone(Type type, string property) =>
        Has(type, property).ShouldBeFalse();
}
