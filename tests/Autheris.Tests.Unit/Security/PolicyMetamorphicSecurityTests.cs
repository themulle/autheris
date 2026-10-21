namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Analytics;
using Autheris.Application.Governance.Policies;
using Autheris.Application.Governance.Rights;
using Autheris.Application.Identity;
using Autheris.Domain.Common;
using Autheris.Domain.Governance;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Metamorphic security testing for policies, access rules, rights mapping, and FinOps costs.
/// Validates mathematical invariants: Monotonicity on privilege drop, Group order commutativity,
/// Claim type isomorphism, Customer realm ceiling invariance, and FinOps cost monotonicity.
/// </summary>
[Trait("Category", "Security")]
[Trait("Category", "Metamorphic")]
public sealed class PolicyMetamorphicSecurityTests
{
    private static readonly TenantId TestTenant = new("tenant-metamorphic");
    private const string TestRealm = "workforce-entra";

    #region 1. Scoped Access Policy Engine Metamorphic Invariants

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task M_POL_01_GroupOrderCommutativity_AnyPermutation_YieldsIdenticalDecision(int seed)
    {
        // Axiom: Evaluating a policy with groups [G1, G2, ..., Gn] must yield the exact same decision
        // regardless of the order in which groups appear in the caller's ClaimsPrincipal.
        var rng = new Random(seed * 42);
        var repository = Substitute.For<IAccessPolicyRepository>();
        var epochService = Substitute.For<IPolicyEpochService>();
        var sut = new ScopedAccessPolicyEngine(repository, epochService);

        var groups = new[] { "sales-dept", "marketing-dept", "finance-dept", "audit-team", "executive" };
        var table = TableIdentifier.Parse("finance.general_ledger");

        // Define a set of policies
        var permitPolicy = new AccessPolicy
        {
            TenantId = TestTenant.Value,
            PolicyId = "pol-permit-finance",
            Priority = 100,
            Decision = AccessPolicyDecision.Permit,
            Grantees = [new AccessPolicyGrantee(TestTenant.Value, "pol-permit-finance", TestRealm, AccessPolicyGranteeType.Group, "finance-dept")],
            Selectors = [new AccessPolicySelector(TestTenant.Value, "pol-permit-finance", AccessPolicySelectorType.TablePattern, "finance.*")]
        };

        var denyPolicy = new AccessPolicy
        {
            TenantId = TestTenant.Value,
            PolicyId = "pol-deny-sales",
            Priority = 50,
            Decision = AccessPolicyDecision.Deny,
            Grantees = [new AccessPolicyGrantee(TestTenant.Value, "pol-deny-sales", TestRealm, AccessPolicyGranteeType.Group, "sales-dept")],
            Selectors = [new AccessPolicySelector(TestTenant.Value, "pol-deny-sales", AccessPolicySelectorType.TablePattern, "finance.*")]
        };

        repository.GetActivePoliciesForGranteesAsync(
            TestTenant, TestRealm, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var inputGrps = callInfo.ArgAt<IReadOnlyCollection<string>>(2);
                var active = new List<AccessPolicy>();
                if (inputGrps.Contains("finance-dept")) active.Add(permitPolicy);
                if (inputGrps.Contains("sales-dept")) active.Add(denyPolicy);
                return active;
            });

        // Base evaluation with standard order
        var baseClaims = groups.Select(g => new Claim(ClaimTypes.GroupSid, g)).ToList();
        baseClaims.Add(new Claim(ClaimTypes.PrimarySid, "user-meta"));
        var baseContext = new PolicyEvaluationContext(
            new ClaimsPrincipal(new ClaimsIdentity(baseClaims, "TestAuth")),
            TestTenant, TestRealm, table, new HashSet<string>());

        var baseDecision = await sut.EvaluateAsync(baseContext);

