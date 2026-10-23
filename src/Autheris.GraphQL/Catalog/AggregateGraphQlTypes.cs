using System.Security.Claims;
using Autheris.Application.Analytics;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Options;
using HotChocolate;
using HotChocolate.Language;
using HotChocolate.Resolvers;
using HotChocolate.Types;
using HotChocolate.Types.Descriptors;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Autheris.GraphQL.Catalog;

/// <summary>
/// #112 A-1: the typed aggregation surface. Shared types are registered once; per table the column enum, the two inputs, the typed key,
/// the group and the result type are generated from the same <see cref="CatalogTableType.Columns"/> as the entity type (C-10).
/// </summary>
internal static class AggregateGraphQlTypes
{
    private static NonNullTypeNode NonNull(string name) => new(new NamedTypeNode(name));

    private static NonNullTypeNode NonNullList(string name) => new(new ListTypeNode(NonNull(name)));

    public static string KindName(AggregateValueKind kind) => kind switch
    {
        AggregateValueKind.String => "STRING",
        AggregateValueKind.Int => "INT",
        AggregateValueKind.Decimal => "DECIMAL",
        AggregateValueKind.Float => "FLOAT",
        AggregateValueKind.Boolean => "BOOLEAN",
        _ => "DATE_TIME"
    };

    /// <summary>Types shared by the typed aggregate fields and <c>queryMetrics</c>.</summary>
    public static void RegisterShared(List<ITypeSystemMember> types)
    {
        types.Add(new EnumType(d =>
        {
            d.Name(AggregateTypeNames.AggregateFn);
            d.Description("Aggregate function of a typed aggregate selection.");
            foreach (var name in new[] { "COUNT", "COUNT_DISTINCT", "SUM", "AVG", "MIN", "MAX" })
            {
                d.Value(name).Name(name);
            }
        }));

        types.Add(new EnumType(d =>
        {
            d.Name(AggregateTypeNames.ValueKind);
            d.Description("Declared type of a value; stable per alias, independent of the value being null.");
            foreach (var name in new[] { "STRING", "INT", "DECIMAL", "FLOAT", "BOOLEAN", "DATE_TIME" })
            {
                d.Value(name).Name(name);
            }
        }));

        types.Add(new EnumType(d =>
        {
            d.Name(AggregateTypeNames.TimeBucketInterval);
            d.Description("Time bucketing interval for timestamp group-by fields.");
            foreach (var name in new[] { "HOUR", "DAY", "WEEK", "MONTH", "QUARTER", "YEAR" })
            {
                d.Value(name).Name(name);
            }
        }));

        types.Add(new ObjectType(d =>
        {
            d.Name(AggregateTypeNames.AggregateValue);
            d.Description("Exactly one *Value field is set (the one named by kind); all are null when the aggregate is NULL (for example SUM over no non-null rows).");
            d.Field("alias").Type(NonNull("String")).Resolve(ctx => ctx.Parent<AggregateValueDto>().Alias);
            d.Field("kind").Type(NonNull(AggregateTypeNames.ValueKind)).Resolve(ctx => KindName(ctx.Parent<AggregateValueDto>().Kind));
            d.Field("intValue").Type(new NamedTypeNode("Long")).Resolve(ctx => ctx.Parent<AggregateValueDto>().IntValue);
            d.Field("decimalValue").Type(new NamedTypeNode("Decimal")).Resolve(ctx => ctx.Parent<AggregateValueDto>().DecimalValue);
            d.Field("floatValue").Type(new NamedTypeNode("Float")).Resolve(ctx => ctx.Parent<AggregateValueDto>().FloatValue);
            d.Field("stringValue").Type(new NamedTypeNode("String")).Resolve(ctx => ctx.Parent<AggregateValueDto>().StringValue);
            d.Field("dateTimeValue").Type(new NamedTypeNode("DateTime")).Resolve(ctx => ctx.Parent<AggregateValueDto>().DateTimeValue);
        }));
    }

