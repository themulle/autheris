namespace TrinoSqlEngine.Tests.TimeZones;

using System.Data;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Tests.Compiler;
using Xunit;
using static TrinoSqlEngine.Tests.TimeZones.TimeZoneTestSupport;

/// <summary>REQ-SQL-130 (A-4, BC-130-5, SEC-130-05): zone slots are mapped through the closed set by the production binder, in a pre-pass, before the command exists.</summary>
public sealed class TimeZoneBinderTests
{
    private const string Sql = "SELECT convert_timezone(__param_zone, __param_to, e.OccurredAt) AS t FROM dbo.Events e WHERE e.Label = __param_zone";

    private sealed class ZoneCommand() : BinderMutationTests.RecCommand
    {
        public bool BindByName { get; set; }
    }

    private static ZoneCommand Bind(TargetSqlDialect dialect, CompiledSql compiled, object? zone, object? to)
    {
        var command = new ZoneCommand();
        CompiledSqlBinders.For(dialect).Bind(command, compiled, new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["zone"] = zone, ["to"] = to });
        return command;
    }

    private static string Mapped(TargetSqlDialect dialect, string iana)
    {
        Assert.True(TimeZoneRegistry.TryResolve(dialect, iana, out var value));
        return value;
    }

    [Theory, MemberData(nameof(DialectData), MemberType = typeof(TimeZoneTestSupport))]
    public void T19_AValidZoneValueBindsTheMappedRegistryValueAsAString(TargetSqlDialect dialect)
    {
        var compiled = Compile(dialect, Sql);
        var command = Bind(dialect, compiled, "America/New_York", "Asia/Kolkata");

        var zoneSlots = compiled.Parameters.Where(p => p.ZoneArgument).ToList();
        var fromSlot = zoneSlots.Single(s => string.Equals(s.SourceName, "zone", StringComparison.OrdinalIgnoreCase));
        var toSlot = zoneSlots.Single(s => string.Equals(s.SourceName, "to", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(Mapped(dialect, "America/New_York"), command.P(fromSlot.Ordinal).Value);
        Assert.Equal(Mapped(dialect, "Asia/Kolkata"), command.P(toSlot.Ordinal).Value);
        Assert.All(zoneSlots, s => Assert.Equal(DbType.String, command.P(s.Ordinal).DbType));
        Assert.Same(Mapped(dialect, "America/New_York"), command.P(fromSlot.Ordinal).Value);

        // the ordinary slot of the same name is untouched: it binds the raw value through the normal client path
        var valueSlot = compiled.Parameters.Single(p => p.Origin == ParameterOrigin.ClientNamed && !p.ZoneArgument);
        Assert.Equal("America/New_York", command.P(valueSlot.Ordinal).Value);
    }

    public static IEnumerable<object?[]> HostileValues() =>
    [
        ["Europe/Evil"], ["x' OR 1=1--"], [new string('a', 65)], [""], ["europe/berlin"], ["Europe/Berlin "], ["Europe/Berlin\0"], ["EST"], ["Еurope/Berlin"],
        [null], [42], [true], [3.5m], [DBNull.Value], [new[] { "Europe/Berlin" }], [new object()], [(object)new DateTime(2026, 1, 1)],
        [ClientListValue.Create(new List<object> { "Europe/Berlin" }, SqlParameterType.String)]
    ];

    [Theory, MemberData(nameof(DialectsAndHostile))]
    [Trait("Category", "Security")]
    public void ST_130_2_AHostileZoneValueIsRefusedBeforeAnythingReachesTheCommand(TargetSqlDialect dialect, object? hostile)
    {
        var compiled = Compile(dialect, Sql);

        // either zone may be the hostile one: the pre-pass refuses before the first parameter is added
        foreach (var (zone, to) in new[] { (hostile, (object?)"Europe/Berlin"), ((object?)"Europe/Berlin", hostile) })
        {
            var command = new ZoneCommand();
            var ex = Assert.Throws<SqlTimeZoneException>(() =>
                CompiledSqlBinders.For(dialect).Bind(command, compiled, new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["zone"] = zone, ["to"] = to }));
            Assert.Equal(GovernedSqlErrorCodes.TimeZoneInvalid, ex.Code);
            Assert.Equal("The time zone is not supported.", ex.Message);
            Assert.Empty(command.Parameters);
            Assert.Equal(string.Empty, command.CommandText);
            Assert.Equal(CommandType.StoredProcedure, command.CommandType);
        }
    }

    public static IEnumerable<object?[]> DialectsAndHostile() =>
        TimeZoneTestSupport.Dialects.SelectMany(d => HostileValues().Select(h => new object?[] { d, h[0] }));

    [Theory, MemberData(nameof(DialectData), MemberType = typeof(TimeZoneTestSupport))]
    public void T19_AMissingZoneValueIsAMissingClientParameter(TargetSqlDialect dialect)
    {
        var compiled = Compile(dialect, Sql);
        var command = new ZoneCommand();
        Assert.Throws<MissingClientParameterException>(() =>
            CompiledSqlBinders.For(dialect).Bind(command, compiled, new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["to"] = "UTC" }));
        Assert.Empty(command.Parameters);
    }

    [Theory, MemberData(nameof(DialectData), MemberType = typeof(TimeZoneTestSupport))]
    public void T19_AZoneSlotIsBoundThroughTheStringTypeNeverInferred(TargetSqlDialect dialect)
    {
        var compiled = Compile(dialect, Sql);
        var command = Bind(dialect, compiled, "UTC", "UTC");
        foreach (var slot in compiled.Parameters.Where(p => p.ZoneArgument))
        {
            Assert.Equal(DbType.String, command.P(slot.Ordinal).DbType);
            Assert.IsType<string>(command.P(slot.Ordinal).Value);
        }
    }

    [Theory, MemberData(nameof(DialectData), MemberType = typeof(TimeZoneTestSupport))]
    public void T19_TheCheckedExecutionPathUsesTheSameZoneRules(TargetSqlDialect dialect)
    {
        var compiled = Compile(dialect, "UPDATE dbo.Events SET Label = 'x' WHERE convert_timezone(__param_zone, 'UTC', OccurredAt) > TIMESTAMP '2026-01-01 00:00:00'",
            statements: StatementPermissions.Update);
        var command = new ZoneCommand();
        Assert.Equal(GovernedSqlErrorCodes.TimeZoneInvalid, Assert.Throws<SqlTimeZoneException>(() =>
            CompiledSqlBinders.For(dialect).Bind(command, compiled, new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["zone"] = "Europe/Evil" })).Code);
        Assert.Empty(command.Parameters);
    }

    // ---- BC-130-5(d) every other consumer of the parameters refuses a zone slot ----

    [Theory, MemberData(nameof(DialectData), MemberType = typeof(TimeZoneTestSupport))]
    [Trait("Category", "Security")]
    public void ST_130_4_AZoneSlotIsOnlyConsumableThroughTheBinder(TargetSqlDialect dialect)
    {
        var compiled = Compile(dialect, Sql);
        var ex = Assert.Throws<InvalidOperationException>(compiled.EnsureNoZoneArguments);
        Assert.DoesNotContain("zone", ex.Message, StringComparison.OrdinalIgnoreCase);

        // a statement without a zone slot (a literal zone, or none) passes
        Compile(dialect, "SELECT convert_timezone('UTC', 'Europe/Berlin', e.OccurredAt) FROM dbo.Events e").EnsureNoZoneArguments();
        Compile(dialect, "SELECT e.Id FROM dbo.Events e").EnsureNoZoneArguments();
    }
}
