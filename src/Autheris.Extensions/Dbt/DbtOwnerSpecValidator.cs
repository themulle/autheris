namespace Autheris.Extensions.Dbt;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Mail;
using System.Text.Json;
using Autheris.Application.Governance;
using Autheris.Domain.Model;

/// <summary>A rejected owner payload. <see cref="Code"/> is a governed code, <see cref="Field"/> names the offending field; neither ever carries a value (I393B-SC-01, T-15).</summary>
public sealed record DbtOwnerError(string Code, string Field);

public sealed record DbtOwnerParseResult(IReadOnlyList<DataOwnerSpec> Owners, DbtOwnerError? Error);

/// <summary>
/// Pure parse and format validation of the <c>owner</c> / <c>owners</c> keys of a dbt governance import item (issue #393 part b, decisions 3-5;
/// I393B-SC-01, SC-05, SC-09, SC-10). Strict: unknown keys, groups, reserved prefixes, control and bidi characters reject. No value is ever
/// echoed in an error.
/// </summary>
public static class DbtOwnerSpecValidator
{
    public const int MaxOwnersPerTable = 10;
    public const int MaxSidLength = 256;
    public const int MaxEmailLength = 320;
    public const int MaxDisplayNameLength = 200;

    public const string InvalidCode = "dbt_owner_invalid";
    public const string GroupUnsupportedCode = "dbt_owner_group_unsupported";
    public const string SelfAssignmentCode = "dbt_owner_self_assignment";
    public const string TooManyCode = "dbt_owner_too_many";

    private static readonly string[] ReservedSidPrefixes = ["system:", "S-1-5-21-FORWARD-GRP-"];
    private static readonly string[] OwnerKeys = ["owner", "owners"];
    private static readonly string[] GroupKeys = ["group", "groups"];

    /// <summary>True when the item carries any owner related key (<c>owner</c>, <c>owners</c>, <c>group</c>, <c>groups</c>).</summary>
    public static bool HasOwnerKeys(JsonElement item) =>
        item.ValueKind == JsonValueKind.Object &&
        OwnerKeys.Concat(GroupKeys).Any(key => item.TryGetProperty(key, out _));

    public static DbtOwnerParseResult Parse(JsonElement item)
    {
        var owners = new List<DataOwnerSpec>();
        if (item.ValueKind != JsonValueKind.Object)
        {
            return new DbtOwnerParseResult(owners, null);
        }

        foreach (var key in GroupKeys)
        {
            if (item.TryGetProperty(key, out _))
            {
                return Fail(GroupUnsupportedCode, key);
            }
        }

        foreach (var key in OwnerKeys)
        {
            if (!item.TryGetProperty(key, out var value))
            {
                continue;
            }

            switch (value.ValueKind)
            {
                case JsonValueKind.Array:
                    foreach (var element in value.EnumerateArray())
                    {
                        if (owners.Count > MaxOwnersPerTable)
                        {
                            return Fail(TooManyCode, key);
                        }

                        var error = ParseOne(element, owners);
                        if (error is not null)
                        {
                            return new DbtOwnerParseResult(owners, error);
                        }
                    }

                    break;
                case JsonValueKind.Object or JsonValueKind.String:
                    {
                        var error = ParseOne(value, owners);
                        if (error is not null)
                        {
                            return new DbtOwnerParseResult(owners, error);
                        }

                        break;
                    }
                default:
                    return Fail(InvalidCode, key);
            }
        }

        return new DbtOwnerParseResult(owners, null);
    }

    /// <summary>Format validation, owner count and the self-assignment check against <paramref name="callerIdentifiers"/> (I393B-SC-05).</summary>
    public static DbtOwnerError? Validate(IReadOnlyList<DataOwnerSpec> owners, IReadOnlyCollection<string> callerIdentifiers)
    {
        ArgumentNullException.ThrowIfNull(owners);
        ArgumentNullException.ThrowIfNull(callerIdentifiers);
        if (owners.Count > MaxOwnersPerTable)
        {
            return new DbtOwnerError(TooManyCode, "owners");
        }

        var callerSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var identifier in callerIdentifiers)
        {
            foreach (var candidate in OwnerIdentifier.Candidates(identifier))
            {
                callerSet.Add(candidate);
            }
        }

