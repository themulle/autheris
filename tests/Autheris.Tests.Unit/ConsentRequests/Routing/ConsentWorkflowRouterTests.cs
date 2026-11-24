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
using Autheris.Domain.Security;
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

    private static readonly Guid DomainId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    private sealed class StubProtection(Guid? domainId) : IProtectionSnapshotProvider
    {
        public ValueTask<EffectiveProtection> GetAsync(TenantId tenant, TableIdentifier table, CancellationToken ct = default) =>
            ValueTask.FromResult(new EffectiveProtection(
                domainId is { } id ? new ContainerRef(ContainerKind.Domain, id) : default,
                null,
                ProtectionClass.Confidential,
                ProtectionClass.Confidential,
                null,
                new Dictionary<string, ColumnProtection>(),
                CatalogVisibilityMode.Open));
    }

    private static ConsentWorkflowRouter Router(IReadOnlyList<ConsentWorkflowRoute> routes, Guid? tableDomain)
    {
        var adminRepo = Substitute.For<IConsentWorkflowAdminRepository>();
        adminRepo.GetRoutingInputsForTableAsync(Tenant, Table, Arg.Any<CancellationToken>())
            .Returns(new ConsentWorkflowTableInputs(routes, new HashSet<string>(["owner-1"])));
        return new ConsentWorkflowRouter(
            auditRepository: Substitute.For<IAuditLogRepository>(),
            adminRepository: adminRepo,
            logger: NullLogger<ConsentWorkflowRouter>.Instance,
            timeProvider: new FakeTimeProvider(FixedNow),
            protectionProvider: new StubProtection(tableDomain));
    }

    [Fact]
    public async Task Domain_Route_WinsOverTenant()
    {
        var domainSystemId = Guid.NewGuid();
        var routes = new List<ConsentWorkflowRoute>
        {
            new(Tenant.Value, WorkflowRouteScopeKind.Tenant, "*", ConsentWorkflowMode.External, Guid.NewGuid(), 90, 30, 72, true, "admin", FixedNow),
            new(Tenant.Value, WorkflowRouteScopeKind.Domain, DomainId.ToString("D"), ConsentWorkflowMode.External, domainSystemId, 14, 7, 24, true, "admin", FixedNow)
        };

        var resolution = await Router(routes, DomainId).ResolveForTableAsync(Tenant, Table);

        resolution.Mode.ShouldBe(ConsentWorkflowMode.External);
        resolution.ExternalSystemId.ShouldBe(domainSystemId);
        resolution.MaxDurationDays.ShouldBe(14);
        resolution.MatchedScopeKind.ShouldBe(WorkflowRouteScopeKind.Domain);
        resolution.ConflictDetected.ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Removed_Or_Unknown_Scope_Routes_Are_Inert()
    {
        // Routes whose scope kind was removed (data owner / data source) or is unknown never match, never auto-approve and never act as wildcard.
        var routes = new List<ConsentWorkflowRoute>
        {
            new(Tenant.Value, WorkflowRouteScopeKind.Unknown, "owner-1", ConsentWorkflowMode.External, Guid.NewGuid(), 30, 15, 24, true, "admin", FixedNow),
            new(Tenant.Value, (WorkflowRouteScopeKind)99, "sales_db", ConsentWorkflowMode.External, Guid.NewGuid(), 30, 15, 24, true, "admin", FixedNow),
            new(Tenant.Value, WorkflowRouteScopeKind.Unknown, "*", ConsentWorkflowMode.External, Guid.NewGuid(), 30, 15, 24, true, "admin", FixedNow)
        };

        var resolution = await Router(routes, DomainId).ResolveForTableAsync(Tenant, Table);

        resolution.Mode.ShouldBe(ConsentWorkflowMode.Internal);
        resolution.ExternalSystemId.ShouldBeNull();
        resolution.MatchedScopeKind.ShouldBeNull();
    }

    [Fact]
    public void Stored_Removed_Scope_Strings_Parse_To_Unknown_Never_Tenant()
    {
        ConsentWorkflowSqlMapping.TryParseRouteScopeKind("DATA_OWNER", out var owner).ShouldBeFalse();
        ConsentWorkflowSqlMapping.TryParseRouteScopeKind("DATA_SOURCE", out var source).ShouldBeFalse();
        ConsentWorkflowSqlMapping.TryParseRouteScopeKind("DOMAIN", out var domain).ShouldBeTrue();
        owner.ShouldBe(WorkflowRouteScopeKind.Unknown);
        source.ShouldBe(WorkflowRouteScopeKind.Unknown);
        domain.ShouldBe(WorkflowRouteScopeKind.Domain);
    }

    [Fact]
    public async Task Domain_Route_Does_Not_Match_A_Table_In_Another_Or_No_Domain()
    {
        var routes = new List<ConsentWorkflowRoute>
        {
            new(Tenant.Value, WorkflowRouteScopeKind.Domain, DomainId.ToString("D"), ConsentWorkflowMode.External, Guid.NewGuid(), 14, 7, 24, true, "admin", FixedNow)
        };

        (await Router(routes, Guid.NewGuid()).ResolveForTableAsync(Tenant, Table)).Mode.ShouldBe(ConsentWorkflowMode.Internal);
        (await Router(routes, null).ResolveForTableAsync(Tenant, Table)).Mode.ShouldBe(ConsentWorkflowMode.Internal);
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
        resolution.ExpiryNoticeLeadHours.ShouldBe(ConsentWorkflowRouter.FallbackExpiryNoticeLeadHours);
        resolution.MatchedScopeKind.ShouldBeNull();
        resolution.ConflictDetected.ShouldBeFalse();
    }

    [Fact]
    public async Task Domain_Max_Above_Tenant_IsRejected()
    {
        var adminRepo = Substitute.For<IConsentWorkflowAdminRepository>();
        adminRepo.GetWorkflowRouteAsync(Tenant, WorkflowRouteScopeKind.Tenant, "*", Arg.Any<CancellationToken>())
            .Returns(new ConsentWorkflowRoute(Tenant.Value, WorkflowRouteScopeKind.Tenant, "*", ConsentWorkflowMode.Internal, null, 30, 14, 48, true, "admin", FixedNow));

        var configRepo = Substitute.For<IConsentWorkflowConfigRepository>();
        var body = new ConsentWorkflowAdminEndpoints.UpsertWorkflowRouteRequest(
            Mode: "INTERNAL",
            ExternalSystemId: null,
            MaxDurationDays: 45,
            RequestTimeoutDays: 14,
            ExpiryNoticeLeadHours: 24);

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
            "DOMAIN",
            "dom-1",
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
