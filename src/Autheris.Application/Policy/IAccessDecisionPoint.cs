namespace Autheris.Application.Policy;

using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

public enum AccessTransport
{
    GraphQL,
    Rest,
    OData,
    Sql,
    WebSql,
    Mcp,
    ArrowFlight,
    Lakehouse,
    Dbt,
    Cdc
}

public enum AccessOperation
{
    Read,
    Write,
    Explain,
    Subscribe,
    VendCredentials,
    SchemaRead
}

public sealed record AccessRequest(
    ClaimsPrincipal Principal,
    TenantId Tenant,
    TableIdentifier Table,
    AccessOperation Operation,
    IReadOnlyList<string> Columns,
    AccessTransport Transport,
    string? Purpose = null,
    TableMetadata? TableMetadata = null);

public sealed record AccessDecision(
    TableIdentifier Table,
    bool IsAllowed,
    IReadOnlyDictionary<string, ColumnAccessLevel> ColumnAccess,
    string? RowFilterSql,
    IReadOnlyList<string> DeniedReasons,
    AccessTransport Transport)
{
    public static AccessDecision Allowed(
        TableIdentifier table,
        IReadOnlyDictionary<string, ColumnAccessLevel> columnAccess,
        AccessTransport transport,
        string? rowFilterSql = null) =>
        new(table, true, columnAccess, rowFilterSql, [], transport);

    public static AccessDecision Denied(
        TableIdentifier table,
        IReadOnlyList<string> reasons,
        AccessTransport transport) =>
        new(table, false, new Dictionary<string, ColumnAccessLevel>(), null, reasons, transport);
}

public interface IAccessDecisionPoint
{
    ValueTask<AccessDecision> DecideAsync(
        AccessRequest request,
        CancellationToken cancellationToken = default);
}
