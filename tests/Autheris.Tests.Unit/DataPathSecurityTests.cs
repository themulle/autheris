using System.Security.Claims;
using System.Text.Json;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Exceptions;
using Autheris.GraphQL.DynamicTypes;
using Autheris.GraphQL.Types;
using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Persistence;
using HotChocolate;
using HotChocolate.Resolvers;
using HotChocolate.Types;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit;

public class DataPathSecurityTests : IDisposable
{
    private readonly SqliteGovernanceRepository _repository;
    private readonly ConsentResolutionService _resolutionService;
    private readonly ConsentCacheService _cacheService;
    private readonly ColumnMaskingProvider _maskingProvider;
    private readonly GatewayExecutionService _executionService;
    private readonly Query _query;

    public DataPathSecurityTests()
    {
        var epochService = new EpochValidationService();
        _repository = new SqliteGovernanceRepository(epochService);
        _resolutionService = new ConsentResolutionService();
        var memoryCache = new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var eventBus = new Autheris.Infrastructure.Messaging.InProcessChannelEventBus();
        _cacheService = new ConsentCacheService(memoryCache, epochService, eventBus);
        var gatewayOptions = Options.Create(new GatewayOptions
        {
            DataMasking = new DataMaskingOptions { HmacSecretKeyVaultRef = "Test-Key-Vault-Ref-12345" }
        });
        _maskingProvider = new ColumnMaskingProvider(gatewayOptions);
        var devEnv = Substitute.For<Microsoft.Extensions.Hosting.IHostEnvironment>();
        devEnv.EnvironmentName.Returns("Development");
        var sqlExecutor = new SqlDataSourceExecutor(options: gatewayOptions, environment: devEnv);
        _executionService = new GatewayExecutionService(
            _repository,
            _resolutionService,
            _cacheService,
            _maskingProvider,
            new ChunkedQueryExecutor(500),
            gatewayOptions,
            dataSourceExecutors: [sqlExecutor]);
        _query = new Query();
    }

    public void Dispose()
    {
        _repository.Dispose();
    }

