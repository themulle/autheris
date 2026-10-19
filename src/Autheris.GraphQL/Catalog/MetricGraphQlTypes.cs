using System.Security.Claims;
using Autheris.Application.Analytics;
using Autheris.Domain.Options;
using HotChocolate;
using HotChocolate.Language;
using HotChocolate.Resolvers;
using HotChocolate.Types;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Autheris.GraphQL.Catalog;

/// <summary>
/// #112 A-8: the typed <c>queryMetrics</c> surface. The schema is global and identical for every caller, so the filter is a generic typed
/// shape (dimension name plus a one-of typed leaf), never generated from tenant manifest data.
/// </summary>
internal static class MetricGraphQlTypes
{
    private static NonNullTypeNode NonNull(string name) => new(new NamedTypeNode(name));

    private static NonNullTypeNode NonNullList(string name) => new(new ListTypeNode(NonNull(name)));

    public static void Register(List<ITypeSystemMember> types)
    {
        types.Add(new InputObjectType(d =>
        {
            d.Name(AggregateTypeNames.TypedFilter);
            d.Description("Exactly one typed filter leaf.");
            d.OneOf();
            d.Field("string").Type(new NamedTypeNode("AutherisStringFilter"));
            d.Field("int").Type(new NamedTypeNode("AutherisIntFilter"));
            d.Field("long").Type(new NamedTypeNode("AutherisLongFilter"));
            d.Field("float").Type(new NamedTypeNode("AutherisFloatFilter"));
            d.Field("decimal").Type(new NamedTypeNode("AutherisDecimalFilter"));
            d.Field("boolean").Type(new NamedTypeNode("AutherisBooleanFilter"));
            d.Field("dateTime").Type(new NamedTypeNode("AutherisDateTimeFilter"));
        }));

        types.Add(new InputObjectType(d =>
        {
            d.Name(AggregateTypeNames.DimensionFilter);
            d.Field("name").Type(NonNull("String"));
            d.Field("where").Type(NonNull(AggregateTypeNames.TypedFilter));
        }));

        types.Add(new InputObjectType(d =>
        {
            d.Name(AggregateTypeNames.MetricFilter);
            d.Field("and").Type(new ListTypeNode(NonNull(AggregateTypeNames.MetricFilter)));
            d.Field("or").Type(new ListTypeNode(NonNull(AggregateTypeNames.MetricFilter)));
            d.Field("not").Type(new NamedTypeNode(AggregateTypeNames.MetricFilter));
            d.Field("dimension").Type(new NamedTypeNode(AggregateTypeNames.DimensionFilter));
        }));

        types.Add(new InputObjectType(d =>
        {
            d.Name(AggregateTypeNames.MetricOrderBy);
            d.Description("Ordering of a metric result: exactly one of dimension or metric.");
            d.Field("dimension").Type(new NamedTypeNode("String"));
            d.Field("metric").Type(new NamedTypeNode("String"));
            d.Field("direction").Type(NonNull("AutherisSortDirection")).DefaultValue(new EnumValueNode("ASC"));
        }));

        types.Add(new ObjectType(d =>
        {
            d.Name(AggregateTypeNames.DimensionValue);
            d.Field("name").Type(NonNull("String")).Resolve(ctx => ctx.Parent<AggregateValueDto>().Alias);
            d.Field("kind").Type(NonNull(AggregateTypeNames.ValueKind)).Resolve(ctx => AggregateGraphQlTypes.KindName(ctx.Parent<AggregateValueDto>().Kind));
            d.Field("stringValue").Type(new NamedTypeNode("String")).Resolve(ctx => ctx.Parent<AggregateValueDto>().StringValue);
            d.Field("intValue").Type(new NamedTypeNode("Long")).Resolve(ctx => ctx.Parent<AggregateValueDto>().IntValue);
            d.Field("decimalValue").Type(new NamedTypeNode("Decimal")).Resolve(ctx => ctx.Parent<AggregateValueDto>().DecimalValue);
            d.Field("floatValue").Type(new NamedTypeNode("Float")).Resolve(ctx => ctx.Parent<AggregateValueDto>().FloatValue);
            d.Field("booleanValue").Type(new NamedTypeNode("Boolean")).Resolve(ctx => ctx.Parent<AggregateValueDto>().BooleanValue);
            d.Field("dateTimeValue").Type(new NamedTypeNode("DateTime")).Resolve(ctx => ctx.Parent<AggregateValueDto>().DateTimeValue);
        }));

        types.Add(new ObjectType(d =>
        {
            d.Name(AggregateTypeNames.MetricGroup);
            d.Field("dimensions").Type(NonNullList(AggregateTypeNames.DimensionValue)).Resolve(ctx => ctx.Parent<MetricGroupDto>().Dimensions);
            d.Field("rowCount").Type(NonNull("Long")).Resolve(ctx => ctx.Parent<MetricGroupDto>().RowCount);
            d.Field("isMergedOther").Type(NonNull("Boolean")).Resolve(ctx => ctx.Parent<MetricGroupDto>().IsMergedOther);
            d.Field("values").Type(NonNullList(AggregateTypeNames.AggregateValue)).Resolve(ctx => ctx.Parent<MetricGroupDto>().Values);
        }));

        types.Add(new ObjectType(d =>
        {
            d.Name(AggregateTypeNames.MetricDefinition);
            d.Field("name").Type(NonNull("String")).Resolve(ctx => ctx.Parent<MetricDefinitionDto>().Name);
            d.Field("type").Type(NonNull("String")).Resolve(ctx => ctx.Parent<MetricDefinitionDto>().Type);
            d.Field("label").Type(new NamedTypeNode("String")).Resolve(ctx => ctx.Parent<MetricDefinitionDto>().Label);
            d.Field("description").Type(new NamedTypeNode("String")).Resolve(ctx => ctx.Parent<MetricDefinitionDto>().Description);
            d.Field("unit").Type(new NamedTypeNode("String")).Resolve(ctx => ctx.Parent<MetricDefinitionDto>().Unit);
        }));

        types.Add(new ObjectType(d =>
        {
            d.Name(AggregateTypeNames.MetricQueryResult);
            d.Field("metrics").Type(NonNullList("String")).Resolve(ctx => ctx.Parent<MetricResultDto>().Metrics);
            d.Field("manifestVersion").Type(NonNull("String")).Resolve(ctx => ctx.Parent<MetricResultDto>().ManifestVersion);
            d.Field("groups").Type(NonNullList(AggregateTypeNames.MetricGroup)).Resolve(ctx => ctx.Parent<MetricResultDto>().Groups);
            d.Field("count").Type(NonNull("Long")).Resolve(ctx => ctx.Parent<MetricResultDto>().Count);
            d.Field("suppressed").Type(NonNull("Boolean")).Resolve(ctx => ctx.Parent<MetricResultDto>().Suppressed);
            d.Field("suppressedGroupsCount").Type(NonNull("Int")).Resolve(ctx => ctx.Parent<MetricResultDto>().SuppressedGroupsCount);
            d.Field("minimumGroupSize").Type(NonNull("Int")).Resolve(ctx => ctx.Parent<MetricResultDto>().MinimumGroupSize);
            d.Field("mergedIntoOther").Type(NonNull("Boolean")).Resolve(ctx => ctx.Parent<MetricResultDto>().MergedIntoOther);
            d.Field("truncated").Type(NonNull("Boolean")).Resolve(ctx => ctx.Parent<MetricResultDto>().Truncated);
            d.Field("noiseApplied").Type(NonNull("Boolean")).Resolve(ctx => ctx.Parent<MetricResultDto>().NoiseApplied);
            d.Field("metricDefinitions").Type(NonNullList(AggregateTypeNames.MetricDefinition)).Resolve(ctx => ctx.Parent<MetricResultDto>().MetricDefinitions);
        }));
    }

