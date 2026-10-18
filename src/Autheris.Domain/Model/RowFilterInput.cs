namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;
using Autheris.Domain.Interfaces;

/// <summary>
/// GraphQL input for dynamic approver row filters.
/// </summary>
public sealed class RowFilterInput
{
    public string? RowFilterSql { get; init; }
    public string? FilterGroup { get; init; }
    public List<RowFilterPredicateInput>? Predicates { get; init; }
}

/// <summary>
/// Individual predicate inside a RowFilterInput.
/// </summary>
public sealed class RowFilterPredicateInput
{
    public string ColumnName { get; init; } = string.Empty;
    public string Operator { get; init; } = "EQ";
    public string? ValueType { get; init; }
    public string? ValueJson { get; init; }
    public string? Value { get; init; }
    public List<string>? Values { get; init; }
}

/// <summary>
/// Approver column masking override specification.
/// </summary>
public sealed class ColumnMaskingInput
{
    public List<ColumnMaskRuleInput>? Rules { get; init; }
}

public sealed class ColumnMaskRuleInput
{
    public string ColumnName { get; init; } = string.Empty;
    public string? MaskingType { get; init; }
    public ColumnAccessLevel MaskingLevel { get; init; } = ColumnAccessLevel.Deny;
}

/// <summary>
/// Structured operational context provided by a requester.
/// </summary>
public sealed class AccessRequestContextInput
{
    public string? Application { get; init; }
    public string? Tool { get; init; }
    public string? Justification { get; init; }
    public string? ExpectedDuration { get; init; }
}
