namespace Autheris.Application.Federation.Interfaces;

using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Federation.Services;

/// <summary>
/// Enforces in-memory data masking rules on aggregated federated subgraph response trees.
/// </summary>
public interface ISubgraphResultMasker
{
    /// <summary>
    /// Recursively traverses a GraphQL response object or list and applies data masking
    /// to sensitive fields (e.g. Email, IBAN, SSN, salary) for principals without clear consent.
    /// <paramref name="aliasToFieldMap"/> maps the response path of an aliased field (chain of response keys joined
    /// with '.', list indices omitted, e.g. <c>customer.contact</c>) to the real field name.
    /// </summary>
    object? MaskResultData(object? data, ClaimsPrincipal? principal, IReadOnlyDictionary<string, string>? aliasToFieldMap = null);

    /// <summary>
    /// SR-L-12: same as <see cref="MaskResultData"/> but consults the catalog classification (tenant-scoped) without blocking.
    /// </summary>
    ValueTask<object?> MaskResultDataAsync(object? data, ClaimsPrincipal? principal, IReadOnlyDictionary<string, string>? aliasToFieldMap, CancellationToken cancellationToken = default);

    /// <summary>
    /// GQL-15: Builds the per-request masking context lazily.
    /// </summary>
    ValueTask<SubgraphResultMasker.MaskContext> CreateMaskContextAsync(ClaimsPrincipal? principal, CancellationToken cancellationToken = default);

    /// <summary>
    /// GQL-15: Determines whether caller principal is an admin exempt from subgraph masking.
    /// </summary>
    bool IsAdmin(ClaimsPrincipal? principal);

    /// <summary>
    /// GQL-15: Masks a single leaf output field at runtime.
    /// </summary>
    object? MaskLeaf(string responseName, string fieldName, string? parentType, object? value, SubgraphResultMasker.MaskContext ctx);

    /// <summary>
    /// GQL-15: Masks a single leaf output field at runtime and reports if masking was applied.
    /// </summary>
    object? MaskLeaf(string responseName, string fieldName, string? parentType, object? value, SubgraphResultMasker.MaskContext ctx, out bool masked);
}
