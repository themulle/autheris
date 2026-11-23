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

/// <summary>Single approval of virtual filters and access profiles: a save activates, SQL filters follow the owner-group rule, no self-widening.</summary>
public sealed class VirtualFilterSingleApprovalTests
{
    private static readonly TenantId Tenant = new("tenant_lwe");
    private static readonly Sid GovAuthor = new("S-1-5-21-GOV-AUTHOR");
    private static readonly Sid PartialOwner = new("S-1-5-21-OWNER-ONE");
    private static readonly Sid FullOwnerDelegate = new("S-1-5-21-OWNER-ALL");

    private static readonly VirtualFilterActor GovAuthorActor =
        new(GovAuthor, IsSync: false, Roles: new HashSet<string>([nameof(GatewayRole.GovernanceAdmin)], StringComparer.OrdinalIgnoreCase));

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
        return new VirtualFilterAdministrationService(repo, audit ?? NullAuditLogRepository.Instance, opts, catalog: catalog, ownership: owners);
    }

    private static MandatoryRowFilterResolver CreateResolver(InMemoryFilterRepo repo)
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
            new StructuredFilterSqlBuilder());
    }

    private static HttpContext HttpCtx(Sid sid, string role, object? body = null)
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddAuditWriterOver(NullAuditLogRepository.Instance)
            .BuildServiceProvider();
        var ctx = new DefaultHttpContext { RequestServices = services };
        ctx.Items[SecurityPrincipalContext.ItemKey] = new SecurityPrincipalContext
        {
            UserSid = sid,
            TenantId = Tenant,
            GroupSids = new HashSet<Sid>(),
            TenantRoles = role is "ClusterAdmin" or "GovernanceAdmin" ? new HashSet<string>() : new HashSet<string>([role]),
            ClusterRoles = role is "ClusterAdmin" or "GovernanceAdmin" ? new HashSet<string>([role]) : new HashSet<string>(),
            AuthenticationScheme = "Test"
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

    private sealed class InMemoryFilterRepo : IVirtualFilterRepository
    {
        private readonly List<VirtualFilter> _filters = [];
        private readonly List<VirtualFilterAccessProfile> _profiles = [];
        private long _generation = 1;

        public Task<VirtualFilterSnapshot> LoadSnapshotAsync(CancellationToken ct = default) =>
            Task.FromResult(new VirtualFilterSnapshot(_generation, _filters.ToList(), _profiles.ToList()));

        public Task<long> GetGenerationAsync(CancellationToken ct = default) => Task.FromResult(_generation);

        public Task ApplyAsync(VirtualFilterChangeSet changes, CancellationToken ct = default)
        {
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
            _generation++;
            return Task.CompletedTask;
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
