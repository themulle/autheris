namespace Autheris.Application.Governance.Provisioning;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

/// <summary>The Unity Catalog securable kinds the provisioning path manages.</summary>
public enum UcSecurableType
{
    Catalog = 0,
    Schema = 1,
    Table = 2
}

/// <summary>
/// The privileges the provisioning path may change: <c>SELECT</c> on tables and <c>USE_SCHEMA</c> on schemas. Catalogs are only
/// read; the provisioning principal holds <c>MANAGE</c> on the allowed schemas, never on a catalog.
/// </summary>
public static class UcPrivileges
{
    public const string Select = "SELECT";
    public const string UseSchema = "USE_SCHEMA";
    public const string UseCatalog = "USE_CATALOG";

    /// <summary>The only privilege Autheris adds on a securable of <paramref name="type"/>; null for a catalog (never changed).</summary>
    public static string? AddableOn(UcSecurableType type) => type switch
    {
        UcSecurableType.Table => Select,
        UcSecurableType.Schema => UseSchema,
        UcSecurableType.Catalog => null,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown securable type.")
    };

    /// <summary>The path segment of the Unity Catalog permissions API for <paramref name="type"/>.</summary>
    public static string ApiSegment(UcSecurableType type) => type switch
    {
        UcSecurableType.Table => "table",
        UcSecurableType.Schema => "schema",
        UcSecurableType.Catalog => "catalog",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown securable type.")
    };
}

/// <summary>The privileges one principal holds directly on a securable (inherited privileges are not listed).</summary>
public sealed record UcPrivilegeAssignment(string Principal, IReadOnlyList<string> Privileges);

/// <summary>One entry of a permissions update: privileges added to and removed from one principal.</summary>
public sealed record UcPermissionChange(string Principal, IReadOnlyList<string> Add, IReadOnlyList<string> Remove);

/// <summary>A workspace principal as Unity Catalog names it, with the identity provider id it was resolved from.</summary>
/// <param name="Name">The Unity Catalog principal name: user name, group display name or application id.</param>
/// <param name="ExternalId">The identity provider object id (users, groups) or the application id (service principals).</param>
public sealed record UcPrincipal(GranteeType Type, string Name, string ExternalId);

public enum UcPrincipalResolutionStatus
{
    Resolved = 0,
    NotFound = 1,
    Ambiguous = 2
}

/// <summary>The outcome of resolving an Autheris grantee to a workspace principal. <see cref="Principal"/> is set only when resolved.</summary>
public sealed record UcPrincipalResolution(UcPrincipalResolutionStatus Status, UcPrincipal? Principal)
{
    public static UcPrincipalResolution NotFound { get; } = new(UcPrincipalResolutionStatus.NotFound, null);
    public static UcPrincipalResolution Ambiguous { get; } = new(UcPrincipalResolutionStatus.Ambiguous, null);

    public static UcPrincipalResolution Resolved(UcPrincipal principal) =>
        new(UcPrincipalResolutionStatus.Resolved, principal ?? throw new ArgumentNullException(nameof(principal)));
}

/// <summary>Stable error codes of the Unity Catalog client; the only failure detail that is stored or returned.</summary>
public static class UcErrorCodes
{
    public const string Configuration = "UC_CONFIGURATION";
    public const string InvalidRequest = "UC_INVALID_REQUEST";
    public const string SecurableNotAllowed = "UC_SECURABLE_NOT_ALLOWED";
    public const string PrivilegeNotAllowed = "UC_PRIVILEGE_NOT_ALLOWED";
    public const string AuthFailed = "UC_AUTH_FAILED";
    public const string Forbidden = "UC_FORBIDDEN";
    public const string NotFound = "UC_NOT_FOUND";
    public const string RateLimited = "UC_RATE_LIMITED";
    public const string Unavailable = "UC_UNAVAILABLE";
    public const string Rejected = "UC_REJECTED";
    public const string Http = "UC_HTTP_ERROR";
    public const string Timeout = "UC_TIMEOUT";
    public const string Protocol = "UC_PROTOCOL_VIOLATION";
    public const string ResponseTooLarge = "UC_RESPONSE_TOO_LARGE";

    /// <summary>Whether a later attempt may succeed without a configuration change.</summary>
    public static bool IsTransient(string code) =>
        code is RateLimited or Unavailable or Timeout or Http or AuthFailed;
}

/// <summary>
/// A failed Unity Catalog call. Carries a stable <see cref="Code"/> and, when Databricks sent one, its validated error code;
/// never response text, principal names or tokens.
/// </summary>
public sealed class UcProvisioningException(string code, string? remoteErrorCode = null)
    : Exception($"The Unity Catalog call failed ({code}).")
{
    public string Code { get; } = code;

    /// <summary>The Databricks <c>error_code</c> token (upper-case letters and underscores only), or null.</summary>
    public string? RemoteErrorCode { get; } = remoteErrorCode;
}

/// <summary>
/// Reads and changes Unity Catalog privileges and resolves workspace principals for one provisioning-managed Databricks data
/// source, as its dedicated provisioning principal. Every call is limited to the securables inside the data source's allowed
/// schemas. Updates change tables and schemas only: they may add only <see cref="UcPrivileges.AddableOn"/> of the securable
/// kind, and remove any privilege on a table but only <c>USE_SCHEMA</c> on a schema.
/// </summary>
public interface IUnityCatalogPermissionsClient
{
    Task<IReadOnlyList<UcPrivilegeAssignment>> GetPermissionsAsync(
        string dataSource, UcSecurableType type, string fullName, CancellationToken ct);

    /// <summary>Applies <paramref name="changes"/> in one call and returns the direct privileges after the update.</summary>
    Task<IReadOnlyList<UcPrivilegeAssignment>> UpdatePermissionsAsync(
        string dataSource, UcSecurableType type, string fullName, IReadOnlyList<UcPermissionChange> changes, CancellationToken ct);

    /// <summary>
    /// Whether <paramref name="principal"/> effectively holds one of <paramref name="privileges"/> on the securable (directly, inherited from
    /// a parent securable or through a group), as the effective-permissions API reports it.
    /// </summary>
    Task<bool> HasEffectivePrivilegeAsync(
        string dataSource, UcSecurableType type, string fullName, string principal, IReadOnlyCollection<string> privileges, CancellationToken ct);

    /// <summary>
    /// Resolves a grantee through the workspace SCIM API: groups and users by external id (the identity provider object id),
    /// service principals by application id. Never by display name alone; zero or several matches are not resolved.
    /// </summary>
    Task<UcPrincipalResolution> ResolvePrincipalAsync(
        string dataSource, GranteeType type, string externalId, CancellationToken ct);
}
