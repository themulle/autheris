namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;

/// <summary>
/// Represents an input parameter of a declarative SQL endpoint (e.g. @country, @fromDate).
/// </summary>
public sealed record SqlEndpointParameter(
    string Name,
    string Token,
    Type ClrType,
    bool IsRequired = true,
    object? DefaultValue = null,
    string? Description = null,
    string? TargetColumn = null,
    string? TargetTable = null,
    string? ComparisonOperator = null);

/// <summary>
/// Represents a projected output column of a declarative SQL endpoint.
/// </summary>
public sealed record SqlEndpointProjection(
    string ColumnName,
    string? Alias,
    Type InferredClrType,
    bool IsNullable = true,
    string? Description = null);

/// <summary>
/// Immutable definition of a declarative SQL endpoint exposed as REST, GraphQL and MCP.
/// </summary>
public sealed record SqlEndpointDefinition(
    string Name,
    string Summary,
    string RawSql,
    string? DataSource = null,
    IReadOnlyList<SqlEndpointParameter>? Parameters = null,
    IReadOnlyList<SqlEndpointProjection>? Projections = null,
    IReadOnlyList<string>? ReferencedTables = null,
    string? HttpMethod = "GET",
    int TimeoutSeconds = 30)
{
    public IReadOnlyList<SqlEndpointParameter> Parameters { get; init; } = Parameters ?? [];
    public IReadOnlyList<SqlEndpointProjection> Projections { get; init; } = Projections ?? [];
    public IReadOnlyList<string> ReferencedTables { get; init; } = ReferencedTables ?? [];
}
