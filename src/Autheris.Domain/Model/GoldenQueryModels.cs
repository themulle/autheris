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
);
