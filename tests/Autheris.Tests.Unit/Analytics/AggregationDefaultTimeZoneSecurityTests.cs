using System.Security.Claims;
using Autheris.Application.Analytics;
using Autheris.Application.DataSources.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Autheris.Tests.Unit.GraphQL;
using HotChocolate;
using HotChocolate.Execution;
using HotChocolate.Language;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Analytics;

/// <summary>#1190: the configured default bucket zone is validated, never echoed, and never weakens the planner or the group limits.</summary>
[Trait("Category", "Security")]
public sealed class AggregationDefaultTimeZoneSecurityTests
{
    private const string FixedParserMessage = "The time zone is not a recognized IANA zone id.";

    private static string? Validate(string? zone)
    {
        var options = new AnalyticsOptions();
        options.Aggregation.DefaultTimeZone = zone!;
        var result = new AnalyticsOptionsValidator().Validate(null, options);
        return result.Failed ? result.FailureMessage : null;
    }

    private static ObjectValueNode OmittedZoneBucket() => new(
        new ObjectFieldNode("column", new EnumValueNode("created_at")),
        new ObjectFieldNode("bucket", new EnumValueNode("DAY")));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\n")]
    public void Validator_BlankOrWhitespaceDefault_FailsClosed(string? zone) =>
        Validate(zone).ShouldBe(AnalyticsOptionsValidatorDefaultTimeZoneTests.ExpectedMessage);

    [Fact]
    public void Validator_WrongCasePaddedOrOverlongDefault_FailsClosed()
    {
        foreach (var zone in new[] { "europe/berlin", "EUROPE/BERLIN", " Europe/Berlin", "Europe/Berlin ", "utc", new string('a', 4096) })
        {
            Validate(zone).ShouldBe(AnalyticsOptionsValidatorDefaultTimeZoneTests.ExpectedMessage, zone.Length > 64 ? "overlong" : zone);
        }
    }

    [Fact]
    public void Validator_UnknownDefault_MessageNamesOnlyKey_NotValue()
    {
        const string Secret = "Secret/Zone-12345";

        var message = Validate(Secret)!;

        message.ShouldContain("Analytics:Aggregation:DefaultTimeZone");
        message.ShouldNotContain(Secret);
        message.ShouldNotContain("Secret");
    }

    [Fact]
    public void Validator_AliasDefault_EtcUtc_Accepted() => Validate("Etc/UTC").ShouldBeNull();

    [Fact]
    public void Parser_BadDefaultBypassingValidator_FixedInvalidQueryMessage_NoValueLeak()
    {
        const string Bad = "Secret/Zone-12345";
        var options = new AggregationOptions { DefaultTimeZone = Bad };

        var ex = Should.Throw<GraphQLException>(() => TimeBucketDefaultTimeZoneParserTests.ParseTimeBuckets(OmittedZoneBucket(), options));

        ex.Errors.Single().Code.ShouldBe("INVALID_QUERY");
        ex.Errors.Single().Message.ShouldBe(FixedParserMessage);
        ex.Message.ShouldNotContain(Bad);
        ex.Errors.Single().Message.ShouldNotContain(Bad);
    }

    [Fact]
    public void Planner_NonUtcDefaultOnUnsupportedDialect_ThrowsTimeBucketUnsupported_NoFallback()
    {
        var spec = TimeBucketDefaultTimeZoneParserTests
            .ParseTimeBuckets(OmittedZoneBucket(), TimeBucketDefaultTimeZoneParserTests.Berlin()).TimeBuckets!.Single();

        spec.TimeZone.ShouldBe("Europe/Berlin");
        var ex = Should.Throw<AggregationTimeBucketUnsupportedException>(() =>
            GovernedTableReadPlanner.BuildTimeBucketExpression("created_at", spec, DatabaseDialect.Databricks));
        ex.Message.ShouldBe(AggregationTimeBucketUnsupportedException.FixedMessage);
    }

    [Fact]
    public void Parser_BerlinDefault_DoesNotChangeMinGroupSizeOrLimitEnforcement()
    {
        var analytics = new AnalyticsOptions();
        var minGroupSizeBefore = analytics.Privacy.DefaultMinGroupSize;
        analytics.Aggregation.DefaultTimeZone = "Europe/Berlin";

        var tooMany = new IntValueNode(AggregationOptions.MaxTimeBucketedLimit + 1);
        var ex = Should.Throw<GraphQLException>(() => TimeBucketDefaultTimeZoneParserTests
            .ParseTimeBuckets(OmittedZoneBucket(), analytics.Aggregation, limit: tooMany));
        ex.Errors.Single().Code.ShouldBe(AggregationErrorCodes.TimeBucketLimitExceeded);

        var ok = TimeBucketDefaultTimeZoneParserTests
            .ParseTimeBuckets(OmittedZoneBucket(), analytics.Aggregation, limit: new IntValueNode(50));
        ok.Limit.ShouldBe(50);
        analytics.Privacy.DefaultMinGroupSize.ShouldBe(minGroupSizeBefore);
    }

    [Fact]
    public async Task Resolver_DefaultZone_AbsentFromErrorResponses()
    {
        const string Bad = "Secret/Zone-12345";
        var host = new TypedAggregateTestHost();
        host.Analytics.Aggregation.DefaultTimeZone = Bad;
        host.Aggregation.ExecuteAggregationAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<AggregationQuery>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(TypedAggregateTestHost.Result([])));
        var executor = await host.BuildAsync();

        var result = await executor.ExecuteAsync("""
            { sales_dbo_orders_aggregate(timeBuckets: [{ column: created, bucket: DAY }], aggregates: [{ fn: COUNT, alias: "n" }], limit: 5) { count } }
            """);

        var json = result.ToJson();
        TypedAggregateTestHost.Errors(result).Select(e => e.Code).ShouldContain("INVALID_QUERY");
        json.ShouldNotContain(Bad);
        json.ShouldNotContain("Secret");
        await host.Aggregation.DidNotReceiveWithAnyArgs().ExecuteAggregationAsync(default!, default!, default);
    }
}
