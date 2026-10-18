namespace Autheris.Api.Hosting.Warmup;

using System.Text.Json;
using Autheris.Api.Middleware;
using HotChocolate.Execution;

/// <summary>
/// PLAN-OPS-150 A-150-2: reference-identifiable proof that the constant <c>{ __typename }</c> completed for one executor instance.
/// Written by <see cref="GraphQlExecutorWarmupTask"/> (or the gate's own run), read by <see cref="GraphQlExecutorGate"/>.
/// </summary>
public sealed class GraphQlWarmupProof
{
    private IRequestExecutor? _warmed;

    /// <summary>The executor instance for which the warm-up document was proven, or <c>null</c>.</summary>
    public IRequestExecutor? WarmedExecutor => Volatile.Read(ref _warmed);

    internal void Record(IRequestExecutor executor) => Volatile.Write(ref _warmed, executor);
}

/// <summary>Shared warm-up execution and proof rule for the task and the gate (SEC-OPS150-01/-07/-16).</summary>
internal static class GraphQlWarmupRun
{
    internal const string Document = "{ __typename }";

    /// <summary>
    /// Executes the compiled constant in-process (marker only, empty context, no principal) and returns whether it proves the
    /// pipeline. Under PersistedQueriesOnly exactly one trusted-document rejection is the proof; the document is never trusted.
    /// </summary>
    internal static async Task<bool> ProveAsync(IRequestExecutor executor, bool persistedQueriesOnly, CancellationToken ct)
    {
        var request = OperationRequestBuilder.New().SetDocument(Document).MarkAsWarmupRequest().Build();
        await using var executed = await executor.ExecuteAsync(request, ct).ConfigureAwait(false);
        return IsTypenameQuery(executed) || (persistedQueriesOnly && IsTrustedDocumentRejection(executed));
    }

    private static bool IsTrustedDocumentRejection(IExecutionResult result) =>
        result is OperationResult { Errors: { Count: 1 } errors }
        && string.Equals(errors[0].Code, TrustedDocumentsOnlyMiddleware.RejectionCode, StringComparison.Ordinal);

    private static bool IsTypenameQuery(IExecutionResult result)
    {
        if (result is not OperationResult op || op.Errors is { Count: > 0 })
        {
            return false;
        }

        using var doc = JsonDocument.Parse(op.ToJson());
        return doc.RootElement.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("__typename", out var name)
            && name.ValueKind == JsonValueKind.String
            && name.GetString() == "Query";
    }
}
