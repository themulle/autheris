namespace Autheris.Tests.Unit.Policy.RowScopes;

using Autheris.Application.Governance.Ownership;
using System;
using System.Collections.Generic;
using System.Data.Common;
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
using Autheris.Tests.Unit.VirtualFilters;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

public sealed class VirtualFilterApprovalReasonTests
{
    private static readonly TenantId Tenant = new("tenant_lwe");
    private static readonly Sid GovAuthor = new("S-1-5-21-GOV-AUTHOR");
    private static readonly Sid FilterAuthor = new("S-1-5-21-FILTER-AUTHOR");
    private static readonly Sid Approver = new("S-1-5-21-APPROVER");
    private static readonly Sid FullOwnerDelegate = new("S-1-5-21-OWNER-ALL");
    private static readonly Sid SyncSid = new("S-1-5-21-TALOS");

    private static readonly VirtualFilterActor GovAuthorActor =
        new(GovAuthor, IsSync: false, Roles: new HashSet<string>([nameof(GatewayRole.GovernanceAdmin)], StringComparer.OrdinalIgnoreCase));

    private static readonly VirtualFilterActor FilterAdminActor =
        new(FilterAuthor, IsSync: false, Roles: new HashSet<string>([nameof(GatewayRole.FilterAdmin)], StringComparer.OrdinalIgnoreCase));

    private static readonly VirtualFilterActor FullOwnerActor =
        new(FullOwnerDelegate, IsSync: false, Roles: new HashSet<string>([nameof(GatewayRole.Consumer)], StringComparer.OrdinalIgnoreCase));

    private static readonly VirtualFilterActor ApproverActor =
        new(Approver, IsSync: false, Roles: new HashSet<string>([nameof(GatewayRole.GovernanceAdmin)], StringComparer.OrdinalIgnoreCase));

    private static readonly VirtualFilterActor SyncActor = new(SyncSid, IsSync: true);

    private static readonly TableIdentifier ClientTable = new("lwetem_prod", "conf", "client");
    private static readonly TableIdentifier CraneTable = new("lwetem_prod", "md", "crane");

    private static VirtualFilter SqlPolicy(string sql = "from conf.client client join md.crane crane on crane.serial_number = client.crane_serial_number where crane.is_delivered is null and target.client_id = client.client_id") => new()
    {
        TenantId = Tenant,
        Name = "active_cranes_sql",
        Source = "lwetem_prod",
        Sql = sql
    };

