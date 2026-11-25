namespace Autheris.Tests.Unit.VirtualFilters;

using Autheris.Application.Governance.Ownership;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Tests.Unit.TestDoubles;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

/// <summary>
/// Single approval of virtual filters and access profiles: a save activates, SQL filters follow the owner-group rule, no self-widening.
/// SR15-06: replaces the former four-eyes approval; only Data Governance (or the file sync) changes an access profile.
/// </summary>
public sealed class VirtualFilterSingleApprovalTests
{
    private static readonly TenantId Tenant = new("tenant_lwe");
    private static readonly Sid GovAuthor = new("S-1-5-21-GOV-AUTHOR");
    private static readonly Sid PartialOwner = new("S-1-5-21-OWNER-ONE");
    private static readonly Sid FullOwnerDelegate = new("S-1-5-21-OWNER-ALL");

    private static readonly VirtualFilterActor GovAuthorActor =
        new(GovAuthor, IsSync: false, Roles: new HashSet<string>([nameof(GatewayRole.GovernanceAdmin)], StringComparer.OrdinalIgnoreCase),
            Groups: ["S-1-5-21-GOV-GROUP"], IsDataGovernance: true);

    private static readonly VirtualFilterActor FilterAdminActor =
        new(new Sid("S-1-5-21-FILTERADMIN"), IsSync: false, Roles: new HashSet<string>([nameof(GatewayRole.FilterAdmin)], StringComparer.OrdinalIgnoreCase), Groups: []);

    private static readonly VirtualFilterActor PartialOwnerActor =
        new(PartialOwner, IsSync: false, Roles: new HashSet<string>([nameof(GatewayRole.Consumer)], StringComparer.OrdinalIgnoreCase));

    private static readonly VirtualFilterActor FullOwnerActor =
        new(FullOwnerDelegate, IsSync: false, Roles: new HashSet<string>([nameof(GatewayRole.Consumer)], StringComparer.OrdinalIgnoreCase));

    private static readonly TableIdentifier ClientTable = new("lwetem_prod", "conf", "client");
    private static readonly TableIdentifier CraneTable = new("lwetem_prod", "md", "crane");

    private static VirtualFilter SqlPolicy(string sql = "from conf.client client join md.crane crane on crane.serial_number = client.crane_serial_number where crane.is_delivered is null and target.client_id = client.client_id") => new()
    {
        TenantId = Tenant,
        Name = "active_cranes_sql",
        Source = "lwetem_prod",
        Sql = sql
    };

    private static TableMetadata OrdersTable() => new()
    {
        Identifier = new TableIdentifier("lwetem_prod", "sales", "orders"),
        Table = new Table { SourceName = "lwetem_prod", SchemaName = "sales", TableName = "orders", SourceType = "PostgreSql", DataSourceType = DataSourceType.Sql },
        Columns = [new TableColumn { ColumnName = "client_id", DataType = "integer" }]
    };

    private static TableMetadata CatalogTable(string schema, string name, params string[] cols) => new()
    {
        Identifier = new TableIdentifier("lwetem_prod", schema, name),
        Table = new Table { SourceName = "lwetem_prod", SchemaName = schema, TableName = name, SourceType = "PostgreSql", DataSourceType = DataSourceType.Sql },
        Columns = cols.Select(c => new TableColumn { ColumnName = c }).ToList()
    };

    private static VirtualFilterAdministrationService CreateService(
        InMemoryFilterRepo repo,
        InMemoryOwnershipRepo owners,
        IAuditLogRepository? audit = null)
    {
        var catalog = new InMemoryCatalog([
            CatalogTable("conf", "client", "client_id", "crane_serial_number"),
            CatalogTable("md", "crane", "serial_number", "is_delivered"),
            OrdersTable()
        ]);
        var opts = Options.Create(new GatewayOptions { VirtualFilters = new VirtualFilterOptions() });
        repo.AuditSink = audit ?? NullAuditLogRepository.Instance;
        return new VirtualFilterAdministrationService(repo, audit ?? NullAuditLogRepository.Instance, opts, catalog: catalog, ownership: owners);
    }

    private static MandatoryRowFilterResolver CreateResolver(InMemoryFilterRepo repo, IContainerOwnership? ownership = null)
    {
        var profile = new VirtualFilterAccessProfile
        {
            TenantId = Tenant,
            Name = "david",
            GranteeType = GranteeType.User,
            GranteeSid = new Sid("S-1-5-21-USER"),
            Scope = "lwetem_prod.*.*",
            Uncovered = UncoveredPolicy.Deny,
            Bindings = [new FilterBinding { FilterName = "active_cranes_sql" }]
        };
        return new MandatoryRowFilterResolver(
            new RepoSnapshotProvider(repo, profile),
            new StructuredFilterSqlBuilder(),
            ownership: ownership);
    }

