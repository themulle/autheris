namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;
using Autheris.Domain.Common;

public sealed record SqlAstNode(
    TableIdentifier Table,
    string Alias,
    IReadOnlyList<string> ProjectedColumns,
    IReadOnlyList<SqlAstNode>? Children = null,
    string? ParentForeignKeyColumn = null,
    string? ChildForeignKeyColumn = null,
    string? WhereFilter = null,
    int? Limit = null,
    IReadOnlyDictionary<string, string>? ColumnTypes = null
);
