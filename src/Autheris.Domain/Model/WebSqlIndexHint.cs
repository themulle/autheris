namespace Autheris.Domain.Model;

using System.Collections.Generic;
using System.Text.Json.Serialization;

public sealed record WebSqlIndexHint(
    [property: JsonPropertyName("table")] string Table,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("helpfulIndexColumns")] IReadOnlyList<string> HelpfulIndexColumns);
