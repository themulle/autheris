namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Domain.Common;

public enum DataContractStatus
{
    Draft = 0,
    Active = 1,
    Broken = 2,
    Deprecated = 3
}

public enum AssertionType
{
    Freshness = 0,
    RowCount = 1,
    SqlExpression = 2,
    Completeness = 3,
    SchemaCompliance = 4
}

public enum DataGovernanceMode
{
    Embedded = 0,
    Federated = 1
}

public sealed record DataContractAssertion(
    string Id,
    AssertionType Type,
    string Description,
    string? Column = null,
    string? Expression = null,
    string? Threshold = null,
    bool IsPassing = true,
    string? FailureReason = null,
    DateTimeOffset? LastEvaluated = null
);

public sealed record DataContractSla(
    int? FreshnessHours = null,
    double? MaxAllowedErrorRatePct = null
);

public sealed record DataContract(
    string Id,
    TableIdentifier Table,
    string Version,
    DataContractStatus Status,
    IReadOnlyList<string> SchemaColumns,
    IReadOnlyList<DataContractAssertion> Assertions,
    DataContractSla? Sla,
    string? OwnerTeam,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
)
{
    public bool IsHealthy => Status == DataContractStatus.Active && Assertions.All(a => a.IsPassing);
}