    public static void RegisterTable(List<ITypeSystemMember> types, CatalogTableType table)
    {
        var names = AggregateTypeNames.For(table.TypeName);
        var columnEnum = AggregateColumnEnum.Build(table);

        types.Add(new EnumType(d =>
        {
            d.Name(names.Column);
            d.Description($"Column field names of {table.TypeName}, spelled exactly like the fields of the entity type.");
            foreach (var (value, _) in columnEnum)
            {
                // Name() keeps the exact spelling; the default naming convention would upper-case it.
                d.Value(value).Name(value);
            }
        }));

        types.Add(new EnumType(d =>
        {
            d.Name(names.GroupByFields);
            d.Description($"Group by field identifiers of {table.TypeName}.");
            foreach (var (value, _) in columnEnum)
            {
                d.Value(value).Name(value);
            }
        }));

        types.Add(new InputObjectType(d =>
        {
            d.Name(names.TimeBucketInput);
            d.Description($"Time-bucketed group-by specification for {table.TypeName}.");
            d.Field("column").Type(NonNull(names.Column));
            d.Field("bucket").Type(NonNull(AggregateTypeNames.TimeBucketInterval));
            d.Field("timeZone").Type(new NamedTypeNode("String")).DefaultValue(new StringValueNode("UTC"));
        }));

        types.Add(new InputObjectType(d =>
        {
            d.Name(names.Input);
            d.Description($"One aggregate over {table.TypeName}.");
            d.Field("fn").Type(NonNull(AggregateTypeNames.AggregateFn));
            d.Field("column").Type(new NamedTypeNode(names.Column));
            d.Field("alias").Type(NonNull("String"));
        }));

        types.Add(new InputObjectType(d =>
        {
            d.Name(names.OrderBy);
            d.Description($"Ordering of an aggregate result of {table.TypeName}: exactly one of column or alias.");
            d.Field("column").Type(new NamedTypeNode(names.Column));
            d.Field("alias").Type(new NamedTypeNode("String"));
            d.Field("direction").Type(NonNull("AutherisSortDirection")).DefaultValue(new EnumValueNode("ASC"));
        }));

        types.Add(new ObjectType(d =>
        {
            d.Name(names.Key);
            d.Description("Typed group key: filled only for the columns named in groupBy, all null on the merged OTHER row.");
            foreach (var column in table.Columns)
            {
                var fieldName = column.FieldName;
                d.Field(fieldName)
                    .Type(new NamedTypeNode(CatalogGraphQlTypeModule.GetScalarName(column.FieldType)))
                    .Resolve(ctx => ctx.Parent<IReadOnlyDictionary<string, object?>>().GetValueOrDefault(fieldName));
            }
        }));

        types.Add(new ObjectType(d =>
        {
            d.Name(names.Group);
            d.Field("key").Type(NonNull(names.Key)).Resolve(ctx => ctx.Parent<AggregateGroupDto>().Key);
            d.Field("rowCount").Type(NonNull("Long")).Resolve(ctx => ctx.Parent<AggregateGroupDto>().RowCount);
            d.Field("isMergedOther").Type(NonNull("Boolean")).Resolve(ctx => ctx.Parent<AggregateGroupDto>().IsMergedOther);
            d.Field("aggregates").Type(NonNullList(AggregateTypeNames.AggregateValue)).Resolve(ctx => ctx.Parent<AggregateGroupDto>().Aggregates);
        }));

        types.Add(new ObjectType(d =>
        {
            d.Name(names.Result);
            d.Description("Governed aggregation result with k-anonymity and differential privacy metadata.");
            d.Field("groups").Type(NonNullList(names.Group)).Resolve(ctx => ctx.Parent<AggregateResultDto>().Groups);
            d.Field("count").Type(NonNull("Long")).Resolve(ctx => ctx.Parent<AggregateResultDto>().Count);
            d.Field("suppressed").Type(NonNull("Boolean")).Resolve(ctx => ctx.Parent<AggregateResultDto>().Suppressed);
            d.Field("suppressedGroupsCount").Type(NonNull("Int")).Resolve(ctx => ctx.Parent<AggregateResultDto>().SuppressedGroupsCount);
            d.Field("minimumGroupSize").Type(NonNull("Int")).Resolve(ctx => ctx.Parent<AggregateResultDto>().MinimumGroupSize);
            d.Field("mergedIntoOther").Type(NonNull("Boolean")).Resolve(ctx => ctx.Parent<AggregateResultDto>().MergedIntoOther);
            d.Field("truncated").Type(NonNull("Boolean")).Resolve(ctx => ctx.Parent<AggregateResultDto>().Truncated);
            d.Field("noiseApplied").Type(NonNull("Boolean")).Resolve(ctx => ctx.Parent<AggregateResultDto>().NoiseApplied);
        }));
    }

