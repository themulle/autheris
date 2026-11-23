namespace Autheris.Tests.Integration.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Policy;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Policy.RowScopes;
using Autheris.Application.Policy.Services;
using Autheris.Application.Services;
using Autheris.Application.Sql.Policy;
using Autheris.Application.VirtualFilters;
using Autheris.Application.VirtualFilters.Federation;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.Extensions.Options;
using TrinoSqlEngine;
using TrinoSqlEngine.Governance;

/// <summary>Grant-model rows S30..S37 (grants carry row-scope entries over named row policies; patterns cover later objects).</summary>
internal sealed partial class CrossTransportParityGrantModelRows
{
    internal static readonly Sid GroupA = new("S-1-5-21-9001-9002-9003-3001");
    internal static readonly Sid GroupB = new("S-1-5-21-9001-9002-9003-3002");

    private static IReadOnlyDictionary<string, ColumnAccessLevel> Cols(ColumnAccessLevel ssn) => new Dictionary<string, ColumnAccessLevel>
    {
        ["id"] = ColumnAccessLevel.Clear,
        ["region"] = ColumnAccessLevel.Clear,
        ["ssn"] = ssn
    };

    private static IReadOnlyDictionary<string, ColumnAccessLevel> AllCols(ColumnAccessLevel level) => new Dictionary<string, ColumnAccessLevel>
    {
        ["id"] = level,
        ["region"] = level,
        ["ssn"] = level
    };

    internal static Guid GrantId(int n) => new($"00000000-0000-0000-0000-0000000030{n:00}");

    private static readonly TableIdentifier GrantOrdersId = new("default", "main", "orders");

