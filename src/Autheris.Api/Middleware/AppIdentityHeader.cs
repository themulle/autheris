namespace Autheris.Api.Middleware;

using System.Diagnostics.CodeAnalysis;
using Autheris.Application.Governance.TrustedDocuments;
using Microsoft.Extensions.Primitives;

/// <summary>
/// Parses the app identity request header <c>appId/version</c>(Issue #281): exactly one value, bounded length checked before
/// any regex, ordinal validation. Anything else (missing, empty, repeated, oversized, malformed) fails.
/// </summary>
public static class AppIdentityHeader
{
    public static bool TryParse(StringValues values, [NotNullWhen(true)] out AppIdentity identity)
    {
        identity = default;
        if (values.Count != 1)
        {
            return false;
        }

        var raw = values[0];
        return raw is { Length: <= AppIdentity.MaxLength } && AppIdentity.TryParse(raw, out identity);
    }
}
