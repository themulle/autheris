namespace Autheris.Tests.Unit.Policy.Pdp;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Infrastructure.Persistence.Migrations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Services;
using Autheris.Application.Security.Totp.Interfaces;
using Autheris.Application.Security.Totp.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Grants are records: no relationship tuples are written anywhere, and nothing is converted into a grant automatically.</summary>
public sealed class AuthzMigrationTests
{
    private const string AdminSid = "S-1-5-21-ADMIN-500";
    private const string Grantee = "S-1-5-21-ALICE-1001";
    private const string Tenant = "tenant-a";

    [Fact]
    [Trait("Category", "Security")]
    public async Task PlanApply_CreatesGrantRecords_NotTuples()
    {
        var tableId = Guid.NewGuid();
        var table = new TableMetadata
        {
            Identifier = new TableIdentifier("sales", "public", "orders"),
            Table = new Table { Id = tableId, SchemaName = "public", TableName = "orders", Sensitivity = "INTERNAL", IsActive = true },
            Columns = [new TableColumn { TableId = tableId, ColumnName = "id", DataType = "int", IsSensitive = false }],
            PrimaryKeyColumns = ["id"]
        };
        var tables = Substitute.For<ITableMetadataRepository>();
        tables.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns(table);
        var created = new List<Consent>();
        var consents = Substitute.For<IConsentRepository>();
        consents.CreateConsentAsync(Arg.Do<Consent>(c => created.Add(c)), Arg.Any<CancellationToken>()).Returns(ci => Task.FromResult(ci.Arg<Consent>()));
        var secrets = new InMemoryTotpSecretStore();
        var totp = new TotpVerificationService();
        var service = new AccessPlanningService(
            global::Autheris.Tests.Unit.TestDoubles.AuditWriterTestServices.Over(Substitute.For<IAuditLogRepository>()), tables, consents, totp, secrets);

        var validUntil = DateTimeOffset.UtcNow.AddDays(30);
        var plan = await service.PlanAccessAsync(
            new AdminPlanAccessRequest("sales.public.orders",
                [new PrincipalAccessGrant(Grantee, new Dictionary<string, string> { ["id"] = "clear" }, null, validUntil)], "report access"),
            AdminSid, Tenant);
        var enrollment = totp.GenerateEnrollment(AdminSid, "admin@autheris.local");
        await secrets.SetSecretAsync(AdminSid, enrollment.SecretBase32);
        var confirm = await service.ConfirmPlanAsync(plan.PlanId, totp.GenerateTotpCode(enrollment.SecretBase32), AdminSid);

        var result = await service.ApplyAccessAsync(new AdminApplyAccessRequest(plan.PlanId, confirm.ConfirmationToken), AdminSid, new TenantId(Tenant));

        result.Success.ShouldBeTrue();
        result.Message!.ShouldNotContain("tuple", Case.Insensitive);
        result.Message!.ShouldNotContain("ReBAC", Case.Insensitive);
        var grant = created.ShouldHaveSingleItem();
        grant.Effect.ShouldBe(ConsentEffect.Allow);
        grant.GranteeSid!.Value.Value.ShouldBe(Grantee);
        grant.TenantId.Value.ShouldBe(Tenant);
        grant.ValidTo.ShouldBe(validUntil);
        grant.ColumnRules.Select(r => (r.ColumnName, r.AccessLevel)).ShouldBe([("id", ColumnAccessLevel.Clear)]);
    }

    private static string Step0011(string engine) => SqlMigrationStep.LoadEmbedded(engine).Single(x => x.Version == 11).Sql;

    [Theory]
    [InlineData("postgresql")]
    [InlineData("sqlserver")]
    [Trait("Category", "Security")]
    public void RoleGranteeConsents_RevokedWithReason(string engine)
    {
        var sql = Step0011(engine);

        sql.ShouldContain("ADR020_ROLE_GRANTEE_UNSUPPORTED");
        sql.ShouldContain("CONSENTS SET is_revoked = 1");
        sql.ShouldContain("grantee_type = ");
        sql.ShouldContain("effect <> ", Case.Insensitive);
        sql.ShouldContain("'Role'", Case.Insensitive);
        sql.ShouldContain("Data.UnmaskSensitive");
        sql.ShouldContain("FUNCTIONAL_RIGHT_MAPPINGS");
    }

    [Theory]
    [InlineData("postgresql")]
    [InlineData("sqlserver")]
    [Trait("Category", "Security")]
    public void RoleGranteeAssignments_Deactivated(string engine)
    {
        var sql = Step0011(engine);

        sql.ShouldContain("deactivated_reason", Case.Sensitive);
        sql.ShouldContain("subject_type = ");
        sql.ShouldContain("ADR020_ROLE_GRANTEE_UNSUPPORTED");
    }

    [Theory]
    [InlineData("postgresql")]
    [InlineData("sqlserver")]
    [Trait("Category", "Security")]
    public async Task PreOd32ProfileAssignments_DeactivatedAndReported(string engine)
    {
        Step0011(engine).ShouldContain("OD32_PROFILE_ASSIGNMENT_REQUIRES_APPROVAL");

        var repo = Substitute.For<IAccessProfileRepository>();
        repo.GetRevokedRoleConsentsAsync(Arg.Any<CancellationToken>()).Returns([new RevokedRoleConsent("c1", Tenant, "analysts", "t1", "ADR020_ROLE_GRANTEE_UNSUPPORTED")]);
        repo.GetDeactivatedAssignmentsAsync(Arg.Any<CancellationToken>())
            .Returns([new DeactivatedProfileAssignment(Tenant, "p1", Grantee, "User", "OD32_PROFILE_ASSIGNMENT_REQUIRES_APPROVAL")]);

        var result = await AuthzMigrationReportEndpoints.GetAsync(Context("ClusterAdmin"), repo, CancellationToken.None);

        var ok = result.ShouldBeAssignableTo<IValueHttpResult>()!;
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        json.ShouldContain("OD32_PROFILE_ASSIGNMENT_REQUIRES_APPROVAL");
        json.ShouldContain("analysts");
    }

    [Theory]
    [InlineData("GovernanceAdmin")]
    [InlineData("TenantAdmin")]
    [InlineData("tenant-a:ClusterAdmin")]
    [Trait("Category", "Security")]
    public async Task MigrationReport_ClusterAdminOnly(string role)
    {
        var repo = Substitute.For<IAccessProfileRepository>();

        var result = await AuthzMigrationReportEndpoints.GetAsync(Context(role), repo, CancellationToken.None);

        ((IStatusCodeHttpResult)result).StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        await repo.DidNotReceiveWithAnyArgs().GetRevokedRoleConsentsAsync(default);
        await repo.DidNotReceiveWithAnyArgs().GetDeactivatedAssignmentsAsync(default);
    }

    private static DefaultHttpContext Context(string role) => new()
    {
        User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "S-1-5-21-ADMIN1"), new Claim("tenant_id", "home"), new Claim(ClaimTypes.Role, role)], "Bearer"))
    };
}