    private static HttpContext HttpCtx(Sid sid, string role, object? body = null, IGovernanceCapabilities? capabilities = null)
    {
        var collection = new ServiceCollection()
            .AddLogging()
            .AddAuditWriterOver(NullAuditLogRepository.Instance);
        if (capabilities is not null)
        {
            collection.AddSingleton(capabilities);
        }

        var services = collection.BuildServiceProvider();
        var ctx = new DefaultHttpContext { RequestServices = services };
        ctx.Items[SecurityPrincipalContext.ItemKey] = new SecurityPrincipalContext
        {
            UserSid = sid,
            TenantId = Tenant,
            GroupSids = new HashSet<Sid>(),
            TenantRoles = role is "ClusterAdmin" or "GovernanceAdmin" ? new HashSet<string>() : new HashSet<string>([role]),
            ClusterRoles = role is "ClusterAdmin" or "GovernanceAdmin" ? new HashSet<string>([role]) : new HashSet<string>(),
            AuthenticationScheme = "Test",
            GroupResolutionStatus = Autheris.Domain.Identity.GroupResolutionStatus.Complete
        };
        ctx.Response.Body = new MemoryStream();
        if (body != null)
        {
            ctx.Request.ContentType = "application/json";
            ctx.Request.Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(body));
        }
        return ctx;
    }

    private static readonly MandatoryFilterQuery OrdersQuery =
        new(new Sid("S-1-5-21-USER"), new HashSet<Sid>(), new HashSet<string>(), Tenant, OrdersTable());

    [Fact]
    public async Task Save_of_structured_filter_activates_and_writes_one_audit_entry_with_hashes()
    {
        var repo = new InMemoryFilterRepo();
        var audit = new Autheris.Tests.Common.Fakes.InMemoryAuditLogRepository();
        var service = CreateService(repo, new InMemoryOwnershipRepo(), audit);
        var filterAdmin = new VirtualFilterActor(new Sid("S-1-5-21-FILTERADMIN"), IsSync: false, Roles: new HashSet<string>([nameof(GatewayRole.FilterAdmin)]));

        var saved = await service.SaveFilterAsync(VirtualFilterTestData.DavidFilter("structured_one"), filterAdmin);

        saved.Status.ShouldBe(FilterApprovalStatus.Active);
        saved.Draft.ShouldBeNull();
        audit.Entries.Count.ShouldBe(1);
        audit.Entries[0].EventType.ShouldBe("VIRTUAL_FILTER_CREATED");
        audit.Entries[0].DetailsJson.ShouldContain("after");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task A14_author_dg_or_owner_of_all_referenced_containers_activates()
    {
        var owners = new InMemoryOwnershipRepo();
        owners.Allow(ClientTable, FullOwnerDelegate);
        owners.Allow(CraneTable, FullOwnerDelegate);

        var repo = new InMemoryFilterRepo();
        var service = CreateService(repo, owners);
        var resolver = CreateResolver(repo);

        var byDg = await service.SaveFilterAsync(SqlPolicy(), GovAuthorActor);
        byDg.Status.ShouldBe(FilterApprovalStatus.Active);

        var byOwner = await service.SaveFilterAsync(SqlPolicy() with { Name = "owner_authored_sql" }, FullOwnerActor);
        byOwner.Status.ShouldBe(FilterApprovalStatus.Active);

        var outcome = await resolver.ResolveAsync(OrdersQuery);
        outcome.IsDenied.ShouldBeFalse();
        outcome.AppliedFilters.ShouldBe(["active_cranes_sql"]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task A14_otherwise_inactive()
    {
        var owners = new InMemoryOwnershipRepo();
        owners.Allow(ClientTable, PartialOwner);
        var repo = new InMemoryFilterRepo();
        var service = CreateService(repo, owners);
        var resolver = CreateResolver(repo);

        // Owner of only one of the two referenced tables: refused, nothing stored, the profile denies.
        await Should.ThrowAsync<UnauthorizedAccessException>(() => service.SaveFilterAsync(SqlPolicy(), PartialOwnerActor));

        (await repo.LoadSnapshotAsync()).Filters.ShouldBeEmpty();
        (await resolver.ResolveAsync(OrdersQuery)).IsDenied.ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task A14_it_never()
    {
        var owners = new InMemoryOwnershipRepo();
        owners.Allow(ClientTable, new Sid("S-1-5-21-FILTERADMIN"));
        owners.Allow(CraneTable, new Sid("S-1-5-21-FILTERADMIN"));
        var repo = new InMemoryFilterRepo();
        var service = CreateService(repo, owners);

        var itAdmin = new VirtualFilterActor(new Sid("S-1-5-21-ITADMIN"), IsSync: false, Roles: new HashSet<string>([nameof(GatewayRole.ClusterAdmin), nameof(GatewayRole.FilterAdmin)]));
        await Should.ThrowAsync<UnauthorizedAccessException>(() => service.SaveFilterAsync(SqlPolicy(), itAdmin));

        // A filter admin who owns none of the referenced tables is refused as well.
        var filterAdmin = new VirtualFilterActor(new Sid("S-1-5-21-NOOWNER"), IsSync: false, Roles: new HashSet<string>([nameof(GatewayRole.FilterAdmin)]));
        await Should.ThrowAsync<UnauthorizedAccessException>(() =>
            service.SaveFilterAsync(SqlPolicy("from conf.client client where client.client_id = target.client_id"), filterAdmin));

        (await repo.LoadSnapshotAsync()).Filters.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Only_governance_admin_or_owner_reaches_sql_policy_through_the_endpoint()
    {
        var repo = new InMemoryFilterRepo();
        var service = CreateService(repo, new InMemoryOwnershipRepo());

        var sqlBody = new { source = "lwetem_prod", sql = SqlPolicy().Sql };
        foreach (var forbiddenRole in new[] { "FilterAdmin", "ClusterAdmin", "Consumer" })
        {
            var ctx = HttpCtx(new Sid("S-1-5-21-CALLER"), forbiddenRole, sqlBody);
            var res = await VirtualFilterEndpoints.PutFilterAsync("active_cranes_sql", ctx, service);
            await res.ExecuteAsync(ctx);
            ctx.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        }

        var govCtx = HttpCtx(GovAuthor, "GovernanceAdmin", sqlBody);
        var govRes = await VirtualFilterEndpoints.PutFilterAsync("active_cranes_sql", govCtx, service);
        await govRes.ExecuteAsync(govCtx);
        govCtx.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        (await repo.LoadSnapshotAsync()).Filters.Single().Status.ShouldBe(FilterApprovalStatus.Active);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Referenced_table_moved_deactivates()
    {
        var owners = new InMemoryOwnershipRepo();
        var repo = new InMemoryFilterRepo();
        var service = CreateService(repo, owners);

        var saved = await service.SaveFilterAsync(SqlPolicy(), GovAuthorActor);
        saved.ReferencedContainers.Select(r => r.Container).ShouldAllBe(c => c == "container-1");
        saved.ReferencedContainers.Count.ShouldBe(2);

        // Still in the recorded containers: enforced.
        (await CreateResolver(repo, owners).ResolveAsync(OrdersQuery)).IsDenied.ShouldBeFalse();

        // One referenced table moves to another container: the filter goes inactive, the profile denies until it is saved again.
        owners.Move(CraneTable, "container-2");
        (await CreateResolver(repo, owners).ResolveAsync(OrdersQuery)).IsDenied.ShouldBeTrue();

        await service.SaveFilterAsync(SqlPolicy(), GovAuthorActor);
        (await CreateResolver(repo, owners).ResolveAsync(OrdersQuery)).IsDenied.ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Referenced_tables_own_policies_apply()
    {
        // A referenced table keeps its own tenant policy: when it is tenanted the definition must compare it with the reserved tenant value.
        IReadOnlyList<TableMetadata> catalog =
        [
            CatalogTable("conf", "client", "client_id", "tenant_id"),
            OrdersTable()
        ];
        var filter = SqlPolicy("from conf.client client where client.client_id = target.client_id");

        Should.Throw<ArgumentException>(() => SqlFilterCompiler.Validate(filter, catalog));
        SqlFilterCompiler.Validate(
            SqlPolicy("from conf.client client where client.tenant_id = '{{tenant}}' and client.client_id = target.client_id"), catalog)
            .SqlTargetColumns.ShouldContain("client_id");
    }

    [Theory]
    [InlineData("pg_sleep(10)")]
    [InlineData("nextval('conf.seq')")]
    [InlineData("dblink_exec('x')")]
    [Trait("Category", "Security")]
    public async Task Side_effect_function_rejected(string call)
    {
        var repo = new InMemoryFilterRepo();
        var service = CreateService(repo, new InMemoryOwnershipRepo());

        await Should.ThrowAsync<ArgumentException>(() =>
            service.SaveFilterAsync(SqlPolicy($"from conf.client client where {call} is not null and client.client_id = target.client_id"), GovAuthorActor));

        (await repo.LoadSnapshotAsync()).Filters.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Reference_depth_bounded()
    {
        var repo = new InMemoryFilterRepo();
        var service = CreateService(repo, new InMemoryOwnershipRepo());

        var nested = "from conf.client client where client.client_id = target.client_id and client.crane_serial_number in " +
                     "(select serial_number from md.crane where serial_number in (select serial_number from md.crane where serial_number in " +
                     "(select serial_number from md.crane where serial_number in (select serial_number from md.crane))))";
        (await Should.ThrowAsync<ArgumentException>(() => service.SaveFilterAsync(SqlPolicy(nested), GovAuthorActor)))
            .Message.ShouldContain("nested");

        // More than five distinct tables is refused as well.
        var many = string.Join(" ", Enumerable.Range(1, 6).Select(i => $"join md.t{i} t{i} on t{i}.serial_number = client.crane_serial_number"));
        var catalog = new[] { CatalogTable("conf", "client", "client_id", "crane_serial_number") }
            .Concat(Enumerable.Range(1, 6).Select(i => CatalogTable("md", $"t{i}", "serial_number"))).ToList();
        (await Should.ThrowAsync<ArgumentException>(async () =>
            SqlFilterCompiler.Validate(SqlPolicy($"from conf.client client {many} where target.client_id = client.client_id"), catalog)))
            .Message.ShouldContain("at most 5");

        (await repo.LoadSnapshotAsync()).Filters.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Writer_without_capacity_refused()
    {
        var repo = new InMemoryFilterRepo();
        var service = CreateService(repo, new InMemoryOwnershipRepo());

        var body = new { source = "lwetem_prod", structured = new { from = new { domain = "lwetem_prod", schema = "sales", table = "orders" } } };
        foreach (var role in new[] { "Consumer" })
        {
            var ctx = HttpCtx(new Sid("S-1-5-21-CALLER"), role, body);
            var res = await VirtualFilterEndpoints.PutFilterAsync("structured_one", ctx, service);
            await res.ExecuteAsync(ctx);
            ctx.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden, role);
        }

        (await repo.LoadSnapshotAsync()).Filters.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Definition_change_takes_effect_on_save()
    {
        var repo = new InMemoryFilterRepo();
        var service = CreateService(repo, new InMemoryOwnershipRepo());
        var resolver = CreateResolver(repo);

        await service.SaveFilterAsync(SqlPolicy("from conf.client client where client.client_id = target.client_id"), GovAuthorActor);
        var v2 = await service.SaveFilterAsync(SqlPolicy(), GovAuthorActor);

        v2.Status.ShouldBe(FilterApprovalStatus.Active);
        v2.Draft.ShouldBeNull();
        var outcome = await resolver.ResolveAsync(OrdersQuery);
        outcome.PredicateSql.ShouldNotBeNull().ShouldContain("crane");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Audit_with_hash_fail_closed()
    {
        var repo = new InMemoryFilterRepo();
        var audit = new Autheris.Tests.Common.Fakes.InMemoryAuditLogRepository { FailWith = new InvalidOperationException("audit down") };
        var service = CreateService(repo, new InMemoryOwnershipRepo(), audit);

        await Should.ThrowAsync<InvalidOperationException>(() => service.SaveFilterAsync(SqlPolicy(), GovAuthorActor));

        (await repo.LoadSnapshotAsync()).Filters.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Author_cannot_widen_own_access()
    {
        var repo = new InMemoryFilterRepo();
        var service = CreateService(repo, new InMemoryOwnershipRepo());
        await service.SaveFilterAsync(SqlPolicy("from conf.client client where client.client_id = target.client_id"), GovAuthorActor);

        var profile = new VirtualFilterAccessProfile
        {
            TenantId = Tenant,
            Name = "own_profile",
            GranteeType = GranteeType.User,
            GranteeSid = GovAuthor,
            Scope = "lwetem_prod.*.*",
            Uncovered = UncoveredPolicy.Skip,
            Bindings = [new FilterBinding { FilterName = "active_cranes_sql" }]
        };

        await Should.ThrowAsync<Autheris.Application.Governance.SingleApproval.SingleApprovalRefusedException>(
            () => service.SaveProfileAsync(profile, GovAuthorActor));
        (await repo.LoadSnapshotAsync()).Profiles.ShouldBeEmpty();

        // Narrowing (deny uncovered) or a different grantee is fine.
        var denied = await service.SaveProfileAsync(profile with { Uncovered = UncoveredPolicy.Deny }, GovAuthorActor);
        denied.Status.ShouldBe(FilterApprovalStatus.Active);
        var other = await service.SaveProfileAsync(profile with { Name = "other_profile", GranteeSid = new Sid("S-1-5-21-OTHER") }, GovAuthorActor);
        other.Status.ShouldBe(FilterApprovalStatus.Active);
    }

    private static VirtualFilterAccessProfile Profile(string name, GranteeType type, Sid grantee, UncoveredPolicy uncovered = UncoveredPolicy.Skip) => new()
    {
        TenantId = Tenant,
        Name = name,
        GranteeType = type,
        GranteeSid = grantee,
        Scope = "lwetem_prod.*.*",
        Uncovered = uncovered,
        Bindings = [new FilterBinding { FilterName = "active_cranes_sql" }]
    };

    private static IGovernanceCapabilities DataGovernanceOfTenant()
    {
        var governance = NSubstitute.Substitute.For<IGovernanceCapabilities>();
        NSubstitute.SubstituteExtensions.Returns(governance.IsGovernance(NSubstitute.Arg.Any<System.Security.Claims.ClaimsPrincipal?>(), Tenant), true);
        return governance;
    }

    private static object ProfileBody(string granteeSid, string uncovered = "deny") => new
    {
        grantee = new { type = "user", sid = granteeSid },
        scope = "lwetem_prod.*.*",
        uncovered,
        bindings = new[] { new { filter = "active_cranes_sql" } }
    };

    [Fact]
    [Trait("Category", "Security")]
    public async Task FilterAdmin_without_data_governance_cannot_activate_or_delete_an_access_profile()
    {
        var repo = new InMemoryFilterRepo();
        var service = CreateService(repo, new InMemoryOwnershipRepo());
        await service.SaveFilterAsync(SqlPolicy("from conf.client client where client.client_id = target.client_id"), GovAuthorActor);
        var profile = Profile("declarative", GranteeType.User, new Sid("S-1-5-21-OTHER"), UncoveredPolicy.Deny);

        await Should.ThrowAsync<UnauthorizedAccessException>(() => service.SaveProfileAsync(profile, FilterAdminActor));
        (await repo.LoadSnapshotAsync()).Profiles.ShouldBeEmpty();

        // A GovernanceAdmin role alone (without the tenant-bound capacity established by the caller) is not enough either.
        var roleOnly = GovAuthorActor with { IsDataGovernance = false };
        await Should.ThrowAsync<UnauthorizedAccessException>(() => service.SaveProfileAsync(profile, roleOnly));

        await service.SaveProfileAsync(profile, GovAuthorActor);
        await Should.ThrowAsync<UnauthorizedAccessException>(() => service.DeleteProfileAsync(Tenant, "declarative", FilterAdminActor));
        (await repo.LoadSnapshotAsync()).Profiles.ShouldHaveSingleItem();

        await service.DeleteProfileAsync(Tenant, "declarative", GovAuthorActor);
        (await repo.LoadSnapshotAsync()).Profiles.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Access_profile_endpoints_need_data_governance_and_fail_closed_without_the_capability_service()
    {
        var repo = new InMemoryFilterRepo();
        var service = CreateService(repo, new InMemoryOwnershipRepo());
        await service.SaveFilterAsync(SqlPolicy("from conf.client client where client.client_id = target.client_id"), GovAuthorActor);
        var governance = DataGovernanceOfTenant();
        var body = ProfileBody("S-1-5-21-OTHER");

        // FilterAdmin (and IT) never reach the profile write.
        foreach (var role in new[] { "FilterAdmin", "ClusterAdmin" })
        {
            var put = HttpCtx(new Sid("S-1-5-21-CALLER"), role, body, governance);
            await (await VirtualFilterEndpoints.PutProfileAsync("declarative", put, service)).ExecuteAsync(put);
            put.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden, role);
        }

        // GovernanceAdmin role without an established Data Governance capacity: refused (no capability service registered).
        var noCapability = HttpCtx(GovAuthor, "GovernanceAdmin", body);
        await (await VirtualFilterEndpoints.PutProfileAsync("declarative", noCapability, service)).ExecuteAsync(noCapability);
        noCapability.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        (await repo.LoadSnapshotAsync()).Profiles.ShouldBeEmpty();

        var dg = HttpCtx(GovAuthor, "GovernanceAdmin", body, governance);
        await (await VirtualFilterEndpoints.PutProfileAsync("declarative", dg, service)).ExecuteAsync(dg);
        dg.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);

        var filterAdminDelete = HttpCtx(new Sid("S-1-5-21-CALLER"), "FilterAdmin", capabilities: governance);
        await (await VirtualFilterEndpoints.DeleteProfileAsync("declarative", filterAdminDelete, service)).ExecuteAsync(filterAdminDelete);
        filterAdminDelete.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        (await repo.LoadSnapshotAsync()).Profiles.ShouldHaveSingleItem();

        var dgDelete = HttpCtx(GovAuthor, "GovernanceAdmin", capabilities: governance);
        await (await VirtualFilterEndpoints.DeleteProfileAsync("declarative", dgDelete, service)).ExecuteAsync(dgDelete);
        dgDelete.Response.StatusCode.ShouldBe(StatusCodes.Status204NoContent);
        (await repo.LoadSnapshotAsync()).Profiles.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Self_widening_refusal_is_a_403_problem_not_a_server_error()
    {
        var repo = new InMemoryFilterRepo();
        var service = CreateService(repo, new InMemoryOwnershipRepo());
        await service.SaveFilterAsync(SqlPolicy("from conf.client client where client.client_id = target.client_id"), GovAuthorActor);

        var ctx = HttpCtx(GovAuthor, "GovernanceAdmin", ProfileBody(GovAuthor.Value, uncovered: "skip"), DataGovernanceOfTenant());
        await (await VirtualFilterEndpoints.PutProfileAsync("own_profile", ctx, service)).ExecuteAsync(ctx);

        ctx.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        (await repo.LoadSnapshotAsync()).Profiles.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Self_widening_guard_covers_the_writers_groups_and_refuses_when_they_are_unknown()
    {
        var repo = new InMemoryFilterRepo();
        var service = CreateService(repo, new InMemoryOwnershipRepo());
        await service.SaveFilterAsync(SqlPolicy("from conf.client client where client.client_id = target.client_id"), GovAuthorActor);
        var ownGroup = Profile("own_group", GranteeType.Group, new Sid("S-1-5-21-GOV-GROUP"));

        (await Should.ThrowAsync<Autheris.Application.Governance.SingleApproval.SingleApprovalRefusedException>(() => service.SaveProfileAsync(ownGroup, GovAuthorActor)))
            .Code.ShouldBe("SelfWidening");

        var unknownGroups = GovAuthorActor with { Groups = null };
        (await Should.ThrowAsync<Autheris.Application.Governance.SingleApproval.SingleApprovalRefusedException>(
            () => service.SaveProfileAsync(Profile("other_group", GranteeType.Group, new Sid("S-1-5-21-OTHER-GROUP")), unknownGroups)))
            .Code.ShouldBe("SelfWideningUnverifiable");
        (await repo.LoadSnapshotAsync()).Profiles.ShouldBeEmpty();

        // Another group with known writer groups is fine.
        await service.SaveProfileAsync(Profile("other_group", GranteeType.Group, new Sid("S-1-5-21-OTHER-GROUP")), GovAuthorActor);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Deletes_are_audited_in_the_unit_of_the_write()
    {
        var repo = new InMemoryFilterRepo();
        var audit = new Autheris.Tests.Common.Fakes.InMemoryAuditLogRepository();
        var service = CreateService(repo, new InMemoryOwnershipRepo(), audit);
        await service.SaveFilterAsync(SqlPolicy("from conf.client client where client.client_id = target.client_id"), GovAuthorActor);
        await service.SaveProfileAsync(Profile("declarative", GranteeType.User, new Sid("S-1-5-21-OTHER"), UncoveredPolicy.Deny), GovAuthorActor);

        // A failing audit leaves the profile in place.
        audit.FailWith = new InvalidOperationException("audit down");
        await Should.ThrowAsync<InvalidOperationException>(() => service.DeleteProfileAsync(Tenant, "declarative", GovAuthorActor));
        (await repo.LoadSnapshotAsync()).Profiles.ShouldHaveSingleItem();

        audit.FailWith = null;
        await service.DeleteProfileAsync(Tenant, "declarative", GovAuthorActor);
        await service.DeleteFilterAsync(Tenant, "active_cranes_sql", GovAuthorActor);

        (await repo.LoadSnapshotAsync()).Profiles.ShouldBeEmpty();
        (await repo.LoadSnapshotAsync()).Filters.ShouldBeEmpty();
        audit.Entries.Select(e => e.EventType).ShouldBe(["VIRTUAL_FILTER_CREATED", "ACCESS_PROFILE_CREATED", "ACCESS_PROFILE_DELETED", "VIRTUAL_FILTER_DELETED"]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ApplySyncAsync_populates_referenced_containers_so_resolver_enforces_synced_sql_filters()
    {
        var repo = new InMemoryFilterRepo();
        var owners = new InMemoryOwnershipRepo();
        var service = CreateService(repo, owners);
        var managedBy = new ManagedBy("governance/access", "commit-1");
        var syncActor = new VirtualFilterActor(new Sid("S-1-5-21-SYNC"), IsSync: true, IsDataGovernance: true);
        var profile = Profile("synced_user", GranteeType.User, new Sid("S-1-5-21-USER"), UncoveredPolicy.Deny) with { ManagedBy = managedBy };
        var request = new VirtualFilterSyncRequest(Tenant, managedBy, [SqlPolicy() with { ManagedBy = managedBy }], [profile]);

        await service.ApplySyncAsync(request, syncActor);

        var syncedFilter = (await repo.LoadSnapshotAsync()).Filters.ShouldHaveSingleItem();
        syncedFilter.ReferencedContainers.Select(r => (r.Table, r.Container)).ShouldBe(
        [
            (ClientTable.ToString(), "container-1"),
            (CraneTable.ToString(), "container-1")
        ]);

        var resolver = new MandatoryRowFilterResolver(new SnapshotRepoProvider(repo), new StructuredFilterSqlBuilder(), ownership: owners);
        var outcome = await resolver.ResolveAsync(OrdersQuery);
        outcome.IsDenied.ShouldBeFalse();
        outcome.AppliedFilters.ShouldBe(["active_cranes_sql"]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SaveFilterAsync_and_DeleteFilterAsync_require_ownership_of_existing_sql_filter_tables()
    {
        var repo = new InMemoryFilterRepo();
        var owners = new InMemoryOwnershipRepo();
        var service = CreateService(repo, owners);
        await service.SaveFilterAsync(SqlPolicy(), GovAuthorActor);

        var partialOwner = new VirtualFilterActor(
            PartialOwner,
            IsSync: false,
            Roles: new HashSet<string>([nameof(GatewayRole.FilterAdmin)], StringComparer.OrdinalIgnoreCase),
            Groups: []);
        owners.Allow(CraneTable, PartialOwner);

        // Overwriting an existing SQL filter with a structured filter must still verify ownership of the old SQL tables.
        var structuredOverwrite = VirtualFilterTestData.DavidFilter("active_cranes_sql");
        await Should.ThrowAsync<UnauthorizedAccessException>(() => service.SaveFilterAsync(structuredOverwrite, partialOwner));

        // Overwriting with SQL referencing only an owned table must still check ownership of the old unowned table.
        var partialSqlOverwrite = SqlPolicy("from md.crane crane where crane.is_delivered is null and target.client_id = crane.serial_number");
        await Should.ThrowAsync<UnauthorizedAccessException>(() => service.SaveFilterAsync(partialSqlOverwrite, partialOwner));

        // Deleting an existing SQL filter also requires GovernanceAdmin or ownership of all referenced tables.
        await Should.ThrowAsync<UnauthorizedAccessException>(() => service.DeleteFilterAsync(Tenant, "active_cranes_sql", partialOwner));
        (await repo.LoadSnapshotAsync()).Filters.ShouldHaveSingleItem();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SelfWideningGuard_blocks_filter_update_delete_and_profile_update_delete_affecting_writer_or_groups()
    {
        var repo = new InMemoryFilterRepo();
        var service = CreateService(repo, new InMemoryOwnershipRepo());
        await service.SaveFilterAsync(SqlPolicy("from conf.client client where client.client_id = target.client_id"), GovAuthorActor);
        await service.SaveProfileAsync(Profile("own_deny_profile", GranteeType.User, GovAuthor, UncoveredPolicy.Deny), GovAuthorActor);
        await service.SaveProfileAsync(Profile("group_deny_profile", GranteeType.Group, new Sid("S-1-5-21-GOV-GROUP"), UncoveredPolicy.Deny), GovAuthorActor);

        // Updating a filter bound to the writer's own or group profile can widen own access.
        (await Should.ThrowAsync<Autheris.Application.Governance.SingleApproval.SingleApprovalRefusedException>(
            () => service.SaveFilterAsync(SqlPolicy(), GovAuthorActor)))
            .Code.ShouldBe("SelfWidening");

        // Deleting a bound filter affecting the writer's own or group profile must be refused for self-widening.
        (await Should.ThrowAsync<Autheris.Application.Governance.SingleApproval.SingleApprovalRefusedException>(
            () => service.DeleteFilterAsync(Tenant, "active_cranes_sql", GovAuthorActor)))
            .Code.ShouldBe("SelfWidening");

        // Updating an existing profile for own SID (even when Uncovered remains Deny) can narrow Scope or alter filters.
        (await Should.ThrowAsync<Autheris.Application.Governance.SingleApproval.SingleApprovalRefusedException>(
            () => service.SaveProfileAsync(Profile("own_deny_profile", GranteeType.User, GovAuthor, UncoveredPolicy.Deny) with { Scope = "lwetem_prod.sales.*" }, GovAuthorActor)))
            .Code.ShouldBe("SelfWidening");

        // Deleting an existing profile for own SID or own group removes mandatory filtering.
        (await Should.ThrowAsync<Autheris.Application.Governance.SingleApproval.SingleApprovalRefusedException>(
            () => service.DeleteProfileAsync(Tenant, "own_deny_profile", GovAuthorActor)))
            .Code.ShouldBe("SelfWidening");
        (await Should.ThrowAsync<Autheris.Application.Governance.SingleApproval.SingleApprovalRefusedException>(
            () => service.DeleteProfileAsync(Tenant, "group_deny_profile", GovAuthorActor)))
            .Code.ShouldBe("SelfWidening");

        // Degraded groups when updating a filter bound to a group profile fails closed as unverifiable.
        var unknownGroupsOther = new VirtualFilterActor(new Sid("S-1-5-21-OTHER-DG"), IsSync: false,
            Roles: new HashSet<string>([nameof(GatewayRole.GovernanceAdmin)], StringComparer.OrdinalIgnoreCase),
            Groups: null, IsDataGovernance: true);
        (await Should.ThrowAsync<Autheris.Application.Governance.SingleApproval.SingleApprovalRefusedException>(
            () => service.SaveFilterAsync(SqlPolicy(), unknownGroupsOther)))
            .Code.ShouldBe("SelfWideningUnverifiable");
    }

    private sealed class SnapshotRepoProvider(InMemoryFilterRepo repo) : IVirtualFilterSnapshotProvider
    {
        public async ValueTask<VirtualFilterSnapshot> GetAsync(CancellationToken ct = default) =>
            await repo.LoadSnapshotAsync(ct).ConfigureAwait(false);
        public void Invalidate() { }
    }

    /// <summary>Models the store transaction: the writes and the audit entries of a change set commit together or not at all.</summary>
    private sealed class InMemoryFilterRepo : IVirtualFilterRepository
    {
        private readonly List<VirtualFilter> _filters = [];
        private readonly List<VirtualFilterAccessProfile> _profiles = [];
        private long _generation = 1;

        public IAuditLogRepository AuditSink { get; set; } = NullAuditLogRepository.Instance;

        public Task<VirtualFilterSnapshot> LoadSnapshotAsync(CancellationToken ct = default) =>
            Task.FromResult(new VirtualFilterSnapshot(_generation, _filters.ToList(), _profiles.ToList()));

        public Task<long> GetGenerationAsync(CancellationToken ct = default) => Task.FromResult(_generation);

        public async Task ApplyAsync(VirtualFilterChangeSet changes, CancellationToken ct = default)
        {
            // Audit inside the "transaction": a failing entry throws before anything is committed.
            foreach (var entry in changes.AuditEntries)
            {
                await AuditSink.RecordAuditEventAsync(entry, ct);
            }

            foreach (var f in changes.SaveFilters)
            {
                _filters.RemoveAll(x => x.TenantId == f.TenantId && x.Name == f.Name);
                _filters.Add(f);
            }
            foreach (var p in changes.SaveProfiles)
            {
                _profiles.RemoveAll(x => x.TenantId == p.TenantId && x.Name == p.Name);
                _profiles.Add(p);
            }
            foreach (var (tenant, name) in changes.DeleteProfiles)
            {
                _profiles.RemoveAll(x => x.TenantId == tenant && x.Name == name);
            }
            foreach (var (tenant, name) in changes.DeleteFilters)
            {
                _filters.RemoveAll(x => x.TenantId == tenant && x.Name == name);
            }
            _generation++;
        }
    }

    private sealed class RepoSnapshotProvider(InMemoryFilterRepo repo, VirtualFilterAccessProfile profile) : IVirtualFilterSnapshotProvider
    {
        public async ValueTask<VirtualFilterSnapshot> GetAsync(CancellationToken ct = default)
        {
            var snap = await repo.LoadSnapshotAsync(ct).ConfigureAwait(false);
            return snap with { Profiles = [profile] };
        }
        public void Invalidate() { }
    }

    private sealed class InMemoryOwnershipRepo : IContainerOwnership
    {
        private readonly HashSet<(string Domain, string Schema, string Table, string Sid)> _allowed = new();

        public void Allow(TableIdentifier table, Sid sid) =>
            _allowed.Add((table.Domain.ToLowerInvariant(), table.Schema.ToLowerInvariant(), table.TableName.ToLowerInvariant(), sid.Value));

        private readonly Dictionary<string, string> _containers = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Moves the table to another container (the container key changes).</summary>
        public void Move(TableIdentifier table, string container) => _containers[table.ToString()] = container;

        public Task<string?> GetContainerKeyForTableAsync(TableIdentifier table, CancellationToken ct = default) =>
            Task.FromResult<string?>(_containers.TryGetValue(table.ToString(), out var key) ? key : "container-1");

        public Task<IReadOnlyList<DataOwner>> GetDataOwnersForTableAsync(TableIdentifier table, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DataOwner>>([]);

        public Task<bool> IsAuthorizedApproverForTableAsync(TableIdentifier table, Sid approverSid, CancellationToken ct = default) =>
            Task.FromResult(_allowed.Contains((table.Domain.ToLowerInvariant(), table.Schema.ToLowerInvariant(), table.TableName.ToLowerInvariant(), approverSid.Value)));
    }

    private sealed class InMemoryCatalog(IReadOnlyList<TableMetadata> tables) : ITableMetadataRepository
    {
        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default) => Task.FromResult(tables);
        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default) =>
            Task.FromResult(tables.FirstOrDefault(t => t.Identifier.Equals(table)));
        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(
            TableIdentifier table, IReadOnlyList<string> primaryKey, IReadOnlyList<TableIndexDefinition> indexes,
            IReadOnlyList<KeyColumnDirection>? keyDirections = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task FlushPendingVersionChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