    public static void AddQueryField(IObjectTypeDescriptor query, CatalogTableType table)
    {
        var names = AggregateTypeNames.For(table.TypeName);
        query.Field(names.Field)
            .Description($"Typed governed aggregation for {table.TypeName} with k-anonymity and differential privacy.")
            .Type(NonNull(names.Result))
            .Argument("groupBy", a => a.Type(new ListTypeNode(NonNull(names.Column))))
            .Argument("timeBuckets", a => a.Type(new ListTypeNode(NonNull(names.TimeBucketInput))))
            .Argument("aggregates", a => a.Type(NonNullList(names.Input)))
            .Argument("where", a => a.Type(new NamedTypeNode(table.FilterTypeName)))
            .Argument("orderBy", a => a.Type(new ListTypeNode(NonNull(names.OrderBy))))
            .Argument("limit", a => a.Type(new NamedTypeNode("Int")).DefaultValue(AggregateArgumentParser.DefaultLimit))
            .Resolve(async ctx => await ResolveAsync(ctx, table).ConfigureAwait(false));
    }

    internal static async Task<AggregateResultDto> ResolveAsync(IResolverContext ctx, CatalogTableType table)
    {
        var principal = ctx.Service<IHttpContextAccessor>()?.HttpContext?.User ?? new ClaimsPrincipal();
        if (principal.Identity?.IsAuthenticated != true)
        {
            throw AggregateErrors.Create("UNAUTHORIZED", AggregateErrors.UnauthorizedMessage);
        }

        var analytics = ctx.Services.GetService<IOptions<AnalyticsOptions>>()?.Value ?? new AnalyticsOptions();
        if (!analytics.Aggregation.Enabled)
        {
            throw AggregateErrors.Create("AGGREGATION_DISABLED", AggregateErrors.DisabledMessage);
        }

        try
        {
            IValueNode? Argument(string name) => ctx.Selection.Arguments.TryGetValue(name, out var a) ? a.ValueLiteral : null;
            IValueNode? Variable(string name) => ctx.Variables.TryGetValue<IValueNode>(name, out var node) ? node : null;

            var parsed = AggregateArgumentParser.Parse(
                table,
                Argument("groupBy"),
                Argument("aggregates"),
                Argument("orderBy"),
                Argument("limit"),
                analytics.Aggregation,
                Variable,
                Argument("timeBuckets") ?? Argument("time_buckets"));

            var whereLiteral = Argument("where") is VariableNode v ? Variable(v.Name.Value) : Argument("where");
            TableFilterClause? where = null;
            if (whereLiteral is ObjectValueNode { Fields.Count: > 0 } whereObject &&
                GraphQlTreeBuilder.ParseObjectFilter(whereObject, table, ctx) is { } filter)
            {
                where = TreeFilterClauseBuilder.Build(filter, ResolveColumn(table));
            }

            var query = new AggregationQuery(
                Table: table.Metadata.Identifier,
                GroupBy: parsed.GroupBy.Select(c => c.ColumnName).ToList(),
                Aggregates: parsed.Aggregates.Select(a => new AggregateSelection(a.Function, a.Column?.ColumnName, a.Alias)).ToList(),
                Where: where,
                OrderBy: parsed.OrderBy.Count == 0 ? null : parsed.OrderBy,
                Limit: parsed.Limit,
                Transport: "graphql",
                TimeBuckets: parsed.TimeBuckets);

            var result = await ctx.Service<IGovernedAggregationService>()
                .ExecuteAggregationAsync(principal, query, ctx.RequestAborted).ConfigureAwait(false);
            return AggregateResultMapper.Map(result, table, parsed.GroupBy, parsed.Aggregates);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && AggregateFailureMapper.ForAggregate(ex) is { } mapped)
        {
            throw mapped;
        }
    }

    private static Func<string, ColumnRef?> ResolveColumn(CatalogTableType table) => name =>
        table.Columns.FirstOrDefault(c => string.Equals(c.ColumnName, name, StringComparison.OrdinalIgnoreCase)) is { } column
            ? new ColumnRef(column.ColumnName, GraphQlTreeBuilder.ToKind(column.FieldType), ColumnValueKinds.IsDateOnly(column.DataType))
            : null;
}