    public static void AddQueryField(IObjectTypeDescriptor query)
    {
        query.Field("queryMetrics")
            .Description("Governed semantic metric query with typed filter, ordering and result values.")
            .Type(NonNull(AggregateTypeNames.MetricQueryResult))
            .Argument("metrics", a => a.Type(NonNullList("String")))
            .Argument("groupBy", a => a.Type(new ListTypeNode(NonNull("String"))))
            .Argument("where", a => a.Type(new NamedTypeNode(AggregateTypeNames.MetricFilter)))
            .Argument("timeGranularity", a => a.Type(new NamedTypeNode("String")))
            .Argument("orderBy", a => a.Type(new ListTypeNode(NonNull(AggregateTypeNames.MetricOrderBy))))
            .Argument("limit", a => a.Type(new NamedTypeNode("Int")))
            .Resolve(async ctx => await ResolveAsync(ctx).ConfigureAwait(false));
    }

    internal static async Task<MetricResultDto> ResolveAsync(IResolverContext ctx)
    {
        var principal = ctx.Service<IHttpContextAccessor>()?.HttpContext?.User ?? new ClaimsPrincipal();
        if (principal.Identity?.IsAuthenticated != true)
        {
            throw AggregateErrors.Create("UNAUTHORIZED", AggregateErrors.UnauthorizedMessage);
        }

        var analytics = ctx.Services.GetService<IOptions<AnalyticsOptions>>()?.Value ?? new AnalyticsOptions();

        try
        {
            IValueNode? Argument(string name) => ctx.Selection.Arguments.TryGetValue(name, out var a) ? a.ValueLiteral : null;
            IValueNode? Variable(string name) => ctx.Variables.TryGetValue<IValueNode>(name, out var node) ? node : null;

            var parsed = MetricRequestParser.Parse(
                Argument("metrics"), Argument("groupBy"), Argument("where"), Argument("timeGranularity"), Argument("orderBy"), Argument("limit"),
                analytics.Aggregation, Variable);

            var request = new MetricQueryRequest(parsed.Metrics, parsed.GroupBy, null, parsed.TimeGranularity, parsed.OrderBy, parsed.Limit)
            {
                TypedWhere = parsed.Where
            };

            var result = await ctx.Service<ISemanticMetricQueryService>().QueryMetricsAsync(principal, request, ctx.RequestAborted).ConfigureAwait(false);
            return MetricResultMapper.Map(result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && AggregateFailureMapper.ForMetrics(ex) is { } mapped)
        {
            throw mapped;
        }
    }
}
