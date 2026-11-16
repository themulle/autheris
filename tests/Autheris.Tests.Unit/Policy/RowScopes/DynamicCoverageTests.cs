using System.Data.Common;
using Autheris.Application.Catalog;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Paging;
using Autheris.Application.Policy;
using Autheris.Application.Policy.RowScopes;
using Autheris.Application.Services;
using Autheris.Application.State;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Infrastructure.Cache;
using Autheris.Tests.Common;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Policy.RowScopes;

[Trait("Category", "Security")]
public sealed class DynamicCoverageTests
{
    private static readonly TenantId Tenant = new("tenant-1");
    private static readonly Sid UserSid = new("S-1-5-21-1001");
    private static readonly Sid OwnerA = new("S-1-5-21-9001");
    private static readonly Sid OwnerB = new("S-1-5-21-9002");

    [Fact]
    public async Task Pattern_grant_covers_object_created_after_approval()
    {
        var epoch = new FakeCatalogVersionProvider(new CatalogVersion(1, 0));
        var grants = new FakeConsentRepo([BuildPatternGrant("pg.sales.fct_*", ConsentWorkflowMode.External)]);
        var owners = new FakeOwnershipRepo();
        var policy = CreatePolicy(grants, epoch, owners, out _);

        var t1 = BuildTable("pg", "sales", "fct_orders", col("id"), col("amount"));
        var d1 = await policy.DecideAsync(BuildQuery(t1), CancellationToken.None);
        d1.IsAllowed.ShouldBeTrue();
        d1.GetEffectiveColumnAccess("amount", t1).ShouldBe(ColumnAccessLevel.Clear);

        epoch.SetVersion(new CatalogVersion(2, 0));
        var t2 = BuildTable("pg", "sales", "fct_invoices", col("id"), col("total"));
        var d2 = await policy.DecideAsync(BuildQuery(t2), CancellationToken.None);
        d2.IsAllowed.ShouldBeTrue();
        d2.GetEffectiveColumnAccess("total", t2).ShouldBe(ColumnAccessLevel.Clear);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("*.*.orders")]
    [InlineData("*.sales.*")]
    [InlineData("pg*.sales.*")]
    [InlineData("pg.sales")]
    [InlineData("pg.sales.orders.extra")]
    [InlineData("pg..orders")]
    [InlineData("pg.sales.ord?")]
    [InlineData("pg.sales.(orders|invoices)")]
    [InlineData("pg.sales.orders[0]")]
    [InlineData("pg.sales.orders ")]
    public void Pattern_must_fix_data_source(string invalidPattern)
    {
        ObjectPatternMatcher.TryParse(invalidPattern, out _, out var error).ShouldBeFalse();
        error.ShouldNotBeNullOrWhiteSpace();

        var consent = BuildPatternGrant(invalidPattern, ConsentWorkflowMode.External);
        Should.Throw<InvalidOperationException>(() => consent.Validate());
    }

    [Fact]
    public async Task New_pii_column_masked_without_explicit_clear()
    {
        var epoch = new FakeCatalogVersionProvider(new CatalogVersion(1, 0));
        var grant = BuildPatternGrant("pg.sales.*", ConsentWorkflowMode.External, ColumnSetMode.AllNonHidden);
        var policy = CreatePolicy(new FakeConsentRepo([grant]), epoch, new FakeOwnershipRepo(), out _);

        var before = BuildTable("pg", "sales", "customers", col("id"), col("display_name"));
        var d1 = await policy.DecideAsync(BuildQuery(before), CancellationToken.None);
        d1.IsAllowed.ShouldBeTrue();
        d1.GetEffectiveColumnAccess("display_name", before).ShouldBe(ColumnAccessLevel.Clear);

        epoch.SetVersion(new CatalogVersion(2, 0));
        var after = BuildTable("pg", "sales", "customers", col("id"), col("display_name"), col("ssn", sensitive: true));
        var d2 = await policy.DecideAsync(BuildQuery(after), CancellationToken.None);
        d2.IsAllowed.ShouldBeTrue();
        d2.GetEffectiveColumnAccess("display_name", after).ShouldBe(ColumnAccessLevel.Clear);
        d2.GetEffectiveColumnAccess("ssn", after).ShouldBe(ColumnAccessLevel.Mask);
    }

