namespace Autheris.Application.Federation.Interfaces;

using System.Collections.Generic;
using System.Security.Claims;

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
}
