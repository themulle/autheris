namespace Autheris.Tests.Unit.Governance;

using System;
using System.Net;
using System.Threading.Tasks;
using Autheris.Application.Governance;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class CasbinEnforcementServiceTests
{
    private static SecurityEvaluationContext CreateContext(IPAddress? clientIp) =>
        new(
            UserSid: new Sid("user-1"),
            GroupSids: [new Sid("group-1")],
            Tenant: new TenantId("tenant-1"),
            TargetTable: new TableIdentifier("sales", "dbo", "orders"),
            RequestedColumns: ["id", "amount"],
            ClientIp: clientIp!,
            Timestamp: DateTimeOffset.UtcNow,
            PurposeId: null,
            Attributes: new Dictionary<string, object?>());

    public static TheoryData<IPAddress?> UnknownIpTestData =>
        new()
        {
            null,
            IPAddress.None,
            IPAddress.IPv6None
        };

    [Theory]
    [MemberData(nameof(UnknownIpTestData))]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "SEC-SIMPL-50-03")]
    public async Task Unknown_client_ip_with_ip_rule_denies(IPAddress? clientIp)
    {
        using var service = new CasbinEnforcementService();
        service.LoadPolicyFromText(new TenantId("tenant-1"), "p, group-1, tenant-1, sales.dbo.orders, read, ctx.ClientIp != null, allow\n");

        var context = CreateContext(clientIp);
        var decision = await service.EvaluatePolicyAsync(context);

        decision.IsAllowed.ShouldBeFalse();
        decision.DeniedReasons.ShouldContain(r => r.Contains("client address unavailable"));
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "SEC-SIMPL-50-03")]
    public async Task Unknown_client_ip_without_ip_rule_is_unaffected()
    {
        using var service = new CasbinEnforcementService();
        service.LoadPolicyFromText(new TenantId("tenant-1"), "p, group-1, tenant-1, sales.dbo.orders, read, true, allow\n");

        var context = CreateContext(IPAddress.None);
        var decision = await service.EvaluatePolicyAsync(context);

        decision.IsAllowed.ShouldBeTrue();
    }
}