    [Fact]
    public async Task Column_classified_pii_later_is_masked()
    {
        var epoch = new FakeCatalogVersionProvider(new CatalogVersion(1, 0));
        var grant = BuildPatternGrant("pg.crm.*", ConsentWorkflowMode.External, ColumnSetMode.AllNonHidden);
        var policy = CreatePolicy(new FakeConsentRepo([grant]), epoch, new FakeOwnershipRepo(), out _);

        var initial = BuildTable("pg", "crm", "leads", col("id"), col("contact_note", sensitive: false));
        var d1 = await policy.DecideAsync(BuildQuery(initial), CancellationToken.None);
        d1.GetEffectiveColumnAccess("contact_note", initial).ShouldBe(ColumnAccessLevel.Clear);

        epoch.SetVersion(new CatalogVersion(1, 1));
        var reclassified = BuildTable("pg", "crm", "leads", col("id"), col("contact_note", sensitive: true));
        var d2 = await policy.DecideAsync(BuildQuery(reclassified), CancellationToken.None);
        d2.GetEffectiveColumnAccess("contact_note", reclassified).ShouldBe(ColumnAccessLevel.Mask);
    }

    [Fact]
    public async Task Hidden_object_never_covered_by_pattern()
    {
        var epoch = new FakeCatalogVersionProvider(new CatalogVersion(1, 0));
        var grant = BuildPatternGrant("pg.sales.*", ConsentWorkflowMode.External, ColumnSetMode.AllNonHidden);
        var policy = CreatePolicy(new FakeConsentRepo([grant]), epoch, new FakeOwnershipRepo(), out _);

        var hiddenTable = BuildTable("pg", "sales", "secret_audit", hiddenTable: true, col("id"), col("payload"));
        var hiddenDecision = await policy.DecideAsync(BuildQuery(hiddenTable), CancellationToken.None);
        hiddenDecision.IsAllowed.ShouldBeFalse();

        var visibleWithHiddenCol = BuildTable(
            "pg", "sales", "orders",
            col("id"),
            col("internal_secret", hidden: true));
        var colDecision = await policy.DecideAsync(BuildQuery(visibleWithHiddenCol), CancellationToken.None);
        colDecision.IsAllowed.ShouldBeTrue();
        colDecision.GetEffectiveColumnAccess("id", visibleWithHiddenCol).ShouldBe(ColumnAccessLevel.Clear);
        colDecision.GetEffectiveColumnAccess("internal_secret", visibleWithHiddenCol).ShouldBe(ColumnAccessLevel.Deny);
    }

