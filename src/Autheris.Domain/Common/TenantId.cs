namespace Autheris.Domain.Common;

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

/// <summary>
/// Streng typisierte, validierte Tenant-Identität.
/// Verhindert Cache-Key Injections und Cross-Tenant Verwechslungen.
/// </summary>
public readonly record struct TenantId
{
    private static readonly Regex SafeTenantIdRegex = new(@"^([a-zA-Z0-9_-]{1,64}|\*)\z", RegexOptions.Compiled);

    public string Value { get; }

    public TenantId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!SafeTenantIdRegex.IsMatch(value))
        {
            throw new ArgumentException(
                $"Ungültiges TenantId-Format: '{value}'. Erwartet: alphanumerisch, '_', '-', max. 64 Zeichen oder '*'.",
                nameof(value));
        }
        Value = value;
    }

    public static readonly TenantId LegacySingleTenant = new("legacy-single-tenant");
    public static readonly TenantId Wildcard = new("*");

    public static bool TryParse(string? value, out TenantId tenantId)
    {
        if (string.IsNullOrWhiteSpace(value) || !SafeTenantIdRegex.IsMatch(value))
        {
            tenantId = default;
            return false;
        }
        tenantId = new TenantId(value);
        return true;
    }

    public override string ToString() => Value;
    public static implicit operator string(TenantId tenantId) => tenantId.Value;
    public static implicit operator TenantId(string value) => new(value);
}

/// <summary>
/// Framework-unabhängiger Sicherheitskontext des Aufrufers (Clean Architecture).
/// Entkoppelt Application- und Domain-Services von ASP.NET Core ClaimsPrincipal.
/// </summary>
public sealed record CallerSecurityContext(
    Sid UserSid,
    IReadOnlyCollection<Sid> GroupSids,
    IReadOnlyCollection<string> Roles,
    TenantId Tenant,
    bool IsGovernanceAdmin,
    bool IsClusterAdmin,
    Autheris.Domain.Model.SubjectIdentity? Subject = null
)
{
    public TenantId TenantId => Tenant;
}
