namespace TrinoSqlEngine.Tests.TimeZones;

using TrinoSqlEngine.Ast.Emit;
using Xunit;
using static TrinoSqlEngine.Tests.TimeZones.TimeZoneTestSupport;

/// <summary>REQ-SQL-130 T-20: the compile cache keeps the zone slots and never skips a decision that depends on the request.</summary>
public sealed class TimeZoneTemplateCacheTests
{
    [Theory, MemberData(nameof(DialectData), MemberType = typeof(TimeZoneTestSupport))]
    public void T20_ACacheHitRebindsAndTheZoneFlagAndMappedValuesSurvive(TargetSqlDialect dialect)
    {
        var engine = new FastSqlEngine();
        const string sql = "SELECT convert_timezone('UTC', __param_to, e.OccurredAt) FROM dbo.Events e";
        var first = engine.Compile(sql.AsMemory(), Request(dialect, available: true), CancellationToken.None);
        var second = engine.Compile(sql.AsMemory(), Request(dialect, available: true), CancellationToken.None);

        Assert.Equal(1, engine.CompileCache.Stats.Hits);
        Assert.Equal(first.Sql, second.Sql);
        Assert.Equal(first.Parameters.Select(p => (p.Marker, p.ZoneArgument, p.Value)), second.Parameters.Select(p => (p.Marker, p.ZoneArgument, p.Value)));
        Assert.Single(second.Parameters.Where(p => p.ZoneArgument));
        Assert.Equal(TimeZoneRegistry.TryResolve(dialect, "UTC", out var utc) ? utc : null, second.Parameters.Single(p => p.Origin == ParameterOrigin.QueryLiteral && p.Value is string).Value);
    }

    [Theory, MemberData(nameof(DialectData), MemberType = typeof(TimeZoneTestSupport))]
    public void T20_AvailabilityIsPartOfTheKeySoACachedTemplateNeverBypassesTheValidator(TargetSqlDialect dialect)
    {
        var engine = new FastSqlEngine();
        const string sql = "SELECT convert_timezone('UTC', 'Europe/Berlin', e.OccurredAt) FROM dbo.Events e";
        engine.Compile(sql.AsMemory(), Request(dialect, available: true), CancellationToken.None);

        var ex = Assert.Throws<SqlTimeZoneException>(() => engine.Compile(sql.AsMemory(), Request(dialect, available: false), CancellationToken.None));
        Assert.Equal(GovernedSqlErrorCodes.TimeZoneUnavailable, ex.Code);
        Assert.Equal(0, engine.CompileCache.Stats.Hits);
    }

    [Theory, MemberData(nameof(DialectData), MemberType = typeof(TimeZoneTestSupport))]
    public void T20_ARejectedStatementIsNeverCached(TargetSqlDialect dialect)
    {
        var engine = new FastSqlEngine();
        const string sql = "SELECT convert_timezone('Europe/Evil', 'UTC', e.OccurredAt) FROM dbo.Events e";
        for (int i = 0; i < 2; i++)
        {
            Assert.Throws<SqlTimeZoneException>(() => engine.Compile(sql.AsMemory(), Request(dialect, available: true), CancellationToken.None));
        }

        Assert.Equal(0, engine.CompileCache.Stats.Entries);
    }
}
