namespace Autheris.Tests.Unit.Security;

using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class DifferentialPrivacyBudgetResetTests
{
    private const string Tenant = "tenant-alpha";
    private const string ClientId = "client-analytics";

    [Fact]
    public void Reset_TenantQualifiedOwnClientId_Returns403()
    {
        var context = CreateContext(ClientId, Tenant);
        var targetKey = $"{Tenant}:{ClientId}";

        var isSelf = GovernanceEndpoints.IsSelfBudgetReset(context, targetKey);
        isSelf.ShouldBeTrue();
    }

    [Fact]
    public void Reset_OwnClientIdDifferentCase_Returns403()
    {
        var context = CreateContext(ClientId, Tenant);
        var targetKey = $"{Tenant}:{ClientId.ToUpperInvariant()}";

        var isSelf = GovernanceEndpoints.IsSelfBudgetReset(context, targetKey);
        isSelf.ShouldBeTrue();
    }

    [Fact]
    public void Reset_CallerWithoutIdentity_Returns403()
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity()); // no claims
        var targetKey = $"{Tenant}:{ClientId}";

        var isSelf = GovernanceEndpoints.IsSelfBudgetReset(context, targetKey);
        isSelf.ShouldBeTrue(); // Fail-closed
    }

    [Fact]
    public void Reset_OtherClientSameTenant_Returns200()
    {
        var context = CreateContext("other-client-id", Tenant);
        var targetKey = $"{Tenant}:{ClientId}";

        var isSelf = GovernanceEndpoints.IsSelfBudgetReset(context, targetKey);
        isSelf.ShouldBeFalse();
    }

    private static DefaultHttpContext CreateContext(string clientId, string tenant)
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-ADMIN"),
            new Claim(ClaimTypes.Role, "GovernanceAdmin"),
            new Claim("client_id", clientId),
            new Claim("tenant_id", tenant)
        ], "TestAuth"));
        return context;
    }
}
