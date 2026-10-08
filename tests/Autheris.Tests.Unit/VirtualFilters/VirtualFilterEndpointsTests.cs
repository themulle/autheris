namespace Autheris.Tests.Unit.VirtualFilters;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Interfaces;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Virtual filters, phase 2: administration API. FilterAdmin writes, FilterSync applies the file repository state,
/// GovernanceAdmin and SecurityAuditor read; everybody stays within their tenant (decision 3).
/// </summary>
public sealed class VirtualFilterEndpointsTests : IDisposable
{
    private const string Tenant = "tenant_lwe";

    private readonly SqliteGovernanceRepository _repository = new(
        Substitute.For<IEpochValidationService>(),
        Options.Create(new GatewayOptions { GovernanceDb = new GovernanceDbOptions { ConnectionString = $"Data Source=vfe_{Guid.NewGuid():N};Mode=Memory;Cache=Shared" } }));

    private readonly VirtualFilterAdministrationService _service;

    public VirtualFilterEndpointsTests()
    {
        _service = new VirtualFilterAdministrationService(_repository, Substitute.For<IAuditLogRepository>());
    }

    public void Dispose() => _repository.Dispose();

    private static HttpContext Context(string tenant, object? body, params string[] roles)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider()
        };
        context.Items[SecurityPrincipalContext.ItemKey] = new SecurityPrincipalContext
        {
            UserSid = new Sid("S-1-5-21-CALLER"),
            TenantId = new TenantId(tenant),
            GroupSids = new HashSet<Sid>(),
            // ClusterAdmin and GovernanceAdmin are global roles; tenant-scoped roles never satisfy them.
            TenantRoles = new HashSet<string>(roles.Where(r => r is not ("ClusterAdmin" or "GovernanceAdmin"))),
            ClusterRoles = new HashSet<string>(roles.Where(r => r is "ClusterAdmin" or "GovernanceAdmin")),
            AuthenticationScheme = "Test"
        };
        context.Response.Body = new MemoryStream();
        if (body != null)
        {
            context.Request.ContentType = "application/json";
            context.Request.Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(body));
        }

        return context;
    }

    private static async Task<int> StatusAsync(IResult result, HttpContext context)
    {
        await result.ExecuteAsync(context);
        return context.Response.StatusCode;
    }

    private static object DavidFilterBody(string? tenant = null) => new
    {
        tenant,
        source = "lwetem_prod",
        key_columns = new[] { "client.client_id" },
        from = new { table = "conf.client", alias = "client" },
        joins = new[] { new { table = "md.crane", alias = "crane", left = "crane.serial_number", right = "client.crane_serial_number" } },
        where = new[] { new { column = "crane.is_delivered", op = "is_null" } }
    };

    private static object DavidProfileBody(string filter = "nicht_ausgelieferte_krane") => new
    {
        grantee = new { type = "user", sid = "S-1-5-21-LWE-DAVID" },
        scope = "lwetem_prod.*.*",
        uncovered = "deny",
        bindings = new[] { new { filter, target = "lwetem_prod.*.*.client_id" } }
    };

    [Theory]
    [InlineData("Consumer")]
    [InlineData("DataOwner")]
    [InlineData("GovernanceAdmin")]
    [InlineData("FilterSync")]
    public async Task PutFilter_WithoutFilterAdmin_Returns403(string role)
    {
        var context = Context(Tenant, DavidFilterBody(), role);

        var status = await StatusAsync(await VirtualFilterEndpoints.PutFilterAsync("nicht_ausgelieferte_krane", context, _service), context);

        status.ShouldBe(StatusCodes.Status403Forbidden);
        (await _repository.LoadSnapshotAsync()).Filters.ShouldBeEmpty();
    }

    [Fact]
    public async Task PutFilterAndProfile_AsFilterAdmin_AreStoredInTheCallersTenant()
    {
        var putFilter = Context(Tenant, DavidFilterBody(), "FilterAdmin");
        (await StatusAsync(await VirtualFilterEndpoints.PutFilterAsync("nicht_ausgelieferte_krane", putFilter, _service), putFilter)).ShouldBe(StatusCodes.Status200OK);

        var putProfile = Context(Tenant, DavidProfileBody(), "FilterAdmin");
        (await StatusAsync(await VirtualFilterEndpoints.PutProfileAsync("david", putProfile, _service), putProfile)).ShouldBe(StatusCodes.Status200OK);

        var snapshot = await _repository.LoadSnapshotAsync();
        var filter = snapshot.Filters.ShouldHaveSingleItem();
        filter.TenantId.ShouldBe(new TenantId(Tenant));
        filter.Structured!.From.ShouldBe(new TableIdentifier("lwetem_prod", "conf", "client"));
        filter.Structured.Where.ShouldHaveSingleItem().Operator.ShouldBe(FilterConditionOperator.IsNull);
        var profile = snapshot.Profiles.ShouldHaveSingleItem();
        profile.Name.ShouldBe("david");
        profile.Uncovered.ShouldBe(UncoveredPolicy.Deny);
        profile.GranteeSid.ShouldBe(new Sid("S-1-5-21-LWE-DAVID"));
        profile.Bindings.ShouldHaveSingleItem().TargetPattern.ShouldBe("lwetem_prod.*.*.client_id");
    }

    [Fact]
    public async Task PutFilter_ForAnotherTenant_Returns403_UnlessClusterAdmin()
    {
        var foreign = Context(Tenant, DavidFilterBody(tenant: "other"), "FilterAdmin");
        (await StatusAsync(await VirtualFilterEndpoints.PutFilterAsync("nicht_ausgelieferte_krane", foreign, _service), foreign)).ShouldBe(StatusCodes.Status403Forbidden);

        var cluster = Context(Tenant, DavidFilterBody(tenant: "other"), "ClusterAdmin");
        (await StatusAsync(await VirtualFilterEndpoints.PutFilterAsync("nicht_ausgelieferte_krane", cluster, _service), cluster)).ShouldBe(StatusCodes.Status200OK);
        (await _repository.LoadSnapshotAsync()).Filters.ShouldHaveSingleItem().TenantId.ShouldBe(new TenantId("other"));
    }

    [Fact]
    public async Task InvalidInput_Returns400_WithReason()
    {
        var context = Context(Tenant, DavidProfileBody(), "FilterAdmin");   // binds an unknown filter

        var status = await StatusAsync(await VirtualFilterEndpoints.PutProfileAsync("david", context, _service), context);

        status.ShouldBe(StatusCodes.Status400BadRequest);
        context.Response.Body.Position = 0;
        (await new StreamReader(context.Response.Body).ReadToEndAsync()).ShouldContain("nicht_ausgelieferte_krane");
    }

    [Fact]
    public async Task ManagedRow_Returns409()
    {
        await _service.ApplySyncAsync(new VirtualFilterSyncRequest(new TenantId(Tenant), new ManagedBy("governance/access", "c1"), [VirtualFilterModelTests.DavidFilter()], []),
            new VirtualFilterActor(new Sid("S-1-5-21-TALOS"), IsSync: true));
        var context = Context(Tenant, DavidFilterBody(), "FilterAdmin");

        (await StatusAsync(await VirtualFilterEndpoints.PutFilterAsync("nicht_ausgelieferte_krane", context, _service), context)).ShouldBe(StatusCodes.Status409Conflict);
    }

    [Theory]
    [InlineData("GovernanceAdmin", StatusCodes.Status200OK)]
    [InlineData("SecurityAuditor", StatusCodes.Status200OK)]
    [InlineData("FilterAdmin", StatusCodes.Status200OK)]
    [InlineData("Consumer", StatusCodes.Status403Forbidden)]
    public async Task List_IsReadableForGovernanceRoles(string role, int expected)
    {
        var context = Context(Tenant, null, role);

        (await StatusAsync(await VirtualFilterEndpoints.ListAsync(context, _service), context)).ShouldBe(expected);
    }

    [Fact]
    public async Task List_ShowsOnlyTheCallersTenant()
    {
        await _service.SaveFilterAsync(VirtualFilterModelTests.DavidFilter(), new VirtualFilterActor(new Sid("S-1-5-21-A"), false));
        await _service.SaveFilterAsync(VirtualFilterModelTests.DavidFilter() with { TenantId = new TenantId("other") }, new VirtualFilterActor(new Sid("S-1-5-21-A"), false));
        var context = Context(Tenant, null, "GovernanceAdmin");

        await StatusAsync(await VirtualFilterEndpoints.ListAsync(context, _service), context);

        context.Response.Body.Position = 0;
        using var doc = await JsonDocument.ParseAsync(context.Response.Body);
        doc.RootElement.GetProperty("filters").GetArrayLength().ShouldBe(1);
    }

    private static object SyncBody(string commit) => new
    {
        path = "governance/access",
        commit,
        filters = new Dictionary<string, object> { ["nicht_ausgelieferte_krane"] = DavidFilterBody() },
        profiles = new Dictionary<string, object> { ["david"] = DavidProfileBody() }
    };

    [Fact]
    public async Task SyncPlan_AndApply_UseTheirOwnRoles()
    {
        var plan = Context(Tenant, SyncBody("c1"), "FilterSync");
        (await StatusAsync(await VirtualFilterEndpoints.PlanSyncAsync(plan, _service), plan)).ShouldBe(StatusCodes.Status200OK);
        plan.Response.Body.Position = 0;
        (await new StreamReader(plan.Response.Body).ReadToEndAsync()).ShouldContain("nicht_ausgelieferte_krane");

        var applyAsAdmin = Context(Tenant, SyncBody("c1"), "FilterAdmin");
        (await StatusAsync(await VirtualFilterEndpoints.ApplySyncAsync(applyAsAdmin, _service), applyAsAdmin)).ShouldBe(StatusCodes.Status403Forbidden);

        var apply = Context(Tenant, SyncBody("c1"), "FilterSync");
        (await StatusAsync(await VirtualFilterEndpoints.ApplySyncAsync(apply, _service), apply)).ShouldBe(StatusCodes.Status200OK);
        var snapshot = await _repository.LoadSnapshotAsync();
        snapshot.Filters.ShouldHaveSingleItem().ManagedBy.ShouldBe(new ManagedBy("governance/access", "c1"));
        snapshot.Profiles.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task ApplySync_WithForce_NeedsFilterAdmin()
    {
        var apply = Context(Tenant, SyncBody("c1"), "FilterSync");
        await StatusAsync(await VirtualFilterEndpoints.ApplySyncAsync(apply, _service), apply);
        object emptied = new { path = "governance/access", commit = "c2" };

        var refused = Context(Tenant, emptied, "FilterSync");
        (await StatusAsync(await VirtualFilterEndpoints.ApplySyncAsync(refused, _service), refused)).ShouldBe(StatusCodes.Status409Conflict);

        var forcedBySync = Context(Tenant, emptied, "FilterSync");
        forcedBySync.Request.QueryString = new QueryString("?force=true");
        (await StatusAsync(await VirtualFilterEndpoints.ApplySyncAsync(forcedBySync, _service), forcedBySync)).ShouldBe(StatusCodes.Status403Forbidden);

        // SR15-07: FilterAdmin alone cannot force sync
        var forcedByAdminOnly = Context(Tenant, emptied, "FilterAdmin");
        forcedByAdminOnly.Request.QueryString = new QueryString("?force=true");
        (await StatusAsync(await VirtualFilterEndpoints.ApplySyncAsync(forcedByAdminOnly, _service), forcedByAdminOnly)).ShouldBe(StatusCodes.Status403Forbidden);

        // SR15-07: force=true requires BOTH FilterSync AND FilterAdmin roles
        var forcedBySyncAndAdmin = Context(Tenant, emptied, "FilterSync", "FilterAdmin");
        forcedBySyncAndAdmin.Request.QueryString = new QueryString("?force=true");
        (await StatusAsync(await VirtualFilterEndpoints.ApplySyncAsync(forcedBySyncAndAdmin, _service), forcedBySyncAndAdmin)).ShouldBe(StatusCodes.Status200OK);
        (await _repository.LoadSnapshotAsync()).Profiles.ShouldBeEmpty();
    }

    [Fact]
    public void Roles_FilterAdminAndFilterSync_AreNotImpliedByGovernanceAdmin()
    {
        GatewayRoleExtensions.TryParseRole("FilterAdmin", out var admin).ShouldBeTrue();
        GatewayRoleExtensions.TryParseRole("FilterSync", out var sync).ShouldBeTrue();
        GatewayRole.GovernanceAdmin.Implies(admin).ShouldBeFalse();
        GatewayRole.GovernanceAdmin.Implies(sync).ShouldBeFalse();
        GatewayRole.TenantAdmin.Implies(admin).ShouldBeFalse();
        GatewayRole.ClusterAdmin.Implies(admin).ShouldBeTrue();
        admin.Implies(sync).ShouldBeFalse();
    }

    [Fact]
    public async Task ConfigSyncStatus_ReturnsMetadataAndCounts()
    {
        await _service.SaveFilterAsync(VirtualFilterModelTests.DavidFilter(), new VirtualFilterActor(new Sid("S-1-5-21-A"), false));
        var context = Context(Tenant, null, "GovernanceAdmin");

        var result = await VirtualFilterEndpoints.ConfigSyncStatusAsync(context, _service);
        var status = await StatusAsync(result, context);

        status.ShouldBe(StatusCodes.Status200OK);
        context.Response.Body.Position = 0;
        using var doc = await JsonDocument.ParseAsync(context.Response.Body);
        doc.RootElement.GetProperty("status").GetString().ShouldBe("InSync");
        doc.RootElement.GetProperty("filterCount").GetInt32().ShouldBe(1);
        doc.RootElement.GetProperty("profileCount").GetInt32().ShouldBe(0);
        doc.RootElement.GetProperty("requireApproval").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task ConfigSyncStatus_Forbidden_ForUnauthorizedRoles()
    {
        var context = Context(Tenant, null, "Consumer");
        var result = await VirtualFilterEndpoints.ConfigSyncStatusAsync(context, _service);
        var status = await StatusAsync(result, context);
        status.ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task PutFilter_WithRequireApproval_SavesPendingAndRequiresDistinctApprover()
    {
        var approvalOptions = Options.Create(new GatewayOptions { VirtualFilters = new VirtualFilterOptions { RequireApproval = true } });
        var approvalService = new VirtualFilterAdministrationService(_repository, Substitute.For<IAuditLogRepository>(), approvalOptions);

        // FilterAdmin creates filter -> saved as PendingApproval (HTTP 200)
        var createContext = Context(Tenant, DavidFilterBody(), "FilterAdmin");
        var putResult = await VirtualFilterEndpoints.PutFilterAsync("filter_test", createContext, approvalService);
        var putStatus = await StatusAsync(putResult, createContext);
        putStatus.ShouldBe(StatusCodes.Status200OK);

        // Creator attempts to approve -> HTTP 400 (Four-eyes principle violation)
        var selfApproveContext = Context(Tenant, null, "GovernanceAdmin"); // UserSid is "S-1-5-21-CALLER" by default in Context()
        var selfApproveResult = await VirtualFilterEndpoints.ApproveFilterAsync("filter_test", selfApproveContext, approvalService);
        var selfApproveStatus = await StatusAsync(selfApproveResult, selfApproveContext);
        selfApproveStatus.ShouldBe(StatusCodes.Status400BadRequest);

        // Non-admin attempts to approve -> HTTP 403 Forbidden
        var unauthorizedContext = Context(Tenant, null, "Consumer");
        var unauthResult = await VirtualFilterEndpoints.ApproveFilterAsync("filter_test", unauthorizedContext, approvalService);
        var unauthStatus = await StatusAsync(unauthResult, unauthorizedContext);
        unauthStatus.ShouldBe(StatusCodes.Status403Forbidden);

        // Distinct GovernanceAdmin approves -> HTTP 200 OK
        var supervisorContext = Context(Tenant, null, "GovernanceAdmin");
        var supervisorSec = (SecurityPrincipalContext)supervisorContext.Items[SecurityPrincipalContext.ItemKey]!;
        supervisorContext.Items[SecurityPrincipalContext.ItemKey] = supervisorSec with { UserSid = new Sid("S-1-5-21-SUPERVISOR") };
        var approveResult = await VirtualFilterEndpoints.ApproveFilterAsync("filter_test", supervisorContext, approvalService);
        var approveStatus = await StatusAsync(approveResult, supervisorContext);
        approveStatus.ShouldBe(StatusCodes.Status200OK);

        var snapshot = await _repository.LoadSnapshotAsync();
        var stored = snapshot.Filters.ShouldHaveSingleItem();
        stored.Status.ShouldBe(FilterApprovalStatus.Active);
        stored.ApprovedBy.ShouldBe(new Sid("S-1-5-21-SUPERVISOR"));
    }
}
