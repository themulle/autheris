namespace Autheris.Application.Connectors.CrossDomain;

using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// Evaluates Zero-Trust consent policies and ABAC rules for individual tables participating in a cross-domain join.
/// Ensures strict per-table authorization isolation (SEC-CDJ-02).
/// </summary>
public interface ICrossDomainAccessResolver
{
    Task<TableAccessDecision> ResolveAccessAsync(
        ClaimsPrincipal principal,
        TableIdentifier table,
        TableMetadata metadata,
        TenantId? tenant,
        CancellationToken ct = default);
}
