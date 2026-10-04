namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;

public sealed record ComponentHealth(
    string ComponentName,
    string Status, // "Healthy", "Degraded", "Unhealthy"
    string? Details = null,
    DateTimeOffset CheckedAt = default
);

public sealed record GatewaySystemMetrics(
    DateTimeOffset Timestamp,
    string Version,
    TimeSpan Uptime,
    long MemoryAllocatedBytes,
    int ThreadCount,
    long PolicyEpoch,
    int QuarantinedModelsCount,
    ResourceGroupMetrics ResourceGroups,
    IReadOnlyList<ComponentHealth> Components
);
