using System.Text.Json;
using Autheris.Application.Analytics;
using HotChocolate.Execution;
using HotChocolate.Language;
using HotChocolate.Types;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.GraphQL;

public sealed class TimeBucketDefaultTimeZoneSchemaTests
{
    private const string InputType = "sales_dbo_orders_time_bucket_input";

    private static async Task<string?> TimeZoneDefaultAsync(TypedAggregateTestHost host)
    {
        var executor = await host.BuildAsync();
        var input = executor.Schema.Types.OfType<InputObjectType>().Single(t => t.Name == InputType);
        return (input.Fields["timeZone"].DefaultValue as StringValueNode)?.Value;
    }

    private static async Task<AggregationQuery?> RunAsync(TypedAggregateTestHost host, string timeZoneArg, Dictionary<string, object?>? variables = null)
    {
        AggregationQuery? captured = null;
        host.Aggregation.ExecuteAggregationAsync(Arg.Any<System.Security.Claims.ClaimsPrincipal>(), Arg.Do<AggregationQuery>(q => captured = q), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(TypedAggregateTestHost.Result([])));
        var executor = await host.BuildAsync();
        var varDecl = variables is null ? string.Empty : "($tz: String)";
        var request = OperationRequestBuilder.New().SetDocument($$"""
            query{{varDecl}} {
              sales_dbo_orders_aggregate(
                timeBuckets: [{ column: created, bucket: DAY{{timeZoneArg}} }],
                aggregates: [{ fn: COUNT, alias: "n" }], limit: 5) { count }
            }
            """);
        if (variables != null)
        {
            request.SetVariableValues(variables);
        }

        var result = await executor.ExecuteAsync(request.Build());
        TypedAggregateTestHost.Errors(result).ShouldBeEmpty();
        return captured;
    }

    [Fact]
    public async Task Introspection_DefaultConfig_TimeZoneDefaultIsUtc() =>
        (await TimeZoneDefaultAsync(new TypedAggregateTestHost())).ShouldBe("UTC");

    [Fact]
    public async Task Introspection_BerlinConfig_TimeZoneDefaultIsBerlin()
    {
        var host = new TypedAggregateTestHost();
        host.Analytics.Aggregation.DefaultTimeZone = "Europe/Berlin";

        (await TimeZoneDefaultAsync(host)).ShouldBe("Europe/Berlin");
    }

    [Fact]
    public async Task Resolver_OmittedTimeZone_BerlinConfig_SendsBerlinSpec()
    {
        var host = new TypedAggregateTestHost();
        host.Analytics.Aggregation.DefaultTimeZone = "Europe/Berlin";

        var captured = await RunAsync(host, string.Empty);

        captured!.TimeBuckets!.Single().TimeZone.ShouldBe("Europe/Berlin");
    }

    [Fact]
    public async Task Resolver_ExplicitUtc_BerlinConfig_SendsUtcSpec()
    {
        var host = new TypedAggregateTestHost();
        host.Analytics.Aggregation.DefaultTimeZone = "Europe/Berlin";

        var captured = await RunAsync(host, ", timeZone: \"UTC\"");

        captured!.TimeBuckets!.Single().TimeZone.ShouldBe("UTC");
    }

    [Fact]
    public async Task Resolver_NullVariableTimeZone_BerlinConfig_SendsBerlinSpec()
    {
        var host = new TypedAggregateTestHost();
        host.Analytics.Aggregation.DefaultTimeZone = "Europe/Berlin";

        var captured = await RunAsync(host, ", timeZone: $tz", new Dictionary<string, object?> { ["tz"] = null });

        captured!.TimeBuckets!.Single().TimeZone.ShouldBe("Europe/Berlin");
    }
}
