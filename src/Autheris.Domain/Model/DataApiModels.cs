namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using Autheris.Domain.Common;

public sealed record DatasetQueryRequest(
    TableIdentifier Table,
    IReadOnlyList<string>? SelectColumns = null,
    string? FilterExpression = null,
    string? OrderBy = null,
    int Limit = 50,
    int Offset = 0);

public sealed record DatasetColumnInfo(
    string Name,
    string Type,
    bool Masked);

public sealed record DatasetQueryEnvelope(
    string Dataset,
    int Count,
    int Offset,
    int Limit,
    bool HasMore,
    IReadOnlyList<DatasetColumnInfo> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Data);
