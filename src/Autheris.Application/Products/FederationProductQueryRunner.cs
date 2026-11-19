namespace Autheris.Application.Products;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.DataSources;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Options.DataSources;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Stage 1 through the governed federation engine as the product (definer). The engine is resolved per run from a scope; when it is not
/// available the load fails closed. A truncated result fails the load: a product never serves a silently cut result.
/// </summary>
public sealed class FederationProductQueryRunner(IServiceScopeFactory scopes) : IProductQueryRunner
{
    public async Task<ProductQueryResult> RunAsync(ProductDefiner definer, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(definer);
        using var scope = scopes.CreateScope();
        IProductDefinerExecutor executor;
        try
        {
            // the load has no caller ingress: it is judged by the WebSql transport flag of every source it reads (the transport gate denies an unstamped scope)
            scope.ServiceProvider.GetService<IIngressTransportContext>()?.Set(DataSourceTransportFlags.WebSql);
            executor = scope.ServiceProvider.GetService<IProductDefinerExecutor>() ?? throw new ProductLoadException(ProductErrorCodes.LoadFailed);
        }
        catch (Exception ex) when (ex is not ProductLoadException and not OperationCanceledException)
        {
            // the federation engine is not available in this host: fail closed, never run the definition elsewhere
            throw new ProductLoadException(ProductErrorCodes.LoadFailed, ex);
        }

        var maxRows = definer.Limits.MaxRows > 0 ? Math.Min(definer.Limits.MaxRows + 1, SqlRowLimit.HardMaxRows) : SqlRowLimit.HardMaxRows;
        var request = new GovernedSqlQueryRequest(
            definer.Product.Definition,
            RowLimit: new SqlRowLimit(maxRows, maxRows),
            Transport: SqlTransports.ProductLoad,
            ReadOnlyRequired: true);
        var result = await executor.ExecuteAsync(definer, request, ct).ConfigureAwait(false);
        if (result.Truncated)
        {
            throw new ProductLoadException(ProductErrorCodes.LimitExceeded);
        }

        var columns = result.Columns.ToList();
        return new ProductQueryResult(columns, Rows(result.Rows, columns, ct));
    }

    private static async IAsyncEnumerable<IReadOnlyList<object?>> Rows(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, List<string> columns, [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            var values = new object?[columns.Count];
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = row.TryGetValue(columns[i], out var v) ? v : null;
            }

            yield return values;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }
}
