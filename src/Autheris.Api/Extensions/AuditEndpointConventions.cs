using Autheris.Domain.Audit;
using Microsoft.AspNetCore.Builder;

namespace Autheris.Api.Extensions;

public static class AuditEndpointConventions
{
    public static TBuilder WithAudit<TBuilder>(this TBuilder b, AuditLevel level, string eventType)
        where TBuilder : IEndpointConventionBuilder
    {
        return b.WithMetadata(new AuditPolicy(level, eventType));
    }

    public static TBuilder WithAuditExemption<TBuilder>(this TBuilder b, string justification)
        where TBuilder : IEndpointConventionBuilder
    {
        return b.WithMetadata(new AuditExemption(justification));
    }
}
