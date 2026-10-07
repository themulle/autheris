using System.Security.Claims;
using System.Text.Json;
using Autheris.Application.Common;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Catalog;
using HotChocolate;
using HotChocolate.Execution.Configuration;
using HotChocolate.Language;
using HotChocolate.Resolvers;
using HotChocolate.Types;
using HotChocolate.Types.Descriptors;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Autheris.GraphQL.Catalog;

public sealed class CatalogGraphQlTypeModule : ITypeModule
{
    private readonly ITableMetadataRepository _metadataRepo;
    private readonly ITableRelationRepository _relationRepo;
    private readonly Microsoft.Extensions.Logging.ILogger<CatalogGraphQlTypeModule>? _logger;

    public CatalogGraphQlTypeModule(
        ITableMetadataRepository metadataRepo,
        ITableRelationRepository relationRepo,
        Microsoft.Extensions.Logging.ILogger<CatalogGraphQlTypeModule>? logger = null)
    {
        _metadataRepo = metadataRepo ?? throw new ArgumentNullException(nameof(metadataRepo));
        _relationRepo = relationRepo ?? throw new ArgumentNullException(nameof(relationRepo));
        _logger = logger;
    }

    public event EventHandler<EventArgs>? TypesChanged
    {
        add { }
        remove { }
    }

    public async ValueTask<IReadOnlyCollection<ITypeSystemMember>> CreateTypesAsync(
        IDescriptorContext context,
        CancellationToken cancellationToken)
    {
        var schemaModel = await CatalogSchemaModel.BuildAsync(_metadataRepo, _relationRepo, _logger, cancellationToken)
            .ConfigureAwait(false);

        var types = new List<ITypeSystemMember>();

        // 1. Sort Direction Enum
        types.Add(new EnumType(d =>
        {
            d.Name("AutherisSortDirection");
            d.Description("Specifies ordering direction.");
            d.Value("ASC").Description("Ascending order.");
            d.Value("DESC").Description("Descending order.");
        }));

        // 2. Operator Filter Input Types
        RegisterOperatorFilterTypes(types);

        // 3. For each table: Filter, OrderBy, ObjectType
        foreach (var table in schemaModel.Tables)
        {
            // Table Filter Input
            types.Add(new InputObjectType(d =>
            {
                d.Name(table.FilterTypeName);
                d.Description($"Filter input for {table.TypeName}.");
                d.Field("and").Type(new ListTypeNode(new NonNullTypeNode(new NamedTypeNode(table.FilterTypeName))));
                d.Field("or").Type(new ListTypeNode(new NonNullTypeNode(new NamedTypeNode(table.FilterTypeName))));
                d.Field("not").Type(new NamedTypeNode(table.FilterTypeName));

                foreach (var col in table.Columns)
                {
                    d.Field(col.FieldName).Type(new NamedTypeNode(GetFilterTypeName(col.FieldType)));
                }
            }));

            // Table OrderBy Input
            types.Add(new InputObjectType(d =>
            {
                d.Name(table.OrderByTypeName);
                d.Description($"Order-by input for {table.TypeName}.");
                foreach (var col in table.Columns)
                {
                    d.Field(col.FieldName).Type(new NamedTypeNode("AutherisSortDirection"));
                }
            }));

            // Table Object Type
            types.Add(new ObjectType(d =>
            {
                d.Name(table.TypeName);
                d.Description($"Entity type for {table.Metadata.Identifier}.");

                // Columns
                foreach (var col in table.Columns)
                {
                    var scalarName = GetScalarName(col.FieldType);
                    ITypeNode typeNode = col.IsNullable
                        ? new NamedTypeNode(scalarName)
                        : new NonNullTypeNode(new NamedTypeNode(scalarName));

                    d.Field(col.FieldName)
                        .Type(typeNode)
                        .Resolve(ctx => ResolveColumnValue(ctx, col));
                }

                // Relations
                foreach (var rel in table.Relations)
                {
                    if (rel.IsList)
                    {
                        var field = d.Field(rel.FieldName)
                            .Type(new NonNullTypeNode(new ListTypeNode(new NonNullTypeNode(new NamedTypeNode(rel.TargetTypeName)))))
                            .Argument("where", a => a.Type(new NamedTypeNode($"{rel.TargetTypeName}_filter")))
                            .Argument("orderBy", a => a.Type(new ListTypeNode(new NonNullTypeNode(new NamedTypeNode($"{rel.TargetTypeName}_order_by")))))
                            .Argument("first", a => a.Type(new NamedTypeNode("Int")).DefaultValue(100))
                            .Argument("offset", a => a.Type(new NamedTypeNode("Int")).DefaultValue(0))
                            .Resolve(ctx => ResolveRelationValue(ctx, rel));
                    }
                    else
                    {
                        d.Field(rel.FieldName)
                            .Type(new NamedTypeNode(rel.TargetTypeName))
                            .Argument("where", a => a.Type(new NamedTypeNode($"{rel.TargetTypeName}_filter")))
                            .Resolve(ctx => ResolveRelationValue(ctx, rel));
                    }
                }
            }));
        }

        // 4. Query Root Extension
        types.Add(new ObjectTypeExtension(d =>
        {
            d.Name("Query");

            foreach (var table in schemaModel.Tables)
            {
                d.Field(table.QueryFieldName)
                    .Description($"Query {table.TypeName}. Keyset paging can be performed via where: {{ id: {{ gt: $lastId }} }}, orderBy: [{{ id: ASC }}].")
                    .Type(new NonNullTypeNode(new ListTypeNode(new NonNullTypeNode(new NamedTypeNode(table.TypeName)))))
                    .Argument("where", a => a.Type(new NamedTypeNode(table.FilterTypeName)))
                    .Argument("orderBy", a => a.Type(new ListTypeNode(new NonNullTypeNode(new NamedTypeNode(table.OrderByTypeName)))))
                    .Argument("first", a => a.Type(new NamedTypeNode("Int")).DefaultValue(100))
                    .Argument("offset", a => a.Type(new NamedTypeNode("Int")).DefaultValue(0))
                    .Resolve(async ctx => await ResolveRootQueryAsync(ctx, table, schemaModel).ConfigureAwait(false));
            }
        }));

        return types;
    }

