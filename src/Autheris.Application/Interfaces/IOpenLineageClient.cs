namespace Autheris.Application.Interfaces;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;

public sealed record OpenLineageDataset(
    string Namespace,
    string Name,
    IReadOnlyDictionary<string, object>? Facets = null
);

public sealed record OpenLineageRunEvent(
    string EventType,
    DateTimeOffset EventTime,
    string Producer,
    string SchemaUrl,
    object Job,
    IReadOnlyList<OpenLineageDataset> Inputs,
    IReadOnlyList<OpenLineageDataset> Outputs
);

public interface IOpenLineageClient
{
    Task<bool> PushLineageGraphAsync(TenantId tenant, CancellationToken ct = default);
    Task<bool> PushLineageEventAsync(OpenLineageRunEvent runEvent, CancellationToken ct = default);
}
