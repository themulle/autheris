namespace Autheris.Tests.Unit.Policy.Pdp;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
}