    private sealed class IsolatedHttpContextAccessor(HttpContext context) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = context;
    }

    private static IHttpContextAccessor CreateAccessor(Sid userSid, IEnumerable<string>? roles = null, IEnumerable<Sid>? groupSids = null)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, userSid.Value),
            new(ClaimTypes.NameIdentifier, userSid.Value),
            new("objectSid", userSid.Value),
            new(ClaimTypes.Name, $"CORP\\{userSid.Value}")
        };
        if (roles != null)
        {
            foreach (var r in roles)
            {
                claims.Add(new Claim(ClaimTypes.Role, r));
            }
        }
        if (groupSids != null)
        {
            foreach (var g in groupSids)
            {
                claims.Add(new Claim(ClaimTypes.GroupSid, g.Value));
            }
        }

        var identity = new ClaimsIdentity(claims, "TestAuth", ClaimTypes.Name, ClaimTypes.Role);
        var principal = new ClaimsPrincipal(identity);
        var context = new DefaultHttpContext { User = principal };
        return new IsolatedHttpContextAccessor(context);
    }

    [Fact]
    public async Task Catalog_RoleConsents_AreFilteredByCallerRoles()
    {
        // Table finance_table_2 has a Role consent for role 'FinanceManager'
        var table = new TableIdentifier("finance", "dbo", "finance_table_2");
        var meta = await _repository.GetTableMetadataAsync(table);
        meta.ShouldNotBeNull();

        await _repository.CreateConsentAsync(new Consent
        {
            TableId = meta.Table.Id,
            TableIdentifier = table,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.Role,
            RoleName = "FinanceManager",
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(30)
        });

        // User WITHOUT role 'FinanceManager'
        var userWithoutRole = new Sid("S-1-5-21-USER-NO-ROLE");
        var accessorWithoutRole = CreateAccessor(userWithoutRole, roles: new[] { "Auditor" });

        var catalogWithoutRole = await _query.GetCatalogAsync(_repository, accessorWithoutRole);
        catalogWithoutRole.ShouldNotContain(t => t.TableName == "finance_table_2");

        // User WITH role 'FinanceManager'
        var userWithRole = new Sid("S-1-5-21-USER-HAS-ROLE");
        var accessorWithRole = CreateAccessor(userWithRole, roles: new[] { "FinanceManager" });

        var catalogWithRole = await _query.GetCatalogAsync(_repository, accessorWithRole);
        catalogWithRole.ShouldContain(t => t.TableName == "finance_table_2");
    }

    [Fact]
    public async Task Catalog_WhenAllowAuthenticatedCatalogDiscoveryEnabled_AllAuthenticatedUsersSeeAllTables()
    {
        var userWithoutRole = new Sid("S-1-5-21-USER-NO-ROLE");
        var accessorWithoutRole = CreateAccessor(userWithoutRole, roles: new[] { "Auditor" });

        var options = Options.Create(new GatewayOptions
        {
            Catalog = new DataCatalogOptions
            {
                AllowAuthenticatedCatalogDiscovery = true
            }
        });

        var catalog = await _query.GetCatalogAsync(_repository, accessorWithoutRole, options);
        catalog.ShouldContain(t => t.TableName == "finance_table_2");
        catalog.ShouldContain(t => t.TableName == "finance_table_1");
    }

    [Fact]
    public async Task Catalog_WhenOpenSchemaEnabled_AllowsAnonymousAndNonPrivilegedUsers()
    {
        var options = Options.Create(new GatewayOptions
        {
            OpenSchema = true
        });

        // Anonymous accessor
        var anonymousAccessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext()
        };

        var catalog = await _query.GetCatalogAsync(_repository, anonymousAccessor, options);
        catalog.ShouldContain(t => t.TableName == "finance_table_1");
        catalog.ShouldContain(t => t.TableName == "finance_table_2");
    }

    [Fact]
    public async Task Catalog_WhenDomainSpecifiedOrInScope_FiltersToDomain()
    {
        var options = Options.Create(new GatewayOptions { OpenSchema = true });
        var httpContext = new DefaultHttpContext();
        httpContext.Items["DomainScope"] = "finance";
        var accessor = new HttpContextAccessor { HttpContext = httpContext };

        var catalogFromScope = await _query.GetCatalogAsync(domain: null, first: null, after: null, search: null,
            _repository, _repository, accessor, options);
        catalogFromScope.All(t => t.Domain == "finance").ShouldBeTrue();

        var emptyDomainCatalog = await _query.GetCatalogAsync(domain: "nonexistent", first: null, after: null, search: null,
            _repository, _repository, accessor, options);
        emptyDomainCatalog.ShouldBeEmpty();
    }

    [Fact]
    public async Task Catalog_WhenPaginationAndSearchSpecified_PaginatesAndFilters()
    {
        var options = Options.Create(new GatewayOptions { OpenSchema = true });
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };

        var searchResult = await _query.GetCatalogAsync(domain: null, first: 1, after: null, search: "finance_table_1",
            _repository, _repository, accessor, options);
        searchResult.Count.ShouldBe(1);
        searchResult[0].TableName.ShouldBe("finance_table_1");
    }

    [Fact]
    public async Task Catalog_DenyColumns_AreExcludedFromCatalogDto()
    {
        var table = new TableIdentifier("finance", "dbo", "finance_table_1");
        var meta = await _repository.GetTableMetadataAsync(table);
        meta.ShouldNotBeNull();

        var userSid = new Sid("S-1-5-21-USER-COLUMN-DENY");

        // Allow table consent
        await _repository.CreateConsentAsync(new Consent
        {
            TableId = meta.Table.Id,
            TableIdentifier = table,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = userSid,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(30)
        });

        // Hard DENY column 'email'
        var emailCol = meta.Columns.First(c => c.ColumnName == "email");
        await _repository.CreateConsentAsync(new Consent
        {
            TableId = meta.Table.Id,
            TableIdentifier = table,
            Effect = ConsentEffect.Deny,
            GranteeType = GranteeType.User,
            GranteeSid = userSid,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(30),
            ColumnRules = new List<ConsentColumnRule>
            {
                new()
                {
                    TableColumnId = emailCol.Id,
                    ColumnName = "email",
                    AccessLevel = ColumnAccessLevel.Deny
                }
            }
        });

        var accessor = CreateAccessor(userSid);
        var catalog = await _query.GetCatalogAsync(_repository, accessor);

        var tableDto = catalog.FirstOrDefault(t => t.TableName == "finance_table_1");
        tableDto.ShouldNotBeNull();
        tableDto.Columns.ShouldNotContain("email");
        tableDto.Columns.ShouldContain("amount");
    }

    [Fact]
    public async Task ChildRelations_AllColumnsRespectAccessLevelAndMasking()
    {
        var childTableId = new TableIdentifier("finance", "dbo", "finance_items");
        var meta = await _repository.GetTableMetadataAsync(childTableId);
        meta.ShouldNotBeNull();

        var userSid = new Sid("S-1-5-21-USER-CHILD-REL");

        // Allow child table, but DENY ProductName and MASK Price
        var prodCol = meta.Columns.First(c => c.ColumnName == "product_name");
        var priceCol = meta.Columns.First(c => c.ColumnName == "price");

        await _repository.CreateConsentAsync(new Consent
        {
            TableId = meta.Table.Id,
            TableIdentifier = childTableId,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = userSid,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(30),
            ColumnRules = new List<ConsentColumnRule>
            {
                new()
                {
                    TableColumnId = prodCol.Id,
                    ColumnName = "product_name",
                    AccessLevel = ColumnAccessLevel.Deny
                },
                new()
                {
                    TableColumnId = priceCol.Id,
                    ColumnName = "price",
                    AccessLevel = ColumnAccessLevel.Mask
                }
            }
        });

        var accessor = CreateAccessor(userSid);
        var result = await _executionService.LoadInvoiceItemsBatchAsync(accessor.HttpContext?.User, new[] { "INV-1" });

        result.ShouldContainKey("INV-1");
        var items = result["INV-1"];
        items.ShouldNotBeEmpty();

        foreach (var item in items)
        {
            // Denied column must be nullified
            item.ProductName.ShouldBeNull();
            // Masked or denied price must be zeroed/nullified
            item.Price.ShouldBe(0m);
        }
    }

    [Fact]
    public async Task DynamicTableType_WhenContextDataMissing_FailsClosed()
    {
        var table = new TableIdentifier("finance", "dbo", "finance_table_1");
        var meta = new TableMetadata
        {
            Table = new Table { Id = Guid.NewGuid(), SourceName = "finance", SchemaName = "dbo", TableName = "finance_table_1", DisplayName = "Finance 1" },
            Identifier = table,
            Columns = new List<TableColumn>
            {
                new() { Id = Guid.NewGuid(), ColumnName = "secret_col", DataType = "varchar", IsSensitive = true }
            }
        };

        var dynamicType = new DynamicTableType(meta, _maskingProvider);
        var schema = SchemaBuilder.New()
            .AddType(dynamicType)
            .AddQueryType(d => d.Name("Query").Field("dummy").Resolve(_ => "ok"))
            .Create();

        var objType = schema.Types.GetType<IObjectTypeDefinition>("finance_finance_table_1");
        var field = (ObjectField)objType.Fields["secret_col"];

        // Simulate resolver context WITHOUT "ColumnAccess" in ContextData
        var resolverContext = Substitute.For<IResolverContext>();
        resolverContext.Parent<IReadOnlyDictionary<string, object?>>()
            .Returns(new Dictionary<string, object?> { ["secret_col"] = "CONFIDENTIAL_DATA" });
        resolverContext.ContextData.Returns(new Dictionary<string, object?>()); // No ColumnAccess

        var resolvedVal = await field.Resolver!.Invoke(resolverContext);
        // Currently fails-open (returns CONFIDENTIAL_DATA), must fail-closed (secret_col null)
        resolvedVal.ShouldBeNull();
    }

    [Fact]
    public async Task GatewayExecution_WhenUserHasNoSid_ThrowsUnauthorized()
    {
        var table = new TableIdentifier("finance", "dbo", "finance_table_1");

        // Authenticated user with Name claim but NO SID claims
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, "unidentified_user")
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var principal = new ClaimsPrincipal(identity);

        var ex = await Should.ThrowAsync<GatewaySecurityException>(async () =>
        {
            await _executionService.ExecuteTableQueryAsync(principal, table);
        });

        ex.ErrorCode.ShouldBe("UNAUTHORIZED");
    }

    [Fact]
    public async Task GatewayExecution_AuditLog_SerializesSafelyWithSpecialCharacters()
    {
        var table = new TableIdentifier("finance", "dbo", "finance_table_1");
        var userSid = new Sid("S-1-5-21-USER-AUDIT-SAFE");

        // User has NO consent -> will be DENIED with reasons containing special chars
        var accessor = CreateAccessor(userSid);

        var ex = await Should.ThrowAsync<GatewaySecurityException>(async () =>
        {
            await _executionService.ExecuteTableQueryAsync(accessor.HttpContext?.User, table);
        });
        ex.ErrorCode.ShouldBe("FORBIDDEN");

        var auditEntries = await _repository.GetAuditLogEntriesAsync(10);
        var entry = auditEntries.FirstOrDefault(e => e.ActorSid == userSid && e.TargetTable == table.ToString());
        entry.ShouldNotBeNull();

        // DetailsJson must be valid, well-formed JSON
        Should.NotThrow(() =>
        {
            using var doc = JsonDocument.Parse(entry.DetailsJson);
            doc.RootElement.TryGetProperty("is_allowed", out var isAllowedProp).ShouldBeTrue();
            isAllowedProp.GetBoolean().ShouldBeFalse();
        });
    }

    [Fact]
    public async Task GatewayExecution_RowFilter_IsEnforcedOnReturnedData()
    {
        var table = new TableIdentifier("finance", "dbo", "finance_table_1");
        var meta = await _repository.GetTableMetadataAsync(table);
        meta.ShouldNotBeNull();

        var userSid = new Sid("S-1-5-21-USER-ROW-FILTER");

        var idCol = meta.Columns.First(c => c.ColumnName == "id");

        // Consent with row filter: id = 1
        await _repository.CreateConsentAsync(new Consent
        {
            TableId = meta.Table.Id,
            TableIdentifier = table,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = userSid,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(30),
            RowFilters = new List<ConsentRowFilter>
            {
                new()
                {
                    TableColumnId = idCol.Id,
                    ColumnName = "id",
                    Operator = "EQ",
                    ValueType = "int",
                    ValueJson = "1"
                }
            }
        });

        var accessor = CreateAccessor(userSid);
        var (rows, decision) = await _executionService.ExecuteTableQueryAsync(accessor.HttpContext?.User, table, first: 10);

        decision.IsAllowed.ShouldBeTrue();
        decision.CombinedRowFilterSql.ShouldNotBeNullOrWhiteSpace();

        // Rows MUST be filtered: only rows matching id = 1 must be returned!
        rows.ShouldNotBeEmpty();
        rows.ShouldAllBe(r => r.ContainsKey("id") && Convert.ToInt32(r["id"]) == 1);
    }

    [Fact]
    public async Task GatewayExecution_WhenPartialColumnConsent_DefaultsToDenyForUnlistedColumns()
    {
        var table = new TableIdentifier("finance", "dbo", "finance_table_1");
        var meta = await _repository.GetTableMetadataAsync(table);
        meta.ShouldNotBeNull();

        var userSid = new Sid("S-1-5-21-USER-PARTIAL-COLUMNS");
        var idCol = meta.Columns.First(c => c.ColumnName == "id");

        // Consent specifies ONLY id (Clear). All other columns (amount, name, etc.) are unlisted.
        await _repository.CreateConsentAsync(new Consent
        {
            TableId = meta.Table.Id,
            TableIdentifier = table,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = userSid,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(30),
            ColumnRules = new List<ConsentColumnRule>
            {
                new()
                {
                    TableColumnId = idCol.Id,
                    ColumnName = "id",
                    AccessLevel = ColumnAccessLevel.Clear
                }
            }
        });

        var accessor = CreateAccessor(userSid);
        var (rows, decision) = await _executionService.ExecuteTableQueryAsync(accessor.HttpContext?.User, table, first: 5);

        decision.IsAllowed.ShouldBeTrue();
        rows.ShouldNotBeEmpty();

        // Under Zero Trust, unlisted columns must NOT be present in rows!
        foreach (var row in rows)
        {
            row.ContainsKey("id").ShouldBeTrue();
            row.ContainsKey("amount").ShouldBeFalse();
            row.ContainsKey("name").ShouldBeFalse();
            row.ContainsKey("email").ShouldBeFalse();
            row.ContainsKey("created_at").ShouldBeFalse();
        }
    }

    [Fact]
    public async Task GetCatalogAsync_NonAdmin_OnlyReturnsGrantedColumnsUnderZeroTrust()
    {
        var table = new TableIdentifier("finance", "dbo", "finance_table_1");
        var meta = await _repository.GetTableMetadataAsync(table);
        meta.ShouldNotBeNull();

        var userSid = new Sid("S-1-5-21-USER-CATALOG-COLUMNS");
        var idCol = meta.Columns.First(c => c.ColumnName == "id");

        // Consent specifies ONLY id (Clear).
        await _repository.CreateConsentAsync(new Consent
        {
            TableId = meta.Table.Id,
            TableIdentifier = table,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = userSid,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(30),
            ColumnRules = new List<ConsentColumnRule>
            {
                new()
                {
                    TableColumnId = idCol.Id,
                    ColumnName = "id",
                    AccessLevel = ColumnAccessLevel.Clear
                }
            }
        });

        var accessor = CreateAccessor(userSid);
        var catalog = await _query.GetCatalogAsync(_repository, accessor);

        var financeTable = catalog.FirstOrDefault(t => t.TableName == "finance_table_1");
        financeTable.ShouldNotBeNull();

        // Under Zero Trust, non-admins should ONLY see columns that are granted!
        financeTable.Columns.ShouldBe(new[] { "id" });
    }

    [Fact]
    public async Task GatewayExecution_RowFilter_SupportsTupleInWithoutFailingClosed()
    {
        var table = new TableIdentifier("finance", "dbo", "finance_table_1");
        var meta = await _repository.GetTableMetadataAsync(table);
        meta.ShouldNotBeNull();

        var userSid = new Sid("S-1-5-21-USER-TUPLE-IN");

        // Custom decision with Tuple-IN filter: (id, name) IN ((1, 'Sample finance_table_1 Record #1'), (2, 'Sample finance_table_1 Record #2'))
        var decision = TableAccessDecision.Allowed(
            table,
            meta.Columns.ToDictionary(c => c.ColumnName, _ => ColumnAccessLevel.Clear),
            rowFilterSql: "(id, name) IN ((1, 'Sample finance_table_1 Record #1'), (2, 'Sample finance_table_1 Record #2'))",
            hasUnconstrainedColumnAllow: true);

        await _cacheService.SetCachedDecisionAsync(userSid, table, decision, TimeSpan.FromMinutes(5));

        var accessor = CreateAccessor(userSid);
        var (rows, returnedDecision) = await _executionService.ExecuteTableQueryAsync(accessor.HttpContext?.User, table, first: 10);

        returnedDecision.IsAllowed.ShouldBeTrue();
        // Without tuple-IN normalization, DataTable.Select throws syntax error and returns 0 rows
        rows.Count.ShouldBe(2);
        rows.Select(r => Convert.ToInt32(r["id"])).ShouldBe(new[] { 1, 2 });
    }

    [Fact]
    public async Task GatewayExecution_WhenSubqueryCannotBeEvaluatedInMemory_FailsClosed()
    {
        var table = new TableIdentifier("finance", "dbo", "finance_table_1");
        var meta = await _repository.GetTableMetadataAsync(table);
        meta.ShouldNotBeNull();

        var userSid = new Sid("S-1-5-21-USER-EXISTS-FAILCLOSED");

        // Custom decision with EXISTS subquery filter which cannot be evaluated in memory
        var decision = TableAccessDecision.Allowed(
            table,
            meta.Columns.ToDictionary(c => c.ColumnName, _ => ColumnAccessLevel.Clear),
            rowFilterSql: "EXISTS (SELECT 1 FROM some_secret_table WHERE some_secret_table.id = finance_table_1.id)",
            hasUnconstrainedColumnAllow: true);

        await _cacheService.SetCachedDecisionAsync(userSid, table, decision, TimeSpan.FromMinutes(5));

        var accessor = CreateAccessor(userSid);
        var (rows, returnedDecision) = await _executionService.ExecuteTableQueryAsync(accessor.HttpContext?.User, table, first: 10);

        returnedDecision.IsAllowed.ShouldBeTrue();
        // Zero-Trust: If subquery cannot be evaluated in memory against mock data, it MUST fail closed (0 rows), never fail open!
        rows.Count.ShouldBe(0);
    }

    [Fact]
    public async Task LoadInvoiceItemsBatchAsync_WhenAccessLevelIsDenyOrUndefined_FailsClosed()
    {
        var childTableId = new TableIdentifier("finance", "dbo", "finance_items");
        var userSid = new Sid("S-1-5-21-USER-ITEMS-FAILCLOSED");

        // Explicit Deny or missing columns without unconstrained allow
        var decision = TableAccessDecision.Allowed(
            childTableId,
            new Dictionary<string, ColumnAccessLevel>
            {
                ["sensitive_note"] = ColumnAccessLevel.Deny,
                ["product_name"] = ColumnAccessLevel.Deny,
                ["price"] = ColumnAccessLevel.Deny
            },
            hasUnconstrainedColumnAllow: false);

        await _cacheService.SetCachedDecisionAsync(userSid, childTableId, decision, TimeSpan.FromMinutes(5));

        var accessor = CreateAccessor(userSid);
        var result = await _executionService.LoadInvoiceItemsBatchAsync(accessor.HttpContext?.User, new[] { "INV-001" });

        result.ShouldContainKey("INV-001");
        var items = result["INV-001"];
        items.ShouldNotBeEmpty();
        foreach (var item in items)
        {
            item.SensitiveNote.ShouldBeNull();
            item.ProductName.ShouldBeNull();
            item.Price.ShouldBe(0m);
        }
    }

    [Fact]
    public async Task LoadInvoiceItemsBatchAsync_WhenAccessLevelIsClear_DoesNotMaskEvenIfMaskingRuleConfigured()
    {
        var childTableId = new TableIdentifier("finance", "dbo", "finance_items");
        var userSid = new Sid("S-1-5-21-USER-CLEAR-NOTE");

        // Setup mock metadata with masking rule for sensitive_note
        var colId = Guid.NewGuid();
        var maskingRule = new MaskingRule
        {
            Id = Guid.NewGuid(),
            TableColumnId = colId,
            RuleType = "REDACT"
        };

        // Explicit Clear access for sensitive_note
        var decision = TableAccessDecision.Allowed(
            childTableId,
            new Dictionary<string, ColumnAccessLevel>
            {
                ["sensitive_note"] = ColumnAccessLevel.Clear,
                ["product_name"] = ColumnAccessLevel.Clear,
                ["price"] = ColumnAccessLevel.Clear
            },
            hasUnconstrainedColumnAllow: true);

        await _cacheService.SetCachedDecisionAsync(userSid, childTableId, decision, TimeSpan.FromMinutes(5));

        var accessor = CreateAccessor(userSid);
        var result = await _executionService.LoadInvoiceItemsBatchAsync(accessor.HttpContext?.User, new[] { "INV-001" });

        result.ShouldContainKey("INV-001");
        var items = result["INV-001"];
        items.ShouldNotBeEmpty();
        // Since caller has Clear access, SensitiveNote must NOT be REDACTED
        items[0].SensitiveNote.ShouldBe("Confidential spec for item 1 of invoice INV-001");
    }

    [Fact]
    public async Task LoadInvoiceItemsBatchAsync_WhenChildTableHasRowFilter_FiltersChildItemsCorrectly()
    {
        var childTableId = new TableIdentifier("finance", "dbo", "finance_items");
        var userSid = new Sid("S-1-5-21-USER-CHILD-RLS");

        // Allowed with a row filter on price > 2000 (item 1 is 1250, item 2 is 2500)
        var decision = TableAccessDecision.Allowed(
            childTableId,
            new Dictionary<string, ColumnAccessLevel>
            {
                ["sensitive_note"] = ColumnAccessLevel.Clear,
                ["product_name"] = ColumnAccessLevel.Clear,
                ["price"] = ColumnAccessLevel.Clear
            },
            rowFilterSql: "([price] > 2000)",
            hasUnconstrainedColumnAllow: true);

        await _cacheService.SetCachedDecisionAsync(userSid, childTableId, decision, TimeSpan.FromMinutes(5));

        var accessor = CreateAccessor(userSid);
        var result = await _executionService.LoadInvoiceItemsBatchAsync(accessor.HttpContext?.User, new[] { "INV-100" });

        result.ShouldContainKey("INV-100");
        var items = result["INV-100"];
        // Item 1 (price 1250) is excluded by row filter; only item 2 (price 2500) remains!
        items.Count.ShouldBe(1);
        items[0].Id.ShouldBe("INV-100-ITEM-2");
        items[0].Price.ShouldBe(2500.00m);
    }

    [Fact]
    public async Task LoadInvoiceItemsBatchAsync_RowFilterIsEvaluatedOnRawValues_BeforeMasking()
    {
        var childTableId = new TableIdentifier("finance", "dbo", "finance_items");
        var userSid = new Sid("S-1-5-21-USER-CHILD-RLS-MASK");

        // price is MASKED (shown as 0) but the row filter must still see the raw price: only item 2 (2500) matches > 2000.
        var decision = TableAccessDecision.Allowed(
            childTableId,
            new Dictionary<string, ColumnAccessLevel>
            {
                ["sensitive_note"] = ColumnAccessLevel.Clear,
                ["product_name"] = ColumnAccessLevel.Clear,
                ["price"] = ColumnAccessLevel.Mask
            },
            rowFilterSql: "([price] > 2000)",
            hasUnconstrainedColumnAllow: true);

        await _cacheService.SetCachedDecisionAsync(userSid, childTableId, decision, TimeSpan.FromMinutes(5));

        var accessor = CreateAccessor(userSid);
        var result = await _executionService.LoadInvoiceItemsBatchAsync(accessor.HttpContext?.User, new[] { "INV-200" });

        var items = result["INV-200"];
        items.Count.ShouldBe(1);
        items[0].Id.ShouldBe("INV-200-ITEM-2");
        items[0].Price.ShouldBe(0m); // masked
    }
}
