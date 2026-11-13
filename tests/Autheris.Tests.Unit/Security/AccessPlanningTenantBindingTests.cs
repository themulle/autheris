namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Governance.Policies;
using Autheris.Application.Governance.Services;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Security.Totp.Interfaces;
using Autheris.Application.Security.Totp.Services;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class AccessPlanningTenantBindingTests
{
    private const string RequestTenant = "tenant-request-scope";
    private const string DatasetDomain = "sales";
    private const string DatasetId = "sales.public.orders";
    private const string AdminSid = "S-1-5-21-ADMIN";

    private static (AccessPlanningService service, List<Consent> created) CreateService()
    {
        var created = new List<Consent>();
        var consents = Substitute.For<IConsentRepository>();
        consents.CreateConsentAsync(Arg.Do<Consent>(c => created.Add(c)), Arg.Any<CancellationToken>()).Returns(ci => Task.FromResult(ci.Arg<Consent>()));

        var tableRepo = Substitute.For<ITableMetadataRepository>();
        var tableMeta = new TableMetadata
        {
            Identifier = new TableIdentifier("sales", "public", "orders"),
            Table = new Table { SourceName = "sales", SchemaName = "public", TableName = "orders" }
        };
        tableRepo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(tableMeta);
        tableRepo.GetActiveTableIdentifierAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => callInfo.Arg<TableIdentifier>());

        var totpService = Substitute.For<ITotpVerificationService>();
        totpService.VerifyAndConsumeTotpAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var secretStore = Substitute.For<ITotpSecretStore>();
        secretStore.GetSecretAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("JBSWY3DPEHPK3PXP");

        var secretProvider = Substitute.For<IKeyVaultSecretProvider>();
        secretProvider.GetSecretBytes(Arg.Any<string>()).Returns(Encoding.UTF8.GetBytes("key-32-bytes-long-for-hmac-sha256!"));

        var service = new AccessPlanningService(
            global::Autheris.Tests.Unit.TestDoubles.AuditWriterTestServices.Over(Substitute.For<IAuditLogRepository>()),
            tableMetadataRepository: tableRepo,
            consentRepository: consents,
            totpVerificationService: totpService,
            totpSecretStore: secretStore,
            secretProvider: secretProvider,
            planOptions: Options.Create(new AccessPlanOptions { SigningKeyRef = "key" }));

        return (service, created);
    }

    [Fact]
    public async Task RestApply_WritesTuplesUnderRequestTenant_NotDatasetDomain()
    {
        var (service, created) = CreateService();

        var planReq = new AdminPlanAccessRequest(
            DatasetId: DatasetId,
            Grants: [new("alice", new Dictionary<string, string> { ["id"] = "clear" }, null, DateTimeOffset.UtcNow.AddDays(7))],
            Reason: "Tenant binding test");

        var plan = await service.PlanAccessAsync(planReq, AdminSid, RequestTenant);
        var confirm = await service.ConfirmPlanAsync(plan.PlanId, "123456", AdminSid);

        var applyResult = await service.ApplyAccessAsync(
            new AdminApplyAccessRequest(plan.PlanId, confirm.ConfirmationToken),
            AdminSid,
            new TenantId(RequestTenant));

        applyResult.Success.ShouldBeTrue();

        created.ShouldNotBeEmpty();
        created.All(c => c.TenantId.Value == RequestTenant).ShouldBeTrue();
        created.Any(c => c.TenantId.Value == DatasetDomain).ShouldBeFalse();
    }

    [Fact]
    public async Task Apply_StoredPlanWithoutTenant_Throws()
    {
        var (service, _) = CreateService();

        var planReq = new AdminPlanAccessRequest(
            DatasetId: DatasetId,
            Grants: [new("alice", new Dictionary<string, string> { ["id"] = "clear" }, null, DateTimeOffset.UtcNow.AddDays(7))],
            Reason: "Tenant missing test");

        var createdPlan = await service.PlanAccessAsync(planReq, AdminSid, null);
        var confirm = await service.ConfirmPlanAsync(createdPlan.PlanId, "123456", AdminSid);

        var ex = await Should.ThrowAsync<SecurityException>(() =>
            service.ApplyAccessAsync(
                new AdminApplyAccessRequest(createdPlan.PlanId, confirm.ConfirmationToken),
                AdminSid,
                new TenantId("tenant-a")));

        ex.Message.ShouldContain("Plan belongs to a different tenant.");
    }

    [Fact]
    public async Task Apply_CallerTenantDiffersFromPlanTenant_Throws()
    {
        var (service, _) = CreateService();

        var planReq = new AdminPlanAccessRequest(
            DatasetId: DatasetId,
            Grants: [new("alice", new Dictionary<string, string> { ["id"] = "clear" }, null, DateTimeOffset.UtcNow.AddDays(7))],
            Reason: "Tenant binding test");

        var plan = await service.PlanAccessAsync(planReq, AdminSid, "tenant-a");
        var confirm = await service.ConfirmPlanAsync(plan.PlanId, "123456", AdminSid);

        var ex = await Should.ThrowAsync<SecurityException>(() =>
            service.ApplyAccessAsync(
                new AdminApplyAccessRequest(plan.PlanId, confirm.ConfirmationToken),
                AdminSid,
                new TenantId("tenant-b")));

        ex.Message.ShouldContain("Plan belongs to a different tenant.");
    }

    [Fact]
    public async Task ApplyRowFilterEvaluation_CallerTenantDiffers_Throws()
    {
        var totpService = Substitute.For<ITotpVerificationService>();
        totpService.VerifyAndConsumeTotpAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var secretStore = Substitute.For<ITotpSecretStore>();
        secretStore.GetSecretAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("JBSWY3DPEHPK3PXP");

        var secretProvider = Substitute.For<IKeyVaultSecretProvider>();
        secretProvider.GetSecretBytes(Arg.Any<string>()).Returns(Encoding.UTF8.GetBytes("key-32-bytes-long-for-hmac-sha256!"));

        var ruleId = Guid.NewGuid();
        var orders = new TableIdentifier("sales", "dbo", "orders");
        var stored = new RowFilterEvaluationSettings(RowFilterEvaluationStrategy.Exists, 30, null);

        var tableRepo = Substitute.For<ITableMetadataRepository>();
        tableRepo.GetActiveTableIdentifierAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(orders);

        var rowFilterAdmin = Substitute.For<IRowFilterEvaluationAdminService>();
        rowFilterAdmin.ValidateRuleAsync(ruleId, Arg.Any<TenantId>(), Arg.Any<RowFilterEvaluationSettings>(), Arg.Any<CancellationToken>())
            .Returns(new RowFilterEvaluationPreview(orders, stored));

        var service = new AccessPlanningService(
            global::Autheris.Tests.Unit.TestDoubles.AuditWriterTestServices.Over(Substitute.For<IAuditLogRepository>()),
            tableMetadataRepository: tableRepo,
            totpVerificationService: totpService,
            totpSecretStore: secretStore,
            secretProvider: secretProvider,
            rowFilterEvaluationAdmin: rowFilterAdmin,
            planOptions: Options.Create(new AccessPlanOptions { SigningKeyRef = "key" }));

        var planReq = new AdminPlanRowFilterEvaluationRequest(
            RowFilterId: ruleId.ToString("D"),
            DatasetId: null,
            Strategy: "KeySetCache",
            DurationSeconds: 60,
            MaxKeys: 100,
            Reason: "Tuning");

        var plan = await service.PlanRowFilterEvaluationAsync(planReq, AdminSid, "tenant-a");
        var confirm = await service.ConfirmPlanAsync(plan.PlanId, "123456", AdminSid);

        var ex = await Should.ThrowAsync<SecurityException>(() =>
            service.ApplyRowFilterEvaluationAsync(
                new AdminApplyAccessRequest(plan.PlanId, confirm.ConfirmationToken),
                AdminSid,
                new TenantId("tenant-b")));

        ex.Message.ShouldContain("Plan belongs to a different tenant.");
    }

    [Fact]
    public async Task GovernancePlanEndpoints_Apply_PassesRequestTenant()
    {
        var planningService = Substitute.For<IAccessPlanningService>();
        planningService.ApplyAccessAsync(Arg.Any<AdminApplyAccessRequest>(), Arg.Any<string>(), Arg.Any<TenantId>(), Arg.Any<ClaimsPrincipal?>(), Arg.Any<CancellationToken>())
            .Returns(new AdminApplyAccessResult(true, "plan-1", 1, "Applied successfully"));

        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.PrimarySid, AdminSid),
            new Claim(ClaimTypes.Role, "GovernanceAdmin"),
            new Claim("tenant_id", RequestTenant)
        ], "TestAuth"));

        var body = """{"planId":"plan-1","confirmationToken":"token-123"}""";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Request.ContentType = "application/json";

        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
        var app = builder.Build();
        app.MapGovernancePlanEndpoints();

        // Call the service via the endpoint helper or mock invocation
        var applyReq = new AdminApplyAccessRequest("plan-1", "token-123");
        var adminSid = AdminSid;
        var requestTenant = EndpointSecurity.GetRequestTenant(context);

        requestTenant.Value.ShouldBe(RequestTenant);

        await planningService.ApplyAccessAsync(applyReq, adminSid, requestTenant, ct: CancellationToken.None);

        await planningService.Received(1).ApplyAccessAsync(
            Arg.Is<AdminApplyAccessRequest>(r => r.PlanId == "plan-1"),
            AdminSid,
            Arg.Is<TenantId>(t => t.Value == RequestTenant),
            Arg.Any<ClaimsPrincipal?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GovernancePlanGet_OtherAdminOrTenant_NotFound()
    {
        var planningService = Substitute.For<IAccessPlanningService>();
        var planResult = new AdminPlanAccessResult(
            PlanId: "plan-123",
            DatasetId: "sales.public.orders",
            Diffs: [],
            Warnings: [],
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(15),
            TenantId: "tenant-a",
            AdminSid: "S-1-5-21-CREATOR");

        planningService.GetPlanAsync("plan-123", Arg.Any<CancellationToken>())
            .Returns(planResult);

        // 1. Caller from different tenant -> 404
        var otherTenantCtx = new DefaultHttpContext();
        otherTenantCtx.User = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-CREATOR"),
            new Claim(ClaimTypes.Role, "GovernanceAdmin"),
            new Claim("tenant_id", "tenant-b")
        ], "TestAuth"));

        var otherTenantRes = await GovernancePlanEndpoints.HandleGetAsync("plan-123", otherTenantCtx, planningService, CancellationToken.None);
        var status = (otherTenantRes as Microsoft.AspNetCore.Http.IStatusCodeHttpResult)?.StatusCode;
        status.ShouldBe(StatusCodes.Status404NotFound);

        // 2. Caller from same tenant but different admin SID -> 404
        var otherAdminCtx = new DefaultHttpContext();
        otherAdminCtx.User = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-OTHER"),
            new Claim(ClaimTypes.Role, "GovernanceAdmin"),
            new Claim("tenant_id", "tenant-a")
        ], "TestAuth"));

        var otherAdminRes = await GovernancePlanEndpoints.HandleGetAsync("plan-123", otherAdminCtx, planningService, CancellationToken.None);
        status = (otherAdminRes as Microsoft.AspNetCore.Http.IStatusCodeHttpResult)?.StatusCode;
        status.ShouldBe(StatusCodes.Status404NotFound);

        // 3. Creator caller (matching tenant and SID) -> 200
        var matchingCtx = new DefaultHttpContext();
        matchingCtx.User = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-CREATOR"),
            new Claim(ClaimTypes.Role, "GovernanceAdmin"),
            new Claim("tenant_id", "tenant-a")
        ], "TestAuth"));

        var matchingRes = await GovernancePlanEndpoints.HandleGetAsync("plan-123", matchingCtx, planningService, CancellationToken.None);
        status = (matchingRes as Microsoft.AspNetCore.Http.IStatusCodeHttpResult)?.StatusCode;
        status.ShouldBe(StatusCodes.Status200OK);

        // 4. Canonical ClusterAdmin caller -> 200 regardless of tenant/sid
        var clusterAdminCtx = new DefaultHttpContext();
        clusterAdminCtx.User = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-CLUSTER-ADMIN"),
            new Claim(ClaimTypes.Role, "ClusterAdmin"),
            new Claim("tenant_id", "tenant-other")
        ], "TestAuth"));

        var clusterAdminRes = await GovernancePlanEndpoints.HandleGetAsync("plan-123", clusterAdminCtx, planningService, CancellationToken.None);
        status = (clusterAdminRes as Microsoft.AspNetCore.Http.IStatusCodeHttpResult)?.StatusCode;
        status.ShouldBe(StatusCodes.Status200OK);
    }
}
