namespace Autheris.Api.Endpoints;

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

    public sealed record WebSqlRequestDto(
        string? Sql,
        Dictionary<string, object?>? Parameters,
        string? DataSource);

    public static IEndpointRouteBuilder MapWebSqlEndpoints(this IEndpointRouteBuilder app, GatewayOptions? gatewayOptions = null)
    {
        // OpenAPI description of the endpoint (documentation only, no data). Like the other specs it is anonymous only
        // in OpenSchema mode.
        var openApi = app.MapGet("/api/v1/sql/openapi.json", () => Results.Json(BuildOpenApiSpec(gatewayOptions)))
            .WithName("GetWebSqlOpenApiSpec")
            .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);
        if (gatewayOptions?.IsOpenSchemaAllowed == true)
        {
            openApi.AllowAnonymous();
        }
        else
        {
            openApi.RequireAuthorization();
        }

        app.MapPost("/api/v1/sql", HandleWebSqlRequest)
           .WithName("ExecuteGovernedWebSqlV1")
           .WithMetadata(new ParquetOutputSupportedMetadata())
           .WithRequestBodyLimit(2 * 1024 * 1024)
           .RequireAuthorization()
           .WithAudit(AuditLevel.Full, AuditEventTypes.WebSqlQuery);

        app.MapPost("/api/sql", HandleWebSqlRequest)
           .WithName("ExecuteGovernedWebSql")
           .WithMetadata(new ParquetOutputSupportedMetadata())
           .WithRequestBodyLimit(2 * 1024 * 1024)
           .RequireAuthorization()
           .WithAudit(AuditLevel.Full, AuditEventTypes.WebSqlQuery);

        app.MapPost("/v1/statement", HandleWebSqlRequest)
           .WithName("ExecuteTrinoStatementV1")
           .WithRequestBodyLimit(2 * 1024 * 1024)
           .RequireAuthorization()
           .WithAudit(AuditLevel.Full, AuditEventTypes.WebSqlQuery);

        app.MapGet("/v1/statement/queued/{statementId}", HandleTrinoQueuedStatementRequest)
           .WithName("GetTrinoQueuedStatementV1")
           .RequireAuthorization()
           .WithAudit(AuditLevel.Full, AuditEventTypes.WebSqlQuery);

        app.MapGet("/v1/statement/executing/{statementId}", HandleTrinoQueuedStatementRequest)
           .WithName("GetTrinoExecutingStatementV1")
           .RequireAuthorization()
           .WithAudit(AuditLevel.Full, AuditEventTypes.WebSqlQuery);

        app.MapDelete("/v1/statement/{statementId}", HandleTrinoCancelStatementRequest)
           .WithName("CancelTrinoStatementV1")
           .RequireAuthorization()
           .WithAudit(AuditLevel.Full, AuditEventTypes.WebSqlQuery);

        app.MapGet("/api/v1/sql/statements/{statementId}", HandleTrinoQueuedStatementRequest)
           .WithName("GetWebSqlStatementV1")
           .RequireAuthorization()
           .WithAudit(AuditLevel.Full, AuditEventTypes.WebSqlQuery);

        app.MapGet("/api/sql/statements/{statementId}", HandleTrinoQueuedStatementRequest)
           .WithName("GetWebSqlStatement")
           .RequireAuthorization()
           .WithAudit(AuditLevel.Full, AuditEventTypes.WebSqlQuery);

        app.MapDelete("/api/v1/sql/statements/{statementId}", HandleTrinoCancelStatementRequest)
           .WithName("CancelWebSqlStatementV1")
           .RequireAuthorization()
           .WithAudit(AuditLevel.Full, AuditEventTypes.WebSqlQuery);

        app.MapDelete("/api/sql/statements/{statementId}", HandleTrinoCancelStatementRequest)
           .WithName("CancelWebSqlStatement")
           .RequireAuthorization()
           .WithAudit(AuditLevel.Full, AuditEventTypes.WebSqlQuery);

        return app;
    }
}
