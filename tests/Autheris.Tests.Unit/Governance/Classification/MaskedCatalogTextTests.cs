namespace Autheris.Tests.Unit.Governance.Classification;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Catalog.Search;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Policy;
using Autheris.Application.Policy.Classification;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Policy.RowScopes;
using Autheris.Application.Services;
using Autheris.Application.Sql.Masking;
using Autheris.Application.Sql.Policy;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.GraphQL.Types;
using Autheris.Tests.Common;
using Autheris.Tests.Unit.Catalog.Discovery;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class MaskedCatalogTextTests
{
    private static readonly TenantId Tenant = new("tenant-a");
    private static readonly Sid UserSid = new("S-1-5-21-USER-4B");
    private static readonly TableIdentifier CustomersId = new("crm", "dbo", "customers");
    private static readonly DateTimeOffset FixedNow = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    private static TableMetadata BuildTable(
        Guid idColId,
        Guid ssnColId,
        IReadOnlyDictionary<string, IReadOnlyList<ColumnClassification>>? colClasses = null,
        IReadOnlyDictionary<string, IReadOnlyList<ColumnMaskOverride>>? colOverrides = null) => new()
    {
        Identifier = CustomersId,
        OwnerTenantId = Tenant.Value,
        Table = new Table
        {
            Id = Guid.NewGuid(),
            SourceName = "crm",
            SchemaName = "dbo",
            TableName = "customers",
            DisplayName = "Customers",
            Description = "Customer directory",
            IsActive = true
        },
        Columns =
        [
            new TableColumn
            {
                Id = idColId,
                ColumnName = "id",
                DataType = "bigint",
                DisplayName = "Customer Identifier",
                Description = "Surrogate customer primary identifier.",
                LongDescription = "Long form text of the customer identifier."
            },
            new TableColumn
            {
                Id = ssnColId,
                ColumnName = "ssn",
                DataType = "varchar",
                DisplayName = "Taxpayer National Secret Identifier",
                Description = "Secret government tax number issued to taxpayers.",
                LongDescription = "Long form secret taxpayer number text.",
                Texts = new LocalizedTexts(new Dictionary<string, LocalizedTextEntry>
                {
                    ["de"] = new("Geheime Steuernummer", "Vertrauliche nationale Steuerkennung")
                }),
                Meta = new Dictionary<string, string> { ["role"] = "identifier", ["unit"] = "tax-id" }
            }
        ],
        ColumnClassifications = colClasses ?? new Dictionary<string, IReadOnlyList<ColumnClassification>>(StringComparer.OrdinalIgnoreCase)
        {
            ["ssn"] =
            [
                new ColumnClassification
                {
                    Scope = ClassificationScopes.Global,
                    TableColumnId = ssnColId.ToString(),
                    Code = ClassificationScopes.PiiSpecialCode,
                    Source = "OWNER",
                    AssignedAt = FixedNow
                }
            ]
        },
        ColumnMaskOverrides = colOverrides ?? new Dictionary<string, IReadOnlyList<ColumnMaskOverride>>(StringComparer.OrdinalIgnoreCase)
    };

    private static Consent BuildAllowConsent()
    {
        var id = Guid.NewGuid();
        return new Consent
        {
            Id = id,
            TenantId = Tenant,
            TableIdentifier = CustomersId,
            GranteeType = GranteeType.User,
            GranteeSid = UserSid,
            Effect = ConsentEffect.Allow,
            ValidFrom = FixedNow.AddDays(-1),
            ValidTo = FixedNow.AddDays(30)
        };
    }

    private static async Task<(TableAccessPolicy Policy, TestInMemoryClassificationStore ClassStore)> CreatePolicyAsync(
        IReadOnlyList<Consent> consents,
        CatalogTextVisibility piiSpecialText = CatalogTextVisibility.Hidden,
        ClassificationMaskRule[]? extraRules = null,
        bool unmask = false)
    {
        var time = new AdjustableClassificationTimeProvider(FixedNow);
        var classStore = new TestInMemoryClassificationStore(Tenant.Value);
        await classStore.UpsertClassificationMaskRuleAsync(new ClassificationMaskRule
        {
            Scope = ClassificationScopes.Global,
            Code = ClassificationScopes.PiiSpecialCode,
            MaskFunction = MaskFunction.TypeSafeRedact,
            CatalogText = piiSpecialText,
            UpdatedBy = "admin",
            UpdatedAt = FixedNow
        });
        if (extraRules is not null)
        {
            foreach (var r in extraRules)
            {
                await classStore.UpsertClassificationMaskRuleAsync(r);
            }
        }

        var options = new GatewayOptions { Tenancy = TenancyShape.Multi.Options() };
        var maskProvider = new SqlDataMaskingProvider(Options.Create(options), new FakeClassificationSecretProvider(), timeProvider: time);
        var resolver = new ClassificationAccessResolver(maskProvider, classStore, time);
        var rowFactory = new RowPolicyFactory(new PolicyPredicateRenderer());
        var policy = new TableAccessPolicy(
            new TestInMemoryConsentRepository(consents, filterWindow: false),
            new ConsentResolutionService(logger: NullLogger<ConsentResolutionService>.Instance),
            new TestNoOpConsentCache(),
            options,
            new NoOpMandatoryRowFilterResolver(),
            rowPolicyFactory: rowFactory,
            maskPolicyFactory: new ColumnMaskPolicyFactory(maskProvider),
            rowScopeCompiler: new RowScopeCompiler(rowPolicyFactory: rowFactory, classificationResolver: resolver, timeProvider: time),
            timeProvider: time,
            classificationResolver: resolver,
            unmaskResolver: unmask ? new WholeTableUnmask() : null);

        return (policy, classStore);
    }

    private static DiscoveryService CreateDiscovery(
        TableMetadata table,
        TableAccessPolicy policy,
        IClassificationRepository classStore,
        IReadOnlyList<Consent> consents,
        DiscoveryArtifacts? artifacts = null,
        CatalogDiscoveryOptions? discoveryOptions = null)
    {
        var tablesRepo = new FakeDiscoveryTableRepository();
        tablesRepo.Tables.Add(table);
        var consentRepo = new FakeDiscoveryConsentRepository();
        var artifactStore = new FakeDiscoveryArtifactStore();
        if (artifacts is not null) artifactStore.Artifacts[table.Table.Id] = artifacts;

        var pdp = new FakeAccessDecisionPoint(req =>
            policy.DecideAsync(
                new TableAccessQuery(UserSid, Tenant, new HashSet<Sid>(), new HashSet<string>(), req.Object, Claims: req.Subject.Principal?.Claims ?? []),
                CancellationToken.None).GetAwaiter().GetResult());

        return new DiscoveryService(
            tablesRepo,
            pdp,
            consentRepo,
            new FakeDiscoveryApprovalRepository(),
            new FakeDiscoveryOwnershipRepository(),
            artifactStore,
            new FakeAuditWriter(),
            Options.Create(new GatewayOptions { Tenancy = TenancyShape.Multi.Options() }),
            Options.Create(discoveryOptions ?? new CatalogDiscoveryOptions()),
            classificationRepository: classStore);
    }

    [Theory]
    [InlineData("mcp-full")]
    [InlineData("mcp-brief")]
    [InlineData("rest")]
    [InlineData("graphql")]
    public async Task Hidden_text_omitted_on_every_catalog_path(string channel)
    {
        var idCol = Guid.NewGuid();
        var ssnCol = Guid.NewGuid();
        var table = BuildTable(idCol, ssnCol);
        var consents = new[] { BuildAllowConsent() };
        var (policy, classStore) = await CreatePolicyAsync(consents, CatalogTextVisibility.Hidden);
        var statsJson = """{"schemaVersion":1,"columns":{"ssn":{"minValue":"100","maxValue":"999","nullRatio":0.05,"approxDistinctCount":50,"topValues":["111","222"]},"id":{"approxDistinctCount":500}}}""";
        var artifacts = new DiscoveryArtifacts(table.Table.Id, Tenant.Value, null, statsJson, "abc", "owner", FixedNow);
        var discovery = CreateDiscovery(table, policy, classStore, consents, artifacts);
        var principal = DiscoveryTestBuilder.User(UserSid.Value, Tenant.Value);
        var caller = DiscoveryTestBuilder.Caller(principal, Tenant);

        var obj = await discovery.GetAsync(CustomersId, caller);
        obj.ShouldNotBeNull();

        if (channel is "mcp-full" or "mcp-brief")
        {
            var mcp = new McpDatasetCatalog(
                new FakeDiscoveryTableRepository { Tables = { table } },
                new TestInMemoryConsentRepository(consents, filterWindow: false),
                new FakeMcpGatewayExecution(),
                new FakeMcpAuditRepo(),
                options: Options.Create(new GatewayOptions { Tenancy = TenancyShape.Multi.Options(), Mcp = new McpOptions { Enabled = true } }),
                discoveryService: discovery);
            var desc = await mcp.DescribeDatasetAsync(principal, "crm.dbo.customers", language: null, brief: channel == "mcp-brief");
            var ssn = desc.Columns.Single(c => c.Name == "ssn");
            ssn.Type.ShouldBe("varchar");
            ssn.DisplayName.ShouldBeNull();
            ssn.Description.ShouldBeNull();
            ssn.Semantics.ShouldBeNull();
            ssn.ValueProfile.ShouldBeNull();

            var idColOut = desc.Columns.Single(c => c.Name == "id");
            if (channel == "mcp-brief")
            {
                // The brief shape is column signatures only: no column text on any column, hidden or not.
                idColOut.Description.ShouldBeNull();
                desc.Columns.ShouldAllBe(c => c.Description == null && c.DisplayName == null);
            }
            else
            {
                idColOut.Description.ShouldBe("Surrogate customer primary identifier.");
            }
        }
        else if (channel == "rest")
        {
            var restDetail = CatalogDiscoveryProjection.ToDatasetDetail(obj, language: null);
            var ssn = restDetail.Columns.Single(c => c.Name == "ssn");
            ssn.Name.ShouldBe("ssn");
            ssn.Type.ShouldBe("varchar");
            ssn.DisplayName.ShouldBeNull();
            ssn.Description.ShouldBeNull();
            restDetail.Columns.Single(c => c.Name == "id").Description.ShouldBe("Surrogate customer primary identifier.");
        }
        else
        {
            var gql = Query.ToTableMetadataDto(obj);
            var ssn = gql.Columns.Single(c => c.Name == "ssn");
            ssn.Name.ShouldBe("ssn");
            ssn.DataType.ShouldBe("varchar");
            ssn.DisplayName.ShouldBeNull();
            ssn.Description.ShouldBeNull();
            ssn.Statistics.ShouldBeNull();
            gql.Columns.Single(c => c.Name == "id").Description.ShouldBe("Surrogate customer primary identifier.");
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task DescribeDataset_HiddenMaskedColumn_OmitsLongDescription()
    {
        var table = BuildTable(Guid.NewGuid(), Guid.NewGuid());
        var consents = new[] { BuildAllowConsent() };
        var (policy, classStore) = await CreatePolicyAsync(consents, CatalogTextVisibility.Hidden);
        var artifacts = new DiscoveryArtifacts(table.Table.Id, Tenant.Value, null, null, "abc", "owner", FixedNow);
        var discovery = CreateDiscovery(table, policy, classStore, consents, artifacts);
        var principal = DiscoveryTestBuilder.User(UserSid.Value, Tenant.Value);

        var mcp = new McpDatasetCatalog(
            new FakeDiscoveryTableRepository { Tables = { table } },
            new TestInMemoryConsentRepository(consents, filterWindow: false),
            new FakeMcpGatewayExecution(),
            new FakeMcpAuditRepo(),
            options: Options.Create(new GatewayOptions { Tenancy = TenancyShape.Multi.Options(), Mcp = new McpOptions { Enabled = true } }),
            discoveryService: discovery);
        var desc = await mcp.DescribeDatasetAsync(principal, "crm.dbo.customers", language: null, brief: false);

        desc.Columns.Single(c => c.Name == "ssn").LongDescription.ShouldBeNull();
        desc.Columns.Single(c => c.Name == "id").LongDescription.ShouldBe("Long form text of the customer identifier.");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task GetTableSchema_HiddenMaskedColumn_OmitsDescriptionAndLongDescription()
    {
        var table = BuildTable(Guid.NewGuid(), Guid.NewGuid());
        var consents = new[] { BuildAllowConsent() };
        var (policy, classStore) = await CreatePolicyAsync(consents, CatalogTextVisibility.Hidden);
        var artifacts = new DiscoveryArtifacts(table.Table.Id, Tenant.Value, null, null, "abc", "owner", FixedNow);
        var discovery = CreateDiscovery(table, policy, classStore, consents, artifacts);
        var caller = DiscoveryTestBuilder.Caller(DiscoveryTestBuilder.User(UserSid.Value, Tenant.Value), Tenant);

        var obj = await discovery.GetAsync(CustomersId, caller);
        obj.ShouldNotBeNull();
        // get_table_schema reads its columns from this projected detail row.
        var ssnDiscovery = obj.Columns.Single(c => c.Name == "ssn");
        ssnDiscovery.LongDescription.ShouldBeNull();
        obj.HiddenTextColumns.ShouldContain("ssn");
        var detail = CatalogDiscoveryProjection.ToDatasetDetail(obj, language: null);
        var ssn = detail.Columns.Single(c => c.Name == "ssn");
        ssn.Description.ShouldBeNull();
        ssn.DisplayName.ShouldBeNull();
        detail.Columns.Single(c => c.Name == "id").Description.ShouldNotBeNull();
    }

    private static async Task<TestInMemoryClassificationStore> HiddenRuleStoreAsync()
    {
        var classStore = new TestInMemoryClassificationStore(Tenant.Value);
        await classStore.UpsertClassificationMaskRuleAsync(new ClassificationMaskRule
        {
            Scope = ClassificationScopes.Global,
            Code = ClassificationScopes.PiiSpecialCode,
            MaskFunction = MaskFunction.TypeSafeRedact,
            CatalogText = CatalogTextVisibility.Hidden,
            UpdatedBy = "admin",
            UpdatedAt = FixedNow
        });
        return classStore;
    }

    [Fact]
    public async Task Search_TermOnlyInHiddenColumnDescription_DropsHit()
    {
        var table = BuildTable(Guid.NewGuid(), Guid.NewGuid());
        var hidden = await ClassificationAccessResolver.ResolveIndexingHiddenTextColumnsAsync(table, await HiddenRuleStoreAsync());
        hidden.ShouldContain("ssn");

        // The caller sees the table without a third column, so the restricted-table text match applies.
        var full = table with { Columns = [.. table.Columns, new TableColumn { ColumnName = "denied_col", DataType = "int" }] };
        var all = new Dictionary<TableIdentifier, TableMetadata> { [CustomersId] = full };
        var permitted = new Dictionary<TableIdentifier, TableMetadata> { [CustomersId] = table };
        var hit = new CatalogSearchHit(CustomersId, "Customers", "Customer directory", "crm", "NORMAL", 0.9, 5.0, null, ["taxpayers"], [], [], null);

        CatalogDiscoveryService.ShapeHit(hit, permitted, all, ["taxpayers"]).ShouldNotBeNull();
        var hiddenText = new Dictionary<TableIdentifier, IReadOnlySet<string>> { [CustomersId] = hidden };
        CatalogDiscoveryService.ShapeHit(hit, permitted, all, ["taxpayers"], hiddenText: hiddenText).ShouldBeNull();
    }

    [Fact]
    public async Task GraphQlCatalogList_RuleLevelHidden_OmitsColumnText()
    {
        var table = BuildTable(Guid.NewGuid(), Guid.NewGuid());
        var hidden = await ClassificationAccessResolver.ResolveIndexingHiddenTextColumnsAsync(table, await HiddenRuleStoreAsync());
        var hiddenText = new Dictionary<TableIdentifier, IReadOnlySet<string>> { [CustomersId] = hidden };

        var dto = Query.ToCatalogDto(table, isAdminView: true, status: "GRANTED", language: null, hiddenText);

        dto.ShouldNotBeNull();
        var ssn = dto.Columns.Single(c => c.Name == "ssn");
        ssn.DataType.ShouldBe("varchar");
        ssn.DisplayName.ShouldBeNull();
        ssn.Description.ShouldBeNull();
        dto.Columns.Single(c => c.Name == "id").Description.ShouldBe("Surrogate customer primary identifier.");
    }

    [Fact]
    public async Task GetDatasetDetail_RepositoryHiddenRule_OmitsDisplayNameDescriptionLongDescription()
    {
        var table = BuildTable(Guid.NewGuid(), Guid.NewGuid());
        var consents = new[] { BuildAllowConsent() };
        var classStore = await HiddenRuleStoreAsync();
        var tables = new FakeDiscoveryTableRepository { Tables = { table } };
        var consentRepo = new TestInMemoryConsentRepository(consents, filterWindow: false);
        var options = new GatewayOptions { Tenancy = TenancyShape.Multi.Options() };
        var service = new CatalogDiscoveryService(
            tables,
            new FakeAuditWriter(),
            Options.Create(options),
            Autheris.Tests.Unit.TestSupport.CatalogVisibilityTestFactory.Create(tables, consentRepo, options),
            consentRepository: consentRepo,
            classificationRepository: classStore);
        var principal = DiscoveryTestBuilder.User(UserSid.Value, Tenant.Value);

        var detail = await service.GetDatasetDetailAsync(CustomersId, new RequestContext(Tenant, UserSid, User: principal));

        detail.ShouldNotBeNull();
        var ssn = detail.Columns.Single(c => c.Name == "ssn");
        ssn.Type.ShouldBe("varchar");
        ssn.DisplayName.ShouldBeNull();
        ssn.Description.ShouldBeNull();
        detail.Columns.Single(c => c.Name == "id").Description.ShouldBe("Surrogate customer primary identifier.");
    }

    [Fact]
    public async Task Examples_selecting_hidden_text_column_dropped()
    {
        var table = BuildTable(Guid.NewGuid(), Guid.NewGuid());
        var consents = new[] { BuildAllowConsent() };
        var (policy, classStore) = await CreatePolicyAsync(consents, CatalogTextVisibility.Hidden);
        var discovery = CreateDiscovery(table, policy, classStore, consents);
        var principal = DiscoveryTestBuilder.User(UserSid.Value, Tenant.Value);

        var opts = Options.Create(new GatewayOptions { Tenancy = TenancyShape.Multi.Options(), Mcp = new McpOptions { Enabled = true } });
        var golden = new GoldenQueryService(opts, NullLogger<GoldenQueryService>.Instance);
        golden.RegisterGoldenQuery(new GoldenQuery("q-ssn", "crm", "customers", "Lookup SSN", "Selects SSN", "SELECT id, ssn FROM crm.dbo.customers", null, [], FixedNow));
        golden.RegisterGoldenQuery(new GoldenQuery("q-safe", "crm", "customers", "List IDs", "Selects ID only", "SELECT id FROM crm.dbo.customers", null, [], FixedNow));

        var mcp = new McpDatasetCatalog(
            new FakeDiscoveryTableRepository { Tables = { table } },
            new TestInMemoryConsentRepository(consents, filterWindow: false),
            new FakeMcpGatewayExecution(),
            new FakeMcpAuditRepo(),
            goldenQueryService: golden,
            options: opts,
            graphQlMap: new FakeGraphQlMap(CustomersId),
            discoveryService: discovery);

        var desc = await mcp.DescribeDatasetAsync(principal, "crm.dbo.customers", language: null, brief: false);
        desc.Examples.Select(e => e.Title).ShouldBe(["List IDs"]);
        desc.GraphQl.ShouldNotBeNull();
        desc.GraphQl.ExampleQuery.ShouldBeNull();
    }

    [Fact]
    public async Task Hidden_wins_over_visible_across_classifications()
    {
        var idCol = Guid.NewGuid();
        var ssnCol = Guid.NewGuid();
        var multiClasses = new Dictionary<string, IReadOnlyList<ColumnClassification>>(StringComparer.OrdinalIgnoreCase)
        {
            ["ssn"] =
            [
                new ColumnClassification { Scope = ClassificationScopes.Global, TableColumnId = ssnCol.ToString(), Code = ClassificationScopes.PiiCode, Source = "OWNER", AssignedAt = FixedNow },
                new ColumnClassification { Scope = ClassificationScopes.Global, TableColumnId = ssnCol.ToString(), Code = ClassificationScopes.PiiSpecialCode, Source = "OWNER", AssignedAt = FixedNow }
            ]
        };
        var table = BuildTable(idCol, ssnCol, colClasses: multiClasses);
        var grantVisible = BuildAllowConsent();
        var grantHidden = BuildAllowConsent();
        var consents = new[] { grantVisible, grantHidden };

        var (policy, classStore) = await CreatePolicyAsync(
            consents,
            piiSpecialText: CatalogTextVisibility.Hidden,
            extraRules:
            [
                new ClassificationMaskRule
                {
                    Scope = ClassificationScopes.Global,
                    Code = ClassificationScopes.PiiCode,
                    MaskFunction = MaskFunction.Hash,
                    CatalogText = CatalogTextVisibility.Visible,
                    UpdatedBy = "admin",
                    UpdatedAt = FixedNow
                }
            ]);

        var decision = await policy.DecideAsync(
            new TableAccessQuery(UserSid, Tenant, new HashSet<Sid>(), new HashSet<string>(), table, Claims: []),
            CancellationToken.None);
        decision.IsAllowed.ShouldBeTrue();
        decision.IsCatalogTextHidden("ssn").ShouldBeTrue();

        var discovery = CreateDiscovery(table, policy, classStore, consents);
        var obj = await discovery.GetAsync(CustomersId, DiscoveryTestBuilder.Caller(DiscoveryTestBuilder.User(UserSid.Value, Tenant.Value), Tenant));
        obj.ShouldNotBeNull();
        obj.Columns.Single(c => c.Name == "ssn").Description.ShouldBeNull();
    }

    [Fact]
    public async Task Clear_caller_sees_text()
    {
        var table = BuildTable(Guid.NewGuid(), Guid.NewGuid());
        var consents = new[] { BuildAllowConsent() };
        var (policy, classStore) = await CreatePolicyAsync(consents, CatalogTextVisibility.Hidden, unmask: true);
        var discovery = CreateDiscovery(table, policy, classStore, consents);
        var principal = DiscoveryTestBuilder.User(UserSid.Value, Tenant.Value);

        var obj = await discovery.GetAsync(CustomersId, DiscoveryTestBuilder.Caller(principal, Tenant));
        obj.ShouldNotBeNull();
        var ssn = obj.Columns.Single(c => c.Name == "ssn");
        ssn.State.ShouldBe(DiscoveryColumnState.Clear);
        ssn.DisplayName.ShouldBe("Taxpayer National Secret Identifier");
        ssn.Description.ShouldBe("Secret government tax number issued to taxpayers.");
    }

    [Fact]
    public async Task Default_visible_keeps_today_output()
    {
        var table = BuildTable(Guid.NewGuid(), Guid.NewGuid());
        var consents = new[] { BuildAllowConsent() };
        var (policy, classStore) = await CreatePolicyAsync(consents, CatalogTextVisibility.Visible);
        var discovery = CreateDiscovery(table, policy, classStore, consents);
        var principal = DiscoveryTestBuilder.User(UserSid.Value, Tenant.Value);

        var obj = await discovery.GetAsync(CustomersId, DiscoveryTestBuilder.Caller(principal, Tenant));
        obj.ShouldNotBeNull();
        var ssn = obj.Columns.Single(c => c.Name == "ssn");
        ssn.State.ShouldBe(DiscoveryColumnState.Masked);
        ssn.DisplayName.ShouldBe("Taxpayer National Secret Identifier");
        ssn.Description.ShouldBe("Secret government tax number issued to taxpayers.");
    }

    [Fact]
    public async Task Search_does_not_match_hidden_column_text()
    {
        var classStore = new TestInMemoryClassificationStore(Tenant.Value);
        await classStore.UpsertClassificationMaskRuleAsync(new ClassificationMaskRule
        {
            Scope = ClassificationScopes.Global,
            Code = ClassificationScopes.PiiSpecialCode,
            MaskFunction = MaskFunction.TypeSafeRedact,
            CatalogText = CatalogTextVisibility.Hidden,
            UpdatedBy = "admin",
            UpdatedAt = FixedNow
        });

        var table = BuildTable(Guid.NewGuid(), Guid.NewGuid());
        var engine = new CatalogSearchEngine(
            new FakeEmbeddingGenerator(),
            Options.Create(new CatalogSearchOptions()),
            NullLogger<CatalogSearchEngine>.Instance,
            new AdjustableClassificationTimeProvider(FixedNow),
            classificationRepository: classStore);

        await engine.RebuildIndexAsync([table]);

        var hiddenTermHits = await engine.SearchAsync(
            new CatalogSearchQuery("taxpayers", Limit: 10, Mode: CatalogSearchMode.KeywordBm25),
            _ => true);
        hiddenTermHits.ShouldBeEmpty();

        var germanHiddenHits = await engine.SearchAsync(
            new CatalogSearchQuery("Steuernummer", Limit: 10, Mode: CatalogSearchMode.KeywordBm25),
            _ => true);
        germanHiddenHits.ShouldBeEmpty();

        var colNameHits = await engine.SearchAsync(
            new CatalogSearchQuery("ssn", Limit: 10, Mode: CatalogSearchMode.KeywordBm25),
            _ => true);
        colNameHits.Count.ShouldBe(1);
        colNameHits[0].RelevantColumns.ShouldContain("ssn");

        var visibleTermHits = await engine.SearchAsync(
            new CatalogSearchQuery("Surrogate", Limit: 10, Mode: CatalogSearchMode.KeywordBm25),
            _ => true);
        visibleTermHits.Count.ShouldBe(1);
        visibleTermHits[0].RelevantColumns.ShouldNotContain("ssn");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RestrictedScope_HideMaskedColumnCatalogTextOption_ControlsCatalogTextAndExamples_AcrossMcpRestAndGraphQl(bool hideOptionEnabled)
    {
        var table = BuildTable(Guid.NewGuid(), Guid.NewGuid());
        var consents = new[] { BuildAllowConsent() };
        var (policy, classStore) = await CreatePolicyAsync(consents, CatalogTextVisibility.Visible, unmask: true);
        var discovery = CreateDiscovery(
            table,
            policy,
            classStore,
            consents,
            discoveryOptions: new CatalogDiscoveryOptions
            {
                Discovery = new CatalogDiscoverySettings
                {
                    HideMaskedColumnCatalogTextForRestrictedScopes = hideOptionEnabled
                }
            });

        var restrictedUser = DiscoveryTestBuilder.User(UserSid.Value, Tenant.Value, scopes: "Agent.Read");
        var restrictedCaller = DiscoveryTestBuilder.Caller(restrictedUser, Tenant);
        var restrictedObj = await discovery.GetAsync(CustomersId, restrictedCaller);
        restrictedObj.ShouldNotBeNull();

        // 1. REST catalog
        var restDetail = CatalogDiscoveryProjection.ToDatasetDetail(restrictedObj!, language: null);
        var ssnRest = restDetail.Columns.Single(c => c.Name == "ssn");
        restDetail.Columns.Single(c => c.Name == "id").Description.ShouldBe("Surrogate customer primary identifier.");
        if (hideOptionEnabled)
        {
            ssnRest.DisplayName.ShouldBeNull();
            ssnRest.Description.ShouldBeNull();
        }
        else
        {
            ssnRest.DisplayName.ShouldBe("Taxpayer National Secret Identifier");
            ssnRest.Description.ShouldBe("Secret government tax number issued to taxpayers.");
        }

        // 2. GraphQL catalog
        var gql = Query.ToTableMetadataDto(restrictedObj);
        var ssnGql = gql.Columns.Single(c => c.Name == "ssn");
        gql.Columns.Single(c => c.Name == "id").Description.ShouldBe("Surrogate customer primary identifier.");
        if (hideOptionEnabled)
        {
            ssnGql.DisplayName.ShouldBeNull();
            ssnGql.Description.ShouldBeNull();
        }
        else
        {
            ssnGql.DisplayName.ShouldBe("Taxpayer National Secret Identifier");
            ssnGql.Description.ShouldBe("Secret government tax number issued to taxpayers.");
        }

        // 3. MCP describe_dataset (full & brief), list_datasets, and examples
        var opts = Options.Create(new GatewayOptions { Tenancy = TenancyShape.Multi.Options(), Mcp = new McpOptions { Enabled = true } });
        var golden = new GoldenQueryService(opts, NullLogger<GoldenQueryService>.Instance);
        golden.RegisterGoldenQuery(new GoldenQuery("q-ssn", "crm", "customers", "Lookup SSN", "Selects SSN", "SELECT id, ssn FROM crm.dbo.customers", null, [], FixedNow));
        golden.RegisterGoldenQuery(new GoldenQuery("q-safe", "crm", "customers", "List IDs", "Selects ID only", "SELECT id FROM crm.dbo.customers", null, [], FixedNow));

        var mcp = new McpDatasetCatalog(
            new FakeDiscoveryTableRepository { Tables = { table } },
            new TestInMemoryConsentRepository(consents, filterWindow: false),
            new FakeMcpGatewayExecution(),
            new FakeMcpAuditRepo(),
            goldenQueryService: golden,
            options: opts,
            graphQlMap: new FakeGraphQlMap(CustomersId),
            discoveryService: discovery);

        var descFull = await mcp.DescribeDatasetAsync(restrictedUser, "crm.dbo.customers", language: null, brief: false);
        var ssnMcp = descFull.Columns.Single(c => c.Name == "ssn");
        descFull.Columns.Single(c => c.Name == "id").Description.ShouldBe("Surrogate customer primary identifier.");
        if (hideOptionEnabled)
        {
            ssnMcp.DisplayName.ShouldBeNull();
            ssnMcp.Description.ShouldBeNull();
            ssnMcp.LongDescription.ShouldBeNull();
            descFull.Examples.Select(e => e.Title).ShouldBe(["List IDs"]);
        }
        else
        {
            ssnMcp.DisplayName.ShouldBe("Taxpayer National Secret Identifier");
            ssnMcp.Description.ShouldBe("Secret government tax number issued to taxpayers.");
            descFull.Examples.Select(e => e.Title).ShouldContain("Lookup SSN");
        }

        var descBrief = await mcp.DescribeDatasetAsync(restrictedUser, "crm.dbo.customers", language: null, brief: true);
        descBrief.Columns.ShouldAllBe(c => c.Description == null && c.DisplayName == null);

        var listMatch = await mcp.ListDatasetsAsync(restrictedUser, search: "Taxpayer", domain: null, offset: null, limit: null, tag: null, language: null, overview: null, format: null, accessStatus: null);
        if (hideOptionEnabled)
        {
            listMatch.Datasets.ShouldBeEmpty();
        }
        else
        {
            listMatch.Datasets.Count.ShouldBe(1);
        }

        // 4. Unrestricted caller always sees catalog text even when HideMaskedColumnCatalogTextForRestrictedScopes is true
        var unrestrictedUser = DiscoveryTestBuilder.User(UserSid.Value, Tenant.Value);
        var unrestrictedObj = await discovery.GetAsync(CustomersId, DiscoveryTestBuilder.Caller(unrestrictedUser, Tenant));
        unrestrictedObj!.Columns.Single(c => c.Name == "ssn").Description.ShouldBe("Secret government tax number issued to taxpayers.");
    }

    [Fact]
    public async Task RestrictedScope_WithRuleLevelHiddenCatalogText_OmitsTextEvenWhenBaseConsentIsClear()
    {
        var table = BuildTable(Guid.NewGuid(), Guid.NewGuid());
        var consents = new[] { BuildAllowConsent() };
        var (policy, classStore) = await CreatePolicyAsync(consents, CatalogTextVisibility.Hidden, unmask: true);
        var discovery = CreateDiscovery(table, policy, classStore, consents);

        var restrictedUser = DiscoveryTestBuilder.User(UserSid.Value, Tenant.Value, scopes: "Agent.Read");
        var obj = await discovery.GetAsync(CustomersId, DiscoveryTestBuilder.Caller(restrictedUser, Tenant));
        obj.ShouldNotBeNull();

        var ssn = obj!.Columns.Single(c => c.Name == "ssn");
        ssn.State.ShouldBe(DiscoveryColumnState.Masked);
        ssn.DisplayName.ShouldBeNull();
        ssn.Description.ShouldBeNull();
        ssn.LongDescription.ShouldBeNull();
        obj.HiddenTextColumns.ShouldContain("ssn");
    }

    private sealed class FakeGraphQlMap(TableIdentifier id) : IGraphQlCatalogMap
    {
        public Task<GraphQlTableMapping?> GetTableAsync(TableIdentifier table, CancellationToken ct = default) =>
            Task.FromResult<GraphQlTableMapping?>(table.Equals(id)
                ? new GraphQlTableMapping(
                    Table: id,
                    QueryField: "crm_dbo_customers",
                    TypeName: "Customer",
                    FilterType: "CustomerFilter",
                    OrderByType: "CustomerOrder",
                    Columns:
                    [
                        new GraphQlColumnMapping("id", "id", "Int"),
                        new GraphQlColumnMapping("ssn", "ssn", "String")
                    ],
                    Relations: [])
                : null);

        public Task<string?> GetRootFieldAsync(TableIdentifier table, CancellationToken ct = default) =>
            Task.FromResult<string?>(table.Equals(id) ? "crm_dbo_customers" : null);

        public Task<TableIdentifier?> ResolveRootFieldAsync(string field, CancellationToken ct = default) =>
            Task.FromResult<TableIdentifier?>(field == "crm_dbo_customers" ? id : null);

        public Task<IReadOnlyList<TableIdentifier>?> ResolveDocumentTablesAsync(string document, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TableIdentifier>?>([id]);
    }

    private sealed class FakeMcpAuditRepo : IAuditLogRepository
    {
        public Task RecordAuditEventAsync(AuditLogEntry entry, CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordAuditEventAsync(AuditLogEntry entry, System.Data.Common.DbTransaction existingTx, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<AuditLogEntry>> GetAuditLogEntriesAsync(int limit = 100, TenantId? tenantId = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AuditLogEntry>>([]);
        public Task<IReadOnlyList<AuditLogEntry>> QueryAuditLogsAsync(string? targetTable = null, Sid? actorSid = null, DateTimeOffset? since = null, int limit = 1000, TenantId? tenantId = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AuditLogEntry>>([]);
        public Task<bool> VerifyAuditHashChainAsync(CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class FakeMcpGatewayExecution : IGatewayExecutionService
    {
        public Task<(IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, TableAccessDecision Decision)> ExecuteTableQueryAsync(
            ClaimsPrincipal? principal, TableIdentifier table, int? first = null, int? after = null, CancellationToken ct = default) =>
            Task.FromResult<(IReadOnlyList<IReadOnlyDictionary<string, object?>>, TableAccessDecision)>(([], TableAccessDecision.Denied(table, "fake")));

        public Task<(IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, TableAccessDecision Decision)> ExecuteTableQueryAsync(
            ClaimsPrincipal? principal, TableIdentifier table, int? first, int? after, IReadOnlyDictionary<string, object?>? queryArguments, IReadOnlyList<string>? requestedFields = null, IReadOnlyDictionary<string, string[]>? requestHeaders = null, CancellationToken ct = default) =>
            Task.FromResult<(IReadOnlyList<IReadOnlyDictionary<string, object?>>, TableAccessDecision)>(([], TableAccessDecision.Denied(table, "fake")));

        public Task<TableAccessDecision> CheckTableAccessAsync(ClaimsPrincipal? principal, TableIdentifier table, CancellationToken ct = default) =>
            Task.FromResult(TableAccessDecision.Denied(table, "fake"));
    }

    private sealed class FakeEmbeddingGenerator : ILocalEmbeddingGenerator
    {
        public int EmbeddingDimensions => 4;
        public string ModelIdentifier => "fake-test";
        public ReadOnlyMemory<float> GenerateNormalizedEmbedding(string text) => new float[] { 1, 0, 0, 0 };
        public Task<IReadOnlyList<ReadOnlyMemory<float>>> GenerateNormalizedEmbeddingsAsync(IReadOnlyList<string> texts, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ReadOnlyMemory<float>>>(texts.Select(_ => (ReadOnlyMemory<float>)new float[] { 1, 0, 0, 0 }).ToList());
    }

    private sealed class WholeTableUnmask : Autheris.Application.Policy.Protection.IUnmaskGrantResolver
    {
        public ValueTask<Autheris.Application.Policy.Protection.UnmaskCoverage> ResolveAsync(
            TenantId tenant, IReadOnlyCollection<Sid> subjects, bool groupsComplete, TableIdentifier table, CancellationToken ct = default) =>
            ValueTask.FromResult(new Autheris.Application.Policy.Protection.UnmaskCoverage(wholeTable: true, new HashSet<string>()));
    }
}
