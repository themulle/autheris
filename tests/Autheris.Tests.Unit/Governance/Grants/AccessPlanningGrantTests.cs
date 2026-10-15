using Autheris.Application.Governance.Grants;
using Autheris.Application.Governance.Services;
using Autheris.Domain.Model;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance.Grants;

/// <summary>WP-SC-16 / SEC-SC-20: MCP admin_plan_access never plans a permanent or over-limit grant for a user principal.</summary>
public sealed class AccessPlanningGrantTests
{
    private static AccessPlanningService Service() =>
        new(Substitute.For<Autheris.Application.Interfaces.IAuditLogRepository>());

    private static AdminPlanAccessRequest Plan(DateTimeOffset? validUntil) => new(
        "default.sales.orders",
        [new PrincipalAccessGrant("analyst_bob", new Dictionary<string, string> { ["id"] = "clear" }, null, validUntil)],
        "Authorized financial report");

    [Fact]
    public async Task SEC_SC_20_PlanAccess_WithoutValidUntil_IsRejected()
    {
        var ex = await Should.ThrowAsync<GrantPolicyViolationException>(() => Service().PlanAccessAsync(Plan(null), "S-1-5-21-ADMIN"));
        ex.ErrorCode.ShouldBe(GrantErrorCodes.RequiresExpiry);
    }

    [Fact]
    public async Task SEC_SC_20_PlanAccess_BeyondMaximum_IsRejected()
    {
        // Metadata is unavailable here, so the grant is classified Sensitive (fail closed): max 90 days.
        var ex = await Should.ThrowAsync<GrantPolicyViolationException>(() =>
            Service().PlanAccessAsync(Plan(DateTimeOffset.UtcNow.AddDays(120)), "S-1-5-21-ADMIN"));
        ex.ErrorCode.ShouldBe(GrantErrorCodes.ExceedsMaximum);
    }

    [Fact]
    public async Task SEC_SC_20_PlanAccess_WithinMaximum_IsPlanned()
    {
        var plan = await Service().PlanAccessAsync(Plan(DateTimeOffset.UtcNow.AddDays(30)), "S-1-5-21-ADMIN");
        plan.PlanId.ShouldNotBeNullOrEmpty();
    }
}
