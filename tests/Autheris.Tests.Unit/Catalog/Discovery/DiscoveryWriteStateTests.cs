namespace Autheris.Tests.Unit.Discovery;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.ConsentRequests;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Policy.WriteGrants;
using Autheris.Application.Services;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.GraphQL.Types;
using Autheris.Tests.Common;
using HotChocolate;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class DiscoveryWriteStateTests
{
    private static readonly TenantId Tenant = new("tenant-a");
    private static readonly Sid CallerSid = new("S-1-5-21-CALLER");
    private static readonly Sid DataOwnerSid = new("S-1-5-21-OWNER");
    private static readonly Sid NonOwnerSid = new("S-1-5-21-OTHER");

    [Fact]
    public async Task WriteState_EqualsDecisionPointWriteDecision()
    {
        var orders = MakeOrdersTable("orders");
        var policyDep = MakeOrdersTable("policy_rules");
        var readOnly = MakeOrdersTable("locked_table");

        var now = DateTimeOffset.UtcNow;
        var consents = new InMemoryConsentRepo
        {
            Consents =
            {
                new Consent
                {
                    Id = Guid.NewGuid(),
                    TableId = orders.Table.Id,
                    TableIdentifier = orders.Identifier,
                    TenantId = Tenant,
                    GranteeType = GranteeType.User,
                    GranteeSid = CallerSid,
                    Effect = ConsentEffect.Allow,
                    ValidFrom = now.AddMinutes(-5),
                    ValidTo = now.AddHours(1),
                    WriteRules =
                    [
                        new WriteRule(Guid.NewGuid(), WriteOperation.Insert, ["id", "status", "amount", "audit_key", "internal_flag"], WriteRowScope.OwnRows),
                        new WriteRule(Guid.NewGuid(), WriteOperation.Update, ["id", "status", "audit_key"], WriteRowScope.OwnRows),
                        new WriteRule(Guid.NewGuid(), WriteOperation.Delete, [], WriteRowScope.OwnRows)
                    ]
                },
                new Consent
                {
                    Id = Guid.NewGuid(),
                    TableId = readOnly.Table.Id,
                    TableIdentifier = readOnly.Identifier,
                    TenantId = Tenant,
                    GranteeType = GranteeType.User,
                    GranteeSid = CallerSid,
                    Effect = ConsentEffect.Allow,
                    ValidFrom = now.AddMinutes(-5),
                    ValidTo = now.AddHours(1)
                }
            }
        };

        var policyDepIndex = new InMemoryPolicyDepIndex { PolicyTables = { policyDep.Identifier } };
        var pdp = CreateRealPdp(consents, policyDepIndex, allowDeleteGrants: false);
        var discovery = CreateDiscoveryService([orders, policyDep, readOnly], consents, pdp: pdp);

        var callerPrincipal = BuildPrincipal(CallerSid);
        var caller = new CatalogCaller(callerPrincipal, Tenant, CatalogChannel.Rest);
        var subject = SecurityPrincipalContext.FromPrincipal(callerPrincipal, Tenant);

        // Compare DiscoveryObject.Write against direct AccessDecisionPoint write decisions
        var ordersObj = await discovery.GetAsync(orders.Identifier, caller);
        ordersObj.ShouldNotBeNull();

        var pdpInsert = await pdp.DecideAsync(new AccessRequest(subject, orders, AccessOperation.Insert, AccessTransport.Discovery));
        var pdpUpdate = await pdp.DecideAsync(new AccessRequest(subject, orders, AccessOperation.Update, AccessTransport.Discovery));
        var pdpDelete = await pdp.DecideAsync(new AccessRequest(subject, orders, AccessOperation.Delete, AccessTransport.Discovery));

        ordersObj!.Write.ShouldNotBeNull();
        ordersObj.Write!.CanInsert.ShouldBe(pdpInsert.IsAllowed);
        ordersObj.Write.CanInsert.ShouldBeTrue();
        ordersObj.Write.InsertColumns.ShouldBe(["id", "status", "amount"]);
        ordersObj.Write.CanUpdate.ShouldBe(pdpUpdate.IsAllowed);
        ordersObj.Write.CanUpdate.ShouldBeTrue();
        ordersObj.Write.UpdateRowScope.ShouldBe(pdpUpdate.WriteGrant!.RowScope);
        ordersObj.Write.UpdateColumns.ShouldBe(["status"]);
        ordersObj.Write.CanDelete.ShouldBe(pdpDelete.IsAllowed);
        ordersObj.Write.CanDelete.ShouldBeFalse();
        ordersObj.Write.Requestable.ShouldBeTrue();

        var protectedCol = ordersObj.Columns.Single(c => string.Equals(c.Name, "audit_key", StringComparison.Ordinal));
        protectedCol.WriteProtected.ShouldBeTrue();
        ordersObj.Columns.ShouldNotContain(c => string.Equals(c.Name, "internal_flag", StringComparison.Ordinal));

        // GraphQL DTO mirrors DiscoveryObject.Write and DiscoveryColumn.WriteProtected identically
        var gqlObj = await new Query().GetCatalogObjectAsync(
            orders.Identifier.Domain,
            orders.Identifier.Schema,
            orders.Identifier.TableName,
            discoveryService: discovery,
            httpContextAccessor: CreateHttpAccessor(callerPrincipal));
        gqlObj.ShouldNotBeNull();
        AssertWriteState(gqlObj!.Write, ordersObj.Write!);
        gqlObj.Columns.Single(c => string.Equals(c.Name, "audit_key", StringComparison.Ordinal)).WriteProtected.ShouldBeTrue();

        // Policy-dependency table cannot carry write grants -> !Requestable & all false
        var depObj = await discovery.GetAsync(policyDep.Identifier, caller);
        depObj.ShouldNotBeNull();
        AssertWriteState(depObj!.Write, new DiscoveryWriteState(false, false, false, null, [], [], Requestable: false));

        // Table without write grants (read consent only) -> Can* false, Requestable true
        var noWriteObj = await discovery.GetAsync(readOnly.Identifier, caller);
        noWriteObj.ShouldNotBeNull();
        AssertWriteState(noWriteObj!.Write, new DiscoveryWriteState(false, false, false, null, [], [], Requestable: true));

        // When Delete grants are enabled at PDP level, Discovery reflects CanDelete = true
        var deleteEnabledPdp = CreateRealPdp(consents, policyDepIndex, allowDeleteGrants: true);
        var deleteDiscovery = CreateDiscoveryService([orders], consents, pdp: deleteEnabledPdp);
        var withDelete = await deleteDiscovery.GetAsync(orders.Identifier, caller);
        withDelete!.Write!.CanDelete.ShouldBeTrue();

        // Non-WebSql/non-Discovery transports remain rejected fail-closed by PDP
        var restWrite = await pdp.DecideAsync(new AccessRequest(subject, orders, AccessOperation.Insert, AccessTransport.Rest));
        restWrite.IsAllowed.ShouldBeFalse();
        restWrite.Reasons.ShouldContain(r => string.Equals(r.Code, "WRITE_TRANSPORT_NOT_WIRED", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WriteState_ReadsNoData()
    {
        var orders = MakeOrdersTable("orders");
        var now = DateTimeOffset.UtcNow;
        var consents = new InMemoryConsentRepo
        {
            Consents =
            {
                new Consent
                {
                    Id = Guid.NewGuid(),
                    TableId = orders.Table.Id,
                    TableIdentifier = orders.Identifier,
                    TenantId = Tenant,
                    GranteeType = GranteeType.User,
                    GranteeSid = CallerSid,
                    Effect = ConsentEffect.Allow,
                    ValidFrom = now.AddMinutes(-5),
                    ValidTo = now.AddHours(1),
                    RowFilters = [new ConsentRowFilter { ColumnName = "status", Operator = "EQ", ValueType = "string", ValueJson = "\"SECRET_VALUE\"" }],
                    WriteRules =
                    [
                        new WriteRule(Guid.NewGuid(), WriteOperation.Update, ["status"], WriteRowScope.OwnRows)
                    ]
                }
            }
        };

        var spyPdp = new RecordingAccessDecisionPoint(CreateRealPdp(consents));
        var discovery = CreateDiscoveryService([orders], consents, pdp: spyPdp);
        var caller = new CatalogCaller(BuildPrincipal(CallerSid), Tenant, CatalogChannel.Rest);

        var page = await discovery.ListAsync(new DiscoveryQuery(Surface: DiscoverySurface.Rest), caller);
        page.Items.Count.ShouldBe(1);
        var item = page.Items[0];
        item.Write.ShouldNotBeNull();
        item.Write!.CanUpdate.ShouldBeTrue();
        item.Write.UpdateRowScope.ShouldBe(WriteRowScope.OwnRows);
        item.Write.UpdateColumns.ShouldBe(["status"]);

        // PDP evaluated metadata only via AccessTransport.Discovery; zero SQL connections or predicates leaked
        spyPdp.Recorded.Count.ShouldBe(4);
        spyPdp.Recorded.ShouldAllBe(r => r.Transport == AccessTransport.Discovery);
        spyPdp.Recorded.Select(r => r.Operation).ShouldBe([
            AccessOperation.Discover,
            AccessOperation.Insert,
            AccessOperation.Update,
            AccessOperation.Delete
        ]);
    }

    [Fact]
    public async Task WriteAccessRequest_RoutesToDataOwner()
    {
        var orders = MakeOrdersTable("orders");
        var consents = new InMemoryConsentRepo();
        var approvals = new InMemoryApprovalRepo(consents);
        var ownership = new InMemoryOwnershipRepo();
        ownership.OwnersByTable[orders.Identifier] =
        [
            new DataOwner
            {
                AdSid = DataOwnerSid,
                AdAccount = "CORP\\owner",
                DisplayName = "Orders Owner",
                Email = "owner@example.com",
                IsActive = true
            }
        ];

        var tableRepo = new InMemoryTableRepo([orders]);
        var pdp = CreateRealPdp(consents);
        var discovery = CreateDiscoveryService([orders], consents, approvals, ownership, pdp);

        var callerPrincipal = BuildPrincipal(CallerSid);
        var caller = new CatalogCaller(callerPrincipal, Tenant, CatalogChannel.Rest);

        // Before request: Discovery shows Requestable = true, CanInsert = false, and exposes DataOwner info
        var before = await discovery.GetAsync(orders.Identifier, caller);
        before.ShouldNotBeNull();
        before!.Write!.CanInsert.ShouldBeFalse();
        before.Write.Requestable.ShouldBeTrue();
        before.Owners.ShouldHaveSingleItem().DisplayName.ShouldBe("Orders Owner");

        // Submit write request using Discovery object identifiers
        var requestService = new ConsentRequestService(tableRepo, approvals);
        var submitResult = await requestService.SubmitAsync(new ConsentRequestSubmission(
            before.Domain,
            before.Schema,
            before.Name,
            "Daily ETL ingestion",
            7,
            null,
            CallerSid,
            callerPrincipal,
            Tenant,
            RequestedWriteRules: [new WriteRule(Guid.Empty, WriteOperation.Insert, ["status", "amount"], WriteRowScope.OwnRows)],
            RequestWriteOnly: true));
        submitResult.Status.ShouldBe("PENDING");

        var pending = approvals.Requests.ShouldHaveSingleItem();

        // Non-owner cannot approve the routed request
        var nonOwnerEx = await Should.ThrowAsync<GraphQLException>(() => new Mutation().ApproveConsentRequestAsync(
            pending.Id,
            null,
            approvals,
            ownership,
            consents,
            tableRepo,
            CreateHttpAccessor(BuildPrincipal(NonOwnerSid))));
        nonOwnerEx.Errors.ShouldContain(e => string.Equals(e.Code, "FORBIDDEN", StringComparison.Ordinal));

        // DataOwner approves the write request
        var ownerResult = await new Mutation().ApproveConsentRequestAsync(
            pending.Id,
            null,
            approvals,
            ownership,
            consents,
            tableRepo,
            CreateHttpAccessor(BuildPrincipal(DataOwnerSid)));
        ownerResult.Status.ShouldBe("APPROVED");

        // Fresh Discovery instance reflects approved write grant computed by PDP
        var freshDiscovery = CreateDiscoveryService([orders], consents, approvals, ownership, CreateRealPdp(consents));
        var after = await freshDiscovery.GetAsync(orders.Identifier, caller);
        after.ShouldNotBeNull();
        after!.Write!.CanInsert.ShouldBeTrue();
        after.Write.InsertColumns.ShouldBe(["status", "amount"]);
    }

    private static TableMetadata MakeOrdersTable(string name)
    {
        var tableId = Guid.NewGuid();
        return new TableMetadata
        {
            Identifier = new TableIdentifier("sales", "dbo", name),
            OwnerTenantId = Tenant.Value,
            PrimaryKeyColumns = ["id"],
            Table = new Table
            {
                Id = tableId,
                SourceName = "sales",
                SchemaName = "dbo",
                TableName = name,
                DisplayName = name,
                IsActive = true,
                Sensitivity = "NORMAL",
                ObjectKind = DiscoveryObjectKind.Table
            },
            Columns =
            [
                new TableColumn { Id = Guid.NewGuid(), TableId = tableId, ColumnName = "id", DataType = "int", WriteProtected = false },
                new TableColumn { Id = Guid.NewGuid(), TableId = tableId, ColumnName = "status", DataType = "varchar", WriteProtected = false },
                new TableColumn { Id = Guid.NewGuid(), TableId = tableId, ColumnName = "amount", DataType = "decimal", WriteProtected = false },
                new TableColumn { Id = Guid.NewGuid(), TableId = tableId, ColumnName = "audit_key", DataType = "varchar", WriteProtected = true },
                new TableColumn { Id = Guid.NewGuid(), TableId = tableId, ColumnName = "internal_flag", DataType = "bit", WriteProtected = false, DiscoveryHidden = true }
            ]
        };
    }

    private static ClaimsPrincipal BuildPrincipal(Sid sid) =>
        new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, sid.Value),
            new Claim(ClaimTypes.NameIdentifier, sid.Value),
            new Claim(ClaimTypes.Name, sid.Value),
            new Claim("tenant_id", Tenant.Value!),
            GroupResolutionClaims.CreateMarker(),
            GroupResolutionClaims.CreateStatus(GroupResolutionStatus.Complete)
        ], "TestAuth"));

    private static AccessDecisionPoint CreateRealPdp(
        IConsentRepository consentRepo,
        IRowPolicyDependencyIndex? policyDeps = null,
        bool allowDeleteGrants = false)
    {
        var options = Options.Create(new GatewayOptions { Tenancy = TenancyShape.Multi.Options() });
        var evaluator = new WriteGrantEvaluator(consentRepo, policyDeps, allowDeleteGrants: allowDeleteGrants);
        return new AccessDecisionPoint(
            options,
            new ConsentResolutionService(),
            consentRepo,
            policyDependencyIndex: policyDeps,
            writeGrantEvaluator: evaluator);
    }

    private static DiscoveryService CreateDiscoveryService(
        IReadOnlyList<TableMetadata> tables,
        IConsentRepository consents,
        IConsentApprovalRepository? approvals = null,
        IDataOwnershipRepository? ownership = null,
        IAccessDecisionPoint? pdp = null)
    {
        return new DiscoveryService(
            new InMemoryTableRepo(tables),
            pdp ?? CreateRealPdp(consents),
            consents,
            approvals,
            ownership,
            artifactStore: null,
            auditWriter: new NullAuditWriter(),
            Options.Create(new GatewayOptions { Tenancy = TenancyShape.Multi.Options() }),
            Options.Create(new CatalogDiscoveryOptions()),
            logger: NullLogger<DiscoveryService>.Instance);
    }

    private static IHttpContextAccessor CreateHttpAccessor(ClaimsPrincipal user)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConsentDecisionGuard>(new AllowDecisionGuard());
        services.AddSingleton<IGrantPolicy>(new AllowGrantPolicy());
        var context = new DefaultHttpContext
        {
            User = user,
            RequestServices = services.BuildServiceProvider()
        };
        return new StaticHttpContextAccessor(context);
    }

    private static void AssertWriteState(DiscoveryWriteState? actual, DiscoveryWriteState expected)
    {
        actual.ShouldNotBeNull();
        actual!.CanInsert.ShouldBe(expected.CanInsert);
        actual.CanUpdate.ShouldBe(expected.CanUpdate);
        actual.CanDelete.ShouldBe(expected.CanDelete);
        actual.UpdateRowScope.ShouldBe(expected.UpdateRowScope);
        actual.InsertColumns.ShouldBe(expected.InsertColumns);
        actual.UpdateColumns.ShouldBe(expected.UpdateColumns);
        actual.Requestable.ShouldBe(expected.Requestable);
    }

    private sealed class RecordingAccessDecisionPoint(IAccessDecisionPoint inner) : IAccessDecisionPoint
    {
        public List<AccessRequest> Recorded { get; } = [];

        public ValueTask<TableAccessDecision> DecideAsync(AccessRequest request, CancellationToken ct = default)
        {
            Recorded.Add(request);
            return inner.DecideAsync(request, ct);
        }

        public async ValueTask<IReadOnlyList<TableAccessDecision>> DecideManyAsync(
            SecurityPrincipalContext subject,
            IReadOnlyList<TableMetadata> objects,
            AccessOperation operation,
            AccessTransport transport,
            CancellationToken ct = default)
        {
            foreach (var obj in objects)
            {
                Recorded.Add(new AccessRequest(subject, obj, operation, transport));
            }
            return await inner.DecideManyAsync(subject, objects, operation, transport, ct).ConfigureAwait(false);
        }

        public ValueTask<AccessExplanation> ExplainAsync(AccessRequest request, CancellationToken ct = default)
            => inner.ExplainAsync(request, ct);
    }

    private sealed class InMemoryPolicyDepIndex : IRowPolicyDependencyIndex
    {
        public HashSet<TableIdentifier> PolicyTables { get; } = [];
        public bool IsPolicyDependency(TenantId tenant, TableIdentifier table) => PolicyTables.Contains(table);
        public IReadOnlySet<TableIdentifier> GetPolicyDependencies(TenantId tenant) => PolicyTables;
    }

    private sealed class InMemoryTableRepo(IReadOnlyList<TableMetadata> tables) : ITableMetadataRepository
    {
        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default)
            => Task.FromResult(tables.FirstOrDefault(t => t.Identifier.Equals(table)));
        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default)
            => Task.FromResult(tables);
        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(TableIdentifier table, IReadOnlyList<string> primaryKey, IReadOnlyList<TableIndexDefinition> indexes, IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<long> FlushCatalogVersionAsync(CancellationToken ct = default) => ValueTask.FromResult(1L);
    }

    private sealed class InMemoryConsentRepo : IConsentRepository
    {
        public List<Consent> Consents { get; } = [];

        public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, CancellationToken ct = default)
            => GetActiveConsentsForSubjectsAsync(subjects, table, atTime, null, ct);

        public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, TenantId? tenantId, CancellationToken ct = default)
        {
            var subjectSet = subjects.ToHashSet();
            return Task.FromResult<IReadOnlyList<Consent>>(Consents
                .Where(c => c.TableIdentifier.Equals(table) &&
                            c.GranteeSid.HasValue &&
                            subjectSet.Contains(c.GranteeSid.Value) &&
                            c.IsActive(atTime) &&
                            (!tenantId.HasValue || c.TenantId == tenantId.Value))
                .ToList());
        }

        public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, IEnumerable<string>? roles = null, DateTimeOffset? atTime = null, CancellationToken ct = default)
            => GetAllActiveConsentsForSubjectsAsync(subjects, roles, atTime, null, ct);

        public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, IEnumerable<string>? roles, DateTimeOffset? atTime, TenantId? tenantId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Consent>>(Consents);

        public Task<Consent> CreateConsentAsync(Consent consent, CancellationToken ct = default)
        {
            Consents.Add(consent);
            return Task.FromResult(consent);
        }

        public Task<Consent?> GetConsentByIdAsync(Guid consentId, CancellationToken ct = default)
            => Task.FromResult(Consents.FirstOrDefault(c => c.Id == consentId));
        public Task RevokeConsentAsync(Guid consentId, Sid revokedBySid, string reason, ConsentRevocationAuthority authority, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Consent>> GetExpiringConsentsAsync(DateTimeOffset threshold, int maxResults = 1000, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task ExtendConsentExpiryAsync(Guid consentId, DateTimeOffset newValidTo, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Consent>> GetActiveConsentsByConsentRequestIdAsync(Guid consentRequestId, DateTimeOffset atTime, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<bool> RevokeSystemConsentAsync(Guid consentId, Guid consentRequestId, Sid revokedBySid, string reason, CancellationToken ct = default) => Task.FromResult(false);
        public Task<Autheris.Application.Governance.Paging.ConsentPage> GetConsentPageAsync(Autheris.Application.Governance.Paging.ConsentPageQuery query, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ReplaceConsentsAsync(IReadOnlyList<Guid> revokeConsentIds, IReadOnlyList<Consent> create, Sid actor, string reason, ConsentRevocationAuthority authority, IReadOnlyList<AuditLogEntry>? auditEntries = null, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class InMemoryApprovalRepo(InMemoryConsentRepo consents) : IConsentApprovalRepository
    {
        public List<ConsentRequest> Requests { get; } = [];

        public Task<ConsentRequest> CreateConsentRequestAsync(ConsentRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(request);
        }

        public Task<ConsentRequest?> GetConsentRequestAsync(Guid requestId, CancellationToken ct = default)
            => Task.FromResult(Requests.FirstOrDefault(r => r.Id == requestId));

        public Task<IReadOnlyList<ConsentRequest>> GetRequestsByRequesterSidAsync(Sid requesterSid, string? status = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ConsentRequest>>(Requests.Where(r => r.RequesterSid == requesterSid).ToList());

        public Task<Consent> ExecuteAtomicApprovalWithConsentAsync(
            Guid requestId,
            ApprovalStep approvalStep,
            Consent consentToCreate,
            IReadOnlyList<ConsentRowFilter> rowFilters,
            IReadOnlyList<ConsentColumnRule> columnRules,
            CancellationToken ct = default)
        {
            var req = Requests.First(r => r.Id == requestId);
            req.Status = "APPROVED";
            consentToCreate.RowFilters = rowFilters;
            consentToCreate.ColumnRules = columnRules;
            consents.Consents.Add(consentToCreate);
            return Task.FromResult(consentToCreate);
        }

        public Task<IReadOnlyList<ConsentRequest>> GetPendingRequestsForApproverAsync(Sid approverSid, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ConsentRequest>>([]);

        public Task<ConsentRequest> ApproveConsentRequestStepAsync(Guid requestId, Sid approverSid, CancellationToken ct = default)
        {
            var req = Requests.First(r => r.Id == requestId);
            req.Status = "APPROVED";
            return Task.FromResult(req);
        }
        public Task<ConsentRequest> RejectConsentRequestAsync(Guid requestId, Sid approverSid, string reason, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ConsentRequest?> GetConsentRequestByExternalReferenceAsync(Guid externalSystemId, string externalReference, TenantId tenantId, CancellationToken ct = default) => Task.FromResult<ConsentRequest?>(null);
        public Task ActivateConsentAsync(Guid requestId, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteConsentRequestAsync(Guid requestId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class InMemoryOwnershipRepo : IDataOwnershipRepository
    {
        public Dictionary<TableIdentifier, List<DataOwner>> OwnersByTable { get; } = [];

        public Task<IReadOnlyList<DataOwner>> GetDataOwnersForTableAsync(TableIdentifier table, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DataOwner>>(OwnersByTable.TryGetValue(table, out var list) ? list : []);

        public Task<bool> IsAuthorizedApproverForTableAsync(TableIdentifier table, Sid approverSid, CancellationToken ct = default)
            => Task.FromResult(OwnersByTable.TryGetValue(table, out var list) && list.Any(o => o.IsActive && o.AdSid == approverSid));

        public Task<DataOwnerDelegation> DelegateDataOwnershipAsync(DataOwnerDelegation delegation, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class NullAuditWriter : IAuditWriter
    {
        public Task RecordRequiredAsync(AuditLogEntry entry, CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordBestEffortAsync(AuditLogEntry entry, CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordAsync(AuditLogEntry entry, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class AllowDecisionGuard : IConsentDecisionGuard
    {
        public ConsentDecisionVerdict Evaluate(ClaimsPrincipal? principal) => ConsentDecisionVerdict.Allow();
    }

    private sealed class AllowGrantPolicy : IGrantPolicy
    {
        public GrantDecision Evaluate(GrantRequest request)
            => new(true, null, false, GrantRecertClass.None, GrantClass.Standing, "Allowed");
    }

    private sealed class StaticHttpContextAccessor(HttpContext context) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = context;
    }
}
