namespace Autheris.Api.Telemetry;

using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Autheris.Domain.Diagnostics;
using HotChocolate.Execution;
using HotChocolate.Execution.Configuration;
using HotChocolate.Execution.Instrumentation;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// HotChocolate execution listener producing the graphql.execute span per GraphQL operation.
/// Enforces SEC-OT06-03 (safe operation names, no variables/documents).
/// </summary>
public sealed class GraphQlTelemetryListener : ExecutionDiagnosticEventListener
{
    private static readonly ActivitySource Source = new("Autheris.Core");
    private static readonly Regex ValidOperationNameRegex = new(@"^[_A-Za-z][_0-9A-Za-z]{0,63}\z", RegexOptions.Compiled);

    public override IDisposable ExecuteRequest(RequestContext context)
    {
        Activity? activity = null;
        if (Source.HasListeners())
        {
            activity = Source.StartActivity("graphql.execute", ActivityKind.Internal);
        }

        return new RequestScope(activity, context);
    }

    private sealed class RequestScope(Activity? activity, RequestContext context) : IDisposable
    {
        public void Dispose()
        {
            if (activity == null) return;

            var opName = context.Request.OperationName;
            var safeOpName = !string.IsNullOrWhiteSpace(opName) && ValidOperationNameRegex.IsMatch(opName)
                ? opName
                : "[invalid]";

            activity.SetTag("graphql.operation.name", safeOpName);

            if (context.Result is OperationResult res && res.Errors?.Count > 0)
            {
                activity.SetStatus(ActivityStatusCode.Error);
                activity.SetTag(TelemetryAttributes.ErrorType, "graphql_error");
            }

            activity.Dispose();
        }
    }
}

public static class GraphQlTelemetryExtensions
{
    public static IRequestExecutorBuilder AddGraphQlTelemetry(this IRequestExecutorBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddDiagnosticEventListener<GraphQlTelemetryListener>();
    }
}