    [Fact]
    public async Task Internal_mode_pattern_does_not_cover_object_of_non_approving_owner()
    {
        var epoch = new FakeCatalogVersionProvider(new CatalogVersion(1, 0));
        var grant = BuildPatternGrant(
            "pg.finance.*",
            ConsentWorkflowMode.Internal,
            approvingOwners: [OwnerA.Value]);
        var ownedTable = new TableIdentifier("pg", "finance", "ledger");
        var unapprovedTable = new TableIdentifier("pg", "finance", "payroll");

        var owners = new FakeOwnershipRepo();
        owners.SetOwners(ownedTable, OwnerA);
        owners.SetOwners(unapprovedTable, OwnerB);

        var policy = CreatePolicy(new FakeConsentRepo([grant]), epoch, owners, out _);

        var dOwned = await policy.DecideAsync(
            BuildQuery(BuildTable("pg", "finance", "ledger", col("id"))), CancellationToken.None);
        dOwned.IsAllowed.ShouldBeTrue();

        var dOther = await policy.DecideAsync(
            BuildQuery(BuildTable("pg", "finance", "payroll", col("id"))), CancellationToken.None);
        dOther.IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public async Task External_mode_pattern_covers_all_matching_objects()
    {
        var epoch = new FakeCatalogVersionProvider(new CatalogVersion(1, 0));
        var grant = BuildPatternGrant(
            "pg.finance.*",
            ConsentWorkflowMode.External,
            approvingOwners: [OwnerA.Value]);
        var owners = new FakeOwnershipRepo();
        owners.SetOwners(new TableIdentifier("pg", "finance", "payroll"), OwnerB);

        var policy = CreatePolicy(new FakeConsentRepo([grant]), epoch, owners, out _);
        var table = BuildTable("pg", "finance", "payroll", col("id"));
        var decision = await policy.DecideAsync(BuildQuery(table), CancellationToken.None);

        decision.IsAllowed.ShouldBeTrue();
        decision.GetEffectiveColumnAccess("id", table).ShouldBe(ColumnAccessLevel.Clear);
    }

    [Fact]
    public async Task Catalog_epoch_in_decision_cache_key()
    {
        var epoch = new FakeCatalogVersionProvider(new CatalogVersion(3, 1));
        var grant = BuildPatternGrant("pg.sales.*", ConsentWorkflowMode.External);
        var repo = new FakeConsentRepo([grant]);
        var policy = CreatePolicy(repo, epoch, new FakeOwnershipRepo(), out _);
        var table = BuildTable("pg", "sales", "orders", col("id"));

        var first = await policy.DecideAsync(BuildQuery(table), CancellationToken.None);
        first.IsAllowed.ShouldBeTrue();
        first.CatalogEpoch.ShouldBe(4);
        repo.ReadCount.ShouldBe(1);

        var cached = await policy.DecideAsync(BuildQuery(table), CancellationToken.None);
        cached.IsAllowed.ShouldBeTrue();
        repo.ReadCount.ShouldBe(1);

        epoch.SetVersion(new CatalogVersion(3, 2));
        var afterBump = await policy.DecideAsync(BuildQuery(table), CancellationToken.None);
        afterBump.IsAllowed.ShouldBeTrue();
        afterBump.CatalogEpoch.ShouldBe(5);
        repo.ReadCount.ShouldBe(2);
    }

    [Fact]
    public async Task Explicit_deny_beats_pattern_grant()
    {
        var epoch = new FakeCatalogVersionProvider(new CatalogVersion(1, 0));
        var allowPattern = BuildPatternGrant("pg.sales.*", ConsentWorkflowMode.External);
        var explicitDeny = new Consent
        {
            Id = Guid.NewGuid(),
            TableId = Guid.NewGuid(),
            TableIdentifier = new TableIdentifier("pg", "sales", "orders"),
            Effect = ConsentEffect.Deny,
            TenantId = Tenant,
            GranteeType = GranteeType.User,
            GranteeSid = UserSid,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(30)
        };

        var policy = CreatePolicy(new FakeConsentRepo([allowPattern, explicitDeny]), epoch, new FakeOwnershipRepo(), out _);
        var orders = BuildTable("pg", "sales", "orders", col("id"));
        var invoices = BuildTable("pg", "sales", "invoices", col("id"));

        (await policy.DecideAsync(BuildQuery(orders), CancellationToken.None)).IsAllowed.ShouldBeFalse();
        (await policy.DecideAsync(BuildQuery(invoices), CancellationToken.None)).IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public async Task PatternCoverageMonitor_detects_new_uncovered_internal_object_and_requests_owner_approval()
    {
        var epoch = new FakeCatalogVersionProvider(new CatalogVersion(1, 0));
        var internalGrant = BuildPatternGrant(
            "pg.hr.*",
            ConsentWorkflowMode.Internal,
            approvingOwners: [OwnerA.Value]);
        var consents = new FakeConsentRepo([internalGrant]);
        var catalog = new FakeCatalogRepo(
            BuildTable("pg", "hr", "directory", col("id")),
            BuildTable("pg", "hr", "salaries", col("id")),
            BuildTable("pg", "hr", "hidden_salaries", hiddenTable: true, col("id")));
        var owners = new FakeOwnershipRepo();
        owners.SetOwners(new TableIdentifier("pg", "hr", "directory"), OwnerA);
        owners.SetOwners(new TableIdentifier("pg", "hr", "salaries"), OwnerB);
        owners.SetOwners(new TableIdentifier("pg", "hr", "hidden_salaries"), OwnerB);

        var approvals = new FakeApprovalRepo();
        var audit = new FakeAuditRepo();

        var services = new ServiceCollection();
        services.AddSingleton<IConsentRepository>(consents);
        services.AddSingleton<ITableMetadataRepository>(catalog);
        services.AddSingleton<IDataOwnershipRepository>(owners);
        services.AddSingleton<IConsentApprovalRepository>(approvals);
        services.AddSingleton<IAuditLogRepository>(audit);
        using var provider = services.BuildServiceProvider();

        var monitor = new PatternCoverageMonitor(
            provider.GetRequiredService<IServiceScopeFactory>(),
            epoch,
            new InMemoryClusterStateProvider(),
            NullLogger<PatternCoverageMonitor>.Instance);

        var detected = await monitor.RunOnceAsync(CancellationToken.None);
        detected.ShouldBe(1);
        approvals.CreatedRequests.Count.ShouldBe(1);
        approvals.CreatedRequests[0].TableIdentifier.ShouldBe(new TableIdentifier("pg", "hr", "salaries"));
        audit.Entries.Count.ShouldBe(1);
        audit.Entries[0].EventType.ShouldBe(AuditEventTypes.PatternCoverageChanged);

        (await monitor.RunOnceAsync(CancellationToken.None)).ShouldBe(0);
        approvals.CreatedRequests.Count.ShouldBe(1);
    }

    private static TableAccessPolicy CreatePolicy(
        IConsentRepository consentRepo,
        ICatalogVersionProvider catalogVersionProvider,
        IDataOwnershipRepository ownershipRepo,
        out IConsentCacheService cache)
    {
        var memory = new MemoryCache(new MemoryCacheOptions());
        cache = new ConsentCacheService(
            memory,
            new FakeEpochValidationService(),
            new NoOpEventBus(),
            catalogVersionProvider: catalogVersionProvider);
        return new TableAccessPolicy(
            consentRepo,
            new ConsentResolutionService(),
            cache,
            new GatewayOptions(),
            NullMandatoryRowFilterResolver.Instance,
            catalogVersionProvider: catalogVersionProvider,
            ownershipRepository: ownershipRepo);
    }

    private static TableAccessQuery BuildQuery(TableMetadata metadata) =>
        new(UserSid, Tenant, new HashSet<Sid>(), new HashSet<string>(StringComparer.OrdinalIgnoreCase), metadata);

    private static Consent BuildPatternGrant(
        string pattern,
        ConsentWorkflowMode mode,
        ColumnSetMode columnSetMode = ColumnSetMode.Explicit,
        IReadOnlyList<string>? approvingOwners = null)
    {
        var source = pattern.Split('.')[0];
        return new Consent
        {
            Id = Guid.NewGuid(),
            TableId = Guid.Empty,
            ObjectPattern = pattern,
            DataSource = source,
            ColumnSetMode = columnSetMode,
            WorkflowMode = mode,
            Effect = ConsentEffect.Allow,
            TenantId = Tenant,
            GranteeType = GranteeType.User,
            GranteeSid = UserSid,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(30),
            ApprovingOwners = approvingOwners ?? []
        };
    }

    private static TableMetadata BuildTable(string domain, string schema, string name, params TableColumn[] columns) =>
        BuildTable(domain, schema, name, hiddenTable: false, columns);

    private static TableMetadata BuildTable(string domain, string schema, string name, bool hiddenTable, params TableColumn[] columns) =>
        new()
        {
            Table = new Table
            {
                Id = Guid.NewGuid(),
                SourceName = domain,
                SchemaName = schema,
                TableName = name,
                DiscoveryHidden = hiddenTable
            },
            Identifier = new TableIdentifier(domain, schema, name),
            Columns = columns
        };

    private static TableColumn col(string name, bool sensitive = false, bool hidden = false) =>
        new() { ColumnName = name, IsSensitive = sensitive, DiscoveryHidden = hidden };

    private sealed class FakeCatalogVersionProvider(CatalogVersion initial) : ICatalogVersionProvider
    {
        private CatalogVersion _version = initial;
        public ValueTask<CatalogVersion> GetCatalogVersionAsync(CancellationToken ct = default) => ValueTask.FromResult(_version);
        public Task<string> GetVersionAsync(CancellationToken ct = default) => Task.FromResult($"{_version.StructureVersion}.{_version.ContentVersion}");
        public void SetVersion(CatalogVersion version) => _version = version;
    }

    private sealed class FakeConsentRepo(IReadOnlyList<Consent> consents) : IConsentRepository
    {
        public int ReadCount { get; private set; }

        public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, CancellationToken ct = default)
        {
            ReadCount++;
            return Task.FromResult(consents);
        }

        public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, TenantId? tenantId, CancellationToken ct = default)
        {
            ReadCount++;
            return Task.FromResult(consents);
        }

        public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, IEnumerable<string>? roles = null, DateTimeOffset? atTime = null, CancellationToken ct = default) =>
            Task.FromResult(consents);

