namespace Autheris.GraphQL.Catalog;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Application.Data.Interfaces;
using Autheris.Application.Data.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using HotChocolate;
using HotChocolate.Execution.Processing;
using HotChocolate.Language;
using HotChocolate.Resolvers;
using HotChocolate.Types;
using HotChocolate.Types.Descriptors;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

/// <summary>
/// Model for GraphQL Relay PageInfo.
/// </summary>
public sealed record GraphQlPageInfo(
    bool HasNextPage,
    bool HasPreviousPage,
    string? StartCursor,
    string? EndCursor);

/// <summary>
/// Model for a GraphQL Relay Edge wrapping a node JsonElement and its cryptographic cursor.
/// </summary>
public sealed record GraphQlEdge(
    string Cursor,
    JsonElement Node);

/// <summary>
/// Model for a GraphQL Relay Connection.
/// </summary>
public sealed record GraphQlConnection(
    IReadOnlyList<GraphQlEdge> Edges,
    GraphQlPageInfo PageInfo,
    int? TotalCount = null);

/// <summary>
/// REQ-GQL-420: Relay-compliant keyset cursor connections for governed catalog tables.
/// Generates and validates cryptographic HMAC keyset cursors binding tenant, table,
/// key columns, directions, and query shape to prevent full sorts on the underlying database.
/// </summary>
internal static class ConnectionGraphQlTypes
{
    public const string PageInfoTypeName = "PageInfo";

    public static void RegisterShared(List<ITypeSystemMember> types)
    {
        types.Add(new ObjectType(d =>
        {
            d.Name(PageInfoTypeName);
            d.Description("Information about pagination in a connection.");
            d.Field("hasNextPage")
                .Type<NonNullType<BooleanType>>()
                .Description("When paginating forwards, are there more items?")
                .Resolve(ctx => ctx.Parent<GraphQlPageInfo>().HasNextPage);
            d.Field("hasPreviousPage")
                .Type<NonNullType<BooleanType>>()
                .Description("When paginating backwards, are there more items?")
                .Resolve(ctx => ctx.Parent<GraphQlPageInfo>().HasPreviousPage);
            d.Field("startCursor")
                .Type<StringType>()
                .Description("When paginating backwards, the cursor to continue.")
                .Resolve(ctx => ctx.Parent<GraphQlPageInfo>().StartCursor);
            d.Field("endCursor")
                .Type<StringType>()
                .Description("When paginating forwards, the cursor to continue.")
                .Resolve(ctx => ctx.Parent<GraphQlPageInfo>().EndCursor);
        }));
    }

    public static void RegisterTable(List<ITypeSystemMember> types, CatalogTableType table)
    {
        var edgeTypeName = $"{table.TypeName}Edge";
        var connectionTypeName = $"{table.TypeName}Connection";

        types.Add(new ObjectType(d =>
        {
            d.Name(edgeTypeName);
            d.Description($"An edge in a connection for {table.TypeName}.");
            d.Field("cursor")
                .Type<NonNullType<StringType>>()
                .Description("A cursor for use in pagination.")
                .Resolve(ctx => ctx.Parent<GraphQlEdge>().Cursor);
            d.Field("node")
                .Type(new NonNullTypeNode(new NamedTypeNode(table.TypeName)))
                .Description("The item at the end of the edge.")
                .Resolve(ctx => ctx.Parent<GraphQlEdge>().Node);
        }));

        types.Add(new ObjectType(d =>
        {
            d.Name(connectionTypeName);
            d.Description($"A connection to a list of {table.TypeName} items.");
            d.Field("edges")
                .Type(new NonNullTypeNode(new ListTypeNode(new NonNullTypeNode(new NamedTypeNode(edgeTypeName)))))
                .Description("A list of edges.")
                .Resolve(ctx => ctx.Parent<GraphQlConnection>().Edges);
            d.Field("pageInfo")
                .Type(new NonNullTypeNode(new NamedTypeNode(PageInfoTypeName)))
                .Description("Information to aid in pagination.")
                .Resolve(ctx => ctx.Parent<GraphQlConnection>().PageInfo);
            d.Field("totalCount")
                .Type<IntType>()
                .Description("Identifies the total count of items in the connection.")
                .Resolve(ctx => ctx.Parent<GraphQlConnection>().TotalCount);
        }));
    }

