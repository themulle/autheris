namespace Autheris.Tests.Unit.Streaming;

using System.Collections.Generic;
using Autheris.Application.Streaming.Services;
using Autheris.Domain.Common;
using Shouldly;
using Xunit;

public sealed class StreamingRowFilterCollationTests
{
    [Fact]
    public void NotEquals_CaseVariant_RowDropped()
    {
        // NOT (dept = 'HR') when dept = 'hr'
        // Ordinal comparison: 'hr' != 'HR' (true -> NOT false)
        // Collation comparison: 'hr' == 'HR' (false -> NOT true)
        // Disagreement yields UNKNOWN (null) which SQL/Matches treats as fail-closed (false).
        var payload = new Dictionary<string, object?> { ["dept"] = "hr" };
        var matches = StreamingRowFilterAstEvaluator.Matches(payload, "NOT (dept = 'HR')", DatabaseDialect.PostgreSql);

        matches.ShouldBeFalse();
    }

    [Fact]
    public void NotEquals_TrailingSpace_RowDropped()
    {
        // dept <> 'HR' when dept = 'HR '
        // Ordinal comparison: 'HR ' <> 'HR' is true
        // Collation comparison: 'HR '.TrimEnd() <> 'HR'.TrimEnd() is false
        // Disagreement yields UNKNOWN (null) -> dropped
        var payload = new Dictionary<string, object?> { ["dept"] = "HR " };
        var matches = StreamingRowFilterAstEvaluator.Matches(payload, "dept <> 'HR'", DatabaseDialect.SqlServer);

        matches.ShouldBeFalse();
    }

    [Fact]
    public void Like_CaseVariant_RowDropped()
    {
        // region LIKE 'EU%' when region = 'eu-internal'
        // Case-sensitive LIKE is false, case-insensitive LIKE is true
        // Disagreement yields UNKNOWN (null) -> dropped
        var payload = new Dictionary<string, object?> { ["region"] = "eu-internal" };
        var matches = StreamingRowFilterAstEvaluator.Matches(payload, "region LIKE 'EU%'", DatabaseDialect.PostgreSql);

        matches.ShouldBeFalse();
    }

    [Fact]
    public void NotIn_CaseVariant_RowDropped()
    {
        // dept NOT IN ('HR', 'IT') when dept = 'hr'
        // Ordinal: 'hr' not in set -> NOT IN is true
        // Collation: 'hr' is in set -> NOT IN is false
        // Disagreement yields UNKNOWN (null) -> dropped
        var payload = new Dictionary<string, object?> { ["dept"] = "hr" };
        var matches = StreamingRowFilterAstEvaluator.Matches(payload, "dept NOT IN ('HR', 'IT')", DatabaseDialect.PostgreSql);

        matches.ShouldBeFalse();
    }

    [Fact]
    public void ExactMatch_StillAllowed()
    {
        var payload = new Dictionary<string, object?> { ["dept"] = "HR" };
        var matches = StreamingRowFilterAstEvaluator.Matches(payload, "dept = 'HR'", DatabaseDialect.PostgreSql);

        matches.ShouldBeTrue();
    }

    [Fact]
    public void Like_ExactCase_StillMatches()
    {
        var payload = new Dictionary<string, object?> { ["region"] = "EU-central" };
        var matches = StreamingRowFilterAstEvaluator.Matches(payload, "region LIKE 'EU%'", DatabaseDialect.PostgreSql);

        matches.ShouldBeTrue();
    }
}
