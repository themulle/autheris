namespace Autheris.Tests.Unit.ConsentRequests.Routing;

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.ConsentRequests;
using Autheris.Application.ConsentRequests.Routing;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class ConsentWorkflowRouterTests
{
    private static readonly TenantId Tenant = new("tenant-routing");
    private static readonly TableIdentifier Table = new("sales_db", "dbo", "orders");
    private static readonly DateTimeOffset FixedNow = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task DataSource_Route_WinsOverOwnerAndTenant()
    {
        var dsSystemId = Guid.NewGuid();
        var ownerSystemId = Guid.NewGuid();
        var tenantSystemId = Guid.NewGuid();

        var routes = new List<ConsentWorkflowRoute>
        {
            new(Tenant.Value, WorkflowRouteScopeKind.Tenant, "*", ConsentWorkflowMode.External, tenantSystemId, 90, 30, true, "admin", FixedNow),
            new(Tenant.Value, WorkflowRouteScopeKind.DataOwner, "owner-1", ConsentWorkflowMode.External, ownerSystemId, 45, 20, true, "admin", FixedNow),
            new(Tenant.Value, WorkflowRouteScopeKind.DataSource, "sales_db", ConsentWorkflowMode.External, dsSystemId, 14, 7, true, "admin", FixedNow)
        };

        var adminRepo = Substitute.For<IConsentWorkflowAdminRepository>();
        adminRepo.GetRoutingInputsForTableAsync(Tenant, Table, Arg.Any<CancellationToken>())
            .Returns(new ConsentWorkflowTableInputs(routes, new HashSet<string>(["owner-1"])));

        var router = new ConsentWorkflowRouter(
            auditRepository: Substitute.For<IAuditLogRepository>(),
            adminRepository: adminRepo,
            logger: NullLogger<ConsentWorkflowRouter>.Instance,
            timeProvider: new FakeTimeProvider(FixedNow));

        var resolution = await router.ResolveForTableAsync(Tenant, Table);

        resolution.Mode.ShouldBe(ConsentWorkflowMode.External);
        resolution.ExternalSystemId.ShouldBe(dsSystemId);
        resolution.MaxDurationDays.ShouldBe(14);
        resolution.RequestTimeoutDays.ShouldBe(7);
        resolution.MatchedScopeKind.ShouldBe(WorkflowRouteScopeKind.DataSource);
        resolution.ConflictDetected.ShouldBeFalse();
    }

    [Fact]
    public async Task Conflicting_Owner_Routes_FallBackToInternal()
    {
        var systemA = Guid.NewGuid();
        var systemB = Guid.NewGuid();

        var routes = new List<ConsentWorkflowRoute>
        {
            new(Tenant.Value, WorkflowRouteScopeKind.Tenant, "*", ConsentWorkflowMode.External, systemA, 60, 30, true, "admin", FixedNow),
            new(Tenant.Value, WorkflowRouteScopeKind.DataOwner, "owner-1", ConsentWorkflowMode.External, systemA, 30, 15, true, "admin", FixedNow),
            new(Tenant.Value, WorkflowRouteScopeKind.DataOwner, "owner-2", ConsentWorkflowMode.External, systemB, 45, 20, true, "admin", FixedNow)
        };

        var adminRepo = Substitute.For<IConsentWorkflowAdminRepository>();
        adminRepo.GetRoutingInputsForTableAsync(Tenant, Table, Arg.Any<CancellationToken>())
            .Returns(new ConsentWorkflowTableInputs(routes, new HashSet<string>(["owner-1", "owner-2"])));

        var auditEntries = new List<AuditLogEntry>();
        var auditRepo = Substitute.For<IAuditLogRepository>();
        auditRepo.RecordAuditEventAsync(Arg.Do<AuditLogEntry>(e => auditEntries.Add(e)), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var router = new ConsentWorkflowRouter(
            auditRepository: auditRepo,
            adminRepository: adminRepo,
            logger: NullLogger<ConsentWorkflowRouter>.Instance,
            timeProvider: new FakeTimeProvider(FixedNow));

        var resolution = await router.ResolveForTableAsync(Tenant, Table);

        resolution.Mode.ShouldBe(ConsentWorkflowMode.Internal);
        resolution.ExternalSystemId.ShouldBeNull();
        resolution.ConflictDetected.ShouldBeTrue();
        auditEntries.ShouldHaveSingleItem();
        auditEntries[0].EventType.ShouldBe(AuditEventTypes.ConsentRouteConflict);
        auditEntries[0].Decision.ShouldBe("FALLBACK_INTERNAL");
    }

    [Fact]
    public async Task No_Route_IsInternal()
    {
        var adminRepo = Substitute.For<IConsentWorkflowAdminRepository>();
        adminRepo.GetRoutingInputsForTableAsync(Tenant, Table, Arg.Any<CancellationToken>())
            .Returns(new ConsentWorkflowTableInputs([], new HashSet<string>()));

        var router = new ConsentWorkflowRouter(
            auditRepository: Substitute.For<IAuditLogRepository>(),
            adminRepository: adminRepo,
            logger: NullLogger<ConsentWorkflowRouter>.Instance,
            timeProvider: new FakeTimeProvider(FixedNow));

        var resolution = await router.ResolveForTableAsync(Tenant, Table);

        resolution.Mode.ShouldBe(ConsentWorkflowMode.Internal);
        resolution.ExternalSystemId.ShouldBeNull();
        resolution.MaxDurationDays.ShouldBe(ConsentWorkflowRouter.FallbackMaxDurationDays);
        resolution.RequestTimeoutDays.ShouldBe(ConsentWorkflowRouter.FallbackRequestTimeoutDays);
        resolution.MatchedScopeKind.ShouldBeNull();
        resolution.ConflictDetected.ShouldBeFalse();
    }

    [Fact]
    public async Task Owner_Max_Above_Tenant_IsRejected()
    {
        var adminRepo = Substitute.For<IConsentWorkflowAdminRepository>();
        adminRepo.GetWorkflowRouteAsync(Tenant, WorkflowRouteScopeKind.Tenant, "*", Arg.Any<CancellationToken>())
            .Returns(new ConsentWorkflowRoute(Tenant.Value, WorkflowRouteScopeKind.Tenant, "*", ConsentWorkflowMode.Internal, null, 30, 14, true, "admin", FixedNow));

        var configRepo = Substitute.For<IConsentWorkflowConfigRepository>();
        var body = new ConsentWorkflowAdminEndpoints.UpsertWorkflowRouteRequest(
            Mode: "INTERNAL",
            ExternalSystemId: null,
            MaxDurationDays: 45,
            RequestTimeoutDays: 14);

        var httpContext = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("tenant_id", Tenant.Value),
                new Claim("sub", "S-1-5-21-1001"),
                new Claim(ClaimTypes.Role, "GovernanceAdmin")
            ], "Test"))
        };
        httpContext.Response.Body = new MemoryStream();

        var result = await ConsentWorkflowAdminEndpoints.UpsertRouteAsync(
            "DATA_OWNER",
            "owner-1",
            body,
            httpContext,
            adminRepo,
            configRepo,
            new FakeTimeProvider(FixedNow),
            CancellationToken.None);

        await result.ExecuteAsync(httpContext);
        httpContext.Response.StatusCode.ShouldBe(StatusCodes.Status422UnprocessableEntity);

        httpContext.Response.Body.Position = 0;
        using var doc = await JsonDocument.ParseAsync(httpContext.Response.Body);
        doc.RootElement.GetProperty("code").GetString().ShouldBe("VALIDITY_EXCEEDS_MAXIMUM");
        await adminRepo.DidNotReceive().UpsertWorkflowRouteAsync(Arg.Any<ConsentWorkflowRoute>(), Arg.Any<CancellationToken>());
    }
}