        public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, IEnumerable<string>? roles, DateTimeOffset? atTime, TenantId? tenantId, CancellationToken ct = default) =>
            Task.FromResult(consents);

        public Task<IReadOnlyList<Consent>> GetActiveInternalPatternConsentsAsync(DateTimeOffset atTime, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Consent>>(consents.Where(c => c.IsPatternGrant && c.WorkflowMode == ConsentWorkflowMode.Internal && c.Effect == ConsentEffect.Allow).ToList());

        public Task<Consent> CreateConsentAsync(Consent consent, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Consent?> GetConsentByIdAsync(Guid consentId, CancellationToken ct = default) => Task.FromResult<Consent?>(null);
        public Task RevokeConsentAsync(Guid consentId, Sid revokedBySid, string reason, ConsentRevocationAuthority authority, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<Consent>> GetExpiringConsentsAsync(DateTimeOffset threshold, int maxResults = 1000, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<IReadOnlyList<Consent>> GetActiveConsentsByConsentRequestIdAsync(Guid consentRequestId, DateTimeOffset atTime, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<bool> RevokeSystemConsentAsync(Guid consentId, Guid consentRequestId, Sid revokedBySid, string reason, CancellationToken ct = default) => Task.FromResult(false);
        public Task<ConsentPage> GetConsentPageAsync(ConsentPageQuery query, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ReplaceConsentsAsync(IReadOnlyList<Guid> revokeConsentIds, IReadOnlyList<Consent> create, Sid actor, string reason, ConsentRevocationAuthority authority, IReadOnlyList<AuditLogEntry>? auditEntries = null, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeOwnershipRepo : IDataOwnershipRepository
    {
        private readonly Dictionary<TableIdentifier, List<DataOwner>> _owners = [];

        public void SetOwners(TableIdentifier table, params Sid[] sids) =>
            _owners[table] = sids.Select(s => new DataOwner { AdSid = s, IsActive = true }).ToList();

        public Task<DataOwnerDelegation> DelegateDataOwnershipAsync(DataOwnerDelegation delegation, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<DataOwner>> GetDataOwnersForTableAsync(TableIdentifier table, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DataOwner>>(_owners.TryGetValue(table, out var list) ? list : []);
        public Task<bool> IsAuthorizedApproverForTableAsync(TableIdentifier table, Sid approverSid, CancellationToken ct = default) =>
            Task.FromResult(_owners.TryGetValue(table, out var list) && list.Any(o => o.IsActive && o.AdSid == approverSid));
    }

    private sealed class FakeCatalogRepo(params TableMetadata[] tables) : ITableMetadataRepository
    {
        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TableMetadata>>(tables);
        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default) => Task.FromResult(tables.FirstOrDefault(t => t.Identifier == table));
        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default) => Task.FromResult(metadata);
        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(TableIdentifier table, IReadOnlyList<string> primaryKey, IReadOnlyList<TableIndexDefinition> indexes, IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null, CancellationToken ct = default) => Task.FromResult<TableKeyDefinitionChange?>(null);
        public Task<CatalogVersion> FlushCatalogVersionAsync(CancellationToken ct = default) => Task.FromResult(new CatalogVersion(1, 0));
    }

    private sealed class FakeApprovalRepo : IConsentApprovalRepository
    {
        public List<ConsentRequest> CreatedRequests { get; } = [];
        public Task<ConsentRequest> CreateConsentRequestAsync(ConsentRequest request, CancellationToken ct = default) { CreatedRequests.Add(request); return Task.FromResult(request); }
        public Task<ConsentRequest?> GetConsentRequestAsync(Guid requestId, CancellationToken ct = default) => Task.FromResult<ConsentRequest?>(null);
        public Task<IReadOnlyList<ConsentRequest>> GetPendingRequestsForApproverAsync(Sid approverSid, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ConsentRequest>>([]);
        public Task<ConsentRequest> ApproveConsentRequestStepAsync(Guid requestId, Sid approverSid, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ConsentRequest> RejectConsentRequestAsync(Guid requestId, Sid approverSid, string reason, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ActivateConsentAsync(Guid requestId, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteConsentRequestAsync(Guid requestId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeAuditRepo : IAuditLogRepository
    {
        public List<AuditLogEntry> Entries { get; } = [];
        public Task RecordAuditEventAsync(AuditLogEntry entry, CancellationToken ct = default) { Entries.Add(entry); return Task.CompletedTask; }
        public Task RecordAuditEventAsync(AuditLogEntry entry, DbTransaction existingTx, CancellationToken ct = default) { Entries.Add(entry); return Task.CompletedTask; }
        public Task<IReadOnlyList<AuditLogEntry>> GetAuditLogEntriesAsync(int limit = 100, TenantId? tenantId = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AuditLogEntry>>(Entries);
        public Task<IReadOnlyList<AuditLogEntry>> QueryAuditLogsAsync(string? targetTable = null, Sid? actorSid = null, DateTimeOffset? since = null, int limit = 1000, TenantId? tenantId = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AuditLogEntry>>(Entries);
        public Task<bool> VerifyAuditHashChainAsync(CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class NoOpEventBus : IEventBus
    {
        public event Action? ConnectionRestored { add { } remove { } }
        public Task PublishAsync<T>(string channel, T payload, CancellationToken ct = default) => Task.CompletedTask;
        public IDisposable Subscribe<T>(string channel, Func<T, Task> handler) => DummyDisposable.Instance;
        public Task<long> IncrementCounterAsync(string key, CancellationToken ct = default) => Task.FromResult(1L);
        public Task<long> GetCounterAsync(string key, CancellationToken ct = default) => Task.FromResult(0L);
        private sealed class DummyDisposable : IDisposable { public static readonly DummyDisposable Instance = new(); public void Dispose() { } }
    }
}
