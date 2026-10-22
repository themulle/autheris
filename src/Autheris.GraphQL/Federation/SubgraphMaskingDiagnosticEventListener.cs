namespace Autheris.GraphQL.Federation;

using System;
using System.Collections.Immutable;
using HotChocolate.Execution;
using HotChocolate.Execution.Instrumentation;

/// <summary>
/// Execution listener that propagates internal MaskingApplied flag to the public OperationResult.ContextData.
/// </summary>
public sealed class SubgraphMaskingDiagnosticEventListener : ExecutionDiagnosticEventListener
{
    public override IDisposable ExecuteRequest(RequestContext context)
    {
        return new RequestScope(context);
    }

    private sealed class RequestScope(RequestContext context) : IDisposable
    {
        public void Dispose()
        {
            if (context.ContextData.TryGetValue("MaskingApplied", out var mask) && mask is true && context.Result is OperationResult opResult)
            {
                var dict = opResult.ContextData is not null
                    ? opResult.ContextData.SetItem("MaskingApplied", true)
                    : ImmutableDictionary<string, object?>.Empty.SetItem("MaskingApplied", true);
                opResult.ContextData = dict;
            }
        }
    }
}
