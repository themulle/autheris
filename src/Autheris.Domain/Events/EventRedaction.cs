namespace Autheris.Domain.Events;

using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

/// <summary>
/// SR-L-15: defence-in-depth safety net for outbound event payloads. Strings that look like absolute filesystem
/// paths (POSIX <c>/...</c>, Windows <c>C:\...</c>, UNC <c>\\host\...</c>) are replaced with a placeholder.
/// The primary fix is at the producers; this guards against future regressions.
/// </summary>
public static partial class EventRedaction
{
    /// <summary>Replacement for redacted values.</summary>
    public const string RedactedPath = "[redacted-path]";

    private static readonly JsonSerializerOptions DefaultOptions = new(JsonSerializerDefaults.Web);

    [GeneratedRegex(@"^(?:/|[A-Za-z]:[\\/]|\\\\)", RegexOptions.CultureInvariant)]
    private static partial Regex AbsolutePathPattern();

    /// <summary>True when <paramref name="value"/> starts like an absolute filesystem path.</summary>
    public static bool LooksLikeAbsolutePath(string? value) =>
        !string.IsNullOrEmpty(value) && AbsolutePathPattern().IsMatch(value);

    /// <summary>Serialises the event (runtime type) to a JSON tree with absolute-path strings redacted.</summary>
    public static JsonNode Sanitize(IGatewayEvent @event, JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(@event);

        var node = JsonSerializer.SerializeToNode(@event, @event.GetType(), options ?? DefaultOptions)
                   ?? new JsonObject();
        return Walk(node);
    }

    private static JsonNode Walk(JsonNode root)
    {
        switch (root)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                {
                    if (obj[key] is { } child && IsRedactable(child))
                    {
                        obj[key] = JsonValue.Create(RedactedPath);
                    }
                    else if (child is not null)
                    {
                        Walk(child);
                    }
                }

                break;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    if (arr[i] is { } child && IsRedactable(child))
                    {
                        arr[i] = JsonValue.Create(RedactedPath);
                    }
                    else if (child is not null)
                    {
                        Walk(child);
                    }
                }

                break;
        }

        return root;
    }

    private static bool IsRedactable(JsonNode node) =>
        node is JsonValue val && val.TryGetValue<string>(out var s) && LooksLikeAbsolutePath(s);
}
