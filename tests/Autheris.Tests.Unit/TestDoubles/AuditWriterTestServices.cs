namespace Autheris.Tests.Unit.TestDoubles;

using Autheris.Application.Audit;
using Autheris.Application.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// WP-ARC-A-06: request handlers resolve <see cref="IAuditWriter"/> as a required service. Test service providers that
/// register an <see cref="IAuditLogRepository"/> substitute add the production writer over it, so assertions on the
/// repository keep working.
/// </summary>
internal static class AuditWriterTestServices
{
    public static IAuditWriter Over(IAuditLogRepository repository) =>
        new AuditWriter(repository, NullLogger<AuditWriter>.Instance);

    public static IServiceCollection AddAuditWriterOver(this IServiceCollection services, IAuditLogRepository repository)
    {
        services.AddSingleton(repository);
        services.AddSingleton(Over(repository));
        return services;
    }
}