    private static (VirtualFilterAdministrationService Service, InMemoryFilterRepo Repo, RecordingAuditRepo Audit) CreateService(bool requireApproval)
    {
        var repo = new InMemoryFilterRepo();
        var audit = new RecordingAuditRepo();
        var owners = new InMemoryOwnershipRepo();
        owners.Allow(ClientTable, FullOwnerDelegate);
        owners.Allow(CraneTable, FullOwnerDelegate);
        var catalog = new InMemoryCatalog([
            VirtualFilterTestData.Table("conf", "client", "client_id", "crane_serial_number"),
            VirtualFilterTestData.Table("md", "crane", "serial_number", "is_delivered")
        ]);
        var opts = Options.Create(new GatewayOptions { VirtualFilters = new VirtualFilterOptions { RequireApproval = requireApproval } });
        var service = new VirtualFilterAdministrationService(repo, audit, opts, catalog: catalog, ownership: owners);
        return (service, repo, audit);
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

    private static async Task<JsonDocument> ExecuteJsonAsync(IResult result, HttpContext ctx)
    {
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Position = 0;
        return await JsonDocument.ParseAsync(ctx.Response.Body);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SqlFilter_RequireApprovalFalse_ReasonIsSqlOwnerApproval_StatusPending()
    {
        var (service, _, audit) = CreateService(requireApproval: false);
        var initialSql = "from conf.client client where client.client_id = target.client_id";
        var initialFilter = SqlPolicy(initialSql);

        VirtualFilterAdministrationService.ApprovalReason(
            initialFilter, new VirtualFilterOptions { RequireApproval = false }, GovAuthorActor)
            .ShouldBe(VirtualFilterApprovalReason.SqlDefinitionNeedsOwnerApproval);

        // Even when RequireApproval=true, SQL owner approval reason takes precedence.
        VirtualFilterAdministrationService.ApprovalReason(
            initialFilter, new VirtualFilterOptions { RequireApproval = true }, GovAuthorActor)
            .ShouldBe(VirtualFilterApprovalReason.SqlDefinitionNeedsOwnerApproval);

        // Initial create via PUT endpoint with RequireApproval=false
        var putCtx = HttpCtx(GovAuthor, nameof(GatewayRole.GovernanceAdmin), new { source = "lwetem_prod", sql = initialSql });
        using var putDoc = await ExecuteJsonAsync(await VirtualFilterEndpoints.PutFilterAsync("active_cranes_sql", putCtx, service), putCtx);
        putCtx.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        putDoc.RootElement.GetProperty("status").GetString().ShouldBe("PendingApproval");
        putDoc.RootElement.GetProperty("approval_reason").GetString().ShouldBe("sql_definition_needs_owner_approval");

        // GET list view derives the same approval_reason
        var listCtx = HttpCtx(GovAuthor, nameof(GatewayRole.GovernanceAdmin));
        using var listDoc = await ExecuteJsonAsync(await VirtualFilterEndpoints.ListAsync(listCtx, service), listCtx);
        listCtx.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        var listed = listDoc.RootElement.GetProperty("filters")[0];
        listed.GetProperty("status").GetString().ShouldBe("PendingApproval");
        listed.GetProperty("approval_reason").GetString().ShouldBe("sql_definition_needs_owner_approval");

        // Audit entry for initial pending filter includes snake_case reason
        var initialAudit = audit.Entries.ShouldHaveSingleItem();
        initialAudit.EventType.ShouldBe("VIRTUAL_FILTER_PENDING_APPROVAL");
        using (var auditDoc = JsonDocument.Parse(initialAudit.DetailsJson))
        {
            auditDoc.RootElement.GetProperty("status").GetString().ShouldBe("PendingApproval");
            auditDoc.RootElement.GetProperty("reason").GetString().ShouldBe("sql_definition_needs_owner_approval");
        }

        // Approve v1 -> becomes Active with approval_reason = "none"
        var pendingHash = putDoc.RootElement.GetProperty("pending_hash").GetString()!;
        var approved = await service.ApproveFilterAsync(Tenant, "active_cranes_sql", FullOwnerActor, pendingHash);
        approved.Status.ShouldBe(FilterApprovalStatus.Active);
        VirtualFilterAdministrationService.PendingApprovalReason(approved).ShouldBe(VirtualFilterApprovalReason.None);
        VirtualFilterAdministrationService.FormatApprovalReason(VirtualFilterAdministrationService.PendingApprovalReason(approved)).ShouldBe("none");

        // Draft update on Active SQL filter keeps Status=Active and puts update in Draft
        var updatedSql = SqlPolicy().Sql!;
        var drafted = await service.SaveFilterAsync(SqlPolicy(updatedSql), GovAuthorActor);
        drafted.Status.ShouldBe(FilterApprovalStatus.Active);
        drafted.Draft.ShouldNotBeNull();
        drafted.Draft!.Status.ShouldBe(FilterApprovalStatus.PendingApproval);
        VirtualFilterAdministrationService.PendingApprovalReason(drafted).ShouldBe(VirtualFilterApprovalReason.SqlDefinitionNeedsOwnerApproval);
        VirtualFilterAdministrationService.FormatApprovalReason(VirtualFilterAdministrationService.PendingApprovalReason(drafted))
            .ShouldBe("sql_definition_needs_owner_approval");

        // Audit entry for draft update also includes snake_case reason
        var draftAudit = audit.Entries.Last();
        draftAudit.EventType.ShouldBe("VIRTUAL_FILTER_PENDING_APPROVAL");
        using (var draftAuditDoc = JsonDocument.Parse(draftAudit.DetailsJson))
        {
            draftAuditDoc.RootElement.GetProperty("status").GetString().ShouldBe("DraftPendingApproval");
            draftAuditDoc.RootElement.GetProperty("reason").GetString().ShouldBe("sql_definition_needs_owner_approval");
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task StructuredFilter_RequireApprovalFalse_IsActive_ReasonNone()
    {
        var (service, _, audit) = CreateService(requireApproval: false);
        var filter = VirtualFilterTestData.DavidFilter();

        VirtualFilterAdministrationService.ApprovalReason(
            filter, new VirtualFilterOptions { RequireApproval = false }, FilterAdminActor)
            .ShouldBe(VirtualFilterApprovalReason.None);

        var body = new
        {
            source = "lwetem_prod",
            key_columns = new[] { "client.client_id" },
            from = new { table = "conf.client", alias = "client" },
            joins = new[] { new { table = "md.crane", alias = "crane", left = "crane.serial_number", right = "client.crane_serial_number" } },
            where = new[] { new { column = "crane.is_delivered", op = "is_null" } }
        };

        var putCtx = HttpCtx(FilterAuthor, nameof(GatewayRole.FilterAdmin), body);
        using var putDoc = await ExecuteJsonAsync(await VirtualFilterEndpoints.PutFilterAsync(filter.Name, putCtx, service), putCtx);
        putCtx.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        putDoc.RootElement.GetProperty("status").GetString().ShouldBe("Active");
        putDoc.RootElement.GetProperty("approval_reason").GetString().ShouldBe("none");

        var listCtx = HttpCtx(FilterAuthor, nameof(GatewayRole.FilterAdmin));
        using var listDoc = await ExecuteJsonAsync(await VirtualFilterEndpoints.ListAsync(listCtx, service), listCtx);
        listCtx.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        var listed = listDoc.RootElement.GetProperty("filters")[0];
        listed.GetProperty("status").GetString().ShouldBe("Active");
        listed.GetProperty("approval_reason").GetString().ShouldBe("none");

        var entry = audit.Entries.ShouldHaveSingleItem();
        entry.EventType.ShouldBe("VIRTUAL_FILTER_CREATED");
    }

    [Fact]
    public async Task StructuredFilter_RequireApprovalTrue_ReasonIsOption()
    {
        var (service, _, audit) = CreateService(requireApproval: true);
        var filter = VirtualFilterTestData.DavidFilter();

        VirtualFilterAdministrationService.ApprovalReason(
            filter, new VirtualFilterOptions { RequireApproval = true }, FilterAdminActor)
            .ShouldBe(VirtualFilterApprovalReason.RequireApprovalOption);

        var saved = await service.SaveFilterAsync(filter, FilterAdminActor);
        saved.Status.ShouldBe(FilterApprovalStatus.PendingApproval);
        VirtualFilterAdministrationService.PendingApprovalReason(saved).ShouldBe(VirtualFilterApprovalReason.RequireApprovalOption);
        VirtualFilterAdministrationService.FormatApprovalReason(VirtualFilterAdministrationService.PendingApprovalReason(saved))
            .ShouldBe("require_approval_option");

        var initialAudit = audit.Entries.ShouldHaveSingleItem();
        initialAudit.EventType.ShouldBe("VIRTUAL_FILTER_PENDING_APPROVAL");
        using (var auditDoc = JsonDocument.Parse(initialAudit.DetailsJson))
        {
            auditDoc.RootElement.GetProperty("status").GetString().ShouldBe("PendingApproval");
            auditDoc.RootElement.GetProperty("reason").GetString().ShouldBe("require_approval_option");
        }

        // Approve -> Active -> draft update still reports require_approval_option
        var approved = await service.ApproveFilterAsync(Tenant, filter.Name, ApproverActor, VirtualFilterAdministrationService.PendingHash(saved));
        approved.Status.ShouldBe(FilterApprovalStatus.Active);
        VirtualFilterAdministrationService.PendingApprovalReason(approved).ShouldBe(VirtualFilterApprovalReason.None);

        var drafted = await service.SaveFilterAsync(filter with { KeyColumns = ["client.client_id"] }, FilterAdminActor);
        drafted.Status.ShouldBe(FilterApprovalStatus.Active);
        drafted.Draft.ShouldNotBeNull();
        VirtualFilterAdministrationService.PendingApprovalReason(drafted).ShouldBe(VirtualFilterApprovalReason.RequireApprovalOption);

        var draftAudit = audit.Entries.Last();
        draftAudit.EventType.ShouldBe("VIRTUAL_FILTER_PENDING_APPROVAL");
        using (var draftAuditDoc = JsonDocument.Parse(draftAudit.DetailsJson))
        {
            draftAuditDoc.RootElement.GetProperty("status").GetString().ShouldBe("DraftPendingApproval");
            draftAuditDoc.RootElement.GetProperty("reason").GetString().ShouldBe("require_approval_option");
        }
    }

    [Fact]
    public async Task SyncActor_SqlFilter_ReasonNone_Active()
    {
        var (service, _, audit) = CreateService(requireApproval: false);
        var filter = SqlPolicy();

        VirtualFilterAdministrationService.ApprovalReason(
            filter, new VirtualFilterOptions { RequireApproval = false }, SyncActor)
            .ShouldBe(VirtualFilterApprovalReason.None);

        VirtualFilterAdministrationService.ApprovalReason(
            filter, new VirtualFilterOptions { RequireApproval = true }, SyncActor)
            .ShouldBe(VirtualFilterApprovalReason.None);

        var saved = await service.SaveFilterAsync(filter, SyncActor);
        saved.Status.ShouldBe(FilterApprovalStatus.Active);
        VirtualFilterAdministrationService.PendingApprovalReason(saved).ShouldBe(VirtualFilterApprovalReason.None);
        VirtualFilterAdministrationService.FormatApprovalReason(VirtualFilterAdministrationService.PendingApprovalReason(saved)).ShouldBe("none");

        var entry = audit.Entries.ShouldHaveSingleItem();
        entry.EventType.ShouldBe("VIRTUAL_FILTER_CREATED");
    }

    // Helper types placed after all [Fact]/[Theory] methods so SecurityTestInventoryTests records outer class FQN cleanly.
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

    private sealed class RecordingAuditRepo : IAuditLogRepository
    {
        public List<AuditLogEntry> Entries { get; } = [];

        public Task RecordAuditEventAsync(AuditLogEntry entry, CancellationToken ct = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task RecordAuditEventAsync(AuditLogEntry entry, DbTransaction existingTx, CancellationToken ct = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AuditLogEntry>> GetAuditLogEntriesAsync(int limit = 100, TenantId? tenantId = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AuditLogEntry>>(Entries);

        public Task<IReadOnlyList<AuditLogEntry>> QueryAuditLogsAsync(
            string? targetTable = null,
            Sid? actorSid = null,
            DateTimeOffset? since = null,
            int limit = 1000,
            TenantId? tenantId = null,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AuditLogEntry>>(Entries);

        public Task<bool> VerifyAuditHashChainAsync(CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class InMemoryOwnershipRepo : IContainerOwnership
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
            TableIdentifier table,
            IReadOnlyList<string> primaryKey,
            IReadOnlyList<TableIndexDefinition> indexes,
            IReadOnlyList<KeyColumnDirection>? keyDirections = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task FlushPendingVersionChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
