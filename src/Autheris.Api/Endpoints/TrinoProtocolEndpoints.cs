namespace Autheris.Api.Endpoints;

using System;
using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Autheris.Api.Errors;
using Autheris.Api.Extensions;
using Autheris.Api.Security;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Errors;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Autheris.Domain.Options.DataSources;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public static partial class TrinoProtocolEndpoints
{
    private const int MaxBodyBytes = 2 * 1024 * 1024;

    [GeneratedRegex(@"\b(INSERT|UPDATE|DELETE|MERGE)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DmlKeywordsRegex();

    public static IEndpointRouteBuilder MapTrinoProtocolEndpoints(this IEndpointRouteBuilder app, GatewayOptions? gatewayOptions = null)
    {
        var trino = app.MapGroup("")
            .WithIngressTransport(DataSourceTransportFlags.Trino)
            .RequireAuthorization()
            .WithAudit(AuditLevel.Full, AuditEventTypes.WebSqlQuery);

        trino.MapPost("/v1/statement", HandleTrinoSubmit)
            .WithName("ExecuteTrinoStatementV1")
            .WithRequestBodyLimit(MaxBodyBytes);

        trino.MapGet("/v1/statement/queued/{statementId}/{token:long:min(0)}", HandleTrinoPoll)
            .WithName("GetTrinoQueuedStatementV1");

        trino.MapGet("/v1/statement/executing/{statementId}/{token:long:min(0)}", HandleTrinoPoll)
            .WithName("GetTrinoExecutingStatementV1");

        trino.MapDelete("/v1/statement/queued/{statementId}/{token:long:min(0)}", HandleTrinoCancel)
            .WithName("CancelTrinoQueuedStatementV1");

        trino.MapDelete("/v1/statement/executing/{statementId}/{token:long:min(0)}", HandleTrinoCancel)
            .WithName("CancelTrinoExecutingStatementV1");

        return app;
    }

    internal static async Task HandleTrinoSubmit(
        HttpContext httpContext,
        IGovernedSqlExecutionService sqlService,
        IOptions<GatewayOptions> gatewayOptions,
        ILoggerFactory loggerFactory)
    {
        var ct = httpContext.RequestAborted;
        var logger = loggerFactory.CreateLogger("Autheris.Api.Trino");

        if (!httpContext.Request.Headers.TryGetValue("X-Trino-User", out var trinoUser) ||
            string.IsNullOrWhiteSpace(trinoUser.ToString()))
        {
            await ApiProblems.WriteAsync(httpContext, StatusCodes.Status400BadRequest, ApiErrorCodes.BadRequest,
                "Trino protocol violation: Missing required 'X-Trino-User' header.", ct);
            return;
        }

        var caller = await RequireCallerAsync(httpContext, ct);
        if (caller == null) return;
        var (owner, tenantId) = caller.Value;

        var maxBodySizeFeature = httpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (maxBodySizeFeature != null && !maxBodySizeFeature.IsReadOnly)
        {
            maxBodySizeFeature.MaxRequestBodySize = MaxBodyBytes;
        }

        if (httpContext.Request.ContentLength > MaxBodyBytes)
        {
            await ApiProblems.WriteAsync(httpContext, StatusCodes.Status400BadRequest, ApiErrorCodes.BadRequest,
                "Request body exceeds maximum size limit (2 MB).", ct);
            return;
        }

        using var reader = new StreamReader(httpContext.Request.Body, Encoding.UTF8);
        var sql = await reader.ReadToEndAsync(ct);

        string? dataSource = httpContext.Request.Headers.TryGetValue("X-Trino-Catalog", out var catalogVal) && !string.IsNullOrWhiteSpace(catalogVal.ToString())
            ? catalogVal.ToString().Trim()
            : null;

        var statementClass = WebSqlEndpoints.ClassifyStatement(sql, logger);
        if (statementClass == WebSqlEndpoints.RequestStatementClass.Write ||
            (statementClass == WebSqlEndpoints.RequestStatementClass.Unparsable && DmlKeywordsRegex().IsMatch(sql)))
        {
            var failure = new SqlFailure(SqlFailureKind.PolicyDenied, SqlFailureClassifier.GenericAccessDeniedMessage, "ACCESS_DENIED");
            await TrinoQueryResultsWriter.WriteAsync(httpContext, new StatementExecutionStatus(GenerateStatementId(), "FAILED", null, null, 0, Failure: failure), 0, ct);
            return;
        }

        var webSqlOptions = gatewayOptions.Value.WebSql ?? new WebSqlOptions();
        var rowLimit = SqlRowLimit.For(webSqlOptions, gatewayOptions.Value.RowLimits?.Trino);
        var governedRequest = new GovernedSqlQueryRequest(
            Sql: sql,
            Parameters: null,
            DataSourceName: dataSource,
            Transport: SqlTransports.Trino)
        {
            RowLimit = rowLimit
        };

        var waitTimeout = TrinoQueryResultsWriter.ParseWaitTimeout(httpContext.Request);
        var statementManager = httpContext.RequestServices?.GetRequiredService<IWebSqlStatementManager>()
            ?? throw new InvalidOperationException("IWebSqlStatementManager is required.");

        try
        {
            var status = await statementManager.SubmitOrWaitAsync(
                governedRequest,
                httpContext.User,
                owner,
                tenantId,
                waitTimeout,
                DataSourceTransportFlags.Trino,
                ct);

            await TrinoQueryResultsWriter.WriteAsync(httpContext, status, token: 0, ct: ct);
        }
        catch (Exception ex)
        {
            var failure = SqlFailureClassifier.Classify(ex);
            await TrinoQueryResultsWriter.WriteAsync(httpContext, new StatementExecutionStatus(GenerateStatementId(), "FAILED", null, null, 0, Failure: failure), 0, ct);
        }
    }

    internal static async Task HandleTrinoPoll(
        string statementId,
        long token,
        HttpContext httpContext,
        IWebSqlStatementManager statementManager,
        ILoggerFactory loggerFactory)
    {
        var ct = httpContext.RequestAborted;
        var caller = await RequireCallerAsync(httpContext, ct);
        if (caller == null) return;
        var (owner, tenantId) = caller.Value;

        var waitTimeout = TrinoQueryResultsWriter.ParseWaitTimeout(httpContext.Request);

        try
        {
            var status = await statementManager.GetStatusOrWaitAsync(
                statementId,
                httpContext.User,
                owner,
                tenantId,
                waitTimeout,
                DataSourceTransportFlags.Trino,
                ct);

            await TrinoQueryResultsWriter.WriteAsync(httpContext, status, token, ct: ct);
        }
        catch (System.Collections.Generic.KeyNotFoundException)
        {
            await StatementNotFoundAsync(httpContext);
        }
        catch (SecurityException ex)
        {
            var logger = loggerFactory.CreateLogger("Autheris.Api.Trino");
            logger.LogWarning(ex, "Unauthorized statement access attempt for {StatementId}", statementId);
            await StatementNotFoundAsync(httpContext);
        }
        catch (Exception ex)
        {
            var failure = SqlFailureClassifier.Classify(ex);
            await TrinoQueryResultsWriter.WriteAsync(httpContext, new StatementExecutionStatus(statementId, "FAILED", null, null, 0, Failure: failure), token, ct);
        }
    }

    internal static async Task HandleTrinoCancel(
        string statementId,
        long token,
        HttpContext httpContext,
        IWebSqlStatementManager statementManager)
    {
        var ct = httpContext.RequestAborted;
        var caller = await RequireCallerAsync(httpContext, ct);
        if (caller == null) return;
        var (owner, tenantId) = caller.Value;

        try
        {
            if (!await statementManager.CancelStatementAsync(statementId, httpContext.User, owner, tenantId, ct))
            {
                await StatementNotFoundAsync(httpContext);
                return;
            }

            httpContext.Response.StatusCode = StatusCodes.Status204NoContent;
        }
        catch (SecurityException)
        {
            await StatementNotFoundAsync(httpContext);
        }
    }

    private static async Task<(Sid Owner, TenantId TenantId)?> RequireCallerAsync(HttpContext httpContext, CancellationToken ct)
    {
        var callerIdentity = EndpointSecurity.GetCallerIdentity(httpContext);
        if (string.IsNullOrWhiteSpace(callerIdentity))
        {
            await ApiProblems.WriteAsync(httpContext, StatusCodes.Status401Unauthorized, ApiErrorCodes.Unauthorized,
                "A canonical user identity is required.", ct);
            return null;
        }
        var owner = new Sid(callerIdentity);
        var securityContext = EndpointSecurity.GetSecurityContext(httpContext);
        TenantId tenantId = securityContext?.TenantId ?? EndpointSecurity.GetRequestTenant(httpContext);
        return (owner, tenantId);
    }

    private static Task StatementNotFoundAsync(HttpContext httpContext) =>
        ApiProblems.Indistinguishable(StatusCodes.Status404NotFound, ApiErrorCodes.ResourceNotFound, "Statement not found or expired.").ExecuteAsync(httpContext);

    private static string GenerateStatementId() =>
        $"stmt_{RandomNumberGenerator.GetHexString(32, lowercase: true)}";
}
