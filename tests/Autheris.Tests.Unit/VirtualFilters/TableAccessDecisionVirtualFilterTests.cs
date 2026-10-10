namespace Autheris.Tests.Unit.VirtualFilters;

using System.Collections.Generic;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

/// <summary>
/// F-GOV-14 WI-3 (M1, SEC-20): the access decision carries the applied (filter, binding) pairs and the row filters
/// around the Virtual Filter predicate, so the gateway can drop exactly the Virtual Filter predicate once it enforced
/// every applied pair itself. Any inconsistency denies with <c>VF_ENFORCEMENT_INCOMPLETE</c>.
/// </summary>
public sealed class TableAccessDecisionVirtualFilterTests
{
    private static readonly TableIdentifier Customers = new("crm_api", "public", "customers");

    private static readonly VirtualFilter FilterA = MandatoryRowFilterResolverTests.Filter("filter_a", "client_id");
    private static readonly VirtualFilter FilterB = MandatoryRowFilterResolverTests.Filter("filter_b", "client_id");

    private static readonly FilterBinding BindingA = new()
    {
        FilterName = "filter_a",
        TargetPattern = "crm_api.public.customers",
        ColumnMap = new Dictionary<string, string> { ["client_id"] = "id" }
    };

    private static readonly FilterBinding BindingB = new() { FilterName = "filter_b", TargetPattern = "crm_api.public.customers" };

    private static AppliedVirtualFilterBinding PairA(long generation = 1) => new(FilterA, BindingA, generation);

    private static AppliedVirtualFilterBinding PairB(long generation = 1) => new(FilterB, BindingB, generation);

    private static TableAccessDecision Allowed(string? rowFilter = null) =>
        TableAccessDecision.Allowed(Customers, new Dictionary<string, ColumnAccessLevel>(), rowFilter, hasUnconstrainedColumnAllow: true);

    private static TableAccessDecision WithVf(string? consentFilter = null, long generation = 1) =>
        Allowed(consentFilter).WithMandatoryPredicate("(P_a) AND (P_b)", ["filter_a", "filter_b"], [PairA(generation), PairB(generation)]);

    [Fact]
    public void WithMandatoryThenAdditional_CombinedTextUnchanged()
    {
        var decision = WithVf("region = 'EU'").WithAdditionalRowFilter("CASBIN_1").WithAdditionalRowFilter("CASBIN_2");

        // Previous formula: WithMandatoryPredicate "({c}) AND ({p})", then Restrict "({combined}) AND ({added})".
        decision.CombinedRowFilterSql.ShouldBe("(((region = 'EU') AND ((P_a) AND (P_b))) AND (CASBIN_1)) AND (CASBIN_2)");
        decision.MandatoryRowPredicateSql.ShouldBe("(P_a) AND (P_b)");
        decision.AppliedVirtualFilterBindings.ShouldNotBeNull().Count.ShouldBe(2);
    }

    [Fact]
    public void WithAdditionalRowFilter_WithoutVirtualFilters_ComposesLikeRestrict()
    {
        Allowed().WithAdditionalRowFilter("CASBIN").CombinedRowFilterSql.ShouldBe("CASBIN");
        Allowed("a = 1").WithAdditionalRowFilter("CASBIN").CombinedRowFilterSql.ShouldBe("(a = 1) AND (CASBIN)");
    }

