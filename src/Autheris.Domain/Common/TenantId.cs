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
    private static readonly Regex SafeTenantIdRegex = new(@"^[a-zA-Z0-9_-]{1,64}\z", RegexOptions.Compiled);

    public string Value { get; }

    public TenantId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!SafeTenantIdRegex.IsMatch(value))
        {
            throw new ArgumentException(
                $"Invalid TenantId format: '{value}'. Expected: alphanumeric, '_', '-', max. 64 characters.",
                nameof(value));
        }

        // DT-SC-07: the "_" prefix is reserved for platform scopes; only the dedicated factory below may build one.
        if (IsReserved(value))
        {
            throw new ArgumentException("Invalid TenantId: the '_' prefix is reserved.", nameof(value));
        }

        Value = value;
    }

    private TenantId(string value, bool platformScope)
    {
        _ = platformScope;
        Value = value;
    }

    /// <summary>Prefix of reserved (platform) tenant ids, matched case-insensitively (DT-SC-07).</summary>
    public const char ReservedPrefix = '_';

    /// <summary>
    /// The platform scope bucket (DT-A-6): the tenant of tenant-less platform records (auth-failure aggregation, startup audit).
    /// Never a request tenant: claim, header, realm binding and default tenant reject it.
    /// </summary>
    public static readonly TenantId Platform = new("_platform", true);

    /// <summary>True when the value starts with the reserved <c>_</c> prefix (DT-SC-07).</summary>
    public static bool IsReserved(string? value) => value is { Length: > 0 } && value[0] == ReservedPrefix;

    /// <summary>True for a reserved id such as <see cref="Platform"/>; <c>default</c> is not reserved (it is invalid, DT-SC-04).</summary>
    public static bool IsReserved(TenantId tenantId) => IsReserved(tenantId.Value);

    public static bool TryParse(string? value, out TenantId tenantId)
    {
        if (string.IsNullOrWhiteSpace(value) || !SafeTenantIdRegex.IsMatch(value) || IsReserved(value))
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
