namespace Autheris.GraphQL.Catalog;

using System.Collections.Generic;
using Autheris.Domain.Model;

/// <summary>
/// Spells the arguments and result types of the three root fields of a catalog table (list, aggregate, keyset connection) exactly as
/// the schema builder declares them, so describe tools can name types without GraphQL introspection. Every name derives from the table
/// type name only, never from column data. A parity test compares these strings with the built schema.
/// </summary>
internal static class CatalogRootFieldSignatures
{
    public static IReadOnlyList<McpGraphQlArgument> ListArguments(CatalogTableType t) =>
    [
        new("where", t.FilterTypeName),
        new("orderBy", $"[{t.OrderByTypeName}!]"),
        new("first", "Int"),
        new("offset", "Int")
    ];

    public static GraphQlRootFieldSignature Aggregate(CatalogTableType t)
    {
        var n = AggregateTypeNames.For(t.TypeName);
        return new GraphQlRootFieldSignature(
            n.Field,
            n.Result,
            [
                new("groupBy", $"[{n.Column}!]"),
                new("timeBuckets", $"[{n.TimeBucketInput}!]"),
                new("aggregates", $"[{n.Input}!]!"),
                new("where", t.FilterTypeName),
                new("orderBy", $"[{n.OrderBy}!]"),
                new("limit", "Int")
            ]);
    }

    public static GraphQlRootFieldSignature Connection(CatalogTableType t) =>
        new(
            $"{t.QueryFieldName}Connection",
            $"{t.TypeName}Connection",
            [
                new("first", "Int"),
                new("after", "String"),
                new("where", t.FilterTypeName),
                new("orderBy", $"[{t.OrderByTypeName}!]")
            ]);
}
