namespace Autheris.Tests.Unit.Policy.RowScopes;

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

/// <summary>WP-SIMPL-48 PR 1 (SEC-AZ-15): Four-eyes SQL policy authoring & owner approval per referenced table.</summary>
public sealed class RowPolicyApprovalTests
{
    private static readonly TenantId Tenant = new("tenant_lwe");
    private static readonly Sid GovAuthor = new("S-1-5-21-GOV-AUTHOR");
    private static readonly Sid PartialOwner = new("S-1-5-21-OWNER-ONE");
    private static readonly Sid FullOwnerDelegate = new("S-1-5-21-OWNER-ALL");

    private static readonly VirtualFilterActor GovAuthorActor =
        new(GovAuthor, IsSync: false, Roles: new HashSet<string>([nameof(GatewayRole.GovernanceAdmin)], StringComparer.OrdinalIgnoreCase));

    private static readonly VirtualFilterActor PartialOwnerActor =
        new(PartialOwner, IsSync: false, Roles: new HashSet<string>([nameof(GatewayRole.DataOwner)], StringComparer.OrdinalIgnoreCase));

    private static readonly VirtualFilterActor FullOwnerActor =
        new(FullOwnerDelegate, IsSync: false, Roles: new HashSet<string>([nameof(GatewayRole.DataOwner)], StringComparer.OrdinalIgnoreCase));

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
        bool requireApproval = false)
    {
        var catalog = new InMemoryCatalog([
            CatalogTable("conf", "client", "client_id", "crane_serial_number"),
            CatalogTable("md", "crane", "serial_number", "is_delivered"),
            OrdersTable()
        ]);
        var opts = Options.Create(new GatewayOptions { VirtualFilters = new VirtualFilterOptions { RequireApproval = requireApproval } });
        return new VirtualFilterAdministrationService(repo, NullAuditLogRepository.Instance, opts, catalog: catalog, ownership: owners);
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

    [Fact]
    [Trait("Category", "Security")]
    public async Task Sql_policy_inactive_until_owner_of_every_read_table_approves()
    {
        var repo = new InMemoryFilterRepo();
        var owners = new InMemoryOwnershipRepo();
        owners.Allow(ClientTable, PartialOwner);
        owners.Allow(ClientTable, FullOwnerDelegate);
        owners.Allow(CraneTable, FullOwnerDelegate);
        var service = CreateService(repo, owners, requireApproval: false);
        var resolver = CreateResolver(repo);

        // Even with RequireApproval=false, SQL policies start PendingApproval
        var saved = await service.SaveFilterAsync(SqlPolicy(), GovAuthorActor);
        saved.Status.ShouldBe(FilterApprovalStatus.PendingApproval);

        // Pending SQL policy must NOT apply at runtime (profile Uncovered=Deny denies)
        var pendingOutcome = await resolver.ResolveAsync(new MandatoryFilterQuery(new Sid("S-1-5-21-USER"), new HashSet<Sid>(), new HashSet<string>(), Tenant, OrdersTable()));
        pendingOutcome.IsDenied.ShouldBeTrue();

        // Owner of only ONE of the two referenced tables cannot approve
        var hash = VirtualFilterAdministrationService.PendingHash(saved);
        await Should.ThrowAsync<UnauthorizedAccessException>(() =>
            service.ApproveFilterAsync(Tenant, saved.Name, PartialOwnerActor, hash));

        // Authorized owner/delegate of EVERY referenced table succeeds
        var approved = await service.ApproveFilterAsync(Tenant, saved.Name, FullOwnerActor, hash);
        approved.Status.ShouldBe(FilterApprovalStatus.Active);
        approved.ApprovedBySid.ShouldBe(FullOwnerDelegate.Value);

        var activeOutcome = await resolver.ResolveAsync(new MandatoryFilterQuery(new Sid("S-1-5-21-USER"), new HashSet<Sid>(), new HashSet<string>(), Tenant, OrdersTable()));
        activeOutcome.IsDenied.ShouldBeFalse();
        activeOutcome.AppliedFilters.ShouldBe(["active_cranes_sql"]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Author_cannot_approve_own_sql_policy()
    {
        var repo = new InMemoryFilterRepo();
        var owners = new InMemoryOwnershipRepo();
        owners.Allow(ClientTable, GovAuthor);
        owners.Allow(CraneTable, GovAuthor);
        var service = CreateService(repo, owners);

        var saved = await service.SaveFilterAsync(SqlPolicy(), GovAuthorActor);
        var hash = VirtualFilterAdministrationService.PendingHash(saved);

        // Even though GovAuthor owns every referenced table, four-eyes forbids self-approval
        await Should.ThrowAsync<InvalidOperationException>(() =>
            service.ApproveFilterAsync(Tenant, saved.Name, GovAuthorActor, hash));

        (await repo.LoadSnapshotAsync()).Filters.Single().Status.ShouldBe(FilterApprovalStatus.PendingApproval);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Only_governance_admin_authors_sql_policy()
    {
        var repo = new InMemoryFilterRepo();
        var owners = new InMemoryOwnershipRepo();
        var service = CreateService(repo, owners);

        var filterAdmin = new VirtualFilterActor(new Sid("S-1-5-21-FILTERADMIN"), IsSync: false, Roles: new HashSet<string>([nameof(GatewayRole.FilterAdmin)]));
        await Should.ThrowAsync<UnauthorizedAccessException>(() => service.SaveFilterAsync(SqlPolicy(), filterAdmin));

        var sqlBody = new { source = "lwetem_prod", sql = SqlPolicy().Sql };
        foreach (var forbiddenRole in new[] { "FilterAdmin", "DataOwner", "Consumer" })
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
        (await repo.LoadSnapshotAsync()).Filters.Single().Status.ShouldBe(FilterApprovalStatus.PendingApproval);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Definition_change_requires_reapproval_and_old_version_keeps_applying()
    {
        var repo = new InMemoryFilterRepo();
        var owners = new InMemoryOwnershipRepo();
        owners.Allow(ClientTable, FullOwnerDelegate);
        owners.Allow(CraneTable, FullOwnerDelegate);
        var service = CreateService(repo, owners);
        var resolver = CreateResolver(repo);

        var v1 = await service.SaveFilterAsync(SqlPolicy("from conf.client client where client.client_id = target.client_id"), GovAuthorActor);
        await service.ApproveFilterAsync(Tenant, v1.Name, FullOwnerActor, VirtualFilterAdministrationService.PendingHash(v1));

        // Update SQL definition: new change sits in Draft (PendingApproval) while Active v1 continues applying
        var v2 = await service.SaveFilterAsync(
            SqlPolicy("from conf.client client join md.crane crane on crane.serial_number = client.crane_serial_number where crane.is_delivered is null and target.client_id = client.client_id"),
            GovAuthorActor);

        v2.Status.ShouldBe(FilterApprovalStatus.Active);
        v2.Draft.ShouldNotBeNull();
        v2.Draft!.Status.ShouldBe(FilterApprovalStatus.PendingApproval);

        var query = new MandatoryFilterQuery(new Sid("S-1-5-21-USER"), new HashSet<Sid>(), new HashSet<string>(), Tenant, OrdersTable());
        var whilePending = await resolver.ResolveAsync(query);
        whilePending.IsDenied.ShouldBeFalse();
        whilePending.PredicateSql.ShouldNotBeNull().ShouldNotContain("crane");

        // Approve the Draft -> v2 becomes active
        var promoted = await service.ApproveFilterAsync(Tenant, v1.Name, FullOwnerActor, VirtualFilterAdministrationService.PendingHash(v2));
        promoted.Status.ShouldBe(FilterApprovalStatus.Active);
        promoted.Draft.ShouldBeNull();

        var afterApproval = await resolver.ResolveAsync(query);
        afterApproval.IsDenied.ShouldBeFalse();
        afterApproval.PredicateSql.ShouldNotBeNull().ShouldContain("crane");
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

    private sealed class InMemoryOwnershipRepo : IDataOwnershipRepository
    {
        private readonly HashSet<(string Domain, string Schema, string Table, string Sid)> _allowed = new();

        public void Allow(TableIdentifier table, Sid sid) =>
            _allowed.Add((table.Domain.ToLowerInvariant(), table.Schema.ToLowerInvariant(), table.TableName.ToLowerInvariant(), sid.Value));

        public Task<DataOwnerDelegation> DelegateDataOwnershipAsync(DataOwnerDelegation delegation, CancellationToken ct = default) =>
            throw new NotSupportedException();

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