        // Generate 10 random permutations of the groups
        for (int i = 0; i < 10; i++)
        {
            var permutedGroups = groups.OrderBy(_ => rng.Next()).ToList();
            var permutedClaims = permutedGroups.Select(g => new Claim(ClaimTypes.GroupSid, g)).ToList();
            permutedClaims.Add(new Claim(ClaimTypes.PrimarySid, "user-meta"));

            var permutedContext = new PolicyEvaluationContext(
                new ClaimsPrincipal(new ClaimsIdentity(permutedClaims, "TestAuth")),
                TestTenant, TestRealm, table, new HashSet<string>());

            var permutedDecision = await sut.EvaluateAsync(permutedContext);

            // Invariance check
            permutedDecision.Decision.ShouldBe(baseDecision.Decision,
                $"Order of groups [{string.Join(",", permutedGroups)}] flipped decision from {baseDecision.Decision} to {permutedDecision.Decision}");
            permutedDecision.MatchingPolicy?.PolicyId.ShouldBe(baseDecision.MatchingPolicy?.PolicyId);
        }
    }

    [Fact]
    public async Task M_POL_02_PrivilegeDropMonotonicity_RemovingGroups_NeverGrantsAccess()
    {
        // Axiom: If evaluation for groups G results in DENY, then for ANY sub-set of groups G' ⊆ G,
        // evaluation must NEVER turn into PERMIT (privilege monotonicity).
        var repository = Substitute.For<IAccessPolicyRepository>();
        var epochService = Substitute.For<IPolicyEpochService>();
        var sut = new ScopedAccessPolicyEngine(repository, epochService);

        var table = TableIdentifier.Parse("hr.salaries");

        // The only policy is a Permit policy that requires "hr-admins"
        var permitPolicy = new AccessPolicy
        {
            TenantId = TestTenant.Value,
            PolicyId = "pol-hr-admin",
            Priority = 100,
            Decision = AccessPolicyDecision.Permit,
            Grantees = [new AccessPolicyGrantee(TestTenant.Value, "pol-hr-admin", TestRealm, AccessPolicyGranteeType.Group, "hr-admins")],
            Selectors = [new AccessPolicySelector(TestTenant.Value, "pol-hr-admin", AccessPolicySelectorType.TablePattern, "hr.*")]
        };

        repository.GetActivePoliciesForGranteesAsync(
            TestTenant, TestRealm, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var inputGrps = callInfo.ArgAt<IReadOnlyCollection<string>>(2);
                return inputGrps.Contains("hr-admins") ? [permitPolicy] : [];
            });

        // User does NOT have "hr-admins", but has other groups
        var groups = new List<string> { "contractors", "interns", "sales", "analytics" };

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            groups.Select(g => new Claim(ClaimTypes.GroupSid, g))
                  .Concat([new Claim(ClaimTypes.PrimarySid, "user-drop")]),
            "TestAuth"));

        var baseContext = new PolicyEvaluationContext(principal, TestTenant, TestRealm, table, new HashSet<string>());
        var baseDecision = await sut.EvaluateAsync(baseContext);

        // Base must be Deny
        baseDecision.Decision.ShouldBe(AccessPolicyDecision.Deny);

        // Subsets: Drop groups one by one and test every combination
        for (int i = 1; i < (1 << groups.Count); i++)
        {
            var subset = groups.Where((_, idx) => (i & (1 << idx)) != 0).ToList();
            var subPrincipal = new ClaimsPrincipal(new ClaimsIdentity(
                subset.Select(g => new Claim(ClaimTypes.GroupSid, g))
                      .Concat([new Claim(ClaimTypes.PrimarySid, "user-drop")]),
                "TestAuth"));

            var subContext = new PolicyEvaluationContext(subPrincipal, TestTenant, TestRealm, table, new HashSet<string>());
            var subDecision = await sut.EvaluateAsync(subContext);

            // Metamorphic Assertion: Dropping privileges must NEVER escalate to Permit
            subDecision.Decision.ShouldBe(AccessPolicyDecision.Deny,
                $"Dropping groups to [{string.Join(",", subset)}] illegally escalated to Permit!");
        }
    }

    [Fact]
    public async Task M_POL_03_EqualPriorityTieBreaking_RepositoryOrderIndependent_AlwaysDenies()
    {
        // Axiom: When conflicting policies (Permit vs Deny) share the same priority,
        // the engine must ALWAYS resolve to Deny (Fail-Closed), regardless of the order
        // in which the persistence repository yields them.
        var repository = Substitute.For<IAccessPolicyRepository>();
        var epochService = Substitute.For<IPolicyEpochService>();
        var sut = new ScopedAccessPolicyEngine(repository, epochService);

        var table = TableIdentifier.Parse("core.customers");
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.PrimarySid, "u1"), new Claim(ClaimTypes.GroupSid, "grp-tie")], "TestAuth"));

        var permitPolicy = new AccessPolicy
        {
            TenantId = TestTenant.Value,
            PolicyId = "pol-permit-tie",
            Priority = 200, // Identical priority
            Decision = AccessPolicyDecision.Permit,
            Grantees = [new AccessPolicyGrantee(TestTenant.Value, "pol-permit-tie", TestRealm, AccessPolicyGranteeType.Group, "grp-tie")],
            Selectors = [new AccessPolicySelector(TestTenant.Value, "pol-permit-tie", AccessPolicySelectorType.TablePattern, "*")]
        };

        var denyPolicy = new AccessPolicy
        {
            TenantId = TestTenant.Value,
            PolicyId = "pol-deny-tie",
            Priority = 200, // Identical priority
            Decision = AccessPolicyDecision.Deny,
            Grantees = [new AccessPolicyGrantee(TestTenant.Value, "pol-deny-tie", TestRealm, AccessPolicyGranteeType.Group, "grp-tie")],
            Selectors = [new AccessPolicySelector(TestTenant.Value, "pol-deny-tie", AccessPolicySelectorType.TablePattern, "*")]
        };

        // Variant 1: Repository returns [Permit, Deny]
        repository.GetActivePoliciesForGranteesAsync(
            TestTenant, TestRealm, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns([permitPolicy, denyPolicy]);

        var evalContext1 = new PolicyEvaluationContext(principal, TestTenant, TestRealm, table, new HashSet<string>());
        var decision1 = await sut.EvaluateAsync(evalContext1);
        decision1.Decision.ShouldBe(AccessPolicyDecision.Deny);
        decision1.MatchingPolicy?.PolicyId.ShouldBe("pol-deny-tie");

        // Variant 2 (mutated order): Repository returns [Deny, Permit]
        repository.GetActivePoliciesForGranteesAsync(
            TestTenant, TestRealm, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns([denyPolicy, permitPolicy]);

        var evalContext2 = new PolicyEvaluationContext(principal, TestTenant, TestRealm, table, new HashSet<string>());
        var decision2 = await sut.EvaluateAsync(evalContext2);
        decision2.Decision.ShouldBe(AccessPolicyDecision.Deny);
        decision2.MatchingPolicy?.PolicyId.ShouldBe("pol-deny-tie");
    }

    #endregion

    #region 2. Functional Right Mapper Metamorphic Invariants

    [Theory]
    [InlineData("group")]
    [InlineData("groups")]
    [InlineData("roles")]
    [InlineData(ClaimTypes.Role)]
    [InlineData(ClaimTypes.GroupSid)]
    public async Task M_RIGHT_01_ClaimTypeIsomorphism_AnyClaimType_YieldsIdenticalMappedRights(string claimType)
    {
        // Axiom: Mapping a group/role name 'sec-operators' must yield bit-identical FunctionalRights,
        // regardless of which claim type conveys the identifier ('group', 'groups', 'roles', Role, GroupSid).
        var repository = Substitute.For<IFunctionalRightRepository>();
        var realmRegistry = Substitute.For<IIdentityRealmRegistry>();
        var sut = new FunctionalRightMapper(repository, realmRegistry);

        const string groupName = "sec-operators";
        var expectedRight = FunctionalRight.DataExport;

        var mapping = new FunctionalRightMapping(
            TestTenant.Value, TestRealm, groupName, expectedRight,
            ApprovedBy: "approver", ApprovedAt: DateTimeOffset.UtcNow,
            RecertClass: RecertClass.Standard.ToString(), CreatedBy: "admin",
            CreatedAt: DateTimeOffset.UtcNow);

        repository.GetApprovedMappingsForGroupsAndRolesAsync(
            TestTenant, TestRealm, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var input = callInfo.ArgAt<IReadOnlyCollection<string>>(2);
                return Task.FromResult<IReadOnlyList<FunctionalRightMapping>>(input.Contains(groupName) ? [mapping] : []);
            });

        var claims = new[]
        {
            new Claim(ClaimTypes.PrimarySid, "user-iso"),
            new Claim(claimType, groupName)
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));

        var rights = await sut.GetRightsForCallerAsync(principal, TestTenant, TestRealm);

        // Metamorphic assertion
        rights.ShouldContain(expectedRight,
            $"Claim type '{claimType}' failed to resolve mapped functional right '{expectedRight.Value}'");
    }

    [Theory]
    [InlineData(IdentityRealmKind.EntraExternalId)]
    [InlineData(IdentityRealmKind.AzureAdB2C)]
    [InlineData(IdentityRealmKind.Customer)]
    public async Task M_RIGHT_02_CustomerRealmCeilingInvariance_AdminRights_AlwaysFilteredFailClosed(IdentityRealmKind customerKind)
    {
        // Axiom: For any customer realm, even if the database contains approved mappings for
        // TenantAdmin, ClusterAdmin, GovernanceAdmin or Privileged rights, GetRightsForCallerAsync
        // must strictly filter them out fail-closed.
        var repository = Substitute.For<IFunctionalRightRepository>();
        var realmRegistry = Substitute.For<IIdentityRealmRegistry>();
        var sut = new FunctionalRightMapper(repository, realmRegistry);

        var customerRealm = new IdentityRealm(
            "ext-customer", customerKind,
            new TenantBindingSpecification(TenantBindingKind.Fixed, TestTenant.Value),
            RealmGroupSource.Resolver, RealmProvisioningMode.None,
            issuers: ["https://customer.auth.com"]);

        realmRegistry.GetRealm("ext-customer").Returns(customerRealm);

        // Poisoned database mappings attempt to give a customer group admin rights
        var adminMapping = new FunctionalRightMapping(
            TestTenant.Value, "ext-customer", "ext-users", FunctionalRight.TenantAdmin,
            ApprovedBy: "rogue-admin", ApprovedAt: DateTimeOffset.UtcNow,
            RecertClass: RecertClass.Privileged.ToString(), CreatedBy: "rogue-admin",
            CreatedAt: DateTimeOffset.UtcNow);

        var clusterMapping = new FunctionalRightMapping(
            TestTenant.Value, "ext-customer", "ext-users", FunctionalRight.ClusterAdmin,
            ApprovedBy: "rogue-admin", ApprovedAt: DateTimeOffset.UtcNow,
            RecertClass: RecertClass.Privileged.ToString(), CreatedBy: "rogue-admin",
            CreatedAt: DateTimeOffset.UtcNow);

        var standardMapping = new FunctionalRightMapping(
            TestTenant.Value, "ext-customer", "ext-users", FunctionalRight.Consumer,
            ApprovedBy: "admin", ApprovedAt: DateTimeOffset.UtcNow,
            RecertClass: RecertClass.Standard.ToString(), CreatedBy: "admin",
            CreatedAt: DateTimeOffset.UtcNow);

        repository.GetApprovedMappingsForGroupsAndRolesAsync(
            TestTenant, "ext-customer", Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<FunctionalRightMapping>>([adminMapping, clusterMapping, standardMapping]));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.PrimarySid, "cust-user"), new Claim(ClaimTypes.GroupSid, "ext-users")], "TestAuth"));

        var rights = await sut.GetRightsForCallerAsync(principal, TestTenant, "ext-customer");

        // Allowed standard right is present
        rights.ShouldContain(FunctionalRight.Consumer);

        // Metamorphic Security Invariance: Administrative rights MUST NEVER leak into customer realms
        rights.ShouldNotContain(FunctionalRight.TenantAdmin);
        rights.ShouldNotContain(FunctionalRight.ClusterAdmin);
        rights.ShouldNotContain(FunctionalRight.GovernanceAdmin);
        rights.ShouldNotContain(FunctionalRight.GovernanceApprove);
    }

    #endregion

    #region 3. FinOps Aggregation Cost Calculator Metamorphic Invariants

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 6)]
    public void M_FINOPS_01_CostMonotonicity_AddingDimensions_StrictlyIncreasesCost(int baseDims, int addedDims)
    {
        // Axiom: For any aggregation query Q, adding one or more GroupBy dimensions
        // must strictly increase the calculated complexity cost: Cost(Q + D) > Cost(Q).
        var table = TableIdentifier.Parse("core.orders");

        var baseQuery = new AggregationQuery(
            Table: table,
            GroupBy: Enumerable.Range(1, baseDims).Select(i => $"dim_{i}").ToList(),
            Aggregates: [new AggregateSelection(AggregateFunction.Sum, "amount")]);

        var extendedQuery = new AggregationQuery(
            Table: table,
            GroupBy: Enumerable.Range(1, baseDims + addedDims).Select(i => $"dim_{i}").ToList(),
            Aggregates: [new AggregateSelection(AggregateFunction.Sum, "amount")]);

        double baseCost = AggregationCostCalculator.CalculateCost(baseQuery);
        double extendedCost = AggregationCostCalculator.CalculateCost(extendedQuery);

        // Metamorphic Monotonicity assertion
        extendedCost.ShouldBeGreaterThan(baseCost,
            $"Adding {addedDims} dimensions did not strictly increase cost! (base={baseCost}, extended={extendedCost})");

        // Specific cost step verification: Each GroupBy dimension has exact weight 10.0
        (extendedCost - baseCost).ShouldBe(addedDims * AggregationCostCalculator.GroupByColumnWeight);
    }

    [Fact]
    public void M_FINOPS_02_CostMonotonicity_CountDistinct_CostlierThanStandardAggregation()
    {
        // Axiom: CountDistinct involves hyperloglog or hash sets and must be strictly
        // costlier than standard selection/sum aggregations.
        var table = TableIdentifier.Parse("core.analytics");

        var sumQuery = new AggregationQuery(
            Table: table,
            GroupBy: ["country"],
            Aggregates: [new AggregateSelection(AggregateFunction.Sum, "revenue")]);

        var countDistinctQuery = new AggregationQuery(
            Table: table,
            GroupBy: ["country"],
            Aggregates: [new AggregateSelection(AggregateFunction.CountDistinct, "user_id")]);

        double sumCost = AggregationCostCalculator.CalculateCost(sumQuery);
        double countDistinctCost = AggregationCostCalculator.CalculateCost(countDistinctQuery);

        // Metamorphic assertion
        countDistinctCost.ShouldBeGreaterThan(sumCost);
        (countDistinctCost - sumCost).ShouldBe(
            AggregationCostCalculator.CountDistinctWeight - AggregationCostCalculator.AggregateSelectionWeight);
    }

    [Fact]
    public void M_FINOPS_03_CostSubAdditivity_UnionQuery_CostNeverExceedsSumOfParts()
    {
        // Axiom: In query complexity analysis, the cost of evaluating two independent sub-queries
        // together in one combined query must satisfy sub-additivity: Cost(Q1 ∪ Q2) <= Cost(Q1) + Cost(Q2)
        // (shared overhead is amortized).
        var table = TableIdentifier.Parse("core.events");

        var q1 = new AggregationQuery(
            Table: table,
            GroupBy: ["region"],
            Aggregates: [new AggregateSelection(AggregateFunction.Sum, "bytes")]);

        var q2 = new AggregationQuery(
            Table: table,
            GroupBy: ["device"],
            Aggregates: [new AggregateSelection(AggregateFunction.Avg, "duration")]);

        var combined = new AggregationQuery(
            Table: table,
            GroupBy: ["region", "device"],
            Aggregates:
            [
                new AggregateSelection(AggregateFunction.Sum, "bytes"),
                new AggregateSelection(AggregateFunction.Avg, "duration")
            ]);

        double cost1 = AggregationCostCalculator.CalculateCost(q1);
        double cost2 = AggregationCostCalculator.CalculateCost(q2);
        double combinedCost = AggregationCostCalculator.CalculateCost(combined);

        // Sub-additivity
        combinedCost.ShouldBeLessThanOrEqualTo(cost1 + cost2);
    }

    #endregion
}