        foreach (var owner in owners)
        {
            var error = ValidateFormat(owner);
            if (error is not null)
            {
                return error;
            }

            if (OwnerIdentifier.ShareCandidate(owner.Sid, callerSet) ||
                OwnerIdentifier.ShareCandidate(owner.Account, callerSet) ||
                OwnerIdentifier.ShareCandidate(owner.Email, callerSet))
            {
                return new DbtOwnerError(SelfAssignmentCode, "sid");
            }
        }

        return null;
    }

    private static DbtOwnerError? ParseOne(JsonElement element, List<DataOwnerSpec> owners)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            owners.Add(new DataOwnerSpec(element.GetString() ?? string.Empty, null, null, null, "PRIMARY", LookupOnly: true));
            return null;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return new DbtOwnerError(InvalidCode, "owner");
        }

        string? sid = null, account = null, displayName = null, email = null;
        var role = "PRIMARY";
        foreach (var property in element.EnumerateObject())
        {
            var name = property.Name.ToLowerInvariant();
            if (name is "group" or "groups")
            {
                return new DbtOwnerError(GroupUnsupportedCode, name);
            }

            if (property.Value.ValueKind != JsonValueKind.String)
            {
                return new DbtOwnerError(InvalidCode, name is "sid" or "account" or "display_name" or "email" or "role" ? name : "owner");
            }

            var text = property.Value.GetString() ?? string.Empty;
            switch (name)
            {
                case "sid": sid = text; break;
                case "account": account = text; break;
                case "display_name": displayName = text; break;
                case "email": email = text; break;
                case "role":
                    if (string.Equals(text, "primary", StringComparison.OrdinalIgnoreCase))
                    {
                        role = "PRIMARY";
                    }
                    else if (string.Equals(text, "delegate", StringComparison.OrdinalIgnoreCase))
                    {
                        role = "DELEGATE";
                    }
                    else
                    {
                        return new DbtOwnerError(InvalidCode, "role");
                    }

                    break;
                default:
                    return new DbtOwnerError(InvalidCode, "owner");
            }
        }

        if (string.IsNullOrEmpty(sid))
        {
            return new DbtOwnerError(InvalidCode, "sid");
        }

        owners.Add(new DataOwnerSpec(sid, account, displayName, email, role));
        return null;
    }

    private static DbtOwnerError? ValidateFormat(DataOwnerSpec owner)
    {
        if (!IsPlainIdentifier(owner.Sid, MaxSidLength) || HasReservedPrefix(owner.Sid))
        {
            return new DbtOwnerError(InvalidCode, "sid");
        }

        if (owner.LookupOnly)
        {
            return null;
        }

        if (owner.Account is not null && (!IsPlainIdentifier(owner.Account, MaxSidLength) || HasReservedPrefix(owner.Account)))
        {
            return new DbtOwnerError(InvalidCode, "account");
        }

        if (owner.Email is not null && !IsValidEmail(owner.Email))
        {
            return new DbtOwnerError(InvalidCode, "email");
        }

        if (owner.DisplayName is not null && !IsValidDisplayName(owner.DisplayName))
        {
            return new DbtOwnerError(InvalidCode, "display_name");
        }

        return null;
    }

    private static bool HasReservedPrefix(string value)
    {
        var normalized = OwnerIdentifier.Normalize(value);
        return ReservedSidPrefixes.Any(prefix => normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsPlainIdentifier(string value, int maxLength) =>
        value.Length is > 0 && value.Length <= maxLength && value.All(c => !char.IsWhiteSpace(c) && !IsControlOrFormat(c));

    private static bool IsValidEmail(string email)
    {
        if (!IsPlainIdentifier(email, MaxEmailLength))
        {
            return false;
        }

        return MailAddress.TryCreate(email, out var parsed) && string.Equals(parsed.Address, email, StringComparison.Ordinal);
    }

    private static bool IsValidDisplayName(string name) =>
        name.Length is > 0 && name.Length <= MaxDisplayNameLength && name.All(c => !IsControlOrFormat(c));

    // Control, format (bidi overrides, zero-width), line and paragraph separators: display names reach LLM callers via MCP (I393B-SC-10).
    private static bool IsControlOrFormat(char c) =>
        CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.Control or UnicodeCategory.Format
            or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;

    private static DbtOwnerParseResult Fail(string code, string field) => new([], new DbtOwnerError(code, field));
}
