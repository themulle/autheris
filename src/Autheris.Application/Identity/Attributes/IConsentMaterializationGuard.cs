namespace Autheris.Application.Identity.Attributes;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// GOV-12: the single check every path that turns a request into a stored consent must pass (A-10 caller attribute source tables and
/// the caller attribute registry rules). The consent write seam (<see cref="AttributeValidatingConsentRepository"/>), the approval
/// materialisation in the governance repositories and the request creation services all call this one implementation.
/// </summary>
public interface IConsentMaterializationGuard
{
    /// <summary>Returns when a consent for the table with these filters may be stored; otherwise throws (fail closed).</summary>
    /// <exception cref="ConsentOnAttributeSourceException">The table is read by a caller attribute table provider (any tenant).</exception>
    /// <exception cref="InvalidOperationException">A caller attribute filter violates the registry rules, or the registry is unavailable.</exception>
    ValueTask EnsureMayMaterializeAsync(TenantId tenant, TableIdentifier table, IReadOnlyList<ConsentRowFilter> filters, CancellationToken ct = default);
}

/// <summary>A consent on a caller attribute source table is refused (maps to HTTP 409 <see cref="Code"/>).</summary>
public sealed class ConsentOnAttributeSourceException()
    : InvalidOperationException("A consent on a caller attribute source table is refused: the table is read with the gateway service identity.")
{
    public const string ErrorCode = "CONSENT_ON_ATTRIBUTE_SOURCE";

    public string Code => ErrorCode;
}
