using Autheris.Application.Analytics;
using Autheris.GraphQL.Interceptors;
using HotChocolate.Language;
using HotChocolate.Resolvers;
using HotChocolate.Types;
using HotChocolate.Types.Descriptors;

namespace Autheris.GraphQL.Catalog;

/// <summary>
/// The flat aggregation surface: <c>&lt;table&gt;_groups</c> returns one row per group whose fields are the group columns and the
/// requested aggregates. It reads its selection, hands the same arguments to the shared parser and runs through
/// <see cref="AggregateExecutionCore"/>, so it has no data path of its own.
/// </summary>
internal static class FlatAggregateGraphQlTypes
{
    private static NonNullTypeNode NonNull(string name) => new(new NamedTypeNode(name));

    public static void RegisterTable(List<ITypeSystemMember> types, CatalogTableType table)
    {
        var names = AggregateTypeNames.For(table.TypeName);
        types.Add(new ObjectType(d =>
        {
            d.Name(names.GroupRow);
            d.Description($"One group of {table.TypeName}: selected column fields form the group set, count and aggregate fields are computed per group.");
            foreach (var field in FlatAggregateRowFields.For(table))
            {
                var fieldName = field.FieldName;
                switch (field.Role)
                {
                    case FlatAggregateRowFieldRole.Column:
                        var column = field.Column!;
                        var descriptor = d.Field(fieldName)
                            .Type(new NamedTypeNode(CatalogGraphQlTypeModule.GetScalarName(column.FieldType)))
                            .Resolve(ctx => ctx.Parent<FlatGroupRow>().Key.GetValueOrDefault(column.FieldName));
                        if (column.FieldType == CatalogFieldType.DateTime)
                        {
                            descriptor.Argument("bucket", a => a.Type(new NamedTypeNode(AggregateTypeNames.TimeBucketInterval)));
                            descriptor.Argument("timeZone", a => a.Type(new NamedTypeNode("String")));
                        }

                        break;
                    case FlatAggregateRowFieldRole.Count:
                        d.Field(fieldName).Type(NonNull("Long")).Resolve(ctx => ctx.Parent<FlatGroupRow>().Values.GetValueOrDefault(fieldName));
                        break;
                    case FlatAggregateRowFieldRole.MergedOther:
                        d.Field(fieldName).Type(NonNull("Boolean")).Resolve(ctx => ctx.Parent<FlatGroupRow>().IsMergedOther);
                        break;
                    default:
                        d.Field(fieldName)
                            .Type(new NamedTypeNode(FlatAggregateRowFields.ScalarName(field.Kind!.Value)))
                            .Resolve(ctx => ctx.Parent<FlatGroupRow>().Values.GetValueOrDefault(fieldName));
                        break;
                }
            }
        }));
    }

    public static IObjectFieldDescriptor AddQueryField(IObjectTypeDescriptor query, CatalogTableType table)
    {
        var names = AggregateTypeNames.For(table.TypeName);
        return query.Field(names.GroupsField)
            .Description($"Flat governed aggregation for {table.TypeName}: one row per group, selected columns are the group set (k-anonymity applies).")
            .Type(new NonNullTypeNode(new ListTypeNode(NonNull(names.GroupRow))))
            .Argument("where", a => a.Type(new NamedTypeNode(table.FilterTypeName)))
            .Argument("orderBy", a => a.Type(new ListTypeNode(NonNull(names.OrderBy))))
            .Argument("limit", a => a.Type(new NamedTypeNode("Int")).DefaultValue(AggregateArgumentParser.DefaultLimit))
            .Resolve(async ctx => await ResolveAsync(ctx, table).ConfigureAwait(false));
    }

    internal static Task<IReadOnlyList<FlatGroupRow>> ResolveAsync(IResolverContext ctx, CatalogTableType table)
    {
        FlatAggregateSelection? selection = null;
        return AggregateExecutionCore.ExecuteAsync<IReadOnlyList<FlatGroupRow>>(
            ctx,
            table,
            (analytics, variable) =>
            {
                _ = FactsBag(ctx);
                IValueNode? Argument(string name) => ctx.Selection.Arguments.TryGetValue(name, out var a) ? a.ValueLiteral : null;
                selection = FlatAggregateSelectionReader.Read(ReadSelections(ctx, table), table, variable);
                var (groupBy, aggregates, orderBy) = FlatAggregateSelectionReader.ToArgumentNodes(selection, table, Argument("orderBy"), variable);
                return AggregateArgumentParser.Parse(table, groupBy, aggregates, orderBy, Argument("limit"), analytics, variable);
            },
            (result, parsed, bucketColumns) =>
            {
                var dto = AggregateResultMapper.Map(result, table, parsed.GroupBy, parsed.Aggregates, bucketColumns);
                var rows = FlatGroupRowMapper.ToRows(dto, selection!, table);
                FactsBag(ctx)[ctx.ResponseName] = new AggregationFacts(
                    dto.Count, dto.Suppressed, dto.SuppressedGroupsCount, dto.MinimumGroupSize, dto.MinimumGroupSizeSource, dto.MergedIntoOther, dto.Truncated);
                return rows;
            });
    }

    private static System.Collections.Concurrent.ConcurrentDictionary<string, AggregationFacts> FactsBag(IResolverContext ctx) =>
        ctx.ContextData.TryGetValue(AggregationFactsMiddleware.ContextKey, out var bag) &&
        bag is System.Collections.Concurrent.ConcurrentDictionary<string, AggregationFacts> facts
            ? facts
            : throw AggregateErrors.Create(AggregationErrorCodes.NotSupported, AggregateErrors.NotSupportedMessage);

    private static List<(string ResponseKey, string FieldName, IValueNode? Bucket, IValueNode? TimeZone)> ReadSelections(
        IResolverContext ctx, CatalogTableType table)
    {
        var rowType = ctx.Schema.Types.GetType<ObjectType>(AggregateTypeNames.For(table.TypeName).GroupRow);
        var items = new List<(string, string, IValueNode?, IValueNode?)>();
        foreach (var selection in ctx.GetSelections(rowType, ctx.Selection))
        {
            if (selection.IsSkipped(ctx.IncludeConditionFlags) || !selection.IsIncluded(ctx.IncludeConditionFlags))
            {
                continue;
            }

            IValueNode? Argument(string name) => selection.Arguments.TryGetValue(name, out var a) ? a.ValueLiteral : null;
            items.Add((selection.ResponseName, selection.Field.Name, Argument("bucket"), Argument("timeZone")));
        }

        return items;
    }
}
