namespace Autheris.Tests.Unit.Streaming;

using Autheris.Application.Events.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

public sealed class CloudEventSubscriptionMatcherTests
{
    private static CloudEventWebhookSubscription Sub(string filter, params CdcOperation[] ops) =>
        new("s", "t", "https://e.test", filter, ops, "x");

    private static readonly TableIdentifier Orders = new("sales", "public", "orders");

    [Theory]
    [InlineData("*", true)]
    [InlineData("orders", true)]
    [InlineData("ORDERS", true)]
    [InlineData("sales.public.orders", true)]
    [InlineData("invoices", false)]
    public void Matches_TableFilter(string filter, bool expected) =>
        CloudEventSubscriptionMatcher.Matches(Sub(filter), Orders, CdcOperation.Insert).ShouldBe(expected);

    [Fact]
    public void Matches_OperationFilter()
    {
        CloudEventSubscriptionMatcher.Matches(Sub("*", CdcOperation.Update), Orders, CdcOperation.Insert).ShouldBeFalse();
        CloudEventSubscriptionMatcher.Matches(Sub("*", CdcOperation.Insert), Orders, CdcOperation.Insert).ShouldBeTrue();
        CloudEventSubscriptionMatcher.Matches(Sub("*"), Orders, CdcOperation.Delete).ShouldBeTrue();
    }

    [Fact]
    public void Matches_DisabledSubscription_NeverMatches() =>
        CloudEventSubscriptionMatcher.Matches(Sub("*") with { IsEnabled = false }, Orders, CdcOperation.Insert).ShouldBeFalse();

    [Fact]
    public void Matches_UnparsableTableName_UsesPlainNameComparison()
    {
        CloudEventSubscriptionMatcher.Matches(Sub("orders"), "orders", CdcOperation.Insert).ShouldBeTrue();
        CloudEventSubscriptionMatcher.Matches(Sub("orders"), "other", CdcOperation.Insert).ShouldBeFalse();
    }
}
