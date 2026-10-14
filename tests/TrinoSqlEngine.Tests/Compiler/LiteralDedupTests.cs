using System.Collections.Frozen;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Buffer;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;
using Xunit;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>CR-ADG-05: the deduplication key of bound literals must never merge two distinct values.</summary>
public class LiteralDedupTests
{
    private static SqlEmitterContext Context(TargetSqlDialect dialect = TargetSqlDialect.PostgreSql) =>
        new(dialect, DialectCapabilityTable.Default.Get(dialect),
            new ParameterSource(FrozenDictionary<string, PolicyValue>.Empty, new Dictionary<string, object?>()), CancellationToken.None);

    [Fact]
    public void TimestampsDifferingOnlyInFractionalSeconds_GetDistinctMarkers()
    {
        var ctx = Context();
        var a = ctx.BindValue(new DateTime(2026, 1, 1, 0, 0, 0, 100), SqlParameterType.Timestamp, ParameterOrigin.QueryLiteral);
        var b = ctx.BindValue(new DateTime(2026, 1, 1, 0, 0, 0, 200), SqlParameterType.Timestamp, ParameterOrigin.QueryLiteral);
        var c = ctx.BindValue(new DateTime(2026, 1, 1, 0, 0, 0, 100).AddTicks(1), SqlParameterType.Timestamp, ParameterOrigin.QueryLiteral);
        Assert.NotEqual(a, b);
        Assert.NotEqual(a, c);
        Assert.NotEqual(b, c);
    }

    [Fact]
    public void EqualValues_StillShareOneMarker()
    {
        var ctx = Context();
        var a = ctx.BindValue(new DateTime(2026, 1, 1, 0, 0, 0, 100), SqlParameterType.Timestamp, ParameterOrigin.QueryLiteral);
        var b = ctx.BindValue(new DateTime(2026, 1, 1, 0, 0, 0, 100), SqlParameterType.Timestamp, ParameterOrigin.QueryLiteral);
        Assert.Equal(a, b);
        Assert.Single(ctx.Parameters);
    }

    [Fact]
    public void DistinctValuesOfEveryTemporalType_NeverShareAMarker()
    {
        var ctx = Context();
        var values = new (object Value, SqlParameterType Type)[]
        {
            (new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), SqlParameterType.Timestamp),
            (new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified), SqlParameterType.Timestamp),
            (new DateTime(2026, 1, 1, 0, 0, 0, 1), SqlParameterType.Timestamp),
            (new DateTimeOffset(2026, 1, 1, 0, 0, 0, 100, TimeSpan.Zero), SqlParameterType.TimestampTz),
            (new DateTimeOffset(2026, 1, 1, 0, 0, 0, 200, TimeSpan.Zero), SqlParameterType.TimestampTz),
            (new DateTimeOffset(2026, 1, 1, 1, 0, 0, 200, TimeSpan.FromHours(1)), SqlParameterType.TimestampTz),
            (TimeSpan.FromMilliseconds(100), SqlParameterType.Time),
            (TimeSpan.FromMilliseconds(200), SqlParameterType.Time),
            (TimeSpan.FromTicks(1), SqlParameterType.Time),
            (1.1m, SqlParameterType.Decimal),
            (1.10m, SqlParameterType.Decimal),
            (0.1 + 0.2, SqlParameterType.Double),
            (0.3, SqlParameterType.Double),
            (1, SqlParameterType.Int32),
            (1L, SqlParameterType.Int64),
        };

        var markers = values.Select(v => ctx.BindValue(v.Value, v.Type, ParameterOrigin.QueryLiteral)).ToList();
        Assert.Equal(markers.Count, markers.Distinct().Count());
        Assert.Equal(values.Length, ctx.Parameters.Length);
    }

    [Fact]
    public void ValuesOfDifferentClrTypesWithTheSameText_AreNotMerged()
    {
        var ctx = Context();
        var asText = ctx.BindValue("1", SqlParameterType.String, ParameterOrigin.QueryLiteral);
        var asInt = ctx.BindValue(1, SqlParameterType.String, ParameterOrigin.QueryLiteral);
        Assert.NotEqual(asText, asInt);
    }

    [Fact]
    public void Compile_TimestampLiteralsThatDifferByMilliseconds_AreBoundSeparately()
    {
        var engine = new FastSqlEngine();
        var compiled = engine.Compile(
            "SELECT id FROM orders WHERE amount = TIMESTAMP '2026-01-01 00:00:00.100' OR amount = TIMESTAMP '2026-01-01 00:00:00.200'".AsMemory(),
            new CompileRequest
            {
                TargetDialect = TargetSqlDialect.PostgreSql,
                TokenGuards = SqlTokenSecurityOptions.Strict,
                Policy = new GovernancePolicy
                {
                    RowFilters = new DictPolicyProvider(),
                    Masks = new DictMaskProvider(),
                    Catalog = PolicyFixtures.Catalog(),
                    Tenant = new TenantBinding("__autheris_tenant", "acme", SqlParameterType.String)
                }
            }, CancellationToken.None);

        var timestamps = compiled.Parameters.Where(p => p.Type == SqlParameterType.Timestamp).ToList();
        Assert.Equal(2, timestamps.Count);
        Assert.NotEqual(timestamps[0].Value, timestamps[1].Value);
    }
}
