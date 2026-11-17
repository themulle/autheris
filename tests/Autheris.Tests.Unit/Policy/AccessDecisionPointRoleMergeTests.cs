namespace Autheris.Tests.Unit.Policy;

using System;
using System.Collections.Generic;
using Autheris.Application.Policy.Pdp;
using Shouldly;
using Xunit;

public sealed class AccessDecisionPointRoleMergeTests
{
    [Fact]
    public void Merge_is_a_case_insensitive_union_of_both_role_sets()
    {
        var tenant = new HashSet<string>(StringComparer.Ordinal) { "Reader", "Analyst" };
        var cluster = new HashSet<string>(StringComparer.Ordinal) { "reader", "ClusterAdmin" };

        var merged = AccessDecisionPoint.MergeRoles(tenant, cluster);

        merged.Count.ShouldBe(3);
        merged.Contains("READER").ShouldBeTrue();
        merged.Contains("analyst").ShouldBeTrue();
        merged.Contains("clusteradmin").ShouldBeTrue();
        merged.Contains("other").ShouldBeFalse();
    }

    [Fact]
    public void Merge_of_two_empty_sets_is_a_shared_empty_set()
    {
        var empty = new HashSet<string>();

        var first = AccessDecisionPoint.MergeRoles(empty, empty);
        var second = AccessDecisionPoint.MergeRoles(empty, empty);

        first.Count.ShouldBe(0);
        first.Contains("anything").ShouldBeFalse();
        second.ShouldBeSameAs(first);
    }

    [Fact]
    public void Merge_does_not_alias_the_input_sets()
    {
        var tenant = new HashSet<string> { "Reader" };
        var merged = AccessDecisionPoint.MergeRoles(tenant, new HashSet<string>());

        tenant.Add("Late");

        merged.Contains("Late").ShouldBeFalse();
    }
}
