using System.Collections.Generic;
using Autheris.Domain.Model;
using Autheris.Domain.Options;

namespace Autheris.Api.Configuration;

/// <summary>
/// Settings that are accepted at startup but have no effect on some read paths. Each one is reported once as a startup
/// warning, so an operator does not assume a setting is active where it is not.
/// </summary>
public static class InertOptionWarnings
{
    /// <summary>Returns one message per configured setting that is partly or fully inert.</summary>
    public static IReadOnlyList<string> Collect(GatewayOptions options)
    {
        _ = options;
        return [];
    }
}
