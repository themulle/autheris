namespace Autheris.Domain.Model;

using System.Collections.Generic;
using System.Text.Json.Serialization;

public sealed record GraphQlIndexHint(
    [property: JsonPropertyName("root")] string Root,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("helpfulIndexColumns")] IReadOnlyList<string> HelpfulIndexColumns)
{
    public const string NoUsableIndexReason = "NO_USABLE_INDEX";
}
