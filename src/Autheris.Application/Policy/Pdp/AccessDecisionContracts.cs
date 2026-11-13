namespace Autheris.Application.Policy.Pdp;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

public enum AccessOperation
{
    Read,
    Insert,
    Update,
    Delete,
    Execute,
    Discover
}

public enum AccessTransport
{
    GraphQl,
    Rest,
    OData,
    WebSql,
    Trino,
    Mcp,
    Procedure,
    Analytics,
    Olap,
    Federation,
    ArrowExport,
    Streaming,
    Lakehouse,
    EnvoyExtAuthz,
    Discovery,
    Internal
}

public sealed record WriteGrantQuery(
    ClaimsPrincipal? User = null,
    TenantId? Tenant = null,
    TableMetadata? Table = null,
    IReadOnlySet<string>? WrittenColumns = null);

[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifiers should not contain type names", Justification = "Unified authorization model subject-object-action contract (ADR-020).")]
public sealed record AccessRequest(
    SecurityPrincipalContext Subject,
    TableMetadata Object,
    AccessOperation Operation,
    AccessTransport Transport,
    IReadOnlyList<string>? Columns = null,
    IReadOnlyList<Consent>? SupplementalConsents = null,
    WriteGrantQuery? Write = null,
    bool Trace = false);

public interface IAccessDecisionPoint
{
    ValueTask<TableAccessDecision> DecideAsync(AccessRequest request, CancellationToken ct = default);
    ValueTask<IReadOnlyList<TableAccessDecision>> DecideManyAsync(SecurityPrincipalContext subject, IReadOnlyList<TableMetadata> objects, AccessOperation operation, AccessTransport transport, CancellationToken ct = default);
    ValueTask<AccessExplanation> ExplainAsync(AccessRequest request, CancellationToken ct = default);
}

public sealed record ColumnStateExplanation(string ColumnName, ColumnAccessLevel Level, string? MaskType = null);

public sealed record EffectiveGroupsExplanation(IReadOnlyList<string> GroupSids, string Source, string Status);

public sealed record AccessExplanation(
    bool Allowed,
    IReadOnlyList<DecisionReason> Trace,
    IReadOnlyList<ColumnStateExplanation> Columns,
    EffectiveGroupsExplanation Groups,
    IReadOnlyList<DecisionGrantRef> Grants,
    long PolicyEpoch,
    bool SubjectResolved = true);