    internal static IReadOnlyList<ParityScenario> Build(DateTimeOffset now)
    {
        var principal = GroupUser(CrossTransportParityHarness.AliceSid, GroupA, GroupB);
        var table = GrantModelTable("orders");
        var tenant = CrossTransportParityHarness.TenantA;
        var plain = new ParityGrantModelSetup([RegionPolicy(), SegmentPolicy()], []);

        return
        [
            // S30: filtered grant via group A plus unfiltered grant via group B -> all rows, both grant ids.
            new(ParityScenarioId.S30_FilteredAndUnfilteredGrantUnion, principal, tenant, table,
                [GroupGrant(1, GroupA, now, [RegionEntry("EU")]), GroupGrant(2, GroupB, now)], [], VirtualFilterSnapshot.Empty, false, false,
                new(true, GrantIds: [GrantId(1), GrantId(2)], RowSet: ParityRowSet.Unfiltered, ColumnState: Cols(ColumnAccessLevel.Mask)), plain),

            // S31: overlapping grants with different masking -> ssn is Clear only in the rows of the clearing grant (per-cell state).
            new(ParityScenarioId.S31_OverlappingGrantsPerCellMasking, principal, tenant, table,
                [
                    GroupGrant(1, GroupA, now, [RegionEntry("EU")], ColumnRules(ColumnAccessLevel.Clear)),
                    GroupGrant(2, GroupB, now, [RegionEntry("US")], ColumnRules(ColumnAccessLevel.Mask))
                ], [], VirtualFilterSnapshot.Empty, false, false,
                new(true, GrantIds: [GrantId(1), GrantId(2)], RowSet: ParityRowSet.Filtered,
                    ColumnState: Cols(ColumnAccessLevel.RowDependent)), plain),

            // S32: hard policy via group A plus unfiltered grant via group B -> filtered (the hard policy is not bypassable).
            new(ParityScenarioId.S32_HardPolicyWithUnfilteredGrant, principal, tenant, table,
                [GroupGrant(1, GroupB, now)], [], VirtualFilterSnapshot.Empty, false, false,
                new(true, GrantIds: [GrantId(1)], RowSet: ParityRowSet.Filtered, ColumnState: Cols(ColumnAccessLevel.Mask)),
                new ParityGrantModelSetup([ActiveOrgPolicy()], [HardProfile(GroupA)])),

            // S33: pattern grant and an object created after approval -> the later object is covered.
            new(ParityScenarioId.S33_PatternGrantObjectCreatedLater, principal, tenant, GrantModelTable("orders_2027"),
                [PatternGrant(1, "default.main.orders_*", now)], [], VirtualFilterSnapshot.Empty, false, false,
                new(true, GrantIds: [GrantId(1)], RowSet: ParityRowSet.Unfiltered, ColumnState: Cols(ColumnAccessLevel.Mask)), plain),

            // S34: uncovered Deny vs Skip -> both grants are in the match set (the decision lists every matching grant), but only the Skip
            // grant shapes the row set: it becomes unfiltered, and the Deny grant's parameter never reaches the predicate.
            new(ParityScenarioId.S34_UncoveredDenyVersusSkip, principal, tenant, table,
                [
                    GroupGrant(1, GroupA, now, [SegmentEntry("gold", UncoveredPolicy.Deny)]),
                    GroupGrant(2, GroupB, now, [SegmentEntry("gold", UncoveredPolicy.Skip)])
                ], [], VirtualFilterSnapshot.Empty, false, false,
                new(true, GrantIds: [GrantId(1), GrantId(2)], RowSet: ParityRowSet.Unfiltered, ColumnState: Cols(ColumnAccessLevel.Mask),
                    PredicateParameterValues: []), plain),

            // S34b: every matching grant has UncoveredPolicy.Deny -> no grant contributes to the object -> denied with ROW_SCOPE_NO_CONTRIBUTING_GRANT and no row set; the table is unclassified (defaults to confidential) and the caller is not internal, so the protection gate also reports PROTECTION_CLASS_GRANT_REQUIRED.
            new(ParityScenarioId.S34b_UncoveredDenyOnly, principal, tenant, table,
                [
                    GroupGrant(1, GroupA, now, [SegmentEntry("gold", UncoveredPolicy.Deny)]),
                    GroupGrant(2, GroupB, now, [SegmentEntry("silver", UncoveredPolicy.Deny)])
                ], [], VirtualFilterSnapshot.Empty, false, false,
                new(false, GrantIds: [], ColumnState: AllCols(ColumnAccessLevel.Deny),
                    PredicateParameterValues: [],
                    ReasonCodes: ["PROTECTION_CLASS_GRANT_REQUIRED", RowScopeCompiler.NoContributingGrantCode]), plain),

            // S35: composite-key policy (both key columns are compared pair-wise).
            new(ParityScenarioId.S35_CompositeKeyPolicy, principal, tenant, table,
                [GroupGrant(1, GroupA, now, [new GrantRowScopeEntry("composite_live", new Dictionary<string, JsonElement>())])], [],
                VirtualFilterSnapshot.Empty, false, false,
                new(true, GrantIds: [GrantId(1)], RowSet: ParityRowSet.Filtered, ColumnState: Cols(ColumnAccessLevel.Mask),
                    PredicateSqlFragments: ["EXISTS (SELECT 1 FROM", "region", "id"]),
                new ParityGrantModelSetup([CompositeLivePolicy()], [])),

            // S36: cross-source KeySet policy (the key set is loaded from another data source).
            new(ParityScenarioId.S36_CrossSourceKeySet, principal, tenant, table,
                [GroupGrant(1, GroupA, now, [new GrantRowScopeEntry("keyset_cross", new Dictionary<string, JsonElement>())])], [],
                VirtualFilterSnapshot.Empty, false, false,
                new(true, GrantIds: [GrantId(1)], RowSet: ParityRowSet.Filtered, ColumnState: Cols(ColumnAccessLevel.Mask),
                    PredicateParameterValues: ["EU", "US"]),
                new ParityGrantModelSetup([KeySetPolicy()], [],
                    VirtualFilterKeySet.Create([VirtualFilterKeyType.Text], new object?[][] { ["EU"], ["US"] }))),

            // S37: the same policy with parameters from two grants -> one merged predicate.
            new(ParityScenarioId.S37_ParametersFromTwoGrantsMerged, principal, tenant, table,
                [GroupGrant(1, GroupA, now, [RegionEntry("EU", "UK")]), GroupGrant(2, GroupB, now, [RegionEntry("US", "UK")])], [],
                VirtualFilterSnapshot.Empty, false, false,
                new(true, GrantIds: [GrantId(1), GrantId(2)], RowSet: ParityRowSet.Filtered, ColumnState: Cols(ColumnAccessLevel.Mask), PredicateParameterValues: ["EU", "UK", "US"]), plain)
        ];
    }

