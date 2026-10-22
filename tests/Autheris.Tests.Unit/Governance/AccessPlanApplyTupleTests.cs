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
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Application.Security.Rebac.Services;
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

/// <summary>GOV-04: access-plan apply writes the tuples the ReBAC table gate reads, refuses self-grants and four-eyes grants.</summary>
[Trait("Category", "Security")]
public sealed class AccessPlanApplyTupleTests
{
    private const string AdminSid = "S-1-5-21-ADMIN-500";
    private const string Grantee = "S-1-5-21-ALICE-1001";
    private const string Tenant = "tenant-a";

    private readonly ITableMetadataRepository _tableRepo = Substitute.For<ITableMetadataRepository>();
    private readonly InMemoryRebacStore _store = new(NullLogger<InMemoryRebacStore>.Instance);
    private readonly ITotpSecretStore _secrets = new InMemoryTotpSecretStore();
    private readonly ITotpVerificationService _totp = new TotpVerificationService();
    private readonly AccessPlanningService _service;

    public AccessPlanApplyTupleTests()
    {
        _service = new AccessPlanningService(
            Substitute.For<IAuditLogRepository>(), _tableRepo, _store, _totp, _secrets);
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

    private static ZanzibarRebacEvaluator Evaluator(IRebacStore store) => new(
        store,
        Options.Create(new GatewayOptions { Rebac = new RebacOptions { Enabled = true, MaxTraversalDepth = 10, CacheTtlSeconds = 10 } }),
        NullLogger<ZanzibarRebacEvaluator>.Instance);

    [Fact]
    public async Task Apply_WritesTupleMatchedByRebacTableGate()
    {
        var apply = await PlanAndConfirmAsync(Request("sales.public.orders"));

        await _service.ApplyAccessAsync(apply, AdminSid, new TenantId(Tenant));

        (await RebacTableGate.IsAllowedAsync(
            Evaluator(_store), new TenantId(Tenant), new Sid(Grantee), new TableIdentifier("sales", "public", "orders"), CancellationToken.None))
            .ShouldBeTrue();
    }

    [Fact]
    public async Task Apply_TwoPartDatasetId_UsesNormalisedObjectId()
    {
        var apply = await PlanAndConfirmAsync(Request("sales.orders"));

        await _service.ApplyAccessAsync(apply, AdminSid, new TenantId(Tenant));

        (await RebacTableGate.IsAllowedAsync(
            Evaluator(_store), new TenantId(Tenant), new Sid(Grantee), new TableIdentifier("sales", "public", "orders"), CancellationToken.None))
            .ShouldBeTrue();
    }

    [Fact]
    public async Task Apply_WritesNoColumnTuples()
    {
        var rebac = Substitute.For<IRebacStore>();
        IEnumerable<RebacTuple>? written = null;
        await rebac.AddTuplesAsync(Arg.Do<IEnumerable<RebacTuple>>(t => written = t.ToList()), Arg.Any<CancellationToken>());
        var service = new AccessPlanningService(Substitute.For<IAuditLogRepository>(), _tableRepo, rebac, _totp, _secrets);
        var plan = await service.PlanAccessAsync(Request("sales.public.orders"), AdminSid, Tenant);
        var enrollment = _totp.GenerateEnrollment(AdminSid, "admin@autheris.local");
        await _secrets.SetSecretAsync(AdminSid, enrollment.SecretBase32);
        var confirm = await service.ConfirmPlanAsync(plan.PlanId, _totp.GenerateTotpCode(enrollment.SecretBase32), AdminSid);

        await service.ApplyAccessAsync(new AdminApplyAccessRequest(plan.PlanId, confirm.ConfirmationToken), AdminSid, new TenantId(Tenant));

        written.ShouldNotBeNull();
        written!.ShouldAllBe(t => !t.Object.StartsWith("column:", StringComparison.Ordinal));
        written!.ShouldAllBe(t => t.Object == "table:sales.public.orders" && t.User == Grantee && t.TenantId == Tenant);
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