    [Fact]
    public void WithoutEnforced_AllPairs_ReturnsResidualWithBeforeAndAfterFilters()
    {
        var decision = WithVf("region = 'EU'").WithAdditionalRowFilter("CASBIN");

        var residual = decision.WithoutEnforcedVirtualFilters([PairB(), PairA()]);

        residual.CombinedRowFilterSql.ShouldBe("(region = 'EU') AND (CASBIN)");
        residual.MandatoryRowPredicateSql.ShouldBeNull();
        residual.AppliedVirtualFilters.ShouldBeEmpty();
        residual.AppliedVirtualFilterBindings.ShouldBeEmpty();
        residual.IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public void WithoutEnforced_OnlyVirtualFilter_ResidualHasNoRowFilter()
    {
        var residual = WithVf().WithoutEnforcedVirtualFilters([PairA(), PairB()]);

        residual.CombinedRowFilterSql.ShouldBeNull();
        residual.MandatoryRowPredicateSql.ShouldBeNull();
    }

    [Fact]
    public void WithoutEnforced_MissingPair_ThrowsEnforcementIncomplete()
    {
        var ex = Should.Throw<GatewaySecurityException>(() => WithVf().WithoutEnforcedVirtualFilters([PairA()]));

        ex.ErrorCode.ShouldBe("VF_ENFORCEMENT_INCOMPLETE");
    }

    [Fact]
    public void WithoutEnforced_ExtraPair_ThrowsEnforcementIncomplete()
    {
        var decision = Allowed().WithMandatoryPredicate("P_a", ["filter_a"], [PairA()]);

        var ex = Should.Throw<GatewaySecurityException>(() => decision.WithoutEnforcedVirtualFilters([PairA(), PairB()]));

        ex.ErrorCode.ShouldBe("VF_ENFORCEMENT_INCOMPLETE");
    }

    [Fact]
    public void WithoutEnforced_EnforcementFromOtherSnapshotGeneration_ThrowsEnforcementIncomplete()
    {
        // ST-11 (SEC-20): pair identity includes the snapshot generation.
        var ex = Should.Throw<GatewaySecurityException>(() => WithVf(generation: 1).WithoutEnforcedVirtualFilters([PairA(2), PairB(2)]));

        ex.ErrorCode.ShouldBe("VF_ENFORCEMENT_INCOMPLETE");
    }

    [Fact]
    public void WithoutEnforced_ChangedDefinitionOrBinding_ThrowsEnforcementIncomplete()
    {
        var changedFilter = new AppliedVirtualFilterBinding(FilterA with { KeyColumns = ["client.other"] }, BindingA, 1);
        var changedBinding = new AppliedVirtualFilterBinding(FilterB, BindingB with { MaxPushdownKeys = 7 }, 1);

        Should.Throw<GatewaySecurityException>(() => WithVf().WithoutEnforcedVirtualFilters([changedFilter, PairB()]))
            .ErrorCode.ShouldBe("VF_ENFORCEMENT_INCOMPLETE");
        Should.Throw<GatewaySecurityException>(() => WithVf().WithoutEnforcedVirtualFilters([PairA(), changedBinding]))
            .ErrorCode.ShouldBe("VF_ENFORCEMENT_INCOMPLETE");
    }

    [Fact]
    public void WithoutEnforced_EqualPairFromOtherProfileInstance_IsAccepted()
    {
        // SEC-20: identity by value (tenant, filter id, definition hash, binding hash, generation), not by reference.
        var copyOfBinding = BindingA with { ColumnMap = new Dictionary<string, string> { ["client_id"] = "id" } };
        var decision = Allowed().WithMandatoryPredicate("P_a", ["filter_a"], [PairA()]);

        var residual = decision.WithoutEnforcedVirtualFilters([new AppliedVirtualFilterBinding(FilterA, copyOfBinding, 1)]);

        residual.MandatoryRowPredicateSql.ShouldBeNull();
    }

    [Fact]
    public void WithoutEnforced_CombinedMutatedDirectly_ThrowsEnforcementIncomplete()
    {
        // A code path that bypasses WithAdditionalRowFilter cannot make the gateway drop a filter silently.
        var tampered = WithVf("region = 'EU'") with { CombinedRowFilterSql = "((region = 'EU') AND ((P_a) AND (P_b))) AND (STREAM_ABAC)" };

        var ex = Should.Throw<GatewaySecurityException>(() => tampered.WithoutEnforcedVirtualFilters([PairA(), PairB()]));

        ex.ErrorCode.ShouldBe("VF_ENFORCEMENT_INCOMPLETE");
    }

    [Fact]
    public void WithoutEnforced_PredicateWithoutBindings_ThrowsEnforcementIncomplete()
    {
        // A decision built without the applied pairs cannot prove what the enforcement covers.
        var decision = Allowed().WithMandatoryPredicate("P_a", ["filter_a"]);

        Should.Throw<GatewaySecurityException>(() => decision.WithoutEnforcedVirtualFilters([PairA()]))
            .ErrorCode.ShouldBe("VF_ENFORCEMENT_INCOMPLETE");
    }

    [Fact]
    public void WithoutEnforced_NoVirtualFilters_ReturnsSameDecision()
    {
        var decision = Allowed("region = 'EU'").WithAdditionalRowFilter("CASBIN");

        decision.WithoutEnforcedVirtualFilters([]).ShouldBeSameAs(decision);
    }

    [Fact]
    public void WithoutEnforced_NoVirtualFiltersButEnforcement_ThrowsEnforcementIncomplete()
    {
        Should.Throw<GatewaySecurityException>(() => Allowed().WithoutEnforcedVirtualFilters([PairA()]))
            .ErrorCode.ShouldBe("VF_ENFORCEMENT_INCOMPLETE");
    }

    [Fact]
    public void WithMandatoryPredicate_Twice_Throws()
    {
        Should.Throw<System.InvalidOperationException>(() => WithVf().WithMandatoryPredicate("P_c", ["filter_c"], [PairA()]));
    }

    [Fact]
    public void FilterBindingHash_IsStableAndValueBased()
    {
        var copy = BindingA with { ColumnMap = new Dictionary<string, string> { ["client_id"] = "id" } };

        copy.ComputeHash().ShouldBe(BindingA.ComputeHash());
        (BindingA with { Strategy = VirtualFilterExecutionStrategy.PushdownOnly }).ComputeHash().ShouldNotBe(BindingA.ComputeHash());
        PairA().ShouldBe(new AppliedVirtualFilterBinding(FilterA, copy, 1));
        PairA().ShouldNotBe(PairA(2));
    }
}
