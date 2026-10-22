namespace Autheris.Tests.Unit.VirtualFilters;

using Autheris.Tests.Unit.TestDoubles;
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

    private readonly SqliteGovernanceRepository _repository = GovernanceStoreFactory.CreateSqlite(
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
            RequestServices = new ServiceCollection().AddLogging().AddAuditWriterOver(Substitute.For<IAuditLogRepository>()).BuildServiceProvider()
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

    [Fact]
    [Trait("Category", "Security")]
    public async Task List_ExposesPendingHash_TheValueApprovalCompares()
    {
        var approvalOptions = Options.Create(new GatewayOptions { VirtualFilters = new VirtualFilterOptions { RequireApproval = true } });
        var approvalService = new VirtualFilterAdministrationService(_repository, Substitute.For<IAuditLogRepository>(), approvalOptions);
        var createContext = Context(Tenant, DavidFilterBody(), "FilterAdmin");
        await StatusAsync(await VirtualFilterEndpoints.PutFilterAsync("filter_pending_hash", createContext, approvalService), createContext);
        var stored = (await _repository.LoadSnapshotAsync()).Filters.Single();

        var context = Context(Tenant, null, "GovernanceAdmin");
        await StatusAsync(await VirtualFilterEndpoints.ListAsync(context, approvalService), context);

        context.Response.Body.Position = 0;
        using var doc = await JsonDocument.ParseAsync(context.Response.Body);
        var view = doc.RootElement.GetProperty("filters")[0];
        view.GetProperty("pending_hash").GetString().ShouldBe(VirtualFilterAdministrationService.PendingHash(stored));
        view.GetProperty("pending_hash").GetString().ShouldNotBeNullOrWhiteSpace();
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

        // VF-05: every approval carries the reviewed (pending) hash.
        var pendingHash = VirtualFilterAdministrationService.PendingHash((await _repository.LoadSnapshotAsync()).Filters.Single());

        // Creator attempts to approve -> HTTP 400 (Four-eyes principle violation)
        var selfApproveContext = Context(Tenant, null, "GovernanceAdmin"); // UserSid is "S-1-5-21-CALLER" by default in Context()
        selfApproveContext.Request.QueryString = new QueryString($"?hash={pendingHash}");
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
        supervisorContext.Request.QueryString = new QueryString($"?hash={pendingHash}");
        var approveResult = await VirtualFilterEndpoints.ApproveFilterAsync("filter_test", supervisorContext, approvalService);
        var approveStatus = await StatusAsync(approveResult, supervisorContext);
        approveStatus.ShouldBe(StatusCodes.Status200OK);

        var snapshot = await _repository.LoadSnapshotAsync();
        var stored = snapshot.Filters.ShouldHaveSingleItem();
        stored.Status.ShouldBe(FilterApprovalStatus.Active);
        stored.ApprovedBy.ShouldBe(new Sid("S-1-5-21-SUPERVISOR"));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Endpoint_ApproveWithoutHash_Returns400()
    {
        var approvalOptions = Options.Create(new GatewayOptions { VirtualFilters = new VirtualFilterOptions { RequireApproval = true } });
        var approvalService = new VirtualFilterAdministrationService(_repository, Substitute.For<IAuditLogRepository>(), approvalOptions);
        var createContext = Context(Tenant, DavidFilterBody(), "FilterAdmin");
        (await StatusAsync(await VirtualFilterEndpoints.PutFilterAsync("filter_nohash", createContext, approvalService), createContext)).ShouldBe(StatusCodes.Status200OK);

        var approve = Context(Tenant, null, "GovernanceAdmin");
        var sec = (SecurityPrincipalContext)approve.Items[SecurityPrincipalContext.ItemKey]!;
        approve.Items[SecurityPrincipalContext.ItemKey] = sec with { UserSid = new Sid("S-1-5-21-SUPERVISOR") };

        var status = await StatusAsync(await VirtualFilterEndpoints.ApproveFilterAsync("filter_nohash", approve, approvalService), approve);

        status.ShouldBe(StatusCodes.Status400BadRequest);
        approve.Response.Body.Position = 0;
        new StreamReader(approve.Response.Body).ReadToEnd().ShouldContain("expected_hash_required");
        (await _repository.LoadSnapshotAsync()).Filters.Single().Status.ShouldBe(FilterApprovalStatus.PendingApproval);
    }

    [Fact]
    public async Task ConfigSyncStatus_WithPendingApproval_ReturnsPendingApprovalStatus()
    {
        // SR15-47: Dynamic status reporting based on filter states
        var approvalOptions = Options.Create(new GatewayOptions { VirtualFilters = new VirtualFilterOptions { RequireApproval = true } });
        var approvalService = new VirtualFilterAdministrationService(_repository, Substitute.For<IAuditLogRepository>(), approvalOptions);

        var createContext = Context(Tenant, DavidFilterBody(), "FilterAdmin");
        await VirtualFilterEndpoints.PutFilterAsync("filter_pending", createContext, approvalService);

        var context = Context(Tenant, null, "GovernanceAdmin");
        var result = await VirtualFilterEndpoints.ConfigSyncStatusAsync(context, approvalService);
        await StatusAsync(result, context);

        context.Response.Body.Position = 0;
        using var doc = await JsonDocument.ParseAsync(context.Response.Body);
        doc.RootElement.GetProperty("status").GetString().ShouldBe("PendingApproval");
    }

    [Fact]
    public async Task PutFilter_InProduction_SanitizesInvalidOperationException()
    {
        // SR15-49: InvalidOperationException messages should not leak internal database details in production
        var prodEnv = Substitute.For<Microsoft.Extensions.Hosting.IHostEnvironment>();
        prodEnv.EnvironmentName.Returns("Production");

        var services = new ServiceCollection();
        services.AddSingleton(prodEnv);
        services.AddLogging();
        services.AddAuditWriterOver(Substitute.For<IAuditLogRepository>());
        var sp = services.BuildServiceProvider();

        var context = Context(Tenant, DavidFilterBody(), "FilterAdmin");
        context.RequestServices = sp;

        var failingRepo = Substitute.For<IVirtualFilterRepository>();
        failingRepo.LoadSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new VirtualFilterSnapshot(0, Array.Empty<VirtualFilter>(), Array.Empty<VirtualFilterAccessProfile>())));
        failingRepo.When(r => r.ApplyAsync(Arg.Any<VirtualFilterChangeSet>(), Arg.Any<CancellationToken>()))
            .Do(_ => throw new InvalidOperationException("internal database connection string or schema leak"));

        var adminService = new VirtualFilterAdministrationService(failingRepo, Substitute.For<IAuditLogRepository>());

        var result = await VirtualFilterEndpoints.PutFilterAsync("test_filter", context, adminService);
        var status = await StatusAsync(result, context);

        status.ShouldBe(StatusCodes.Status400BadRequest);
        context.Response.Body.Position = 0;
        using var doc = await JsonDocument.ParseAsync(context.Response.Body);
        var error = doc.RootElement.GetProperty("error").GetString();
        error.ShouldNotBeNull();
        error.ShouldBe("The requested virtual filter operation is invalid.");
        error.ShouldNotContain("internal database connection string or schema leak");
    }

    // ---- Binding federation options: strategy, pushdown_format, max_pushdown_keys ----

    private static object BindingProfile(object binding) => new
    {
        grantee = new { type = "user", sid = "S-1-5-21-LWE-DAVID" },
        scope = "web_crm.*.*",
        uncovered = "deny",
        bindings = new[] { binding }
    };

    private async Task PutFilterAsync()
    {
        var put = Context(Tenant, DavidFilterBody(), "FilterAdmin");
        (await StatusAsync(await VirtualFilterEndpoints.PutFilterAsync("nicht_ausgelieferte_krane", put, _service), put)).ShouldBe(StatusCodes.Status200OK);
    }

    /// <summary>A service wired like production: gateway options and catalog come from DI, the endpoint does not validate bindings.</summary>
    private VirtualFilterAdministrationService ServiceWith(Action<IServiceCollection>? configure)
    {
        var services = new ServiceCollection().AddLogging();
        configure?.Invoke(services);
        var provider = services.BuildServiceProvider();
        return new VirtualFilterAdministrationService(
            _repository,
            Substitute.For<IAuditLogRepository>(),
            provider.GetService<IOptions<GatewayOptions>>(),
            snapshots: null,
            catalog: provider.GetService<ITableMetadataRepository>());
    }

    private async Task<(int Status, string Body)> PutBindingAsync(object binding, Action<IServiceCollection>? configure = null)
    {
        var context = Context(Tenant, BindingProfile(binding), "FilterAdmin");
        var status = await StatusAsync(await VirtualFilterEndpoints.PutProfileAsync("david", context, ServiceWith(configure)), context);
        context.Response.Body.Position = 0;
        return (status, await new StreamReader(context.Response.Body).ReadToEndAsync());
    }

    private static Action<IServiceCollection> CatalogWith(string table, HttpBatchType batch) => services =>
    {
        var id = TableIdentifier.Parse(table);
        var catalog = Substitute.For<ITableMetadataRepository>();
        catalog.GetTableMetadataAsync(id, Arg.Any<CancellationToken>()).Returns(new TableMetadata
        {
            Identifier = id,
            Table = new Table
            {
                Id = Guid.NewGuid(), SourceName = id.Domain, SchemaName = id.Schema, TableName = id.TableName,
                DataSourceType = DataSourceType.HttpPlugin, IsActive = true,
                HttpEndpoint = new HttpEndpointDescriptor
                {
                    BaseUrl = "https://crm.local", PathTemplate = "/customers", BatchType = batch,
                    BatchParamName = "ids", PrimaryKeyField = "id"
                }
            }
        });
        services.AddSingleton(catalog);
    };

    [Fact]
    public async Task PutProfile_WithoutFederationFields_KeepsTheDefaults()
    {
        await PutFilterAsync();

        (await PutBindingAsync(new { filter = "nicht_ausgelieferte_krane", target = "web_crm.*.*.id" })).Status.ShouldBe(StatusCodes.Status200OK);

        var binding = (await _repository.LoadSnapshotAsync()).Profiles.ShouldHaveSingleItem().Bindings.ShouldHaveSingleItem();
        binding.Strategy.ShouldBe(VirtualFilterExecutionStrategy.Adaptive);
        binding.PushdownFormat.ShouldBeNull();
        binding.MaxPushdownKeys.ShouldBe(100);
    }

    [Fact]
    public async Task PutProfile_WithFederationFields_StoresAndReturnsThem()
    {
        await PutFilterAsync();

        var (status, body) = await PutBindingAsync(
            new { filter = "nicht_ausgelieferte_krane", target = "web_crm.*.*.id", strategy = "pushdown_only", pushdown_format = "comma_separated", max_pushdown_keys = 250 });

        status.ShouldBe(StatusCodes.Status200OK);
        var binding = (await _repository.LoadSnapshotAsync()).Profiles.ShouldHaveSingleItem().Bindings.ShouldHaveSingleItem();
        binding.Strategy.ShouldBe(VirtualFilterExecutionStrategy.PushdownOnly);
        binding.PushdownFormat.ShouldBe(PushdownParameterFormat.CommaSeparated);
        binding.MaxPushdownKeys.ShouldBe(250);
        using var doc = JsonDocument.Parse(body);
        var view = doc.RootElement.GetProperty("bindings")[0];
        view.GetProperty("strategy").GetString().ShouldBe("pushdown_only");
        view.GetProperty("pushdown_format").GetString().ShouldBe("comma_separated");
        view.GetProperty("max_pushdown_keys").GetInt32().ShouldBe(250);
    }

    [Theory]
    [InlineData("strategy", "turbo")]
    [InlineData("pushdown_format", "carrier_pigeon")]
    [InlineData("strategy", "DuckDbHashJoin")]
    [InlineData("strategy", "duckdbhashjoin")]
    [InlineData("strategy", "ADAPTIVE")]
    [InlineData("strategy", "duck_db_hash_join")]
    [InlineData("pushdown_format", "CommaSeparated")]
    [InlineData("pushdown_format", "commaseparated")]
    [InlineData("pushdown_format", "0")]
    public async Task PutProfile_WithUnknownOrLenientEnumValue_Returns400WithStableCode_AndStoresNothing(string field, string value)
    {
        await PutFilterAsync();
        var binding = new Dictionary<string, object> { ["filter"] = "nicht_ausgelieferte_krane", ["target"] = "web_crm.*.*.id", [field] = value };

        var (status, body) = await PutBindingAsync(binding);

        status.ShouldBe(StatusCodes.Status400BadRequest);
        JsonDocument.Parse(body).RootElement.GetProperty("code").GetString().ShouldBe("VF_BINDING_INVALID");
        (await _repository.LoadSnapshotAsync()).Profiles.ShouldBeEmpty();
    }

    [Fact]
    public async Task PutProfile_NumericEnumValue_Returns400()
    {
        await PutFilterAsync();

        var (status, body) = await PutBindingAsync(new { filter = "nicht_ausgelieferte_krane", target = "web_crm.*.*.id", strategy = 2 });

        status.ShouldBe(StatusCodes.Status400BadRequest);
        (await _repository.LoadSnapshotAsync()).Profiles.ShouldBeEmpty();
    }

    [Fact]
    public async Task PutProfile_EchoesTheDocumentedStrategyName_AndAcceptsItBack()
    {
        await PutFilterAsync();

        var (status, body) = await PutBindingAsync(new { filter = "nicht_ausgelieferte_krane", target = "web_crm.*.*.id", strategy = "duckdb_hash_join" });

        status.ShouldBe(StatusCodes.Status200OK);
        JsonDocument.Parse(body).RootElement.GetProperty("bindings")[0].GetProperty("strategy").GetString().ShouldBe("duckdb_hash_join");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1001)]
    public async Task PutProfile_WithMaxPushdownKeysOutsideTheHardCap_Returns400(int keys)
    {
        await PutFilterAsync();

        var (status, _) = await PutBindingAsync(new { filter = "nicht_ausgelieferte_krane", target = "web_crm.*.*.id", max_pushdown_keys = keys });

        status.ShouldBe(StatusCodes.Status400BadRequest);
        (await _repository.LoadSnapshotAsync()).Profiles.ShouldBeEmpty();
    }

    [Fact]
    public async Task PutProfile_WithMaxPushdownKeysAboveTheGatewayOption_Returns400()
    {
        await PutFilterAsync();
        // Real DI: the limit comes from Gateway:VirtualFilters:Federation:MaxPushdownKeys, the key the planner reads.
        void Configure(IServiceCollection s) => s.AddOptions<GatewayOptions>().Bind(
            Microsoft.Extensions.Configuration.MemoryConfigurationBuilderExtensions.AddInMemoryCollection(
                new Microsoft.Extensions.Configuration.ConfigurationBuilder(),
                new Dictionary<string, string?> { ["Gateway:VirtualFilters:Federation:MaxPushdownKeys"] = "50" })
                .Build().GetSection(GatewayOptions.SectionName));

        (await PutBindingAsync(new { filter = "nicht_ausgelieferte_krane", target = "web_crm.*.*.id", max_pushdown_keys = 51 }, Configure)).Status.ShouldBe(StatusCodes.Status400BadRequest);
        (await PutBindingAsync(new { filter = "nicht_ausgelieferte_krane", target = "web_crm.*.*.id", max_pushdown_keys = 50 }, Configure)).Status.ShouldBe(StatusCodes.Status200OK);
    }

    private static void GatewayLimit(IServiceCollection s, int limit) => s.AddOptions<GatewayOptions>().Bind(
        Microsoft.Extensions.Configuration.MemoryConfigurationBuilderExtensions.AddInMemoryCollection(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder(),
            new Dictionary<string, string?> { ["Gateway:VirtualFilters:Federation:MaxPushdownKeys"] = limit.ToString() })
            .Build().GetSection(GatewayOptions.SectionName));

    [Fact]
    public async Task PutProfile_OmittedMaxPushdownKeys_IsNotCheckedAgainstALowGatewayLimit()
    {
        await PutFilterAsync();
        void Configure(IServiceCollection s) => GatewayLimit(s, 50);

        var (status, _) = await PutBindingAsync(new { filter = "nicht_ausgelieferte_krane", target = "web_crm.*.*.id" }, Configure);

        status.ShouldBe(StatusCodes.Status200OK);
        (await _repository.LoadSnapshotAsync()).Profiles.ShouldHaveSingleItem().Bindings.ShouldHaveSingleItem()
            .MaxPushdownKeys.ShouldBe(FilterBinding.DefaultMaxPushdownKeys);
    }

    [Fact]
    public async Task PutProfile_ExplicitMaxPushdownKeysAboveALowGatewayLimit_Returns400()
    {
        await PutFilterAsync();
        void Configure(IServiceCollection s) => GatewayLimit(s, 50);

        var (status, body) = await PutBindingAsync(new { filter = "nicht_ausgelieferte_krane", target = "web_crm.*.*.id", max_pushdown_keys = 51 }, Configure);

        status.ShouldBe(StatusCodes.Status400BadRequest);
        JsonDocument.Parse(body).RootElement.GetProperty("code").GetString().ShouldBe("VF_PUSHDOWN_KEY_BUDGET_INVALID");
    }

    [Fact]
    public async Task PutProfile_ExplicitMaxPushdownKeysEqualToTheDefault_IsCheckedAgainstALowGatewayLimit()
    {
        await PutFilterAsync();
        void Configure(IServiceCollection s) => GatewayLimit(s, 50);

        var (status, body) = await PutBindingAsync(new { filter = "nicht_ausgelieferte_krane", target = "web_crm.*.*.id", max_pushdown_keys = 100 }, Configure);

        status.ShouldBe(StatusCodes.Status400BadRequest);
        JsonDocument.Parse(body).RootElement.GetProperty("code").GetString().ShouldBe("VF_PUSHDOWN_KEY_BUDGET_INVALID");
        (await _repository.LoadSnapshotAsync()).Profiles.ShouldBeEmpty();
    }

    [Fact]
    public async Task Sync_ExplicitMaxPushdownKeysEqualToTheDefault_IsCheckedAgainstALowGatewayLimit()
    {
        void Configure(IServiceCollection s) => GatewayLimit(s, 50);
        var body = new
        {
            path = "governance/access",
            commit = "c1",
            filters = new Dictionary<string, object> { ["nicht_ausgelieferte_krane"] = DavidFilterBody() },
            profiles = new Dictionary<string, object> { ["david"] = BindingProfile(new { filter = "nicht_ausgelieferte_krane", target = "web_crm.*.*.id", max_pushdown_keys = 100 }) }
        };
        var context = Context(Tenant, body, "FilterSync");

        (await StatusAsync(await VirtualFilterEndpoints.ApplySyncAsync(context, ServiceWith(Configure)), context)).ShouldBe(StatusCodes.Status400BadRequest);
        (await _repository.LoadSnapshotAsync()).Profiles.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("deny", UncoveredPolicy.Deny)]
    [InlineData("Deny", UncoveredPolicy.Deny)]
    [InlineData("DENY", UncoveredPolicy.Deny)]
    [InlineData("skip", UncoveredPolicy.Skip)]
    [InlineData("Skip", UncoveredPolicy.Skip)]
    public async Task PutProfile_UncoveredIsAcceptedInAnyCasing(string value, UncoveredPolicy expected)
    {
        await PutFilterAsync();
        var body = new
        {
            grantee = new { type = "user", sid = "S-1-5-21-LWE-DAVID" },
            scope = "web_crm.*.*",
            uncovered = value,
            bindings = new[] { new { filter = "nicht_ausgelieferte_krane", target = "web_crm.*.*.id" } }
        };
        var context = Context(Tenant, body, "FilterAdmin");

        (await StatusAsync(await VirtualFilterEndpoints.PutProfileAsync("david", context, ServiceWith(null)), context)).ShouldBe(StatusCodes.Status200OK);
        (await _repository.LoadSnapshotAsync()).Profiles.ShouldHaveSingleItem().Uncovered.ShouldBe(expected);
    }

    [Theory]
    [InlineData("allow")]
    [InlineData("deny ")]
    [InlineData("0")]
    public async Task PutProfile_UnknownUncoveredValue_Returns400WithStableCode(string value)
    {
        await PutFilterAsync();
        var body = new
        {
            grantee = new { type = "user", sid = "S-1-5-21-LWE-DAVID" },
            scope = "web_crm.*.*",
            uncovered = value,
            bindings = new[] { new { filter = "nicht_ausgelieferte_krane", target = "web_crm.*.*.id" } }
        };
        var context = Context(Tenant, body, "FilterAdmin");

        (await StatusAsync(await VirtualFilterEndpoints.PutProfileAsync("david", context, ServiceWith(null)), context)).ShouldBe(StatusCodes.Status400BadRequest);
        context.Response.Body.Position = 0;
        JsonDocument.Parse(await new StreamReader(context.Response.Body).ReadToEndAsync()).RootElement.GetProperty("code").GetString().ShouldBe("VF_BINDING_INVALID");
        (await _repository.LoadSnapshotAsync()).Profiles.ShouldBeEmpty();
    }

    [Fact]
    public async Task Sync_OmittedMaxPushdownKeys_IsNotCheckedAgainstALowGatewayLimit()
    {
        void Configure(IServiceCollection s) => GatewayLimit(s, 50);
        var body = new
        {
            path = "governance/access",
            commit = "c1",
            filters = new Dictionary<string, object> { ["nicht_ausgelieferte_krane"] = DavidFilterBody() },
            profiles = new Dictionary<string, object> { ["david"] = BindingProfile(new { filter = "nicht_ausgelieferte_krane", target = "web_crm.*.*.id" }) }
        };
        var context = Context(Tenant, body, "FilterSync");

        (await StatusAsync(await VirtualFilterEndpoints.ApplySyncAsync(context, ServiceWith(Configure)), context)).ShouldBe(StatusCodes.Status200OK);
        (await _repository.LoadSnapshotAsync()).Profiles.ShouldHaveSingleItem();
    }

    [Theory]
    [InlineData("repeated_param")]
    [InlineData("odata_in")]
    public async Task PutProfile_WithAFormatNoEndpointCanSatisfy_Returns400WithStableCode(string format)
    {
        await PutFilterAsync();

        var (status, body) = await PutBindingAsync(new { filter = "nicht_ausgelieferte_krane", target = "web_crm.*.*.id", pushdown_format = format });

        status.ShouldBe(StatusCodes.Status400BadRequest);
        JsonDocument.Parse(body).RootElement.GetProperty("code").GetString().ShouldBe("VF_PUSHDOWN_FORMAT_UNSUPPORTED");
        (await _repository.LoadSnapshotAsync()).Profiles.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("post_batch", HttpBatchType.QueryParameterList, StatusCodes.Status400BadRequest)]
    [InlineData("comma_separated", HttpBatchType.JsonBodyArray, StatusCodes.Status400BadRequest)]
    [InlineData("comma_separated", HttpBatchType.ParallelSingleRequests, StatusCodes.Status400BadRequest)]
    [InlineData("comma_separated", HttpBatchType.QueryParameterList, StatusCodes.Status200OK)]
    [InlineData("post_batch", HttpBatchType.JsonBodyArray, StatusCodes.Status200OK)]
    public async Task PutProfile_FormatIsCheckedAgainstTheCapabilityOfAnExactTarget(string format, HttpBatchType batch, int expected)
    {
        await PutFilterAsync();

        var (status, body) = await PutBindingAsync(
            new { filter = "nicht_ausgelieferte_krane", target = "web_crm.public.customers", pushdown_format = format },
            CatalogWith("web_crm.public.customers", batch));

        status.ShouldBe(expected);
        if (expected == StatusCodes.Status400BadRequest)
        {
            JsonDocument.Parse(body).RootElement.GetProperty("code").GetString().ShouldBe("VF_PUSHDOWN_FORMAT_UNSUPPORTED");
        }
    }

    [Fact]
    public async Task PutProfile_ChangingFederationFields_ChangesTheDefinitionHash()
    {
        await PutFilterAsync();
        var first = await PutBindingAsync(new { filter = "nicht_ausgelieferte_krane", target = "web_crm.*.*.id" });
        var second = await PutBindingAsync(new { filter = "nicht_ausgelieferte_krane", target = "web_crm.*.*.id", strategy = "short_circuit_only" });
        var third = await PutBindingAsync(new { filter = "nicht_ausgelieferte_krane", target = "web_crm.*.*.id", max_pushdown_keys = 7 });

        string Hash(string body) => JsonDocument.Parse(body).RootElement.GetProperty("definition_hash").GetString()!;
        new[] { Hash(first.Body), Hash(second.Body), Hash(third.Body) }.Distinct().Count().ShouldBe(3);
    }

    [Fact]
    public void BindingCanonicalHash_CoversStrategyFormatAndMaxKeys()
    {
        var baseline = new FilterBinding { FilterName = "f", TargetPattern = "a.b.c" };

        baseline.ComputeHash().ShouldNotBe((baseline with { Strategy = VirtualFilterExecutionStrategy.PushdownOnly }).ComputeHash());
        baseline.ComputeHash().ShouldNotBe((baseline with { PushdownFormat = PushdownParameterFormat.PostBatch }).ComputeHash());
        baseline.ComputeHash().ShouldNotBe((baseline with { MaxPushdownKeys = 99 }).ComputeHash());
    }

    [Fact]
    public async Task Sync_WithAnInvalidFederationField_Returns400_AndStoresNothing()
    {
        var body = new
        {
            path = "governance/access",
            commit = "c1",
            filters = new Dictionary<string, object> { ["nicht_ausgelieferte_krane"] = DavidFilterBody() },
            profiles = new Dictionary<string, object> { ["david"] = BindingProfile(new { filter = "nicht_ausgelieferte_krane", target = "web_crm.*.*.id", strategy = "nope" }) }
        };
        var apply = Context(Tenant, body, "FilterSync");

        (await StatusAsync(await VirtualFilterEndpoints.ApplySyncAsync(apply, _service), apply)).ShouldBe(StatusCodes.Status400BadRequest);
        (await _repository.LoadSnapshotAsync()).Profiles.ShouldBeEmpty();
    }
}
