namespace Autheris.Tests.Unit.Governance;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Governance.Services;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Security.Totp.Interfaces;
using Autheris.Application.Security.Totp.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>GOV-04: access-plan apply writes grant records, refuses self-grants and four-eyes grants.</summary>
[Trait("Category", "Security")]
public sealed class AccessPlanApplyGrantTests
{
    private const string AdminSid = "S-1-5-21-ADMIN-500";
    private const string Grantee = "S-1-5-21-ALICE-1001";
    private const string Tenant = "tenant-a";

    private readonly ITableMetadataRepository _tableRepo = Substitute.For<ITableMetadataRepository>();
    private readonly IConsentRepository _consents = Substitute.For<IConsentRepository>();
    private readonly List<Consent> _created = [];
    private readonly ITotpSecretStore _secrets = new InMemoryTotpSecretStore();
    private readonly ITotpVerificationService _totp = new TotpVerificationService();
    private readonly AccessPlanningService _service;

    public AccessPlanApplyGrantTests()
    {
        _service = new AccessPlanningService(
            global::Autheris.Tests.Unit.TestDoubles.AuditWriterTestServices.Over(Substitute.For<IAuditLogRepository>()), _tableRepo, _consents, _totp, _secrets);
        _consents.CreateConsentAsync(Arg.Do<Consent>(c => _created.Add(c)), Arg.Any<CancellationToken>()).Returns(ci => Task.FromResult(ci.Arg<Consent>()));
        UseTable(sensitive: false);
    }

    private void UseTable(bool sensitive)
    {
        var id = Guid.NewGuid();
        var meta = new TableMetadata
        {
            Identifier = new TableIdentifier("sales", "public", "orders"),
            Table = new Table { Id = id, SchemaName = "public", TableName = "orders", Sensitivity = "INTERNAL", IsActive = true },
            Columns =
            [
                new TableColumn { TableId = id, ColumnName = "id", DataType = "int", IsSensitive = false },
                new TableColumn { TableId = id, ColumnName = "amount", DataType = "numeric", IsSensitive = sensitive }
            ],
            PrimaryKeyColumns = ["id"]
        };
        _tableRepo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns(meta);
    }

    private static AdminPlanAccessRequest Request(string datasetId, string principal = Grantee) => new(
        datasetId,
        [new PrincipalAccessGrant(principal, new Dictionary<string, string> { ["id"] = "clear" }, null, DateTimeOffset.UtcNow.AddDays(30))],
        "access for report");

    private async Task<AdminApplyAccessRequest> PlanAndConfirmAsync(AdminPlanAccessRequest request, ClaimsPrincipal? caller = null)
    {
        var plan = await _service.PlanAccessAsync(request, AdminSid, Tenant, caller: caller);
        var enrollment = _totp.GenerateEnrollment(AdminSid, "admin@autheris.local");
        await _secrets.SetSecretAsync(AdminSid, enrollment.SecretBase32);
        var confirm = await _service.ConfirmPlanAsync(plan.PlanId, _totp.GenerateTotpCode(enrollment.SecretBase32), AdminSid);
        return new AdminApplyAccessRequest(plan.PlanId, confirm.ConfirmationToken);
    }

    [Fact]
    public async Task Apply_CreatesAllowGrantRecord_ForTheNormalisedTable()
    {
        var apply = await PlanAndConfirmAsync(Request("sales.public.orders"));

        await _service.ApplyAccessAsync(apply, AdminSid, new TenantId(Tenant));

        var consent = _created.ShouldHaveSingleItem();
        consent.Effect.ShouldBe(ConsentEffect.Allow);
        consent.GranteeSid!.Value.Value.ShouldBe(Grantee);
        consent.TenantId.Value.ShouldBe(Tenant);
        consent.TableIdentifier.ShouldBe(new TableIdentifier("sales", "public", "orders"));
        consent.ColumnRules.ShouldHaveSingleItem().ColumnName.ShouldBe("id");
        consent.ColumnRules[0].AccessLevel.ShouldBe(ColumnAccessLevel.Clear);
    }

    [Fact]
    public async Task Apply_TwoPartDatasetId_UsesTheNormalisedTable()
    {
        var apply = await PlanAndConfirmAsync(Request("sales.orders"));

        await _service.ApplyAccessAsync(apply, AdminSid, new TenantId(Tenant));

        _created.ShouldHaveSingleItem().TableIdentifier.ShouldBe(new TableIdentifier("sales", "public", "orders"));
    }

    [Fact]
    public async Task Apply_RolePrincipal_IsRejected()
    {
        var apply = await PlanAndConfirmAsync(Request("sales.public.orders", "role:analysts"));

        await Should.ThrowAsync<SecurityException>(() => _service.ApplyAccessAsync(apply, AdminSid, new TenantId(Tenant)));
        _created.ShouldBeEmpty();
    }

    [Fact]
    public async Task Plan_GrantToSelf_Throws()
    {
        await Should.ThrowAsync<SecurityException>(() =>
            _service.PlanAccessAsync(Request("sales.public.orders", AdminSid), AdminSid, Tenant));
    }

    [Fact]
    public async Task Apply_GrantToSelfAlias_Throws()
    {
        // Planned without the caller identity (the alias is unknown), applied with the full caller claims.
        var apply = await PlanAndConfirmAsync(Request("sales.public.orders"));
        var caller = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, AdminSid), new Claim("email", Grantee)], "test"));

        await Should.ThrowAsync<SecurityException>(() =>
            _service.ApplyAccessAsync(apply, AdminSid, new TenantId(Tenant), caller: caller));
    }

    [Fact]
    public async Task Apply_SensitiveGrant_RequiresFourEyes_Rejected()
    {
        UseTable(sensitive: true);
        var apply = await PlanAndConfirmAsync(Request("sales.public.orders"));

        var ex = await Should.ThrowAsync<GrantPolicyViolationException>(() =>
            _service.ApplyAccessAsync(apply, AdminSid, new TenantId(Tenant)));

        ex.ErrorCode.ShouldBe(GrantErrorCodes.FourEyesRequired);
    }

    [Fact]
    public async Task Apply_WithoutTenant_Throws()
    {
        var apply = await PlanAndConfirmAsync(Request("sales.public.orders"));

        await Should.ThrowAsync<SecurityException>(() => _service.ApplyAccessAsync(apply, AdminSid, default(TenantId)));
    }
}