    /// <summary>The decision of the decision point for a grant-model scenario, reduced to what the rows assert.</summary>
    internal sealed record GrantModelOutcome(
        bool Allowed,
        bool ExplainAllowed,
        IReadOnlyList<Guid> DecisionGrantIds,
        IReadOnlyList<Guid> ExplainGrantIds,
        ParityRowSet? RowSet,
        IReadOnlyDictionary<string, ColumnAccessLevel> ColumnState,
        IReadOnlyList<string> ParameterValues,
        string PredicateSql,
        IReadOnlyList<string> DecisionReasonCodes);

    internal static async Task<GrantModelOutcome> EvaluateAsync(ParityScenario scenario, CancellationToken ct)
    {
        var setup = scenario.GrantModel ?? throw new ArgumentException("The scenario has no grant model.", nameof(scenario));
        var compiler = new RowScopeCompiler(
            new FixedSnapshotProvider(new VirtualFilterSnapshot(1, setup.Policies, setup.Profiles)),
            new StructuredFilterSqlBuilder(),
            setup.KeySet is null ? null : new FixedKeySetProvider(setup.KeySet),
            new RowPolicyFactory(new PolicyPredicateRenderer(), new PolicyExpressionParser(FastSqlEngine.Default)));
        var pdp = new AccessDecisionPoint(
            Options.Create(new GatewayOptions()),
            new ConsentResolutionService(),
            consentRepository: new CrossTransportParityHarness.InMemoryConsentRepo(scenario.Consents),
            cacheService: NullConsentCacheService.Instance,
            rowScopeCompiler: compiler);

        var subject = SecurityPrincipalContext.FromPrincipal(scenario.Principal, scenario.EffectiveTenant);
        var request = new AccessRequest(subject, scenario.Table, AccessOperation.Read, AccessTransport.GraphQl);
        var decision = await pdp.DecideAsync(request, ct);
        var explanation = await pdp.ExplainAsync(request, ct);

        ParityRowSet? rowSet = !decision.IsAllowed
            ? null
            : decision.RowPolicy is null
                ? ParityRowSet.Unfiltered
                : decision.RowPolicy.Project().Sql.Trim() == "1 = 0" ? ParityRowSet.NoRows : ParityRowSet.Filtered;
        var parameters = decision.RowPolicy?.Project().Parameters.Values
            .Select(v => v?.ToString() ?? string.Empty).ToList() ?? [];
        var columns = scenario.Table.Columns
            .ToDictionary(
                c => c.ColumnName,
                c => !decision.IsAllowed ? ColumnAccessLevel.Deny
                    : decision.GetColumnAccess(c.ColumnName) == ColumnAccessLevel.RowDependent
                        ? ColumnAccessLevel.RowDependent
                        : decision.GetEffectiveColumnAccess(c.ColumnName, scenario.Table),
                StringComparer.OrdinalIgnoreCase);

        return new GrantModelOutcome(
            decision.IsAllowed,
            explanation.Allowed,
            [.. decision.Grants.Select(g => Guid.Parse(g.GrantId))],
            [.. explanation.Grants.Select(g => Guid.Parse(g.GrantId))],
            rowSet,
            columns,
            parameters,
            decision.RowPolicy?.Project().Sql ?? string.Empty,
            [.. decision.Reasons.Select(r => r.Code)]);
    }

