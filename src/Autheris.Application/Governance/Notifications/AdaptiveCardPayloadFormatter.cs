using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Autheris.Domain.Options;

namespace Autheris.Application.Governance.Notifications;

/// <summary>
/// Serializes governance notifications as escaped plain text without markdown formatting, active links, mentions, or raw data values.
/// </summary>
public sealed partial class AdaptiveCardPayloadFormatter
{
    public const int MaxTextFieldLength = 200;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    [GeneratedRegex(@"<at\b[^>]*>.*?</at>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex AtTagRegex();

    [GeneratedRegex(@"\[([^\]]+)\]\(([^)]+)\)", RegexOptions.Singleline)]
    private static partial Regex MarkdownLinkRegex();

    [GeneratedRegex(@"https?://\S+", RegexOptions.IgnoreCase)]
    private static partial Regex HttpUrlRegex();

    [GeneratedRegex(@"@(?=[A-Za-z0-9_.\-])")]
    private static partial Regex AtMentionPrefixRegex();

    public string Format(GovernanceNotificationMessage message, GovernanceNotificationFormat format = GovernanceNotificationFormat.AdaptiveCard)
    {
        ArgumentNullException.ThrowIfNull(message);

        var eventType = SanitizePlainText(message.EventType, 64);
        var tenantId = SanitizePlainText(message.TenantId, 64);
        var subject = SanitizePlainText(message.SubjectOrGroupId, MaxTextFieldLength);
        var summary = SanitizePlainText(message.Summary, MaxTextFieldLength);
        var obj = string.IsNullOrWhiteSpace(message.ObjectName) ? null : SanitizePlainText(message.ObjectName, MaxTextFieldLength);
        var justification = string.IsNullOrWhiteSpace(message.Justification) ? null : SanitizePlainText(message.Justification, MaxTextFieldLength);
        var timestamp = message.OccurredAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

        if (format == GovernanceNotificationFormat.Json)
        {
            var jsonPayload = new JsonNotificationEnvelope(
                eventType,
                tenantId,
                subject,
                summary,
                obj,
                justification,
                timestamp);
            return JsonSerializer.Serialize(jsonPayload, SerializerOptions);
        }

        var facts = new List<AdaptiveCardFact>
        {
            new("Event", eventType),
            new("Tenant", tenantId),
            new("Subject", subject),
            new("OccurredAtUtc", timestamp)
        };
        if (!string.IsNullOrEmpty(obj))
        {
            facts.Add(new AdaptiveCardFact("Object", obj));
        }

        var body = new List<AdaptiveCardBodyElement>
        {
            new("TextBlock", Weight: "Bolder", Size: "Medium", Wrap: true, Text: $"Autheris Governance: {eventType}"),
            new("TextBlock", Wrap: true, Text: summary),
            new("FactSet", Facts: facts)
        };
        if (!string.IsNullOrEmpty(justification))
        {
            body.Add(new AdaptiveCardBodyElement("TextBlock", Wrap: true, Text: $"Justification: {justification}"));
        }

        var envelope = new AdaptiveCardEnvelope(
            "message",
            [
                new AdaptiveCardAttachment(
                    "application/vnd.microsoft.card.adaptive",
                    new AdaptiveCardContent("AdaptiveCard", "http://adaptivecards.io/schemas/adaptive-card.json", "1.4", body))
            ]);

        return JsonSerializer.Serialize(envelope, SerializerOptions);
    }

    public static string SanitizePlainText(string? raw, int maxLength = MaxTextFieldLength)
    {
        if (string.IsNullOrWhiteSpace(raw) || maxLength <= 0)
        {
            return string.Empty;
        }

        var cleaned = AtTagRegex().Replace(raw, "[mention]");
        cleaned = MarkdownLinkRegex().Replace(cleaned, static m => m.Groups[1].Value);
        cleaned = HttpUrlRegex().Replace(cleaned, "[link-removed]");
        cleaned = AtMentionPrefixRegex().Replace(cleaned, "[at]");

        var sb = new StringBuilder(Math.Min(cleaned.Length, maxLength));
        var lastWasSpace = false;
        foreach (var ch in cleaned)
        {
            if (char.IsControl(ch) || char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace && sb.Length > 0)
                {
                    sb.Append(' ');
                    lastWasSpace = true;
                }

                continue;
            }

            if (ch is '\\' or '*' or '_' or '`' or '~' or '<' or '>' or '[' or ']' or '(' or ')')
            {
                var replacement = ch switch
                {
                    '<' => "(",
                    '>' => ")",
                    '[' => "(",
                    ']' => ")",
                    _ => " "
                };
                if (replacement == " ")
                {
                    if (!lastWasSpace && sb.Length > 0)
                    {
                        sb.Append(' ');
                        lastWasSpace = true;
                    }
                }
                else
                {
                    sb.Append(replacement);
                    lastWasSpace = false;
                }

                continue;
            }

            sb.Append(ch);
            lastWasSpace = false;
        }

        var normalized = sb.ToString().Trim();
        if (normalized.Length <= maxLength)
        {
            return normalized;
        }

        var limit = maxLength > 3 ? maxLength - 3 : maxLength;
        if (limit > 0 && char.IsHighSurrogate(normalized[limit - 1]))
        {
            limit--;
        }

        return maxLength > 3
            ? string.Concat(normalized.AsSpan(0, limit).TrimEnd(), "...")
            : normalized[..limit];
    }

    private sealed record JsonNotificationEnvelope(
        string EventType,
        string TenantId,
        string Subject,
        string Summary,
        string? Object,
        string? Justification,
        string OccurredAtUtc);

    private sealed record AdaptiveCardEnvelope(
        string Type,
        IReadOnlyList<AdaptiveCardAttachment> Attachments);

    private sealed record AdaptiveCardAttachment(
        string ContentType,
        AdaptiveCardContent Content);

    private sealed record AdaptiveCardContent(
        string Type,
        string Schema,
        string Version,
        IReadOnlyList<AdaptiveCardBodyElement> Body);

    private sealed record AdaptiveCardBodyElement(
        string Type,
        string? Text = null,
        string? Weight = null,
        string? Size = null,
        bool? Wrap = null,
        IReadOnlyList<AdaptiveCardFact>? Facts = null);

    private sealed record AdaptiveCardFact(string Title, string Value);
}
