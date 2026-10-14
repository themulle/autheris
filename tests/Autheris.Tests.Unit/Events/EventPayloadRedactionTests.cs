namespace Autheris.Tests.Unit.Events;

using System;
using System.Text.Json;
using Autheris.Domain.Events;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class EventPayloadRedactionTests
{
    private static DocumentDepositedEvent Deposit() => new(
        EventId: "e1",
        AppId: "orders",
        Category: "architecture",
        DocId: "overview",
        Title: "T",
        RelativePath: DocumentDepositedEvent.BuildRelativePath("orders", "architecture", "overview"),
        Timestamp: DateTimeOffset.UtcNow);

    [Fact]
    public void DocDeposit_Event_ContainsNoAbsolutePath()
    {
        var ev = Deposit();

        ev.RelativePath.ShouldBe("orders/architecture/overview");
#pragma warning disable CS0618 // deprecated alias must stay relative
        ev.SourceFilePath.ShouldBe(ev.RelativePath);
#pragma warning restore CS0618
        var json = JsonSerializer.Serialize(ev, ev.GetType(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        json.ShouldNotContain("\"/");
        json.ShouldNotContain(":\\\\");
        json.ShouldContain("\"relativePath\":\"orders/architecture/overview\"");
    }

    [Fact]
    public void PolicyDenied_Event_ContainsNoDeniedReasonText()
    {
        var ev = new PolicyDeniedEvent(
            EventId: "e2",
            SubjectSid: "S-1",
            Action: "query",
            Resource: "sales.orders",
            Reason: PolicyDeniedEvent.DefaultReasonCode,
            Timestamp: DateTimeOffset.UtcNow);

        ev.Reason.ShouldBe("POLICY_DENIED");
        ev.ReasonCode.ShouldBe("POLICY_DENIED");
        var json = JsonSerializer.Serialize(ev, ev.GetType(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        json.ShouldContain("\"reasonCode\":\"POLICY_DENIED\"");
    }
}

[Trait("Category", "Security")]
public sealed class EventRedactionTests
{
    [Theory]
    [InlineData("/var/lib/autheris/docs/a.md", true)]
    [InlineData("C:\\data\\docs\\a.md", true)]
    [InlineData("D:/data/a.md", true)]
    [InlineData("\\\\server\\share\\a.md", true)]
    [InlineData("orders/architecture/overview", false)]
    [InlineData("https://example.com/x", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void LooksLikeAbsolutePath_MatchesExpected(string? value, bool expected)
    {
        EventRedaction.LooksLikeAbsolutePath(value).ShouldBe(expected);
    }

    [Fact]
    public void Sanitize_RedactsAbsolutePathStrings_AndKeepsRelativeOnes()
    {
        var ev = new DocumentDepositedEvent(
            EventId: "e1",
            AppId: "orders",
            Category: "architecture",
            DocId: "overview",
            Title: "/etc/passwd",
            RelativePath: "orders/architecture/overview",
            Timestamp: DateTimeOffset.UtcNow);

        var node = EventRedaction.Sanitize(ev).AsObject();

        node["title"]!.GetValue<string>().ShouldBe(EventRedaction.RedactedPath);
        node["relativePath"]!.GetValue<string>().ShouldBe("orders/architecture/overview");
        node["appId"]!.GetValue<string>().ShouldBe("orders");
        node["eventType"]!.GetValue<string>().ShouldBe("docs.document.deposited");
        node.ToJsonString().ShouldNotContain("/etc/passwd");
    }
}
