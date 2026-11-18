namespace Autheris.Application.Products;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

/// <summary>A loader failure with a stable code (never the source error text).</summary>
public sealed class ProductLoadException(string code, Exception? inner = null) : Exception(code, inner)
{
    public string Code { get; } = code;
}

/// <summary>The rows of a stage-1 execution; <see cref="Rows"/> is consumed once, by the table cache stage.</summary>
public sealed record ProductQueryResult(IReadOnlyList<string> Columns, IAsyncEnumerable<IReadOnlyList<object?>> Rows);

/// <summary>
/// Stage 1 of a view: runs the product definition as the product (definer), not as a caller, through the governed federation staging
/// (connection policies, source admission, timeouts and cost limits apply; no DDL, no transaction on the source).
/// </summary>
public interface IProductQueryRunner
{
    Task<ProductQueryResult> RunAsync(DataProduct product, CancellationToken ct);
}

/// <summary>
/// Fails closed with <see cref="ProductErrorCodes.LoadFailed"/> until the definer execution of the federation staging is wired: no product
/// SQL ever runs under a caller identity or without the staging governance.
/// </summary>
public sealed class UnwiredProductQueryRunner : IProductQueryRunner
{
    public Task<ProductQueryResult> RunAsync(DataProduct product, CancellationToken ct) =>
        throw new ProductLoadException(ProductErrorCodes.LoadFailed);
}
