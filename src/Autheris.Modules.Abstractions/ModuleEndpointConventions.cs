using Autheris.Domain.Audit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;

namespace Autheris.Modules.Abstractions;

/// <summary>
/// Endpoint conventions for module routes. The host rejects a module endpoint that uses delegated audit or a body limit
/// above its ceiling when the module is loaded.
/// </summary>
public static class ModuleEndpointConventions
{
    public static TBuilder WithModuleAudit<TBuilder>(this TBuilder builder, AuditLevel level, string eventType)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithMetadata(new AuditPolicy(level, eventType));
    }

    public static TBuilder WithModuleRequestBodyLimit<TBuilder>(this TBuilder builder, long maxBytes)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        return builder.WithMetadata(new RequestSizeLimitAttribute(maxBytes));
    }
}
