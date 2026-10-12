using System.Collections.Immutable;
using HotChocolate;
using HotChocolate.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RequestDelegate = HotChocolate.Execution.RequestDelegate;

namespace Autheris.Api.Middleware;

/// <summary>
/// R-GQL-6 / R-ERR-1: Outside Development an unknown field, a denied table and a denied column must not be
/// distinguishable. HotChocolate 16 reports unknown fields as document validation errors, which never reach an
/// <see cref="IErrorFilter"/>, and without data; denied tables and columns are resolver errors with partial data.
/// This request middleware wraps validation and execution and replaces every such result with one identical,
/// generic error result (same body, same HTTP status 400).
/// </summary>
public sealed class GraphQlEnumerationShieldMiddleware
{
    internal const string Code = "INVALID_QUERY";
    internal const string Message = "The query is invalid or references fields that are not available.";

    /// <summary>Error codes raised by the catalog resolvers for denied tables and unknown or denied columns.</summary>
    private static readonly HashSet<string> CatalogAccessCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "ACCESS_DENIED",
        "INVALID_QUERY"
    };

    /// <summary>Validation errors with these codes do not depend on the catalog and keep their own message.</summary>
    private static readonly HashSet<string> NeutralValidationCodes = new(StringComparer.OrdinalIgnoreCase)
    {
    };

    private readonly RequestDelegate _next;

    public GraphQlEnumerationShieldMiddleware(RequestDelegate next)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
    }

    public async ValueTask InvokeAsync(RequestContext context)
    {
        await _next(context).ConfigureAwait(false);

        if (context.Result is not OperationResult result || result.Errors is not { Count: > 0 } errors)
        {
            return;
        }

        // Unknown environment counts as production (fail-closed).
        var environment = context.RequestServices.GetService<IHostEnvironment>();
        if (environment?.IsDevelopment() == true)
        {
            return;
        }

        bool validationFailed = result.ContextData.ContainsKey(ExecutionContextData.ValidationErrors);
        bool mustShield = validationFailed
            ? errors.Any(e => e.Code is null || !NeutralValidationCodes.Contains(e.Code))
            : errors.Any(e => e.Code is not null && CatalogAccessCodes.Contains(e.Code));

        if (!mustShield)
        {
            return;
        }

        await result.DisposeAsync().ConfigureAwait(false);
        context.Result = new OperationResult(ImmutableList.Create(
            ErrorBuilder.New().SetMessage(Message).SetCode(Code).Build()))
        {
            ContextData = ImmutableDictionary<string, object?>.Empty.Add(ExecutionContextData.ValidationErrors, true)
        };
    }
}
