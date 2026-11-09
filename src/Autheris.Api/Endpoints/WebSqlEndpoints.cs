namespace Autheris.Api.Endpoints;

using Autheris.Domain.Options.DataSources;
using System.Collections.Generic;
using System.Text.Json;
using Autheris.Api.Extensions;
using Autheris.Api.Serialization;
using Autheris.Domain.Audit;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

public static partial class WebSqlEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public const string GenericForbiddenMessage = "The SQL statement was rejected by the gateway security policy.";
    public const string GenericBadRequestMessage = "The SQL request is invalid (syntax error, empty statement, invalid parameter or size limit exceeded). " +
        "WebSQL accepts ANSI/Trino-style SELECT statements: use LIMIT n instead of TOP n and name tables as <dataSource>.<schema>.<table> or <schema>.<table>.";
    public const string GenericTimeoutMessage = "The SQL statement exceeded the execution time limit. Narrow the query (filter, fewer columns, LIMIT) or contact support with the trace id.";
    public const string GenericServerErrorMessage = "The SQL statement could not be executed. Contact support with the trace id.";

    public sealed record WebSqlPageRequest(int? Size);

    public sealed record WebSqlRequestDto(
        string? Sql,
        Dictionary<string, object?>? Parameters,
        string? DataSource,
        WebSqlPageRequest? Page = null,
        string? Next = null);

    public static IEndpointRouteBuilder MapWebSqlEndpoints(this IEndpointRouteBuilder app, GatewayOptions? gatewayOptions = null)
    {
        // Issue #447: the ingress transport comes from endpoint metadata only. /v1/statement* is Trino, everything else WebSql.
        var webSql = app.MapGroup("").WithIngressTransport(DataSourceTransportFlags.WebSql);
        var trino = app.MapGroup("").WithIngressTransport(DataSourceTransportFlags.Trino);

        // OpenAPI description of the endpoint (documentation only, no data).
        webSql.MapGet("/api/v1/sql/openapi.json", () => Results.Json(BuildOpenApiSpec(gatewayOptions)))
            .WithName("GetWebSqlOpenApiSpec")
            .RequireAuthorization()
            .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        webSql.MapPost("/api/v1/sql", HandleWebSqlRequest)
           .WithName("ExecuteGovernedWebSqlV1")
           .WithMetadata(new ParquetOutputSupportedMetadata())
           .WithRequestBodyLimit(2 * 1024 * 1024)
           .RequireAuthorization()
           .WithAudit(AuditLevel.Full, AuditEventTypes.WebSqlQuery);

        webSql.MapPost("/api/sql", HandleWebSqlRequest)
           .WithName("ExecuteGovernedWebSql")
           .WithMetadata(new ParquetOutputSupportedMetadata())
           .WithRequestBodyLimit(2 * 1024 * 1024)
           .RequireAuthorization()
           .WithAudit(AuditLevel.Full, AuditEventTypes.WebSqlQuery);

        trino.MapPost("/v1/statement", HandleWebSqlRequest)
           .WithName("ExecuteTrinoStatementV1")
           .WithRequestBodyLimit(2 * 1024 * 1024)
           .RequireAuthorization()
           .WithAudit(AuditLevel.Full, AuditEventTypes.WebSqlQuery);

        trino.MapGet("/v1/statement/queued/{statementId}", HandleWebSqlStatementPollRequest)
           .WithName("GetTrinoQueuedStatementV1")
           .RequireAuthorization()
           .WithAudit(AuditLevel.Full, AuditEventTypes.WebSqlQuery);

        trino.MapGet("/v1/statement/executing/{statementId}", HandleWebSqlStatementPollRequest)
           .WithName("GetTrinoExecutingStatementV1")
           .RequireAuthorization()
           .WithAudit(AuditLevel.Full, AuditEventTypes.WebSqlQuery);

        trino.MapDelete("/v1/statement/{statementId}", HandleWebSqlStatementCancelRequest)
           .WithName("CancelTrinoStatementV1")
           .RequireAuthorization()
           .WithAudit(AuditLevel.Full, AuditEventTypes.WebSqlQuery);

        webSql.MapGet("/api/v1/sql/statements/{statementId}", HandleWebSqlStatementPollRequest)
           .WithName("GetWebSqlStatementV1")
           .RequireAuthorization()
           .WithAudit(AuditLevel.Full, AuditEventTypes.WebSqlQuery);

        webSql.MapGet("/api/sql/statements/{statementId}", HandleWebSqlStatementPollRequest)
           .WithName("GetWebSqlStatement")
           .RequireAuthorization()
           .WithAudit(AuditLevel.Full, AuditEventTypes.WebSqlQuery);

        webSql.MapDelete("/api/v1/sql/statements/{statementId}", HandleWebSqlStatementCancelRequest)
           .WithName("CancelWebSqlStatementV1")
           .RequireAuthorization()
           .WithAudit(AuditLevel.Full, AuditEventTypes.WebSqlQuery);

        webSql.MapDelete("/api/sql/statements/{statementId}", HandleWebSqlStatementCancelRequest)
           .WithName("CancelWebSqlStatement")
           .RequireAuthorization()
           .WithAudit(AuditLevel.Full, AuditEventTypes.WebSqlQuery);

        return app;
    }
}
