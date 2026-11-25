namespace Autheris.Tests.Unit.Security;

using System.Collections.Generic;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Governance.Services;
using Autheris.Application.Security.Totp.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Tests.Common.Fakes;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Issue #1121: the single-administrator MCP access plan never grants secret data (two distinct approvals are required).</summary>
[Trait("Category", "Security")]
public sealed class AccessPlanSecretFourEyesTests
{
    private const string Admin = "S-1-5-21-1-500";

    private static AccessPlanningService Service(ProtectionClass tableClass, ProtectionClass? columnClass = null)
    {
        var columns = new Dictionary<string, ColumnProtection>(System.StringComparer.OrdinalIgnoreCase);
        if (columnClass is { } cc)
        {
            columns["id"] = new ColumnProtection(cc, null);
        }

        var protection = new EffectiveProtection(
            new ContainerRef(ContainerKind.Domain, System.Guid.NewGuid()), null, tableClass, tableClass, null, columns, CatalogVisibilityMode.Open);
        return new AccessPlanningService(
            global::Autheris.Tests.Unit.TestDoubles.AuditWriterTestServices.Over(new InMemoryAuditLogRepository()),
            global::Autheris.Tests.Unit.TestDoubles.AccessPlanTestTables.OrdersRepository(),
            Substitute.For<IConsentRepository>(),
            new TotpVerificationService(),
            protectionProvider: NullFake.Create<IProtectionSnapshotProvider>((_, _) => new ValueTask<EffectiveProtection>(protection)));
    }

    private static AdminPlanAccessRequest Request() => new(
        "sales.public.orders",
        [new PrincipalAccessGrant("analyst_bob", new Dictionary<string, string> { ["id"] = "clear" }, null, System.DateTimeOffset.UtcNow.AddDays(30))],
        "quarterly reporting");

    [Theory]
    [InlineData(ProtectionClass.Secret, null)]
    [InlineData(ProtectionClass.Internal, ProtectionClass.Secret)]
    public async Task Access_plan_on_secret_data_is_refused(ProtectionClass tableClass, ProtectionClass? columnClass)
    {
        var ex = await Should.ThrowAsync<GrantPolicyViolationException>(() => Service(tableClass, columnClass).PlanAccessAsync(Request(), Admin, "tenant-a"));

        ex.ErrorCode.ShouldBe(GrantErrorCodes.SecretRequiresFourEyes);
    }

    [Fact]
    public async Task Access_plan_on_non_secret_data_is_planned()
    {
        var plan = await Service(ProtectionClass.Internal).PlanAccessAsync(Request(), Admin, "tenant-a");

        plan.PlanId.ShouldNotBeNullOrWhiteSpace();
    }
}
