namespace Autheris.Application.Docs.Services;

using System;
using Autheris.Application.Docs.Interfaces;

/// <summary>
/// Execution context of a caller invoking document deposit or query operations.
/// </summary>
public sealed record DocCaller(
    bool IsCanonicalClusterAdmin,
    bool IsGovernanceAdmin,
    string? TenantId,
    string? AuthorSid,
    bool HasSession,
    bool IsReadOnlySession);

/// <summary>
/// Resolved target writer context for a documentation deposit.
/// </summary>
public sealed record DocWriter(string? TenantId, string? AuthorSid)
{
    public bool IsOfficial => TenantId is null;
}

/// <summary>
/// Denial reason and machine code when deposit authorization fails.
/// </summary>
public sealed record DocDenial(string Reason, string Code = "FORBIDDEN");

/// <summary>
/// Single authorization policy for documentation deposit and reads, shared across REST and MCP.
/// </summary>
public static class DocAccessPolicy
{
    public static DocWriter? ResolveWriter(DocCaller caller, string appId, out DocDenial denial)
    {
        if (caller is null || !caller.HasSession)
        {
            denial = new DocDenial("Forbidden: Depositing documentation requires an active session.", "FORBIDDEN");
            return null;
        }

        if (caller.IsReadOnlySession)
        {
            denial = new DocDenial("Forbidden: Depositing documentation is not allowed in a read-only session.", "FORBIDDEN");
            return null;
        }

        if (caller.IsCanonicalClusterAdmin)
        {
            denial = null!;
            return new DocWriter(TenantId: null, AuthorSid: caller.AuthorSid);
        }

        if (DocumentRules.IsReservedAppId(appId))
        {
            denial = new DocDenial("Forbidden: Depositing documentation for global application requires ClusterAdmin role.", "FORBIDDEN");
            return null;
        }

        if (caller.IsGovernanceAdmin)
        {
            if (string.IsNullOrWhiteSpace(caller.TenantId))
            {
                denial = new DocDenial("Forbidden: Depositing documentation requires a tenant-bound session.", "FORBIDDEN");
                return null;
            }

            denial = null!;
            return new DocWriter(TenantId: caller.TenantId.Trim(), AuthorSid: caller.AuthorSid);
        }

        denial = new DocDenial("Forbidden: Depositing documentation requires GovernanceAdmin or ClusterAdmin role.", "FORBIDDEN");
        return null;
    }

    public static DocAccessScope ResolveReader(DocCaller caller) =>
        DocAccessScope.ForTenant(caller?.TenantId);
}
