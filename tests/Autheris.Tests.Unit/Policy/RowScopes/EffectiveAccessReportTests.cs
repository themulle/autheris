namespace Autheris.Tests.Unit.Policy.RowScopes;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Audit;
using Autheris.Application.Catalog;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Contracts;
using Autheris.Application.Governance.Paging;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Policy.RowScopes;
using Autheris.Application.Services;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Tests.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class EffectiveAccessReportTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TenantId TenantB = new("tenant-b");
    private static readonly Sid AliceSid = new("S-1-5-21-100-1");
    private static readonly Sid BobSid = new("S-1-5-21-100-2");
    private static readonly Sid GroupFinSid = new("S-1-5-21-900-1");
    private static readonly Sid OwnerSid = new("S-1-5-21-200-1");
    private static readonly Sid AdminSid = new("S-1-5-21-300-1");
    private static readonly Sid AuditorSid = new("S-1-5-21-400-1");
    private static readonly DateTimeOffset FixedNow = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    private static readonly TableIdentifier Orders = new("pg", "sales", "orders");
    private static readonly TableIdentifier Invoices = new("pg", "sales", "invoices");
    private static readonly TableIdentifier ForeignOrders = new("pg", "sales", "foreign_orders");

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Report_lists_provenance_group_grant_policy()
    {
        var orders = BuildTable(Orders, TenantA.Value, col("id"), col("region"), col("ssn", sensitive: true));
        var invoices = BuildTable(Invoices, TenantA.Value, col("id"), col("region"));

        var regionPolicy = BuildRegionPolicy("region_policy", "region", ":region", withParam: true);
        var hardPolicy = BuildRegionPolicy("active_only", "region", "EU", withParam: false);
        var hardProfile = new VirtualFilterAccessProfile
        {
            TenantId = TenantA,
            Name = "fin_hard_profile",
            GranteeType = GranteeType.Group,
            GranteeSid = GroupFinSid,
            Scope = "pg.sales.*",
            Uncovered = UncoveredPolicy.Skip,
            Status = FilterApprovalStatus.Active,
            Bindings = [new FilterBinding { FilterName = "active_only", TargetPattern = "pg.sales.*" }]
        };

        var userGrant = new Consent
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            TenantId = TenantA,
            TableIdentifier = Orders,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = AliceSid,
            ValidFrom = FixedNow.AddDays(-1),
            ValidTo = FixedNow.AddDays(10),
            ColumnRules = [new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear }]
        };

        var groupGrant = new Consent
        {
            Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            TenantId = TenantA,
            TableIdentifier = Orders,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.Group,
            GranteeSid = GroupFinSid,
            ValidFrom = FixedNow.AddDays(-1),
            ValidTo = FixedNow.AddDays(10),
            ColumnRules =
            [
                new ConsentColumnRule { ColumnName = "region", AccessLevel = ColumnAccessLevel.Clear },
                new ConsentColumnRule { ColumnName = "ssn", AccessLevel = ColumnAccessLevel.Mask }
            ],
            RowScope =
            [
                new GrantRowScopeEntry(
                    "region_policy",
                    new Dictionary<string, JsonElement>
                    {
                        ["region"] = JsonSerializer.SerializeToElement(new[] { "EU", "US" })
                    },
                    ColumnMap: null)
            ]
        };

        var h = CreateHarness(
            [orders, invoices],
            [userGrant, groupGrant],
            new VirtualFilterSnapshot(3, [regionPolicy, hardPolicy], [hardProfile]));

        var admin = BuildContext(AdminSid, TenantA, clusterRoles: ["GovernanceAdmin"]);
        var alice = BuildContext(AliceSid, TenantA, groupSids: [GroupFinSid]);
        h.Subjects.Register(AliceSid.Value, alice);

        var page1 = await h.Service.GetEffectiveAccessAsync(
            admin,
            new EffectiveAccessReportQuery(AliceSid.Value, "pg.sales.*", Cursor: null, PageSize: 1));

        page1.Outcome.ShouldBe(EffectiveAccessOutcome.Ok);
        page1.Report!.Truncated.ShouldBeTrue();
        page1.Report.NextCursor.ShouldNotBeNullOrWhiteSpace();
        page1.Report.Objects.Count.ShouldBe(1);

        var page2 = await h.Service.GetEffectiveAccessAsync(
            admin,
            new EffectiveAccessReportQuery(AliceSid.Value, "pg.sales.*", page1.Report.NextCursor, PageSize: 10));

        page2.Outcome.ShouldBe(EffectiveAccessOutcome.Ok);
        page2.Report!.Truncated.ShouldBeFalse();
        page2.Report.Objects.Count.ShouldBe(1);

        var ordersEntry = new[] { page1.Report.Objects[0], page2.Report.Objects[0] }
            .Single(o => o.Object == Orders.ToString());

        ordersEntry.Allowed.ShouldBeTrue();
        ordersEntry.ContributingGrants.Count.ShouldBe(2);
        var userContrib = ordersEntry.ContributingGrants.Single(g => g.GrantId == userGrant.Id);
        userContrib.Via.ShouldBe("user");
        userContrib.RowScope.ShouldBeEmpty();

        var groupContrib = ordersEntry.ContributingGrants.Single(g => g.GrantId == groupGrant.Id);
        groupContrib.Via.ShouldBe(GroupFinSid.Value);
        groupContrib.RowScope.Count.ShouldBe(1);
        groupContrib.RowScope[0].Policy.ShouldBe("region_policy");
        groupContrib.RowScope[0].ParameterCount.ShouldBe(2);

        ordersEntry.HardPolicies.ShouldContain(p => p.Policy == "active_only" && p.Via == GroupFinSid.Value);
        ordersEntry.Columns.Single(c => c.ColumnName == "ssn").State.ShouldBe("RowDependent");
    }

    [Fact]
    public async Task Redundant_grant_flagged()
    {
        var orders = BuildTable(Orders, TenantA.Value, col("id"), col("region"));
        var regionPolicy = BuildRegionPolicy("region_policy", "region", ":region", withParam: true);

        var broadSuperset = new Consent
        {
            Id = Guid.Parse("10000000-0000-0000-0000-000000000001"),
            TenantId = TenantA,
            TableIdentifier = Orders,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = AliceSid,
            ValidFrom = FixedNow.AddDays(-1),
            ValidTo = FixedNow.AddDays(10),
            ColumnRules =
            [
                new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear },
                new ConsentColumnRule { ColumnName = "region", AccessLevel = ColumnAccessLevel.Clear }
            ],
            RowScope =
            [
                new GrantRowScopeEntry(
                    "region_policy",
                    new Dictionary<string, JsonElement>
                    {
                        ["region"] = JsonSerializer.SerializeToElement(new[] { "EU", "US", "UK" })
                    },
                    null)
            ]
        };

        var narrowSubset = new Consent
        {
            Id = Guid.Parse("20000000-0000-0000-0000-000000000002"),
            TenantId = TenantA,
            TableIdentifier = Orders,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.Group,
            GranteeSid = GroupFinSid,
            ValidFrom = FixedNow.AddDays(-1),
            ValidTo = FixedNow.AddDays(10),
            ColumnRules =
            [
                new ConsentColumnRule { ColumnName = "region", AccessLevel = ColumnAccessLevel.Mask }
            ],
            RowScope =
            [
                new GrantRowScopeEntry(
                    "region_policy",
                    new Dictionary<string, JsonElement>
                    {
                        ["region"] = JsonSerializer.SerializeToElement(new[] { "EU", "US" })
                    },
                    null)
            ]
        };

        var h = CreateHarness(
            [orders],
            [broadSuperset, narrowSubset],
            new VirtualFilterSnapshot(1, [regionPolicy], []));
        var alice = BuildContext(AliceSid, TenantA, groupSids: [GroupFinSid]);
        var admin = BuildContext(AdminSid, TenantA, clusterRoles: ["GovernanceAdmin"]);
        h.Subjects.Register(AliceSid.Value, alice);

        var result = await h.Service.GetEffectiveAccessAsync(
            admin,
            new EffectiveAccessReportQuery(AliceSid.Value, "pg.sales.orders"));

        result.Outcome.ShouldBe(EffectiveAccessOutcome.Ok);
        var entry = result.Report!.Objects.ShouldHaveSingleItem();
        entry.RedundantGrants.ShouldContain(f => f.GrantId == narrowSubset.Id && f.CoveredBy == broadSuperset.Id);

        var candidateId = Guid.Parse("30000000-0000-0000-0000-000000000003");
        var preview = await h.Preview.PreviewAsync(
            alice,
            [
                new GrantPreviewRequestedItem(
                    "pg.sales.orders",
                    ColumnRules: [new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Mask }],
                    RowScope:
                    [
                        new GrantRowScopeEntry(
                            "region_policy",
                            new Dictionary<string, JsonElement>
                            {
                                ["region"] = JsonSerializer.SerializeToElement(new[] { "EU" })
                            },
                            null)
                    ],
                    ItemId: candidateId)
            ]);

        preview.RedundantGrants.ShouldContain(f => f.GrantId == candidateId && f.CoveredBy == broadSuperset.Id);
    }

    [Fact]
    public async Task Empty_hard_intersection_flagged()
    {
        var orders = BuildTable(Orders, TenantA.Value, col("id"), col("region"));
        var onlyEu = BuildRegionPolicy("hard_eu", "region", "EU", withParam: false);
        var onlyUs = BuildRegionPolicy("hard_us", "region", "US", withParam: false);

        var profileEu = new VirtualFilterAccessProfile
        {
            TenantId = TenantA,
            Name = "profile_eu",
            GranteeType = GranteeType.User,
            GranteeSid = AliceSid,
            Scope = "pg.sales.*",
            Uncovered = UncoveredPolicy.Deny,
            Status = FilterApprovalStatus.Active,
            Bindings = [new FilterBinding { FilterName = "hard_eu", TargetPattern = "pg.sales.*" }]
        };
        var profileUs = new VirtualFilterAccessProfile
        {
            TenantId = TenantA,
            Name = "profile_us",
            GranteeType = GranteeType.Group,
            GranteeSid = GroupFinSid,
            Scope = "pg.sales.*",
            Uncovered = UncoveredPolicy.Deny,
            Status = FilterApprovalStatus.Active,
            Bindings = [new FilterBinding { FilterName = "hard_us", TargetPattern = "pg.sales.*" }]
        };

        var grant = new Consent
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            TableIdentifier = Orders,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = AliceSid,
            ValidFrom = FixedNow.AddDays(-1),
            ValidTo = FixedNow.AddDays(10)
        };

        var h = CreateHarness(
            [orders],
            [grant],
            new VirtualFilterSnapshot(2, [onlyEu, onlyUs], [profileEu, profileUs]));
        var alice = BuildContext(AliceSid, TenantA, groupSids: [GroupFinSid]);
        var admin = BuildContext(AdminSid, TenantA, clusterRoles: ["GovernanceAdmin"]);
        h.Subjects.Register(AliceSid.Value, alice);

        var result = await h.Service.GetEffectiveAccessAsync(
            admin,
            new EffectiveAccessReportQuery(AliceSid.Value, "pg.sales.orders"));

        result.Outcome.ShouldBe(EffectiveAccessOutcome.Ok);
        var entry = result.Report!.Objects.ShouldHaveSingleItem();
        entry.EmptyHardIntersection.ShouldBeTrue();
        entry.HardPolicies.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Report_never_returns_predicate_or_parameter_values_of_other_subjects()
    {
        var orders = BuildTable(Orders, TenantA.Value, col("id"), col("region"));
        var regionPolicy = BuildRegionPolicy("region_policy", "region", ":region", withParam: true);
        var hardPolicy = BuildRegionPolicy("hard_secret", "region", "SECRET-HARD-VALUE", withParam: false);
        var hardProfile = new VirtualFilterAccessProfile
        {
            TenantId = TenantA,
            Name = "bob_hard",
            GranteeType = GranteeType.User,
            GranteeSid = BobSid,
            Scope = "pg.sales.*",
            Uncovered = UncoveredPolicy.Skip,
            Status = FilterApprovalStatus.Active,
            Bindings = [new FilterBinding { FilterName = "hard_secret", TargetPattern = "pg.sales.*" }]
        };

        var bobGrant = new Consent
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            TableIdentifier = Orders,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = BobSid,
            ValidFrom = FixedNow.AddDays(-1),
            ValidTo = FixedNow.AddDays(10),
            RowFilters =
            [
                new ConsentRowFilter { ColumnName = "region", Operator = "IN", ValueType = "string", ValueJson = "[\"SECRET-LEGACY-VALUE\"]" }
            ],
            RowScope =
            [
                new GrantRowScopeEntry(
                    "region_policy",
                    new Dictionary<string, JsonElement>
                    {
                        ["region"] = JsonSerializer.SerializeToElement(new[] { "SECRET-PARAM-ALPHA", "SECRET-PARAM-BETA" })
                    },
                    null)
            ]
        };

        var h = CreateHarness(
            [orders],
            [bobGrant],
            new VirtualFilterSnapshot(5, [regionPolicy, hardPolicy], [hardProfile]));
        var bob = BuildContext(BobSid, TenantA);
        var admin = BuildContext(AdminSid, TenantA, clusterRoles: ["GovernanceAdmin"]);
        h.Subjects.Register(BobSid.Value, bob);

        var result = await h.Service.GetEffectiveAccessAsync(
            admin,
            new EffectiveAccessReportQuery(BobSid.Value, "pg.sales.orders"));

        result.Outcome.ShouldBe(EffectiveAccessOutcome.Ok);
        var json = JsonSerializer.Serialize(result.Report, WebJson);

        json.ShouldContain("region_policy");
        json.ShouldContain("\"parameterCount\":2");
        json.ShouldContain("hard_secret");
        json.ShouldNotContain("SECRET-PARAM-ALPHA");
        json.ShouldNotContain("SECRET-PARAM-BETA");
        json.ShouldNotContain("SECRET-LEGACY-VALUE");
        json.ShouldNotContain("SECRET-HARD-VALUE");
        json.ShouldNotContain("WHERE", Case.Sensitive);

        var auditSerialized = JsonSerializer.Serialize(h.Audit.Entries, WebJson);
        auditSerialized.ShouldNotContain("SECRET-PARAM-ALPHA");
        auditSerialized.ShouldNotContain("SECRET-LEGACY-VALUE");
        auditSerialized.ShouldNotContain("SECRET-HARD-VALUE");
    }

    [Fact]
    public async Task Report_authorization_matches_explain()
    {
        var orders = BuildTable(Orders, TenantA.Value, col("id"));
        var invoices = BuildTable(Invoices, TenantA.Value, col("id"));
        var foreignOrders = BuildTable(ForeignOrders, TenantB.Value, col("id"));

        var h = CreateHarness([orders, invoices, foreignOrders], [], VirtualFilterSnapshot.Empty);
        h.Owners.SetOwners(Orders, OwnerSid);
        h.Subjects.Register(BobSid.Value, BuildContext(BobSid, TenantA));
        h.Subjects.Register(AliceSid.Value, BuildContext(AliceSid, TenantA));

        var userCaller = BuildContext(AliceSid, TenantA);
        var ownerCaller = BuildContext(OwnerSid, TenantA);
        var adminCaller = BuildContext(AdminSid, TenantA, clusterRoles: ["GovernanceAdmin"]);
        var auditorCaller = BuildContext(AuditorSid, TenantA, clusterRoles: ["SecurityAuditor"]);
        var foreignCaller = BuildContext(AdminSid, TenantB, clusterRoles: ["GovernanceAdmin"]);

        // Non-owner user querying another subject -> 403 Forbidden
        var forbidden = await h.Service.GetEffectiveAccessAsync(userCaller, new EffectiveAccessReportQuery(BobSid.Value, "pg.sales.orders"));
        forbidden.Outcome.ShouldBe(EffectiveAccessOutcome.Forbidden);

        // User querying self -> 200 OK
        var selfResult = await h.Service.GetEffectiveAccessAsync(userCaller, new EffectiveAccessReportQuery(AliceSid.Value, "pg.sales.orders"));
        selfResult.Outcome.ShouldBe(EffectiveAccessOutcome.Ok);

        // Owner querying other subject on owned vs unowned object -> OK on owned, Forbidden on unowned
        var ownerOnOwned = await h.Service.GetEffectiveAccessAsync(ownerCaller, new EffectiveAccessReportQuery(BobSid.Value, "pg.sales.orders"));
        ownerOnOwned.Outcome.ShouldBe(EffectiveAccessOutcome.Ok);
        ownerOnOwned.Report!.Objects.Select(o => o.Object).ShouldBe([Orders.ToString()]);

        var ownerOnUnowned = await h.Service.GetEffectiveAccessAsync(ownerCaller, new EffectiveAccessReportQuery(BobSid.Value, "pg.sales.invoices"));
        ownerOnUnowned.Outcome.ShouldBe(EffectiveAccessOutcome.Forbidden);

        // GovernanceAdmin & SecurityAuditor -> 200 OK
        (await h.Service.GetEffectiveAccessAsync(adminCaller, new EffectiveAccessReportQuery(BobSid.Value, "pg.sales.orders"))).Outcome.ShouldBe(EffectiveAccessOutcome.Ok);
        (await h.Service.GetEffectiveAccessAsync(auditorCaller, new EffectiveAccessReportQuery(BobSid.Value, "pg.sales.orders"))).Outcome.ShouldBe(EffectiveAccessOutcome.Ok);

        // Cross-tenant and unknown -> uniform 404 NotFound with identical HTTP body
        var services = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();
        var bodies = new List<(int Status, string Body)>();
        foreach (var query in new[]
        {
            new EffectiveAccessReportQuery(BobSid.Value, ForeignOrders.ToString()),
            new EffectiveAccessReportQuery(BobSid.Value, "pg.sales.unknown_table")
        })
        {
            var r = await h.Service.GetEffectiveAccessAsync(adminCaller, query);
            r.Outcome.ShouldBe(EffectiveAccessOutcome.NotFound);
            var ctx = new DefaultHttpContext { RequestServices = services, TraceIdentifier = "trace-fixed" };
            ctx.Response.Body = new MemoryStream();
            await EffectiveAccessEndpoints.ToResult(r.Outcome, r.Report, ctx).ExecuteAsync(ctx);
            ctx.Response.Body.Position = 0;
            bodies.Add((ctx.Response.StatusCode, await new StreamReader(ctx.Response.Body).ReadToEndAsync()));
        }

        var foreignTenantCall = await h.Service.GetEffectiveAccessAsync(foreignCaller, new EffectiveAccessReportQuery(BobSid.Value, Orders.ToString()));
        foreignTenantCall.Outcome.ShouldBe(EffectiveAccessOutcome.NotFound);

        bodies.Select(b => b.Status).Distinct().ShouldBe([404]);
        bodies.Select(b => b.Body).Distinct().Count().ShouldBe(1);
    }

    [Fact]
    public async Task Preview_is_non_blocking_and_audited()
    {
        var orders = BuildTable(Orders, TenantA.Value, col("id"), col("region"));
        var onlyEu = BuildRegionPolicy("hard_eu", "region", "EU", withParam: false);
        var onlyUs = BuildRegionPolicy("hard_us", "region", "US", withParam: false);
        var profileEu = new VirtualFilterAccessProfile
        {
            TenantId = TenantA,
            Name = "profile_eu",
            GranteeType = GranteeType.User,
            GranteeSid = AliceSid,
            Scope = "pg.sales.*",
            Uncovered = UncoveredPolicy.Deny,
            Status = FilterApprovalStatus.Active,
            Bindings = [new FilterBinding { FilterName = "hard_eu", TargetPattern = "pg.sales.*" }]
        };
        var profileUs = new VirtualFilterAccessProfile
        {
            TenantId = TenantA,
            Name = "profile_us",
            GranteeType = GranteeType.User,
            GranteeSid = AliceSid,
            Scope = "pg.sales.*",
            Uncovered = UncoveredPolicy.Deny,
            Status = FilterApprovalStatus.Active,
            Bindings = [new FilterBinding { FilterName = "hard_us", TargetPattern = "pg.sales.*" }]
        };

        var existingBroad = new Consent
        {
            Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            TenantId = TenantA,
            TableIdentifier = Orders,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = AliceSid,
            ValidFrom = FixedNow.AddDays(-1),
            ValidTo = FixedNow.AddDays(10)
        };

        var h = CreateHarness(
            [orders],
            [existingBroad],
            new VirtualFilterSnapshot(4, [onlyEu, onlyUs], [profileEu, profileUs]));
        var alice = BuildContext(AliceSid, TenantA);
        var approver = BuildContext(OwnerSid, TenantA);
        var itemId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

        var preview = await h.Preview.PreviewAsync(
            approver,
            alice,
            [new GrantPreviewRequestedItem("pg.sales.*", ItemId: itemId)]);

        preview.MatchedObjectCount.ShouldBe(1);
        preview.IncludesFutureObjects.ShouldBeTrue();
        preview.EmptyHardIntersection.ShouldBeTrue();
        preview.RedundantGrants.ShouldContain(f => f.GrantId == itemId && f.CoveredBy == existingBroad.Id);
        preview.AffectedObjects.ShouldBe([Orders.ToString()]);

        var auditEntry = h.Audit.Entries.ShouldHaveSingleItem();
        auditEntry.EventType.ShouldBe(AuditEventTypes.AuthzEffectPreview);
        auditEntry.ActorSid.ShouldBe(OwnerSid);
        auditEntry.Decision.ShouldBe("ALLOW");
        auditEntry.DetailsJson.ShouldContain("\"includesFutureObjects\":true");
        auditEntry.DetailsJson.ShouldContain("\"emptyHardIntersection\":true");
        auditEntry.DetailsJson.ShouldNotContain("WHERE", Case.Sensitive);
    }

    private static TestHarness CreateHarness(
        IReadOnlyList<TableMetadata> tables,
        IReadOnlyList<Consent> consents,
        VirtualFilterSnapshot snapshot)
    {
        var time = new FixedTimeProvider(FixedNow);
        var regionsId = new TableIdentifier("pg", "ref", "regions");
        var repoTables = tables.Any(t => t.Identifier == regionsId)
            ? tables
            : [.. tables, BuildTable(regionsId, TenantA.Value, col("region"))];
        var tableRepo = new FakeTableRepo(repoTables);
        var consentRepo = new FakeConsentRepo(consents);
        var ownerRepo = new FakeOwnershipRepo();
        var visibility = new FakeVisibilityService(tables);
        var subjectResolver = new FakeSubjectResolver();
        var auditWriter = new FakeAuditWriter();
        var snapshotProvider = new FakeSnapshotProvider(snapshot);

        var rowScopeCompiler = new RowScopeCompiler(snapshotProvider, catalog: tableRepo);
        var resolution = new ConsentResolutionService();
        var pdp = new AccessDecisionPoint(
            Options.Create(new GatewayOptions()),
            resolution,
            consentRepository: consentRepo,
            auditWriter: auditWriter,
            timeProvider: time,
            rowScopeCompiler: rowScopeCompiler,
            ownershipRepository: ownerRepo);

        var service = new EffectiveAccessService(
            pdp,
            tableRepo,
            visibility,
            ownerRepo,
            consentRepo,
            subjectResolver,
            auditWriter,
            snapshotProvider,
            timeProvider: time);
        var preview = new GrantEffectPreviewService(
            tableRepo,
            consentRepo,
            auditWriter,
            snapshotProvider,
            timeProvider: time);

        return new TestHarness(service, preview, ownerRepo, subjectResolver, auditWriter);
    }

    private static VirtualFilter BuildRegionPolicy(string name, string column, string value, bool withParam) =>
        new()
        {
            TenantId = TenantA,
            Name = name,
            Source = "pg",
            KeyColumns = [$"r.{column}"],
            Parameters = withParam ? [new RowPolicyParameter("region", RowPolicyParameterType.String, IsList: true)] : [],
            Status = FilterApprovalStatus.Active,
            Structured = new StructuredFilterDefinition
            {
                From = new TableIdentifier("pg", "ref", "regions"),
                FromAlias = "r",
                Where = [new FilterCondition($"r.{column}", FilterConditionOperator.Eq, value)]
            }
        };

    private static TableMetadata BuildTable(TableIdentifier id, string ownerTenant, params TableColumn[] cols) =>
        new()
        {
            Table = new Table
            {
                Id = Guid.NewGuid(),
                SourceName = id.Domain,
                SchemaName = id.Schema,
                TableName = id.TableName,
                SourceType = "postgresql",
                IsActive = true
            },
            Identifier = id,
            OwnerTenantId = ownerTenant,
            Columns = cols
        };

    private static TableColumn col(string name, bool sensitive = false, bool hidden = false) =>
        new() { ColumnName = name, IsSensitive = sensitive, DiscoveryHidden = hidden };

    private static SecurityPrincipalContext BuildContext(
        Sid userSid,
        TenantId tenant,
        IReadOnlyList<Sid>? groupSids = null,
        IReadOnlyList<string>? clusterRoles = null)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, userSid.Value),
            new("tid", tenant.Value)
        };
        if (groupSids is not null)
        {
            foreach (var g in groupSids)
            {
                claims.Add(new Claim(ClaimTypes.GroupSid, g.Value));
            }
        }

        if (clusterRoles is not null)
        {
            foreach (var r in clusterRoles)
            {
                claims.Add(new Claim(ClaimTypes.Role, r));
            }
        }

        return new SecurityPrincipalContext
        {
            Principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")),
            UserSid = userSid,
            TenantId = tenant,
            GroupSids = groupSids is null ? new HashSet<Sid>() : new HashSet<Sid>(groupSids),
            GroupResolutionStatus = GroupResolutionStatus.Complete,
            TenantRoles = new HashSet<string>(),
            ClusterRoles = clusterRoles is null ? new HashSet<string>() : new HashSet<string>(clusterRoles),
            AuthenticationScheme = "test"
        };
    }

    private sealed record TestHarness(
        EffectiveAccessService Service,
        GrantEffectPreviewService Preview,
        FakeOwnershipRepo Owners,
        FakeSubjectResolver Subjects,
        FakeAuditWriter Audit);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeSnapshotProvider(VirtualFilterSnapshot snapshot) : IVirtualFilterSnapshotProvider
    {
        public ValueTask<VirtualFilterSnapshot> GetAsync(CancellationToken ct = default) => ValueTask.FromResult(snapshot);
        public void Invalidate() { }
    }

    private sealed class FakeSubjectResolver : IExplainSubjectResolver
    {
        private readonly Dictionary<string, SecurityPrincipalContext> _map = new(StringComparer.Ordinal);
        public void Register(string sid, SecurityPrincipalContext ctx) => _map[sid] = ctx;

        public ValueTask<SecurityPrincipalContext?> ResolveAsync(string? sid, string? upn, TenantId tenant, CancellationToken ct) =>
            ValueTask.FromResult(sid is not null && _map.TryGetValue(sid, out var found) && found.TenantId == tenant ? found : null);
    }

    private sealed class FakeAuditWriter : IAuditWriter
    {
        public List<AuditLogEntry> Entries { get; } = [];
        public Task RecordRequiredAsync(AuditLogEntry entry, CancellationToken ct = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task RecordBestEffortAsync(AuditLogEntry entry, CancellationToken ct = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task RecordAsync(AuditLogEntry entry, CancellationToken ct = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeVisibilityService(IReadOnlyList<TableMetadata> tables) : ICatalogVisibilityService
    {
        public ValueTask<CatalogVisibleSet> GetVisibleAsync(CatalogCaller caller, SchemaContractDefinition? contract = null, CancellationToken ct = default)
        {
            var rows = tables.Where(t => !t.Table.DiscoveryHidden).ToList();
            return ValueTask.FromResult(new CatalogVisibleSet(rows, CallerScope.Global, isAdminView: true));
        }

        public ValueTask<CatalogVisibleRow?> FindVisibleAsync(CatalogCaller caller, TableIdentifier table, CancellationToken ct = default)
        {
            var found = tables.FirstOrDefault(t => t.Identifier == table && !t.Table.DiscoveryHidden);
            return ValueTask.FromResult<CatalogVisibleRow?>(found is null ? null : new CatalogVisibleRow(found, CatalogOutputShape.Full));
        }

        public ValueTask<IReadOnlySet<TableIdentifier>> FilterVisibleAsync(CatalogCaller caller, IReadOnlyCollection<TableIdentifier> candidates, CancellationToken ct = default)
        {
            var set = tables.Where(t => !t.Table.DiscoveryHidden && candidates.Contains(t.Identifier)).Select(t => t.Identifier).ToHashSet();
            return ValueTask.FromResult<IReadOnlySet<TableIdentifier>>(set);
        }
    }

    private sealed class FakeTableRepo(IReadOnlyList<TableMetadata> tables) : ITableMetadataRepository
    {
        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default) => Task.FromResult(tables);
        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default) => Task.FromResult(tables.FirstOrDefault(t => t.Identifier == table));
        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default) => Task.FromResult(metadata);
        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(TableIdentifier table, IReadOnlyList<string> primaryKey, IReadOnlyList<TableIndexDefinition> indexes, IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null, CancellationToken ct = default) => Task.FromResult<TableKeyDefinitionChange?>(null);
        public Task<CatalogVersion> FlushCatalogVersionAsync(CancellationToken ct = default) => Task.FromResult(new CatalogVersion(1, 0));
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

    private sealed class FakeConsentRepo(IReadOnlyList<Consent> consents) : IConsentRepository
    {
        public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, CancellationToken ct = default) =>
            GetActiveConsentsForSubjectsAsync(subjects, table, atTime, tenantId: null, ct);

        public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, TenantId? tenantId, CancellationToken ct = default)
        {
            var set = subjects.ToHashSet();
            return Task.FromResult<IReadOnlyList<Consent>>(consents.Where(c =>
                (tenantId is null || c.TenantId == tenantId.Value) &&
                c.IsActive(atTime) &&
                ConsentResolutionService.IsTableTargetMatch(c, table) &&
                c.GranteeSid is { } gs && set.Contains(gs)).ToList());
        }

        public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, IEnumerable<string>? roles = null, DateTimeOffset? atTime = null, CancellationToken ct = default) =>
            GetAllActiveConsentsForSubjectsAsync(subjects, roles, atTime, tenantId: null, ct);

        public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, IEnumerable<string>? roles, DateTimeOffset? atTime, TenantId? tenantId, CancellationToken ct = default)
        {
            var set = subjects.ToHashSet();
            var now = atTime ?? FixedNow;
            return Task.FromResult<IReadOnlyList<Consent>>(consents.Where(c =>
                (tenantId is null || c.TenantId == tenantId.Value) &&
                c.IsActive(now) &&
                c.GranteeSid is { } gs && set.Contains(gs)).ToList());
        }

        public Task<Consent> CreateConsentAsync(Consent consent, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Consent?> GetConsentByIdAsync(Guid consentId, CancellationToken ct = default) => Task.FromResult<Consent?>(null);
        public Task RevokeConsentAsync(Guid consentId, Sid revokedBySid, string reason, ConsentRevocationAuthority authority, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<Consent>> GetExpiringConsentsAsync(DateTimeOffset threshold, int maxResults = 1000, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<IReadOnlyList<Consent>> GetActiveConsentsByConsentRequestIdAsync(Guid consentRequestId, DateTimeOffset atTime, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<bool> RevokeSystemConsentAsync(Guid consentId, Guid consentRequestId, Sid revokedBySid, string reason, CancellationToken ct = default) => Task.FromResult(false);
        public Task<ConsentPage> GetConsentPageAsync(ConsentPageQuery query, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ReplaceConsentsAsync(IReadOnlyList<Guid> revokeConsentIds, IReadOnlyList<Consent> create, Sid actor, string reason, ConsentRevocationAuthority authority, IReadOnlyList<AuditLogEntry>? auditEntries = null, CancellationToken ct = default) => Task.CompletedTask;
    }
}
