namespace Autheris.Tests.Unit.TestDoubles;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Audit;
using Autheris.Application.Connectors;
using Autheris.Application.Connectors.CrossDomain;
using Autheris.Application.Interfaces;
using Autheris.Application.Olap;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

/// <summary>
/// WP-ARC-B-07: the OLAP endpoint delegates to <see cref="IOlapStagingService"/>. Tests that exercise the whole path compose the
/// production service over their doubles. The audit writer is resolved eagerly from the request services, so a missing
/// writer stays a composition error (<see cref="InvalidOperationException"/>); only a context without request services
/// (early 401/400 paths) falls back to a lazy resolver that is never reached.
/// </summary>
internal static class OlapEndpointTestHost
{
    public static Task HandleAsync(
        HttpContext httpContext,
        IDuckDbOlapEngine engine,
        ITableMetadataRepository metadataRepository,
        IAutherisConnectorRegistry connectorRegistry,
        ICrossDomainAccessResolver accessResolver,
        IColumnMaskingProvider maskingProvider,
        IOptions<GatewayOptions> gatewayOptions,
        ILoggerFactory loggerFactory)
    {
        var service = new OlapStagingService(
            engine, metadataRepository, connectorRegistry, accessResolver, maskingProvider, gatewayOptions,
            httpContext.RequestServices is null ? new RequestScopedAuditWriter(httpContext) : httpContext.RequestServices.GetRequiredService<IAuditWriter>(), NullLogger<OlapStagingService>.Instance);
        return DuckDbOlapEndpoints.HandleOlapQueryAsync(httpContext, service, gatewayOptions, loggerFactory);
    }

    private sealed class RequestScopedAuditWriter(HttpContext context) : IAuditWriter
    {
        private IAuditWriter Writer => context.RequestServices!.GetRequiredService<IAuditWriter>();

        public Task RecordRequiredAsync(AuditLogEntry entry, CancellationToken ct = default) => Writer.RecordRequiredAsync(entry, ct);

        public Task RecordBestEffortAsync(AuditLogEntry entry, CancellationToken ct = default) => Writer.RecordBestEffortAsync(entry, ct);

        public Task RecordAsync(AuditLogEntry entry, CancellationToken ct = default) => Writer.RecordAsync(entry, ct);
    }
}
