using System.Collections.Generic;

namespace Autheris.Domain.Connectors;

public sealed record ConnectorSplit(
    string SplitId,
    IReadOnlyDictionary<string, object?> SplitProperties,
    bool IsRemotelyAccessible = true)
{
    public static ConnectorSplit Default(string splitId = "split-0") =>
        new(splitId, new Dictionary<string, object?>());
}
