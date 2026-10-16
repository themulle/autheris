namespace Autheris.Application.Federation;

using System;
using System.Linq;
using Autheris.Application.Sql;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

/// <summary>
/// The single evaluator of the federation switch (<c>WebSql:CrossSource:Enabled</c>) and the transport opt-in
/// (<c>WebSql:CrossSource:AllowedTransports</c>) for transports that stage tables of several data sources.
/// Fail closed: a blank transport, an empty list and a disabled switch all deny.
/// </summary>
public interface IFederationGate
{
    /// <summary>
    /// Throws <see cref="WebSqlPolicyException"/> with the fixed <see cref="FederationGate.DeniedMessage"/> when federation is not
    /// enabled for <paramref name="transport"/>. Callers invoke it only after every requested table passed authorization, so the
    /// denial reveals nothing about tables the caller cannot see.
    /// </summary>
    void EnsureAllowed(string? transport);

    /// <summary>Same decision as <see cref="EnsureAllowed"/> without throwing.</summary>
    bool IsEnabledFor(string? transport);
}

/// <inheritdoc cref="IFederationGate"/>
public sealed class FederationGate(IOptions<GatewayOptions> options) : IFederationGate
{
    /// <summary>Identical text for "federation disabled" and "transport not allowed" (no oracle).</summary>
    public const string DeniedMessage = "Cross-source queries are not enabled for this endpoint.";

    public void EnsureAllowed(string? transport)
    {
        if (!IsEnabledFor(transport))
        {
            throw new WebSqlPolicyException(DeniedMessage);
        }
    }

    public bool IsEnabledFor(string? transport)
    {
        if (string.IsNullOrWhiteSpace(transport))
        {
            return false;
        }

        // Options are read on every call (no snapshot at construction).
        var crossSource = options.Value.WebSql?.CrossSource;
        if (crossSource is not { Enabled: true })
        {
            return false;
        }

        return crossSource.GetEffectiveAllowedTransports()
            .Any(t => string.Equals(t, transport, StringComparison.OrdinalIgnoreCase));
    }
}
