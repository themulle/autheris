namespace Autheris.Tests.Unit.Governance.Catalog;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog;
using Autheris.Application.Catalog.Semantic;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.DataSources.Connectors.Adapters;
using Autheris.Application.Governance.Analysis;
using Autheris.Application.Governance.Paging;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Infrastructure.Connectors;
using Autheris.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class CatalogCallerMigrationBatch1Tests
{
    private sealed class RecordingCatalogRepository(IReadOnlyList<TableMetadata> tables) : ITableMetadataRepository
    {
        public int GetAllCalls { get; private set; }
        public List<IReadOnlyCollection<TableIdentifier>> BatchCalls { get; } = [];
        public List<CatalogTableQuery> QueryCalls { get; } = [];
        public List<TableIdentifier> PointCalls { get; } = [];
        public bool ReturnNullPage { get; init; }
        public bool RepeatCursor { get; init; }

        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default)
        {
            PointCalls.Add(table);
            return Task.FromResult(tables.FirstOrDefault(t => t.Identifier == table));
        }

        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default)
        {
            GetAllCalls++;
            return Task.FromResult(tables);
        }

        public Task<IReadOnlyList<TableMetadata>> GetTablesAsync(IReadOnlyCollection<TableIdentifier> identifiers, CancellationToken ct = default)
        {
            BatchCalls.Add(identifiers.ToArray());
            var wanted = identifiers.ToHashSet();
            return Task.FromResult<IReadOnlyList<TableMetadata>>(tables.Where(t => wanted.Contains(t.Identifier)).ToList());
        }

        public Task<CatalogTablePage> QueryTablesAsync(CatalogTableQuery query, CancellationToken ct = default)
        {
            QueryCalls.Add(query);
            if (ReturnNullPage)
            {
                return Task.FromResult<CatalogTablePage>(null!);
            }

            query.EnsureValid(out var domain, out var schema, out var namePrefix, out var after);
            var limit = query.EffectiveLimit;
            var matched = tables
                .Where(t => query.IncludeInactive || t.Table.IsActive)
                .Where(t => domain is null || string.Equals(t.Identifier.Domain, domain, StringComparison.OrdinalIgnoreCase))
                .Where(t => schema is null || string.Equals(t.Identifier.Schema, schema, StringComparison.OrdinalIgnoreCase))
                .Where(t => namePrefix is null || t.Identifier.TableName.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase))
                .Where(t => after is null || CatalogTableCursor.Compare(t.Identifier, after.Value) > 0)
                .OrderBy(t => t.Identifier.Domain, StringComparer.OrdinalIgnoreCase)
                .ThenBy(t => t.Identifier.Schema, StringComparer.OrdinalIgnoreCase)
                .ThenBy(t => t.Identifier.TableName, StringComparer.OrdinalIgnoreCase)
                .Take(limit + 1)
                .ToList();

            var hasMore = RepeatCursor || matched.Count > limit;
            var items = matched.Count > limit ? matched.Take(limit).ToList() : matched;
            var nextAfter = RepeatCursor
                ? (items.Count > 0 ? CatalogTableCursor.Encode(items[0].Identifier) : query.After ?? CatalogTableCursor.Encode(new TableIdentifier("a", "b", "c")))
                : (matched.Count > limit ? CatalogTableCursor.Encode(items[^1].Identifier) : null);
            return Task.FromResult(new CatalogTablePage(items, nextAfter));
        }

        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(TableIdentifier table, IReadOnlyList<string> primaryKey, IReadOnlyList<TableIndexDefinition> indexes, IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class StubConsentRepository(IReadOnlyList<Consent> consents) : IConsentRepository
    {
        public Task<Consent> CreateConsentAsync(Consent consent, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Consent?> GetConsentByIdAsync(Guid consentId, CancellationToken ct = default) => Task.FromResult<Consent?>(null);
        public Task RevokeConsentAsync(Guid consentId, Sid revokedBySid, string reason, ConsentRevocationAuthority authority, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, CancellationToken ct = default) => Task.FromResult(consents);
        public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, TenantId? tenantId, CancellationToken ct = default) => Task.FromResult(consents);
        public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, IEnumerable<string>? roles = null, DateTimeOffset? atTime = null, CancellationToken ct = default) => Task.FromResult(consents);
        public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, IEnumerable<string>? roles, DateTimeOffset? atTime, TenantId? tenantId, CancellationToken ct = default) => Task.FromResult(consents);
        public Task<IReadOnlyList<Consent>> GetExpiringConsentsAsync(DateTimeOffset threshold, int maxResults = 1000, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<IReadOnlyList<Consent>> GetActiveConsentsByConsentRequestIdAsync(Guid consentRequestId, DateTimeOffset atTime, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<bool> RevokeSystemConsentAsync(Guid consentId, Guid consentRequestId, Sid revokedBySid, string reason, CancellationToken ct = default) => Task.FromResult(false);
        public Task<ConsentPage> GetConsentPageAsync(ConsentPageQuery query, CancellationToken ct = default) => Task.FromResult(new ConsentPage([], null));
        public Task ReplaceConsentsAsync(IReadOnlyList<Guid> revokeConsentIds, IReadOnlyList<Consent> create, Sid actor, string reason, ConsentRevocationAuthority authority, IReadOnlyList<AuditLogEntry>? auditEntries = null, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubExecutor : IDataSourceExecutor
    {
        public DataSourceType SupportedType => DataSourceType.Sql;
        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteAsync(DataSourceExecutionContext context, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>([]);
    }

    private sealed class NoOpMaskingProvider : IColumnMaskingProvider
    {
        public object? MaskValue(string columnName, object? rawValue, MaskingRule rule) => rawValue;
    }

    private static TableMetadata MakeTable(string domain, string schema, string name, DatasetVisibility visibility = DatasetVisibility.Summary) => new()
    {
        Identifier = new TableIdentifier(domain, schema, name),
        Table = new Table { SourceName = domain, SchemaName = schema, TableName = name, DataSourceType = DataSourceType.Sql, IsActive = true, Visibility = visibility },
        Columns = [new TableColumn { ColumnName = "id", DataType = "int" }]
    };

    private static ClaimsPrincipal UserWithSid(string sid = "S-1-5-21-TEST") => new(new ClaimsIdentity(
    [
        new Claim(ClaimTypes.PrimarySid, sid),
        new Claim("tenant_id", TestTenants.Default.Value)
    ], "Bearer"));

    [Fact]
    public async Task PageReader_FailsClosed_OnStalledCursorOrNullPageOrBoundOverflow()
    {
        var t1 = MakeTable("crm", "dbo", "orders");
        var t2 = MakeTable("crm", "dbo", "users");

        await Should.ThrowAsync<InvalidOperationException>(() => CatalogTablePageReader.ReadAllAsync(
            new RecordingCatalogRepository([t1]) { ReturnNullPage = true },
            new CatalogTableQuery(Limit: 10)));

        await Should.ThrowAsync<InvalidOperationException>(() => CatalogTablePageReader.ReadAllAsync(
            new RecordingCatalogRepository([t1, t2]) { RepeatCursor = true },
            new CatalogTableQuery(Limit: 1),
            maxPages: 3));

        await Should.ThrowAsync<InvalidOperationException>(() => CatalogTablePageReader.ReadAllAsync(
            new RecordingCatalogRepository([t1, t2]),
            new CatalogTableQuery(Limit: 1),
            maxPages: 1));
    }

    [Fact]
    public async Task BundledConsentRequestService_UsesBatchLookup_AndFailsClosedForHiddenUnconsentedTables()
    {
        var visible = MakeTable("crm", "dbo", "orders");
        var hidden = MakeTable("crm", "dbo", "secrets", DatasetVisibility.Hidden);
        var repo = new RecordingCatalogRepository([visible, hidden]);
        var service = new BundledConsentRequestService(repo, new StubConsentRepository([]));

        var ok = await service.SubmitBundledRequestAsync(
            UserWithSid(),
            new BundledConsentRequest([new RequestableConsentItem("crm.dbo.orders", null, "Read", "analytics")], "purpose", "c-1"));
        ok.Tickets.Count.ShouldBe(1);
        repo.GetAllCalls.ShouldBe(0);
        repo.BatchCalls.Count.ShouldBe(1);

        await Should.ThrowAsync<KeyNotFoundException>(() => service.SubmitBundledRequestAsync(
            UserWithSid(),
            new BundledConsentRequest([new RequestableConsentItem("crm.dbo.secrets", null, "Read", "analytics")], "purpose", "c-2")));
        repo.GetAllCalls.ShouldBe(0);
    }

    [Fact]
    public async Task SemanticMcpCompiler_ReadsScopedCatalogViaQueryTablesAsync_NeverGetAllTablesAsync()
    {
        var orders = MakeTable("sales", "dbo", "orders");
        var hr = MakeTable("hr", "dbo", "payroll");
        var repo = new RecordingCatalogRepository([orders, hr]);
        var consent = new Consent
        {
            TableIdentifier = orders.Identifier,
            TenantId = TestTenants.Default,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid("S-1-5-21-TEST"),
            Effect = ConsentEffect.Allow,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1)
        };
        var compiler = new SemanticMcpCompiler(repo, NullLogger<SemanticMcpCompiler>.Instance, consentRepo: new StubConsentRepository([consent]));

        var scoped = await compiler.GetSemanticResourcesAsync("sales", UserWithSid());
        scoped.ShouldContain(r => r.Uri == "glossary://sales/orders");
        scoped.ShouldNotContain(r => r.Uri.Contains("payroll", StringComparison.OrdinalIgnoreCase));

        var item = await compiler.ReadSemanticResourceAsync(UserWithSid(), "glossary://sales/orders");
        item.ShouldNotBeNull();
        (await compiler.ReadSemanticResourceAsync(UserWithSid(), "glossary://hr/payroll")).ShouldBeNull();

        repo.GetAllCalls.ShouldBe(0);
        repo.QueryCalls.ShouldAllBe(q => q.DataSource != null);
    }

    [Fact]
    public async Task Connectors_And_SemanticBaseTableResolver_NeverCallGetAllTablesAsync()
    {
        var crmOrders = MakeTable("crm", "public", "orders");
        var hrPayroll = MakeTable("hr", "restricted", "payroll");
        var repo = new RecordingCatalogRepository([crmOrders, hrPayroll]);

        await using var scopeProvider = new ServiceCollection().BuildServiceProvider();
        var scopeFactory = scopeProvider.GetRequiredService<IServiceScopeFactory>();
        await using var sqlConnector = new SqlConnector("conn-1", repo, new NoOpMaskingProvider(), scopeFactory);
        (await sqlConnector.Metadata.ListSchemasAsync()).ShouldBe(["public", "restricted"], ignoreOrder: true);
        (await sqlConnector.Metadata.ListTablesAsync("public")).ShouldBe([crmOrders.Identifier]);

        await using var legacyConnector = new LegacyDataSourceExecutorAdapter(new StubExecutor(), metadataRepository: repo);
        (await legacyConnector.Metadata.ListTablesAsync("restricted")).ShouldBe([hrPayroll.Identifier]);

        var resolver = new SemanticBaseTableResolver(repo);
        var resolved = await resolver.TryResolveAsync(new DbtSemanticModel("orders", "Orders", "dbo.orders", [], [], []));
        resolved.ShouldNotBeNull();
        resolved!.Identifier.ShouldBe(crmOrders.Identifier);

        repo.GetAllCalls.ShouldBe(0);
        repo.QueryCalls.Count.ShouldBeGreaterThanOrEqualTo(4);
    }
}