    private static void RegisterOperatorFilterTypes(List<ITypeSystemMember> types)
    {
        // AutherisStringFilter
        types.Add(new InputObjectType(d =>
        {
            d.Name("AutherisStringFilter");
            d.Field("eq").Type(new NamedTypeNode("String"));
            d.Field("neq").Type(new NamedTypeNode("String"));
            d.Field("gt").Type(new NamedTypeNode("String"));
            d.Field("gte").Type(new NamedTypeNode("String"));
            d.Field("lt").Type(new NamedTypeNode("String"));
            d.Field("lte").Type(new NamedTypeNode("String"));
            d.Field("in").Type(new ListTypeNode(new NonNullTypeNode(new NamedTypeNode("String"))));
            d.Field("nin").Type(new ListTypeNode(new NonNullTypeNode(new NamedTypeNode("String"))));
            d.Field("isNull").Type(new NamedTypeNode("Boolean"));
            d.Field("contains").Type(new NamedTypeNode("String"));
            d.Field("startsWith").Type(new NamedTypeNode("String"));
            d.Field("endsWith").Type(new NamedTypeNode("String"));
        }));

        // AutherisIntFilter
        types.Add(new InputObjectType(d =>
        {
            d.Name("AutherisIntFilter");
            d.Field("eq").Type(new NamedTypeNode("Int"));
            d.Field("neq").Type(new NamedTypeNode("Int"));
            d.Field("gt").Type(new NamedTypeNode("Int"));
            d.Field("gte").Type(new NamedTypeNode("Int"));
            d.Field("lt").Type(new NamedTypeNode("Int"));
            d.Field("lte").Type(new NamedTypeNode("Int"));
            d.Field("in").Type(new ListTypeNode(new NonNullTypeNode(new NamedTypeNode("Int"))));
            d.Field("nin").Type(new ListTypeNode(new NonNullTypeNode(new NamedTypeNode("Int"))));
            d.Field("isNull").Type(new NamedTypeNode("Boolean"));
        }));

        // AutherisLongFilter
        types.Add(new InputObjectType(d =>
        {
            d.Name("AutherisLongFilter");
            d.Field("eq").Type(new NamedTypeNode("Long"));
            d.Field("neq").Type(new NamedTypeNode("Long"));
            d.Field("gt").Type(new NamedTypeNode("Long"));
            d.Field("gte").Type(new NamedTypeNode("Long"));
            d.Field("lt").Type(new NamedTypeNode("Long"));
            d.Field("lte").Type(new NamedTypeNode("Long"));
            d.Field("in").Type(new ListTypeNode(new NonNullTypeNode(new NamedTypeNode("Long"))));
            d.Field("nin").Type(new ListTypeNode(new NonNullTypeNode(new NamedTypeNode("Long"))));
            d.Field("isNull").Type(new NamedTypeNode("Boolean"));
        }));

        // AutherisFloatFilter
        types.Add(new InputObjectType(d =>
        {
            d.Name("AutherisFloatFilter");
            d.Field("eq").Type(new NamedTypeNode("Float"));
            d.Field("neq").Type(new NamedTypeNode("Float"));
            d.Field("gt").Type(new NamedTypeNode("Float"));
            d.Field("gte").Type(new NamedTypeNode("Float"));
            d.Field("lt").Type(new NamedTypeNode("Float"));
            d.Field("lte").Type(new NamedTypeNode("Float"));
            d.Field("in").Type(new ListTypeNode(new NonNullTypeNode(new NamedTypeNode("Float"))));
            d.Field("nin").Type(new ListTypeNode(new NonNullTypeNode(new NamedTypeNode("Float"))));
            d.Field("isNull").Type(new NamedTypeNode("Boolean"));
        }));

        // AutherisDecimalFilter
        types.Add(new InputObjectType(d =>
        {
            d.Name("AutherisDecimalFilter");
            d.Field("eq").Type(new NamedTypeNode("Decimal"));
            d.Field("neq").Type(new NamedTypeNode("Decimal"));
            d.Field("gt").Type(new NamedTypeNode("Decimal"));
            d.Field("gte").Type(new NamedTypeNode("Decimal"));
            d.Field("lt").Type(new NamedTypeNode("Decimal"));
            d.Field("lte").Type(new NamedTypeNode("Decimal"));
            d.Field("in").Type(new ListTypeNode(new NonNullTypeNode(new NamedTypeNode("Decimal"))));
            d.Field("nin").Type(new ListTypeNode(new NonNullTypeNode(new NamedTypeNode("Decimal"))));
            d.Field("isNull").Type(new NamedTypeNode("Boolean"));
        }));

        // AutherisBooleanFilter
        types.Add(new InputObjectType(d =>
        {
            d.Name("AutherisBooleanFilter");
            d.Field("eq").Type(new NamedTypeNode("Boolean"));
            d.Field("neq").Type(new NamedTypeNode("Boolean"));
            d.Field("isNull").Type(new NamedTypeNode("Boolean"));
        }));

        // AutherisDateTimeFilter
        types.Add(new InputObjectType(d =>
        {
            d.Name("AutherisDateTimeFilter");
            d.Field("eq").Type(new NamedTypeNode("String"));
            d.Field("neq").Type(new NamedTypeNode("String"));
            d.Field("gt").Type(new NamedTypeNode("String"));
            d.Field("gte").Type(new NamedTypeNode("String"));
            d.Field("lt").Type(new NamedTypeNode("String"));
            d.Field("lte").Type(new NamedTypeNode("String"));
            d.Field("in").Type(new ListTypeNode(new NonNullTypeNode(new NamedTypeNode("String"))));
            d.Field("nin").Type(new ListTypeNode(new NonNullTypeNode(new NamedTypeNode("String"))));
            d.Field("isNull").Type(new NamedTypeNode("Boolean"));
        }));
    }

