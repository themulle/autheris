using Autheris.Application.Analytics;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Options;
using HotChocolate.Language;
using HotChocolate.Resolvers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Autheris.GraphQL.Catalog;

/// <summary>
/// The one execution path of a typed aggregation root field (nested and flat): authentication, the typed <c>where</c>, the governed
/// service call and the failure mapping. The two shapes differ only in how they parse their arguments and map the result.
/// </summary>
internal static class AggregateExecutionCore
{
    public static async Task<T> ExecuteAsync<T>(
        IResolverContext ctx,
        CatalogTableType table,
        Func<AggregationOptions, Func<string, IValueNode?>, ParsedAggregateArguments> parse,
        Func<AggregationResult, ParsedAggregateArguments, IReadOnlySet<string>?, T> map)
    {
        var principal = Autheris.GraphQL.Security.GraphQlCallerPrincipal.Resolve(ctx);
        if (principal.Identity?.IsAuthenticated != true)
        {
            throw AggregateErrors.Create("UNAUTHORIZED", AggregateErrors.UnauthorizedMessage);
        }

        var analytics = ctx.Services.GetService<IOptions<AnalyticsOptions>>()?.Value ?? new AnalyticsOptions();

        try
        {
            IValueNode? Argument(string name) => ctx.Selection.Arguments.TryGetValue(name, out var a) ? a.ValueLiteral : null;
            IValueNode? Variable(string name) => ctx.Variables.TryGetValue<IValueNode>(name, out var node) ? node : null;

            var parsed = parse(analytics.Aggregation, Variable);

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
            await Autheris.GraphQL.Interceptors.DatasetFactsCollector.RecordAggregateRootAsync(ctx, principal, result).ConfigureAwait(false);
            var bucketColumns = parsed.TimeBuckets?.Select(b => b.Column).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return map(result, parsed, bucketColumns);
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
