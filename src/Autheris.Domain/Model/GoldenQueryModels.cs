namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;

public sealed record GoldenQuery(
    string Id,
    string Domain,
    string TableName,
    string Title,
    string Description,
    string QueryText,
    string? VariablesJson = null,
    IReadOnlyList<string>? Tags = null,
    DateTimeOffset CreatedAt = default
)
{
    /// <summary>True for an entry read from the governance store (dbt-maintained, untrusted text): its selections are verified field by field against the caller's view.</summary>
    public bool FromStore { get; init; }
}