    private static string GetScalarName(CatalogFieldType type) => type switch
    {
        CatalogFieldType.String => "String",
        CatalogFieldType.Int => "Int",
        CatalogFieldType.Long => "Long",
        CatalogFieldType.Float => "Float",
        CatalogFieldType.Decimal => "Decimal",
        CatalogFieldType.Boolean => "Boolean",
        CatalogFieldType.DateTime => "String",
        _ => "String"
    };

    private static string GetFilterTypeName(CatalogFieldType type) => type switch
    {
        CatalogFieldType.String => "AutherisStringFilter",
        CatalogFieldType.Int => "AutherisIntFilter",
        CatalogFieldType.Long => "AutherisLongFilter",
        CatalogFieldType.Float => "AutherisFloatFilter",
        CatalogFieldType.Decimal => "AutherisDecimalFilter",
        CatalogFieldType.Boolean => "AutherisBooleanFilter",
        CatalogFieldType.DateTime => "AutherisDateTimeFilter",
        _ => "AutherisStringFilter"
    };

    private static object? ReportMasked(IResolverContext ctx, CatalogColumnField col)
    {
        ctx.ReportError(ErrorBuilder.New()
            .SetMessage($"Column '{col.ColumnName}' is masked.")
            .SetCode("MASKED")
            .SetPath(ctx.Path)
            .Build());
        return null;
    }

