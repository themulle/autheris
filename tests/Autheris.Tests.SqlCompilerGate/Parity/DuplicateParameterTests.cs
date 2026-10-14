namespace Autheris.Tests.SqlCompilerGate.Parity;

using Autheris.Tests.SqlCompilerGate.Databases;
using Autheris.Tests.SqlCompilerGate.Harness;
using Autheris.Tests.SqlCompilerGate.Model;
using Shouldly;
using Xunit.Abstractions;

/// <summary>
/// SEC-ADG-02 item 2: every consumer fails closed on a duplicate parameter name (<c>Consumer_DuplicateParameterName_FailsClosed</c>). Two keys of
/// one decision that name the same parameter (with or without the marker prefix, in another case), or a key that names a parameter the consumer
/// binds itself (the tenant), would let the provider pick one of two values silently.
/// </summary>
public sealed class DuplicateParameterTests(ParityFixture<SqliteGateDatabase> fixture, ITestOutputHelper output)
    : IClassFixture<ParityFixture<SqliteGateDatabase>>
{
    private const string Text = "\"status\" = @__gql_rls_0";

    public static IEnumerable<object[]> Consumers() =>
    [
        [new SqlDataSourceExecutorConsumer()],
        [new TreeSqlCompilerConsumer()],
        [new ProcedureRowScopeConsumer()],
        [new InMemoryFilterRowsConsumer()],
        [new DatasetSpliceConsumer()]
    ];

    private static FilterText Dictionary(params (string Name, object Value)[] values) =>
        new(Text, values.ToDictionary(v => v.Name, v => (object?)v.Value, StringComparer.Ordinal));

    private async Task AssertRejectedAsync(IParityConsumer consumer, FilterText filter, string scenario)
    {
        var outcome = await consumer.RunAsync(fixture.Env, filter);
        output.WriteLine($"{consumer.Name} / {scenario}: {GateProfile.Describe(outcome)}");

        // Fail closed: a typed refusal before the provider sees two values, or (for the in-memory evaluators, which cannot refuse) no row at all.
        switch (outcome)
        {
            case EngineOutcome.Rejected rejected:
                rejected.Reason.ShouldBe(nameof(System.Security.SecurityException), $"{consumer.Name} refused with another exception type ({scenario})");
                break;
            case EngineOutcome.Rows rows:
                rows.Data.ShouldBeEmpty($"{consumer.Name} returned rows for a duplicate parameter name ({scenario})");
                break;
            default:
                Assert.Fail($"{consumer.Name}: unexpected outcome ({scenario})");
                break;
        }
    }

    [Theory]
    [MemberData(nameof(Consumers))]
    public Task Consumer_DuplicateParameterName_WithAndWithoutMarker_FailsClosed(IParityConsumer consumer) =>
        AssertRejectedAsync(consumer, Dictionary(("@__gql_rls_0", "open"), ("__gql_rls_0", "closed")), "marker prefix");

    [Theory]
    [MemberData(nameof(Consumers))]
    public Task Consumer_DuplicateParameterName_InAnotherCase_FailsClosed(IParityConsumer consumer) =>
        AssertRejectedAsync(consumer, Dictionary(("@__gql_rls_0", "open"), ("@__GQL_RLS_0", "closed")), "case");

    [Theory]
    [MemberData(nameof(Consumers))]
    public Task Consumer_ParameterThatNamesTheConsumersOwnParameter_FailsClosed(IParityConsumer consumer)
    {
        // the in-memory consumers bind no parameter of their own; the tree compiler renames a colliding filter parameter (see below)
        if (consumer.OwnTenantParameter is not { } ownName || consumer is TreeSqlCompilerConsumer) return Task.CompletedTask;
        return AssertRejectedAsync(consumer, Dictionary(("@__gql_rls_0", "open"), ("@" + ownName, "B")), "own parameter " + ownName);
    }

    [Fact]
    public async Task TreeSqlCompiler_RenamesAFilterParameterThatCollidesWithItsTenantParameter_AndKeepsTenantIsolation()
    {
        // @gq0 is the tree compiler's own tenant parameter ("A"). A row filter value of the same name ("B") must never replace it.
        var filter = new FilterText("\"status\" = @__gql_rls_0 OR \"tenant_id\" = @gq0",
            new Dictionary<string, object?> { ["@__gql_rls_0"] = "open", ["@gq0"] = "B" });

        var outcome = await new TreeSqlCompilerConsumer().RunAsync(fixture.Env, filter);

        var rows = outcome.ShouldBeOfType<EngineOutcome.Rows>();
        var ids = rows.Data.Select(r => Convert.ToInt64(r[0], System.Globalization.CultureInfo.InvariantCulture)).ToList();
        ids.ShouldAllBe(id => GateModel.Rows.Any(r => r.Table == "orders" && r.Id == id && r.Tenant == GateModel.TenantA), "a tenant-B id was returned: the tenant parameter was replaced");
        ids.ShouldNotBeEmpty();
    }
}
