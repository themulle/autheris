using System.Collections.Generic;
using System.Security;
using Autheris.Application.Services;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Policy;

/// <summary>
/// WP-X0l: the two legacy consumers that cannot bind parameters (the in-memory row filter and the dataset query splice) read the
/// marker text of a typed policy through this inliner, until X0n and X1 remove them.
/// </summary>
public sealed class PolicyMarkerInlinerTests
{
    private static Dictionary<string, object?> Params(params (string Name, object? Value)[] values) =>
        values.ToDictionary(v => v.Name, v => v.Value, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void StringMarker_BecomesAQuotedLiteral()
    {
        PolicyMarkerInliner.Inline("\"region\" = @__gql_rls_0", Params(("@__gql_rls_0", "EU"))).ShouldBe("\"region\" = 'EU'");
    }

    [Fact]
    public void QuotesInAValue_AreDoubled()
    {
        PolicyMarkerInliner.Inline("x = @__gql_rls_0", Params(("@__gql_rls_0", "O'Brien' OR '1'='1"))).ShouldBe("x = 'O''Brien'' OR ''1''=''1'");
    }

    [Fact]
    public void NumbersAndBooleans_AreInvariantLiterals()
    {
        var sql = "a = @__gql_rls_0 AND b = @__gql_rls_1 AND c = @__gql_rls_2 AND d = @__gql_rls_3";

        PolicyMarkerInliner.Inline(sql, Params(("@__gql_rls_0", 42L), ("@__gql_rls_1", 1.5m), ("@__gql_rls_2", true), ("@__gql_rls_3", 2.25d)))
            .ShouldBe("a = 42 AND b = 1.5 AND c = true AND d = 2.25");
    }

    [Fact]
    public void InList_ReplacesEveryMarker()
    {
        PolicyMarkerInliner.Inline("x IN (@__gql_rls_0, @__gql_rls_1)", Params(("@__gql_rls_0", "a"), ("@__gql_rls_1", "b")))
            .ShouldBe("x IN ('a', 'b')");
    }

    [Fact]
    public void AMarkerLikeTextInsideALiteralOrAnIdentifier_IsNotTouched()
    {
        PolicyMarkerInliner.Inline("x = 'mail@__gql_rls_0.test' AND \"@__gql_rls_0\" = @__gql_rls_0", Params(("@__gql_rls_0", "v")))
            .ShouldBe("x = 'mail@__gql_rls_0.test' AND \"@__gql_rls_0\" = 'v'");
    }

    [Fact]
    public void ParameterKeys_AreMatchedWithOrWithoutThePrefix()
    {
        PolicyMarkerInliner.Inline("x = @__gql_rls_0", Params(("__gql_rls_0", "v"))).ShouldBe("x = 'v'");
        PolicyMarkerInliner.Inline("x = @__gql_rls_0", Params((":__gql_rls_0", "v"))).ShouldBe("x = 'v'");
    }

    [Fact]
    public void TextWithoutMarkers_IsReturnedUnchanged()
    {
        PolicyMarkerInliner.Inline("region = 'EU'", null).ShouldBe("region = 'EU'");
        PolicyMarkerInliner.Inline("region = 'EU'", Params(("@__gql_rls_0", "x"))).ShouldBe("region = 'EU'");
    }

    [Fact]
    public void UnboundMarker_FailsClosed()
    {
        Should.Throw<SecurityException>(() => PolicyMarkerInliner.Inline("x = @__gql_rls_9", Params(("@__gql_rls_0", "v"))));
        Should.Throw<SecurityException>(() => PolicyMarkerInliner.Inline("x = @__gql_rls_0", null));
    }

    [Fact]
    public void UnsupportedValue_FailsClosed()
    {
        Should.Throw<SecurityException>(() => PolicyMarkerInliner.Inline("x = @__gql_rls_0", Params(("@__gql_rls_0", new byte[] { 1 }))));
        Should.Throw<SecurityException>(() => PolicyMarkerInliner.Inline("x = @__gql_rls_0", Params(("@__gql_rls_0", null))));
        Should.Throw<SecurityException>(() => PolicyMarkerInliner.Inline("x = @__gql_rls_0", Params(("@__gql_rls_0", double.NaN))));
    }

    [Fact]
    public void OnlyPolicyMarkers_AreInlined_OtherAtSignsStayAndFailClosed()
    {
        // A foreign @name is not a policy marker: it is left alone for the consumer to reject.
        PolicyMarkerInliner.Inline("x = @other", Params(("@__gql_rls_0", "v"))).ShouldBe("x = @other");
    }

    [Fact]
    public void VirtualFilterAndMaskMarkers_AreInlinedToo()
    {
        PolicyMarkerInliner.Inline("x = @__gql_vf_0_1", Params(("@__gql_vf_0_1", 7L))).ShouldBe("x = 7");
    }

    [Fact]
    public void ParserLiteralMarkers_AreInlined() =>
        PolicyMarkerInliner.Inline("x = @__pl_ab12cd34_0", Params(("@__pl_ab12cd34_0", "v"))).ShouldBe("x = 'v'");
}
