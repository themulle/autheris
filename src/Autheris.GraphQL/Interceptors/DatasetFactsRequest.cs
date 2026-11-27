namespace Autheris.GraphQL.Interceptors;

using System.Text.Json;
using HotChocolate.Execution;

/// <summary>
/// Reads the opt-in flag <c>extensions.autheris.datasetFacts</c> of a GraphQL request. Only the JSON literal <c>true</c>
/// enables the response extension; anything else (false, "true", 1, missing, malformed) is treated as off.
/// </summary>
internal static class DatasetFactsRequest
{
    internal static bool IsRequested(IOperationRequest? request)
    {
        try
        {
            var owner = request?.Extensions;
            return owner is not null && IsTrue(owner.Document.RootElement);
        }
        catch (System.Exception)
        {
            return false;
        }
    }

    private static bool IsTrue(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("autheris", out var autheris)
        && autheris.ValueKind == JsonValueKind.Object
        && autheris.TryGetProperty("datasetFacts", out var flag)
        && flag.ValueKind == JsonValueKind.True;
}