    private static object? ResolveColumnValue(IResolverContext ctx, CatalogColumnField col)
    {
        var parent = ctx.Parent<JsonElement>();
        if (parent.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!parent.TryGetProperty(col.ColumnName, out var prop) &&
            !parent.TryGetProperty(col.FieldName, out prop))
        {
            return null;
        }

        if (prop.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return col.FieldType switch
        {
            CatalogFieldType.Int => prop.ValueKind == JsonValueKind.Number
                ? (prop.TryGetInt32(out var n) ? n : (int.TryParse(prop.GetRawText(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var pi) ? pi : ReportMasked(ctx, col)))
                : (int.TryParse(prop.GetString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var strN) ? strN : ReportMasked(ctx, col)),

            CatalogFieldType.Long => prop.ValueKind == JsonValueKind.Number
                ? (prop.TryGetInt64(out var l) ? l : (long.TryParse(prop.GetRawText(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var pl) ? pl : ReportMasked(ctx, col)))
                : (long.TryParse(prop.GetString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var strL) ? strL : ReportMasked(ctx, col)),

            CatalogFieldType.Float => prop.ValueKind == JsonValueKind.Number
                ? (prop.TryGetDouble(out var d) ? d : (double.TryParse(prop.GetRawText(), System.Globalization.NumberStyles.Float | System.Globalization.NumberStyles.AllowThousands, System.Globalization.CultureInfo.InvariantCulture, out var pd) ? pd : ReportMasked(ctx, col)))
                : (double.TryParse(prop.GetString(), System.Globalization.NumberStyles.Float | System.Globalization.NumberStyles.AllowThousands, System.Globalization.CultureInfo.InvariantCulture, out var strD) ? strD : ReportMasked(ctx, col)),

            CatalogFieldType.Decimal => prop.ValueKind == JsonValueKind.Number
                ? (prop.TryGetDecimal(out var m) ? m : (decimal.TryParse(prop.GetRawText(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var pm) ? pm : ReportMasked(ctx, col)))
                : (decimal.TryParse(prop.GetString(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var strM) ? strM : ReportMasked(ctx, col)),

            CatalogFieldType.Boolean => prop.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => prop.TryGetInt64(out var i)
                    ? i != 0
                    : (prop.TryGetDouble(out var dVal) ? Math.Abs(dVal) > double.Epsilon : ReportMasked(ctx, col)),
                JsonValueKind.String => string.Equals(prop.GetString(), "true", StringComparison.OrdinalIgnoreCase) ? true
                    : (string.Equals(prop.GetString(), "false", StringComparison.OrdinalIgnoreCase) ? false
                    : (prop.GetString() == "1" ? true
                    : (prop.GetString() == "0" ? false
                    : ReportMasked(ctx, col)))),
                _ => ReportMasked(ctx, col)
            },

            CatalogFieldType.String or CatalogFieldType.DateTime => prop.ValueKind == JsonValueKind.String ? prop.GetString() : prop.GetRawText(),
            _ => prop.ToString()
        };
    }

    private static object? ResolveRelationValue(IResolverContext ctx, CatalogRelationField rel)
    {
        var parent = ctx.Parent<JsonElement>();
        if (parent.ValueKind != JsonValueKind.Object)
        {
            return rel.IsList ? Array.Empty<JsonElement>() : null;
        }

        var key = ctx.ResponseName;
        if (!parent.TryGetProperty(key, out var prop) &&
            !parent.TryGetProperty(rel.FieldName, out prop))
        {
            return rel.IsList ? Array.Empty<JsonElement>() : null;
        }

        if (prop.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return rel.IsList ? Array.Empty<JsonElement>() : null;
        }

        if (rel.IsList)
        {
            if (prop.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<JsonElement>();
            }

            var list = new List<JsonElement>(prop.GetArrayLength());
            foreach (var item in prop.EnumerateArray())
            {
                list.Add(item);
            }
            return list;
        }

        return prop;
    }

    private static async Task<IReadOnlyList<JsonElement>> ResolveRootQueryAsync(
        IResolverContext ctx,
        CatalogTableType table,
        CatalogSchemaModel schema)
    {
        var treeService = ctx.Service<IGovernedTreeQueryService>();
        var httpContextAccessor = ctx.Service<IHttpContextAccessor>();
        var httpContext = httpContextAccessor?.HttpContext;
        var principal = httpContext?.User ?? new ClaimsPrincipal();

        IReadOnlyDictionary<string, string[]>? headers = null;
        if (httpContext?.Request?.Headers is { Count: > 0 } reqHeaders)
        {
            headers = reqHeaders.ToDictionary(h => h.Key, h => h.Value.Where(v => v != null).Select(v => v!).ToArray(), StringComparer.OrdinalIgnoreCase);
        }

        int maxResponseRows = 1000;
        try
        {
            var options = ctx.Service<IOptions<GatewayOptions>>();
            if (options?.Value?.GraphQL?.MaxResponseRows is { } max && max > 0)
            {
                maxResponseRows = max;
            }
        }
        catch
        {
            // Fallback to default
        }

        string opId;
        lock (ctx.ContextData)
        {
            if (!ctx.ContextData.TryGetValue("AutherisOperationId", out var opIdObj) || opIdObj is not string existingOpId)
            {
                opId = Guid.NewGuid().ToString("N");
                ctx.ContextData["AutherisOperationId"] = opId;
            }
            else
            {
                opId = existingOpId;
            }
        }

        try
        {
            var queryNode = GraphQlTreeBuilder.BuildTree(ctx, table, schema, maxResponseRows);
            using var doc = await treeService.ExecuteAsync(principal, queryNode, headers, opId, ctx.RequestAborted)
                .ConfigureAwait(false);

            var root = doc.RootElement.Clone();
            if (root.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<JsonElement>();
            }

            var list = new List<JsonElement>(root.GetArrayLength());
            foreach (var item in root.EnumerateArray())
            {
                list.Add(item);
            }
            return list;
        }
        catch (GatewayInvalidQueryException ex)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetMessage(ex.Message)
                .SetCode("INVALID_QUERY")
                .Build());
        }
        catch (GatewayForbiddenException)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetMessage("Access denied.")
                .SetCode("ACCESS_DENIED")
                .Build());
        }
        catch (GatewayThrottledException ex)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetMessage(ex.Message)
                .SetCode("TOO_MANY_REQUESTS")
                .Build());
        }
        catch (GatewaySecurityException ex) when (string.Equals(ex.ErrorCode, "RESPONSE_TOO_LARGE", StringComparison.OrdinalIgnoreCase))
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetMessage(ex.Message)
                .SetCode("RESPONSE_TOO_LARGE")
                .Build());
        }
        catch (Exception ex)
        {
            var classified = DataAccessErrorClassifier.Classify(ex);
            if (classified == DataAccessErrorKind.Timeout)
            {
                throw new GraphQLException(ErrorBuilder.New()
                    .SetMessage("The database query timed out.")
                    .SetCode("TIMEOUT")
                    .Build());
            }
            if (classified == DataAccessErrorKind.Unavailable)
            {
                throw new GraphQLException(ErrorBuilder.New()
                    .SetMessage("The database is currently unavailable.")
                    .SetCode("UNAVAILABLE")
                    .Build());
            }
            throw;
        }
    }
}
