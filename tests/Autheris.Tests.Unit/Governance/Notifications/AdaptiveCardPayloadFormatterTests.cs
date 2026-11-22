using System;
using System.Text.Json;
using Autheris.Application.Governance.Notifications;
using Autheris.Domain.Options;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance.Notifications;

[Trait("Category", "Security")]
public sealed class AdaptiveCardPayloadFormatterTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-22T12:00:00Z");

    [Fact]
    public void Justification_escaped_and_truncated()
    {
        var formatter = new AdaptiveCardPayloadFormatter();
        var longJustification = "**Urgent**\r\nNeed *clear* access to `orders` " + new string('x', 240);
        var message = new GovernanceNotificationMessage(
            "PERMISSION_REQUESTED",
            "tenant-a",
            "entra",
            "user:alice",
            "Summary",
            Now,
            ObjectName: "sales.dbo.orders",
            Justification: longJustification);

        var json = formatter.Format(message, GovernanceNotificationFormat.AdaptiveCard);
        using var doc = JsonDocument.Parse(json);
        var body = doc.RootElement.GetProperty("attachments")[0].GetProperty("content").GetProperty("body");
        var justBlock = body[3].GetProperty("text").GetString()!;
        var sanitizedField = justBlock["Justification: ".Length..];
        sanitizedField.Length.ShouldBeLessThanOrEqualTo(AdaptiveCardPayloadFormatter.MaxTextFieldLength);
        sanitizedField.ShouldNotContain("*");
        sanitizedField.ShouldNotContain("`");
        sanitizedField.ShouldNotContain("\n");
        sanitizedField.ShouldNotContain("\r");
    }

    [Fact]
    public void No_links_or_mentions_from_input()
    {
        var formatter = new AdaptiveCardPayloadFormatter();
        var hostileSummary = "Ping <at>Admin</at> @ceo check [click here](https://evil.example/phish) or https://attacker.test/x";
        var message = new GovernanceNotificationMessage(
            "SELF_GRANT",
            "tenant-a",
            "entra",
            "@all",
            hostileSummary,
            Now,
            Justification: "See https://malicious.site and <at>SecOps</at>");

        var json = formatter.Format(message, GovernanceNotificationFormat.AdaptiveCard);
        json.ShouldNotContain("https://evil.example");
        json.ShouldNotContain("https://attacker.test");
        json.ShouldNotContain("https://malicious.site");
        json.ShouldNotContain("<at>");
        json.ShouldNotContain("</at>");
        json.ShouldNotContain("@ceo");
        json.ShouldNotContain("@all");
    }

    [Fact]
    public void Built_by_serializer()
    {
        var formatter = new AdaptiveCardPayloadFormatter();
        var quoteBreaker = "\"}],\"injected\":true,\"x\":\"";
        var message = new GovernanceNotificationMessage("PERMISSION_REQUESTED", "tenant-a", "entra", quoteBreaker, quoteBreaker, Now);

        var cardJson = formatter.Format(message, GovernanceNotificationFormat.AdaptiveCard);
        using var cardDoc = JsonDocument.Parse(cardJson);
        cardDoc.RootElement.GetProperty("attachments")[0].GetProperty("content").TryGetProperty("injected", out _).ShouldBeFalse();

        var plainJson = formatter.Format(message, GovernanceNotificationFormat.Json);
        using var plainDoc = JsonDocument.Parse(plainJson);
        plainDoc.RootElement.TryGetProperty("injected", out _).ShouldBeFalse();
    }
}