    public static void AddQueryField(IObjectTypeDescriptor query, CatalogTableType table, CatalogSchemaModel schemaModel)
    {
        var connectionFieldName = $"{table.QueryFieldName}Connection";
        var connectionTypeName = $"{table.TypeName}Connection";

        query.Field(connectionFieldName)
            .Description($"Relay-compliant keyset cursor connection for {table.TypeName}. Uses cryptographic HMAC keyset cursors to prevent full sorts on the source.")
            .Type(new NonNullTypeNode(new NamedTypeNode(connectionTypeName)))
            .Argument("first", a => a.Type(new NamedTypeNode("Int")).DefaultValue(100))
            .Argument("after", a => a.Type(new NamedTypeNode("String")))
            .Argument("where", a => a.Type(new NamedTypeNode(table.FilterTypeName)))
            .Argument("orderBy", a => a.Type(new ListTypeNode(new NonNullTypeNode(new NamedTypeNode(table.OrderByTypeName)))))
            .Resolve(async ctx => await ResolveConnectionAsync(ctx, table, schemaModel).ConfigureAwait(false));
    }

    private static async Task<GraphQlConnection> ResolveConnectionAsync(
        IResolverContext ctx,
        CatalogTableType table,
        CatalogSchemaModel schemaModel)
    {
        var treeService = ctx.Service<IGovernedTreeQueryService>();
        var cursorEncoder = ctx.Services.GetService<IKeysetCursorEncoder>() ?? new KeysetCursorEncoder();
        var httpContext = ctx.Service<IHttpContextAccessor>()?.HttpContext;
        var principal = (ctx.ContextData.TryGetValue(nameof(ClaimsPrincipal), out var cp) && cp is ClaimsPrincipal p)
            ? p
            : httpContext?.User;

        if (principal == null)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetMessage("Authentication required.")
                .SetCode("UNAUTHORIZED")
                .Build());
        }

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
            // Fallback
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

        // 1. Resolve Primary Key Columns
        var pkCols = table.Metadata.PrimaryKeyColumns.Count > 0
            ? table.Metadata.PrimaryKeyColumns
            : table.Metadata.Indexes.FirstOrDefault(idx => idx.IsUnique)?.Columns;

        if (pkCols == null || pkCols.Count == 0)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetMessage("Table requires a primary key or unique index for keyset pagination.")
                .SetCode("KEYSET_PAGINATION_UNAVAILABLE")
                .Build());
        }

        var directions = ResolveKeyDirections(table.Metadata, pkCols);

        // 2. Parse arguments
        int first = ctx.ArgumentValue<int?>("first") ?? 100;

        if (first < 0 || first > maxResponseRows)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetMessage(GatewayPageRangeException.FixedMessage)
                .SetCode("INVALID_QUERY")
                .Build());
        }

        string? after = ctx.ArgumentValue<string?>("after");

        // 3. Resolve Tenant and Caller SID
        TenantId tenantId;
        if (Autheris.Domain.Tenancy.TenantClaimReader.Read(principal) is { Kind: Autheris.Domain.Tenancy.ClaimTenantKind.Valid } claimTenant)
        {
            tenantId = claimTenant.Tenant;
        }
        else if (ctx.ContextData.TryGetValue("TenantId", out var tObj) && tObj is string tStr && TenantId.TryParse(tStr, out var ctxTenant))
        {
            tenantId = ctxTenant;
        }
        else
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetMessage("Access denied: The caller has no resolvable tenant.")
                .SetCode("TENANT_UNRESOLVED")
                .Build());
        }

        string callerSid = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? principal.Identity?.Name
            ?? string.Empty;

        // 4. Collect requested selected column names for RequestHash
        var selectedColumns = CollectSelectedNodeColumns(ctx);
        var whereFilter = GraphQlTreeBuilder.ParseWhereArgument(ctx.Selection, table, ctx);

        // Build KeysetCursorContext
        var cursorContext = KeysetCursorContextFactory.ForGraphQl(
            tenantId: tenantId.Value,
            metadata: table.Metadata,
            keyColumns: pkCols,
            callerSid: callerSid,
            filter: whereFilter,
            select: selectedColumns,
            keyDirections: directions);

        // 5. Decode cursor and build seek predicate if 'after' is supplied
        TreeFilter? seekPredicate = null;
        if (!string.IsNullOrWhiteSpace(after))
        {
            IReadOnlyDictionary<string, object?> pkValues;
            try
            {
                pkValues = cursorEncoder.DecodeCursor(after, table.Metadata, cursorContext);
            }
            catch (InvalidCursorException ex)
            {
                throw new GraphQLException(ErrorBuilder.New()
                    .SetMessage(ex.Message)
                    .SetCode("INVALID_QUERY")
                    .Build());
            }

            seekPredicate = BuildSeekPredicate(table.Metadata, pkCols, directions, pkValues);
        }

        // 6. Default Ordering based on key columns and directions
        var defaultOrdering = new List<TreeOrder>(pkCols.Count);
        for (int i = 0; i < pkCols.Count; i++)
        {
            bool isDesc = directions != null && directions.Count > i && directions[i] == KeyColumnDirection.Descending;
            defaultOrdering.Add(new TreeOrder(pkCols[i], isDesc));
        }

        // 7. Build Connection Tree Node
        var queryNode = GraphQlTreeBuilder.BuildConnectionTree(
            ctx,
            table,
            schemaModel,
            first,
            maxResponseRows,
            seekPredicate,
            pkCols,
            defaultOrdering);

        // SEC-RG-08: Execute tree query
        JsonDocument doc;
        try
        {
            doc = await treeService.ExecuteAsync(principal, queryNode, headers, opId, ctx.RequestAborted).ConfigureAwait(false);
        }
        catch (GatewayPageRangeException)
        {
            throw new GraphQLException(ErrorBuilder.New().SetMessage(GatewayPageRangeException.FixedMessage).SetCode("INVALID_QUERY").Build());
        }
        catch (GatewayInvalidQueryException ex)
        {
            throw new GraphQLException(ErrorBuilder.New().SetMessage(ex.Message).SetCode("INVALID_QUERY").Build());
        }
        catch (TrinoSqlEngine.Ast.Emit.RequiredFilterColumnMissingException)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetMessage(TrinoSqlEngine.Ast.Emit.GovernedSqlErrorCodes.FixedMessage(TrinoSqlEngine.Ast.Emit.GovernedSqlErrorCodes.RequiredFilterColumnMissing))
                .SetCode(TrinoSqlEngine.Ast.Emit.GovernedSqlErrorCodes.RequiredFilterColumnMissing)
                .Build());
        }
        catch (GatewayForbiddenException)
        {
            throw new GraphQLException(ErrorBuilder.New().SetMessage("Access denied.").SetCode("ACCESS_DENIED").Build());
        }
        catch (GatewayThrottledException ex)
        {
            if (httpContext != null && !httpContext.Response.HasStarted)
            {
                httpContext.Response.Headers.RetryAfter = ex.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            throw new GraphQLException(ErrorBuilder.New().SetMessage(ex.Message).SetCode("TOO_MANY_REQUESTS").Build());
        }

        using (doc)
        {
            // SEC-412 Check: If any PK column is masked for the caller, reject keyset pagination (fail closed)
            var maskedColumns = treeService.GetMaskedColumns(principal, queryNode, opId);
            if (maskedColumns != null)
            {
                foreach (var pk in pkCols)
                {
                    if (maskedColumns.Any(m => m.Path.Count == 0 && string.Equals(m.Column, pk, StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new GraphQLException(ErrorBuilder.New()
                            .SetMessage("Keyset pagination is unavailable when primary key columns are masked.")
                            .SetCode("KEYSET_PAGINATION_UNAVAILABLE")
                            .Build());
                    }
                }
            }

            var root = doc.RootElement.Clone();
            var list = new List<JsonElement>();
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray())
                {
                    list.Add(item);
                }
            }

            bool hasNextPage = false;
            if (list.Count > first)
            {
                hasNextPage = true;
                list.RemoveAt(list.Count - 1);
            }

            // Format Edges with cursors
            var edges = new List<GraphQlEdge>(list.Count);
            foreach (var row in list)
            {
                var rowPk = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var col in pkCols)
                {
                    var canonCol = table.Metadata.GetColumn(col)?.ColumnName ?? col;
                    if (row.TryGetProperty(canonCol, out var prop) || row.TryGetProperty(col, out prop))
                    {
                        rowPk[canonCol] = ReadElementValue(prop);
                    }
                }

                var cursor = cursorEncoder.EncodeCursor(rowPk, cursorContext);
                edges.Add(new GraphQlEdge(cursor, row));
            }

            var pageInfo = new GraphQlPageInfo(
                HasNextPage: hasNextPage,
                HasPreviousPage: !string.IsNullOrWhiteSpace(after),
                StartCursor: edges.Count > 0 ? edges[0].Cursor : null,
                EndCursor: edges.Count > 0 ? edges[^1].Cursor : null);

            return new GraphQlConnection(edges, pageInfo, edges.Count);
        }
    }

    private static IReadOnlyList<string> CollectSelectedNodeColumns(IResolverContext ctx)
    {
        var result = new List<string>();
        var connectionSelectionSet = ctx.Operation.GetSelectionSet(ctx.Selection);
        foreach (var sel in connectionSelectionSet.Selections)
        {
            if (!sel.IsIncluded(ctx.IncludeFlags)) continue;
            if (string.Equals(sel.Field.Name, "edges", StringComparison.Ordinal))
            {
                var edgesSet = ctx.Operation.GetSelectionSet(sel);
                foreach (var edgeSel in edgesSet.Selections)
                {
                    if (!edgeSel.IsIncluded(ctx.IncludeFlags)) continue;
                    if (string.Equals(edgeSel.Field.Name, "node", StringComparison.Ordinal))
                    {
                        var nodeSet = ctx.Operation.GetSelectionSet(edgeSel);
                        foreach (var nodeField in nodeSet.Selections)
                        {
                            if (!nodeField.IsIncluded(ctx.IncludeFlags)) continue;
                            var name = nodeField.Field.Name;
                            if (!name.StartsWith("__", StringComparison.Ordinal))
                            {
                                result.Add(name);
                            }
                        }
                    }
                }
            }
        }

        return result;
    }

    private static TreeFilter BuildSeekPredicate(
        TableMetadata metadata,
        IReadOnlyList<string> pkCols,
        IReadOnlyList<KeyColumnDirection>? directions,
        IReadOnlyDictionary<string, object?> pkValues)
    {
        if (pkCols.Count == 1)
        {
            var col = metadata.GetColumn(pkCols[0])?.ColumnName ?? pkCols[0];
            bool isDesc = directions != null && directions.Count > 0 && directions[0] == KeyColumnDirection.Descending;
            return new TreeComparison(col, isDesc ? TreeFilterOperator.Lt : TreeFilterOperator.Gt, pkValues[col]);
        }

        var orBranches = new List<TreeFilter>(pkCols.Count);
        for (int i = 0; i < pkCols.Count; i++)
        {
            var andTerms = new List<TreeFilter>(i + 1);
            for (int j = 0; j < i; j++)
            {
                var prevCol = metadata.GetColumn(pkCols[j])?.ColumnName ?? pkCols[j];
                andTerms.Add(new TreeComparison(prevCol, TreeFilterOperator.Eq, pkValues[prevCol]));
            }

            var currCol = metadata.GetColumn(pkCols[i])?.ColumnName ?? pkCols[i];
            bool isDesc = directions != null && directions.Count > i && directions[i] == KeyColumnDirection.Descending;
            andTerms.Add(new TreeComparison(currCol, isDesc ? TreeFilterOperator.Lt : TreeFilterOperator.Gt, pkValues[currCol]));

            orBranches.Add(andTerms.Count == 1 ? andTerms[0] : new TreeAndFilter(andTerms));
        }

        return orBranches.Count == 1 ? orBranches[0] : new TreeOrFilter(orBranches);
    }

    private static IReadOnlyList<KeyColumnDirection>? ResolveKeyDirections(TableMetadata metadata, IReadOnlyList<string> pkCols)
    {
        if (metadata.PrimaryKeyColumns.Count > 0 && metadata.PrimaryKeyDirections.Count > 0)
        {
            return metadata.PrimaryKeyDirections;
        }

        var matchingIndex = metadata.Indexes.FirstOrDefault(idx =>
            idx.Columns.Count == pkCols.Count &&
            idx.Columns.SequenceEqual(pkCols, StringComparer.OrdinalIgnoreCase));
        if (matchingIndex?.Directions is { Count: > 0 } indexDirs)
        {
            return indexDirs;
        }

        return null;
    }

    private static object? ReadElementValue(JsonElement elem) => elem.ValueKind switch
    {
        JsonValueKind.Number => elem.TryGetInt64(out var l) ? (elem.TryGetInt32(out var i) ? i : l) : elem.GetDouble(),
        JsonValueKind.String => elem.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => elem.GetRawText()
    };
}
