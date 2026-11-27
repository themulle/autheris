namespace Autheris.GraphQL.Interceptors;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Common;
using Autheris.GraphQL.Security;
using HotChocolate.Resolvers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Per-request collector of the opt-in <c>extensions.autheris.roots</c> facts. Resolvers record the facts of a root that was
/// read successfully; <see cref="DatasetFactsMiddleware"/> turns them into plain response data. A fact entry exists only
/// when the read succeeded and the table is catalog-visible to the caller. <c>rowFilterApplied</c> means "a row filter
/// defined by the data owner or by consent applies to you", not "all rows are returned"; tenant isolation, virtual and
/// mandatory filters are intentionally not reported.
/// </summary>
public sealed class DatasetFactsCollector
{
    public const string ContextKey = "Autheris.DatasetFactsCollector";

    internal const int MaxTags = 64;
    internal const int MaxTagLength = 128;
    internal const int MaxRelations = 200;

    private readonly ConcurrentDictionary<string, RootFacts> _roots = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<TableIdentifier, Lazy<Task<VisibleDataset?>>> _visibility = new();

    internal sealed record VisibleDataset(string Dataset, IReadOnlyList<string> Tags);

    internal sealed record Facts(VisibleDataset Visible, IReadOnlyList<string> RowFilters);

    internal sealed record RelationFacts(string Path, Facts Facts);

    internal sealed record AggregateFacts(int? SuppressedGroupsCount, int MinimumGroupSize, bool MergedIntoOther);

    internal sealed record RootFacts(Facts Facts, bool Truncated, IReadOnlyList<RelationFacts>? Relations, AggregateFacts? Aggregate);

    internal IReadOnlyDictionary<string, RootFacts> Roots => _roots;

    internal static DatasetFactsCollector? From(IResolverContext ctx) =>
        ctx.ContextData.TryGetValue(ContextKey, out var value) ? value as DatasetFactsCollector : null;

    internal static async ValueTask RecordTreeRootAsync(
        IResolverContext ctx,
        IGovernedTreeQueryService tree,
        ClaimsPrincipal? principal,
        TreeQueryNode node,
        string operationId,
        bool truncated)
    {
        var collector = From(ctx);
        if (collector is null)
        {
            return;
        }

        try
        {
            var facts = tree.GetDatasetFacts(principal, node, operationId);
            if (facts.Count == 0 || facts[0].Path.Count != 0)
            {
                return;
            }

            var root = await collector.ResolveAsync(ctx, principal, facts[0]).ConfigureAwait(false);
            if (root is null)
            {
                return;
            }

            var relations = new List<RelationFacts>();
            for (var i = 1; i < facts.Count && relations.Count < MaxRelations; i++)
            {
                if (await collector.ResolveAsync(ctx, principal, facts[i]).ConfigureAwait(false) is { } relation)
                {
                    relations.Add(new RelationFacts(ctx.ResponseName + "." + string.Join('.', facts[i].Path), relation));
                }
            }

            collector._roots[ctx.ResponseName] = new RootFacts(root, truncated, relations, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogSkipped(ctx, ex);
        }
    }

    internal static async ValueTask RecordAggregateRootAsync(IResolverContext ctx, ClaimsPrincipal? principal, Autheris.Application.Analytics.AggregationResult result)
    {
        var collector = From(ctx);
        if (collector is null || result.AccessFacts is not { } facts)
        {
            return;
        }

        try
        {
            if (await collector.ResolveAsync(ctx, principal, facts).ConfigureAwait(false) is not { } root)
            {
                return;
            }

            var aggregate = new AggregateFacts(
                result.SuppressedCountsExposed ? result.SuppressedGroupsCount : null,
                result.MinimumGroupSize,
                result.MergedIntoOther);
            collector._roots[ctx.ResponseName] = new RootFacts(root, result.Truncated, null, aggregate);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogSkipped(ctx, ex);
        }
    }

    private static void LogSkipped(IResolverContext ctx, Exception ex) =>
        ctx.Services.GetService<ILoggerFactory>()?.CreateLogger<DatasetFactsCollector>()
            .LogDebug("Dataset facts skipped for a root: {ExceptionType}", ex.GetType().Name);

    private async ValueTask<Facts?> ResolveAsync(IResolverContext ctx, ClaimsPrincipal? principal, DatasetAccessFacts facts)
    {
        var lazy = _visibility.GetOrAdd(facts.Table, table => new Lazy<Task<VisibleDataset?>>(() => LoadAsync(ctx, principal, table)));
        var visible = await lazy.Value.ConfigureAwait(false);
        return visible is null ? null : new Facts(visible, facts.RowFilterDescriptions);
    }

    private static async Task<VisibleDataset?> LoadAsync(IResolverContext ctx, ClaimsPrincipal? principal, TableIdentifier table)
    {
        var visibility = ctx.Services.GetService<ICatalogVisibilityService>();
        if (visibility is null)
        {
            return null;
        }

        var caller = new CatalogCaller(principal, GraphQlCallerPrincipal.ResolveContextTenantId(ctx, ctx.Services.GetService<IHttpContextAccessor>()), CatalogChannel.GraphQl);
        var batch = await visibility.FindVisibleManyAsync(caller, [table], ctx.RequestAborted).ConfigureAwait(false);
        if (!batch.Rows.TryGetValue(table, out var row))
        {
            return null;
        }

        return new VisibleDataset(McpDatasetCatalog.DatasetId(row.Table.Identifier), McpDatasetCatalog.BoundedTags(row.Table, MaxTags, MaxTagLength));
    }
}
