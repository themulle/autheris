namespace Autheris.Application.Governance.Paging;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// WP-SC-10: opaque keyset cursor. It only encodes a position (a chain sequence or an id) and carries no authority: every page query
/// still applies the tenant and the caller's filters, so a forged or replayed cursor can never widen what a caller may read.
/// </summary>
public static class PageCursor
{
    private const string Prefix = "c1.";

    public static string Encode(string position)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(position);
        return Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(position)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static string Encode(long position) => Encode(position.ToString(CultureInfo.InvariantCulture));

    /// <summary>Returns false for a missing, malformed or foreign cursor (callers answer 400, never fall back to the first page silently).</summary>
    public static bool TryDecode(string? cursor, out string position)
    {
        position = string.Empty;
        if (string.IsNullOrEmpty(cursor) || cursor.Length > 256 || !cursor.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var b64 = cursor[Prefix.Length..].Replace('-', '+').Replace('_', '/');
        b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
        try
        {
            position = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
            return position.Length > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static bool TryDecodeLong(string? cursor, out long position)
    {
        position = 0;
        return TryDecode(cursor, out var raw) &&
               long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out position) &&
               position > 0;
    }
}

/// <summary>Keyset page request over the audit chain, newest first. <see cref="BeforeSequence"/> is exclusive.</summary>
/// <param name="Tenant">Null only for a cluster-wide read (ClusterAdmin); every other caller is bound to its tenant.</param>
public sealed record AuditLogPageQuery(
    TenantId? Tenant,
    string? TargetTable = null,
    Sid? Actor = null,
    DateTimeOffset? Since = null,
    long? BeforeSequence = null,
    int PageSize = AuditLogPageQuery.DefaultPageSize)
{
    public const int DefaultPageSize = 100;
    public const int MaxPageSize = 500;

    public int EffectivePageSize => Math.Clamp(PageSize, 1, MaxPageSize);
}

/// <param name="NextBeforeSequence">Null when this is the last page.</param>
public sealed record AuditLogPage(IReadOnlyList<AuditLogEntry> Items, long? NextBeforeSequence);

/// <summary>Keyset page request over the consents of one tenant, ordered by id. <see cref="AfterId"/> is exclusive.</summary>
public sealed record ConsentPageQuery(
    TenantId Tenant,
    TableIdentifier? Table = null,
    GranteeType? GranteeType = null,
    string? Grantee = null,
    bool IncludeInactive = false,
    Guid? AfterId = null,
    int PageSize = ConsentPageQuery.DefaultPageSize)
{
    public const int DefaultPageSize = 100;
    public const int MaxPageSize = 500;

    public int EffectivePageSize => Math.Clamp(PageSize, 1, MaxPageSize);
}

/// <param name="NextAfterId">Null when this is the last page.</param>
public sealed record ConsentPage(IReadOnlyList<Consent> Items, Guid? NextAfterId);
