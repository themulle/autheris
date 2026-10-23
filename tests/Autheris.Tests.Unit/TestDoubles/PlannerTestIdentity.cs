namespace Autheris.Tests.Unit.TestDoubles;

using System.Security.Claims;
using Autheris.Domain.Common;

/// <summary>SQLA-14: the cross-source planner and the subtree compiler require the caller's principal and tenant (no "default" tenant).</summary>
internal static class PlannerTestIdentity
{
    public static ClaimsPrincipal User { get; } = new(new ClaimsIdentity("test"));

    public static TenantId Tenant { get; } = new("tenant-planner-test");
}