    private static ClaimsPrincipal GroupUser(Sid sid, params Sid[] groups)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, sid.Value),
            new("sub", sid.Value),
            new("tenant_id", CrossTransportParityHarness.TenantA.Value),
            GroupResolutionClaims.CreateMarker(),
            GroupResolutionClaims.CreateStatus(GroupResolutionStatus.Complete)
        };
        claims.AddRange(groups.Select(g => new Claim(ClaimTypes.GroupSid, g.Value, ClaimValueTypes.String, IdentityClaimIssuers.Pipeline)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
    }

    private static TableMetadata GrantModelTable(string name) => new()
    {
        ColumnClassifications = TableMetadataClassificationTestExtensions.PiiFor("ssn"),
        Identifier = new TableIdentifier(GrantOrdersId.Domain, GrantOrdersId.Schema, name),
        Table = new Table { Id = Guid.NewGuid(), SourceName = "default", SchemaName = "main", TableName = name, SourceType = "PostgreSql", IsActive = true },
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "int" },
            new TableColumn { ColumnName = "region", DataType = "varchar" },
            new TableColumn { ColumnName = "ssn", DataType = "varchar" }
        ]
    };

    private static Consent GroupGrant(int n, Sid group, DateTimeOffset now, IReadOnlyList<GrantRowScopeEntry>? scope = null,
        IReadOnlyList<ConsentColumnRule>? columnRules = null) => new()
    {
        Id = GrantId(n),
        TableIdentifier = GrantOrdersId,
        TenantId = CrossTransportParityHarness.TenantA,
        GranteeType = GranteeType.Group,
        GranteeSid = group,
        Effect = ConsentEffect.Allow,
        ValidFrom = now.AddDays(-1),
        ValidTo = now.AddDays(30),
        RowScope = scope ?? [],
        ColumnRules = columnRules ?? []
    };

    private static Consent PatternGrant(int n, string pattern, DateTimeOffset now) => new()
    {
        Id = GrantId(n),
        TableId = Guid.Empty,
        ObjectPattern = pattern,
        DataSource = pattern.Split('.')[0],
        ColumnSetMode = ColumnSetMode.AllNonHidden,
        WorkflowMode = ConsentWorkflowMode.External,
        Effect = ConsentEffect.Allow,
        TenantId = CrossTransportParityHarness.TenantA,
        GranteeType = GranteeType.User,
        GranteeSid = CrossTransportParityHarness.AliceSid,
        ValidFrom = now.AddDays(-1),
        ValidTo = now.AddDays(30)
    };

    private static List<ConsentColumnRule> ColumnRules(ColumnAccessLevel ssn) =>
    [
        new() { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear },
        new() { ColumnName = "region", AccessLevel = ColumnAccessLevel.Clear },
        new() { ColumnName = "ssn", AccessLevel = ssn }
    ];

    private static GrantRowScopeEntry RegionEntry(params string[] regions) => new(
        "region_policy",
        new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase) { ["regions"] = JsonSerializer.SerializeToElement(regions) });

    private static GrantRowScopeEntry SegmentEntry(string segment, UncoveredPolicy uncovered) => new(
        "segment_policy",
        new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase) { ["segments"] = JsonSerializer.SerializeToElement(new[] { segment }) },
        Uncovered: uncovered);

    private static VirtualFilter RegionPolicy() => new()
    {
        TenantId = CrossTransportParityHarness.TenantA,
        Name = "region_policy",
        Source = "default",
        EvaluationMode = RowPolicyEvaluationMode.Live,
        KeyColumns = ["client.region"],
        Parameters = [new RowPolicyParameter("regions", RowPolicyParameterType.String, IsList: true, MaxValues: 10)],
        Structured = new StructuredFilterDefinition
        {
            From = new TableIdentifier("default", "conf", "client"),
            FromAlias = "client",
            Where = [new FilterCondition("client.region", FilterConditionOperator.Eq, ":regions")]
        }
    };

    // Keyed on a column the orders table does not have, so the entry is uncovered for it.
    private static VirtualFilter SegmentPolicy() => new()
    {
        TenantId = CrossTransportParityHarness.TenantA,
        Name = "segment_policy",
        Source = "default",
        EvaluationMode = RowPolicyEvaluationMode.Live,
        KeyColumns = ["client.segment"],
        Parameters = [new RowPolicyParameter("segments", RowPolicyParameterType.String, IsList: true, MaxValues: 10)],
        Structured = new StructuredFilterDefinition
        {
            From = new TableIdentifier("default", "conf", "client"),
            FromAlias = "client",
            Where = [new FilterCondition("client.segment", FilterConditionOperator.Eq, ":segments")]
        }
    };

    private static VirtualFilter ActiveOrgPolicy() => new()
    {
        TenantId = CrossTransportParityHarness.TenantA,
        Name = "hard_org_policy",
        Source = "default",
        EvaluationMode = RowPolicyEvaluationMode.Live,
        KeyColumns = ["org.id"],
        Structured = new StructuredFilterDefinition
        {
            From = new TableIdentifier("default", "conf", "org"),
            FromAlias = "org",
            Where = [new FilterCondition("org.status", FilterConditionOperator.Eq, "ACTIVE")]
        }
    };

    private static VirtualFilterAccessProfile HardProfile(Sid group) => new()
    {
        TenantId = CrossTransportParityHarness.TenantA,
        Name = "hard-group",
        GranteeType = GranteeType.Group,
        GranteeSid = group,
        Scope = "default.main.*",
        Uncovered = UncoveredPolicy.Deny,
        Bindings = [new FilterBinding { FilterName = "hard_org_policy" }]
    };

    private static VirtualFilter CompositeLivePolicy() => new()
    {
        TenantId = CrossTransportParityHarness.TenantA,
        Name = "composite_live",
        Source = "default",
        EvaluationMode = RowPolicyEvaluationMode.Live,
        KeyColumns = ["acl.region", "acl.id"],
        Structured = new StructuredFilterDefinition
        {
            From = new TableIdentifier("default", "auth", "acl"),
            FromAlias = "acl",
            Where = [new FilterCondition("acl.active", FilterConditionOperator.Eq, "Y")]
        }
    };

    private static VirtualFilter KeySetPolicy() => new()
    {
        TenantId = CrossTransportParityHarness.TenantA,
        Name = "keyset_cross",
        Source = "ref_src",
        EvaluationMode = RowPolicyEvaluationMode.KeySet,
        KeyColumns = ["acl.region"],
        Structured = new StructuredFilterDefinition
        {
            From = new TableIdentifier("ref_src", "auth", "acl"),
            FromAlias = "acl",
            Where = [new FilterCondition("acl.active", FilterConditionOperator.Eq, "Y")]
        }
    };

    private sealed class FixedSnapshotProvider(VirtualFilterSnapshot snapshot) : IVirtualFilterSnapshotProvider
    {
        public ValueTask<VirtualFilterSnapshot> GetAsync(CancellationToken ct = default) => ValueTask.FromResult(snapshot);
        public void Invalidate() { }
    }

    private sealed class FixedKeySetProvider(VirtualFilterKeySet keySet) : IVirtualFilterKeySetProvider
    {
        public ValueTask<VirtualFilterKeySet> GetAsync(
            VirtualFilter filter,
            IReadOnlyList<VirtualFilterKeyType> keyTypes,
            TenantId tenant,
            CancellationToken ct = default) => ValueTask.FromResult(keySet);
    }
}
