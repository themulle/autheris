namespace Autheris.Domain.Governance;

using System;
using System.Collections.Generic;
using Autheris.Domain.Common;

public readonly record struct FunctionalRight(string Value)
{
    // Standard Roles
    public static readonly FunctionalRight ClusterAdmin = new("ClusterAdmin");
    public static readonly FunctionalRight TenantAdmin = new("TenantAdmin");
    public static readonly FunctionalRight GovernanceAdmin = new("GovernanceAdmin");
    public static readonly FunctionalRight SchemaPublisher = new("SchemaPublisher");
    public static readonly FunctionalRight Consumer = new("Consumer");

    // Granular privileged rights: an IT or governance capacity, never conferred by a mapping
    public static readonly FunctionalRight GovernanceApprove = new("Governance.Approve");
    public static readonly FunctionalRight GovernanceAdminRight = new("Governance.Admin");
    public static readonly FunctionalRight SecurityAdmin = new("Security.Admin");
    public static readonly FunctionalRight DataProductAdmin = new("DataProduct.Admin");

    // Operational Rights
    public static readonly FunctionalRight DataExport = new("Data.Export");
    public static readonly FunctionalRight DataAsyncJobs = new("Data.AsyncJobs");
    public static readonly FunctionalRight McpUse = new("Mcp.Use");
    public static readonly FunctionalRight McpAdmin = new("Mcp.Admin");
    public static readonly FunctionalRight CatalogCurate = new("Catalog.Curate");

    /// <summary>The values of every privileged right.</summary>
    public static IReadOnlyList<string> PrivilegedValues { get; } =
    [
        GovernanceApprove.Value,
        GovernanceAdminRight.Value,
        SecurityAdmin.Value,
        DataProductAdmin.Value,
        ClusterAdmin.Value
    ];

    public bool IsPrivileged =>
        this == GovernanceApprove ||
        this == GovernanceAdminRight ||
        this == SecurityAdmin ||
        this == DataProductAdmin ||
        this == ClusterAdmin;

    public string Name => Value;

    public override string ToString() => Value ?? string.Empty;

    public static implicit operator string(FunctionalRight right) => right.Value;
    public static implicit operator FunctionalRight(string value) => new(value);
}

public sealed record FunctionalRightMapping(
    TenantId TenantId,
    string RealmId,
    string GroupOrRole,
    FunctionalRight FunctionalRight,
    string? ApprovedBy,
    DateTimeOffset? ApprovedAt,
    string RecertClass,
    string CreatedBy,
    DateTimeOffset CreatedAt
)
{
    /// <summary>A saved mapping is approved (single approval); a row without an approver never is.</summary>
    public bool IsApproved => !string.IsNullOrWhiteSpace(ApprovedBy);
}
