using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Catalog;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Paging;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Services;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.GraphQL.Types;
using Autheris.Tests.Unit.TestDoubles;
using HotChocolate;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.GraphQL;

public sealed class QueryConsentAndCatalogPagingTests
{
    private static readonly TenantId TenantA = new("tenant-a");

    private static ClaimsPrincipal AuthenticatedUser(string sid = "S-USER-1") =>
        new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, sid),
            new Claim(ClaimTypes.NameIdentifier, sid),
            new Claim("tenant_id", TenantA.Value),
            Autheris.Domain.Identity.GroupResolutionClaims.CreateMarker(),
            Autheris.Domain.Identity.GroupResolutionClaims.CreateStatus(Autheris.Domain.Identity.GroupResolutionStatus.Complete)
        ], "TestAuth"));

    private static IHttpContextAccessor AccessorFor(ClaimsPrincipal user) =>
        new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = user } };

    private static TableMetadata MakeTable(int index)
    {
        var tableId = new TableIdentifier("sales", "dbo", $"tbl_{index:D3}");
        return new TableMetadata
        {
            Identifier = tableId,
            OwnerTenantId = TenantA.Value,
            CatalogView = CatalogView.Granted,
            Table = new Table
            {
                Id = Guid.NewGuid(),
                SourceName = "sales",
                SchemaName = "dbo",
                TableName = $"tbl_{index:D3}",
                DataSourceType = DataSourceType.HttpDeclarative,
                IsActive = true,
                Description = $"Table {index}"
            },
            Columns = [new TableColumn { Id = Guid.NewGuid(), ColumnName = "id", DataType = "int" }]
        };
    }

    private sealed class CountingApprovalRepository(
        IReadOnlyList<ConsentRequest> requesterItems,
        IReadOnlyList<ConsentRequest> approverItems) : IConsentApprovalRepository
    {
        public Task<ConsentRequest> CreateConsentRequestAsync(ConsentRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ConsentRequest?> GetConsentRequestAsync(Guid requestId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ConsentRequest>> GetRequestsByRequesterSidAsync(Sid requesterSid, TenantId tenantId, string? status = null, CancellationToken ct = default) =>
            Task.FromResult(requesterItems);
        public Task<IReadOnlyList<ConsentRequest>> GetPendingRequestsForApproverAsync(Sid approverSid, TenantId tenantId, CancellationToken ct = default) =>
            Task.FromResult(approverItems);
        public Task<ConsentRequest> ApproveConsentRequestStepAsync(Guid requestId, Sid approverSid, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ConsentRequest> RejectConsentRequestAsync(Guid requestId, Sid approverSid, string reason, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ActivateConsentAsync(Guid requestId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteConsentRequestAsync(Guid requestId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class CountingTableMetadataRepository(IReadOnlyList<TableMetadata> tables) : ITableMetadataRepository
    {
        public int QueryTablesCalls { get; private set; }
        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default) =>
            Task.FromResult(tables.FirstOrDefault(t => t.Identifier == table));
        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default) =>
            Task.FromResult(tables);
        public Task<CatalogTablePage> QueryTablesAsync(CatalogTableQuery query, CancellationToken ct = default)
        {
            QueryTablesCalls++;
            return Task.FromResult(new CatalogTablePage(tables.ToList(), null));
        }
        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(TableIdentifier table, IReadOnlyList<string> primaryKey, IReadOnlyList<TableIndexDefinition> indexes, IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class CountingConsentRepository(IReadOnlyList<Consent> consents) : IConsentRepository, IConsentDescriptionRepository
    {
        public int GetAllActiveConsentsCalls { get; private set; }
        public int LoadedDescriptionBatchSize { get; private set; }

        public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, TenantId? tenantId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, IEnumerable<string>? roles = null, DateTimeOffset? atTime = null, CancellationToken ct = default) =>
            Task.FromResult(consents);
        public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, IEnumerable<string>? roles, DateTimeOffset? atTime, TenantId? tenantId, CancellationToken ct = default)
        {
            GetAllActiveConsentsCalls++;
            return Task.FromResult(consents);
        }
        public Task<Consent> CreateConsentAsync(Consent consent, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Consent?> GetConsentByIdAsync(Guid consentId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RevokeConsentAsync(Guid consentId, Sid revokedBySid, string reason, ConsentRevocationAuthority authority, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Consent>> GetExpiringConsentsAsync(DateTimeOffset threshold, int maxResults = 1000, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<IReadOnlyList<Consent>> GetActiveConsentsByConsentRequestIdAsync(Guid consentRequestId, DateTimeOffset atTime, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<bool> RevokeSystemConsentAsync(Guid consentId, Guid consentRequestId, Sid revokedBySid, string reason, CancellationToken ct = default) => Task.FromResult(false);
        public Task<ConsentPage> GetConsentPageAsync(ConsentPageQuery query, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ReplaceConsentsAsync(IReadOnlyList<Guid> revokeConsentIds, IReadOnlyList<Consent> create, Sid actor, string reason, ConsentRevocationAuthority authority, IReadOnlyList<AuditLogEntry>? auditEntries = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> UpdateConsentDescriptionWithAuditAsync(Guid consentId, TenantId tenant, string? description, AuditLogEntry audit, CancellationToken ct = default) => Task.FromResult(false);
        public Task LoadDescriptionsAsync(IReadOnlyCollection<Consent> batch, CancellationToken ct = default)
        {
            LoadedDescriptionBatchSize = batch.Count;
            return Task.CompletedTask;
        }
        public Task ReplaceConsentsAsync(IReadOnlyList<Guid> revokeConsentIds, IReadOnlyList<Consent> create, Sid actor, string reason, ConsentRevocationAuthority authority, IReadOnlyList<AuditLogEntry>? auditEntries, IReadOnlySet<Guid>? prunedConsentIds, string? pruneReason = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ReplaceConsentsAsync(IReadOnlyList<Guid> revokeConsentIds, IReadOnlyList<Consent> create, Sid actor, string reason, ConsentRevocationAuthority authority, IReadOnlyList<AuditLogEntry>? auditEntries, IReadOnlyList<ConsentDescriptionUpdate>? descriptionUpdates, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class CountingClassificationRepository : IClassificationRepository
    {
        public int ColumnClassificationLookups { get; private set; }
        public Task<IReadOnlyList<DataClassification>> GetClassificationsAsync(string? tenantScope, bool includeInactive = false, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DataClassification>>([]);
        public Task<DataClassification?> GetClassificationAsync(string scope, string code, CancellationToken ct = default) => Task.FromResult<DataClassification?>(null);
        public Task<DataClassification> UpsertClassificationAsync(DataClassification classification, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ClassificationMaskRule>> GetClassificationMaskRulesAsync(string? tenantScope, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ClassificationMaskRule>>([]);
        public Task<ClassificationMaskRule?> GetClassificationMaskRuleAsync(string scope, string code, CancellationToken ct = default) => Task.FromResult<ClassificationMaskRule?>(null);
        public Task<ClassificationMaskRule> UpsertClassificationMaskRuleAsync(ClassificationMaskRule rule, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ColumnClassification>> GetColumnClassificationsAsync(string tableColumnId, string? tenantScope = null, CancellationToken ct = default)
        {
            ColumnClassificationLookups++;
            return Task.FromResult<IReadOnlyList<ColumnClassification>>([]);
        }
        public Task AssignColumnClassificationAsync(ColumnClassification assignment, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> RemoveColumnClassificationAsync(string scope, string tableColumnId, string code, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ColumnMaskOverride>> GetColumnMaskOverridesAsync(string tableColumnId, string? tenantScope = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ColumnMaskOverride>>([]);
        public Task<ColumnMaskOverride> UpsertColumnMaskOverrideAsync(ColumnMaskOverride maskOverride, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> DeleteColumnMaskOverrideAsync(string scope, string tableColumnId, string? code, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ConsentClassificationAccess>> GetConsentClassificationAccessAsync(string tenantId, Guid consentId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ConsentClassificationAccess>>([]);
        public Task<IReadOnlyList<ConsentClassificationAccess>> GetConsentClassificationAccessBatchAsync(string tenantId, IReadOnlyList<Guid> consentIds, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ConsentClassificationAccess>>([]);
        public Task<ConsentClassificationAccess> UpsertConsentClassificationAccessAsync(ConsentClassificationAccess entry, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FixedVisibilityService(IReadOnlyList<TableMetadata> tables, CallerScope scope = CallerScope.Tenant) : ICatalogVisibilityService
    {
        public ValueTask<CatalogVisibleSet> GetVisibleAsync(CatalogCaller caller, Autheris.Application.Governance.Contracts.SchemaContractDefinition? contract = null, CancellationToken ct = default) =>
            ValueTask.FromResult(new CatalogVisibleSet(tables, scope, isAdminView: false));
        public ValueTask<CatalogVisibleRow?> FindVisibleAsync(CatalogCaller caller, TableIdentifier table, CancellationToken ct = default) =>
            ValueTask.FromResult<CatalogVisibleRow?>(null);
        public ValueTask<IReadOnlySet<TableIdentifier>> FilterVisibleAsync(CatalogCaller caller, IReadOnlyCollection<TableIdentifier> candidates, CancellationToken ct = default) =>
            ValueTask.FromResult<IReadOnlySet<TableIdentifier>>(new HashSet<TableIdentifier>());
    }

    [Fact]
    public async Task ConsentAndCatalogQueries_BoundOversizedAndInvalidPageArguments()
    {
        var user = AuthenticatedUser();
        var accessor = AccessorFor(user);
        var opts = Options.Create(new GatewayOptions { GraphQL = new GraphQLOptions { MaxResponseRows = 3, MaxAllowedOffset = 10 } });

        var requests = Enumerable.Range(0, 12).Select(i => new ConsentRequest
        {
            Id = Guid.NewGuid(),
            TableId = Guid.NewGuid(),
            TableIdentifier = new TableIdentifier("sales", "dbo", $"t_{i}"),
            RequesterSid = new Sid("S-USER-1"),
            TenantId = TenantA,
            Status = "PENDING",
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(7)
        }).ToList();

        var consents = Enumerable.Range(0, 12).Select(i => new Consent
        {
            Id = Guid.NewGuid(),
            TableIdentifier = new TableIdentifier("sales", "dbo", $"t_{i}"),
            TenantId = TenantA,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid("S-USER-1"),
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(7)
        }).ToList();

        var approvalRepo = new CountingApprovalRepository(requests, requests);
        var consentRepo = new CountingConsentRepository(consents);
        var query = new Query();

        (await query.MyAccessRequestsAsync(first: 1000, after: 1, approvalRepository: approvalRepo, httpContextAccessor: accessor, gatewayOptions: opts)).Count.ShouldBe(3);
        (await query.MyAccessRequestsAsync(first: 0, after: 0, approvalRepository: approvalRepo, httpContextAccessor: accessor, gatewayOptions: opts)).Count.ShouldBe(0);
        (await query.MyActiveConsentsAsync(first: 1000, after: 1, consentRepository: consentRepo, httpContextAccessor: accessor, gatewayOptions: opts)).Count.ShouldBe(3);
        consentRepo.LoadedDescriptionBatchSize.ShouldBe(3);

        await Should.ThrowAsync<GraphQLException>(() => query.MyAccessRequestsAsync(first: -1, approvalRepository: approvalRepo, httpContextAccessor: accessor, gatewayOptions: opts));
        await Should.ThrowAsync<GraphQLException>(() => query.MyActiveConsentsAsync(after: 11, consentRepository: consentRepo, httpContextAccessor: accessor, gatewayOptions: opts));
    }

    [Fact]
    public async Task PendingApprovalsForMe_And_CatalogFallback_EliminateNPlusOneQueries()
    {
        var user = AuthenticatedUser();
        var accessor = AccessorFor(user);
        var tables = Enumerable.Range(0, 10).Select(MakeTable).ToList();
        var tableRepo = new CountingTableMetadataRepository(tables);
        var consentRepo = new CountingConsentRepository([]);
        var approvalRepo = new CountingApprovalRepository([], tables.Select(t => new ConsentRequest
        {
            Id = Guid.NewGuid(),
            TableId = t.Table.Id,
            TableIdentifier = t.Identifier,
            RequesterSid = new Sid("S-REQ-1"),
            RequestedGranteeRef = "S-REQ-1",
            TenantId = TenantA,
            Status = "PENDING",
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(5)
        }).ToList());

        var previewService = new GrantEffectPreviewService(
            tableRepo,
            consentRepo,
            AuditWriterTestServices.Over(NullAuditLogRepository.Instance));

        var query = new Query();
        var approvals = await query.PendingApprovalsForMeAsync(
            first: 10,
            after: 0,
            approvalRepository: approvalRepo,
            httpContextAccessor: accessor,
            effectPreview: previewService);

        approvals.Count.ShouldBe(10);
        approvals.ShouldAllBe(a => a.EffectPreview != null);
        tableRepo.QueryTablesCalls.ShouldBe(1);
        consentRepo.GetAllActiveConsentsCalls.ShouldBe(1);

        var classificationRepo = new CountingClassificationRepository();
        var catalog = await query.GetCatalogAsync(
            first: 2,
            after: 3,
            metadataRepository: tableRepo,
            consentRepository: consentRepo,
            approvalRepository: approvalRepo,
            httpContextAccessor: accessor,
            visibilityService: new FixedVisibilityService(tables),
            classificationRepository: classificationRepo);

        catalog.Count.ShouldBe(2);
        catalog[0].TableName.ShouldBe("tbl_003");
        catalog[1].TableName.ShouldBe("tbl_004");
        classificationRepo.ColumnClassificationLookups.ShouldBe(2);
    }

    [Fact]
    public async Task GetCatalogAsync_SearchDoesNotLeakInaccessibleColumnNames()
    {
        var user = AuthenticatedUser();
        var accessor = AccessorFor(user);
        var employeesId = new TableIdentifier("sales", "dbo", "employees");
        var table = new TableMetadata
        {
            Identifier = employeesId,
            OwnerTenantId = TenantA.Value,
            CatalogView = CatalogView.Granted,
            Table = new Table
            {
                Id = Guid.NewGuid(),
                SourceName = "sales",
                SchemaName = "dbo",
                TableName = "employees",
                DataSourceType = DataSourceType.HttpDeclarative,
                IsActive = true
            },
            Columns =
            [
                new TableColumn { Id = Guid.NewGuid(), ColumnName = "public_id", DataType = "int" },
                new TableColumn { Id = Guid.NewGuid(), ColumnName = "secret_salary", DataType = "decimal" }
            ]
        };

        var consent = new Consent
        {
            Id = Guid.NewGuid(),
            TableIdentifier = employeesId,
            TenantId = TenantA,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid("S-USER-1"),
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(7),
            ColumnRules = [new ConsentColumnRule { ColumnName = "public_id", AccessLevel = ColumnAccessLevel.Clear }]
        };

        var tables = new[] { table };
        var query = new Query();
        var tableRepo = new CountingTableMetadataRepository(tables);
        var consentRepo = new CountingConsentRepository([consent]);
        var approvalRepo = new CountingApprovalRepository([], []);
        var visibility = new FixedVisibilityService(tables, CallerScope.Subject);
        var resolution = new ConsentResolutionService();

        (await query.GetCatalogAsync(
            search: "secret_salary",
            metadataRepository: tableRepo,
            consentRepository: consentRepo,
            approvalRepository: approvalRepo,
            httpContextAccessor: accessor,
            visibilityService: visibility,
            consentResolution: resolution)).ShouldBeEmpty();

        (await query.GetCatalogAsync(
            search: "secret_salary",
            metadataRepository: tableRepo,
            consentRepository: consentRepo,
            approvalRepository: approvalRepo,
            httpContextAccessor: accessor,
            visibilityService: visibility,
            consentResolution: null)).ShouldBeEmpty();

        var allowed = await query.GetCatalogAsync(
            search: "public_id",
            metadataRepository: tableRepo,
            consentRepository: consentRepo,
            approvalRepository: approvalRepo,
            httpContextAccessor: accessor,
            visibilityService: visibility,
            consentResolution: resolution);

        allowed.Count.ShouldBe(1);
        allowed[0].TableName.ShouldBe("employees");
    }
}
