namespace Autheris.Application.Identity.Services;

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Caching;
using Autheris.Application.Identity.Validators;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Model;

public sealed partial class ScimService
{
    private async Task<PrivilegedGroupClassification?> PreparePrivilegedGroupAuditAsync(
        TenantId tenantId,
        string realmId,
        string groupId,
        string? groupObjectSid,
        string? previousGroupObjectSid,
        IReadOnlyList<ScimMembershipAuditItem> changes,
        string operation,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (_privilegedGroupAuditor is null)
        {
            return null;
        }

        var classification = await _privilegedGroupAuditor.ClassifyGroupAsync(
            tenantId, realmId, groupId, groupObjectSid, previousGroupObjectSid, ct).ConfigureAwait(false);
        if (!classification.IsPrivileged)
        {
            return classification;
        }

        await _privilegedGroupAuditor.AuditBeforeMutationAsync(
            tenantId, realmId, groupId, groupObjectSid ?? previousGroupObjectSid, changes, classification, operation, now, ct).ConfigureAwait(false);
        return classification;
    }

    private async Task CompleteGroupChangeNotificationsAsync(
        TenantId tenantId,
        string realmId,
        string groupId,
        string operation,
        PrivilegedGroupClassification? classification,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await NotifyIdentityGovernanceChangeAsync(tenantId, realmId, subjectKey: null, groupId: groupId, now, ct).ConfigureAwait(false);

        if (_privilegedGroupAuditor is not null && classification is { IsPrivileged: true })
        {
            await _privilegedGroupAuditor.DispatchNotificationAsync(
                tenantId, realmId, groupId, operation, classification, now, ct).ConfigureAwait(false);
        }
    }

    private Task NotifyIdentityGovernanceChangeAsync(
        TenantId tenantId,
        string realmId,
        string? subjectKey,
        string? groupId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (_governanceChangeNotifier is null)
        {
            return Task.CompletedTask;
        }

        return _governanceChangeNotifier.NotifyAsync(
            GovernanceChangeEvent.ForIdentity(tenantId.Value, realmId, now, subjectKey: subjectKey, groupId: groupId),
            ct);
    }

    private static void ValidateNoSelfNesting(string groupId, IReadOnlyList<ScimGroupMember>? members)
    {
        if (members is null)
        {
            return;
        }

        foreach (var member in members)
        {
            if (ScimRequestValidator.NormalizeMemberType(member.Type) == ScimMemberTypes.Group
                && string.Equals(member.Value, groupId, StringComparison.Ordinal))
            {
                throw new ValidationException("A group cannot be nested as a member of itself.");
            }
        }
    }

    private static void ApplyUserPatchOperation(
        ScimPatchOperation op,
        ref bool activeStatus,
        ref string? displayName,
        ref string? email,
        ref string? objectSid,
        ref string? immutableId,
        ref string? upn,
        ref string? entraObjectId)
    {
        var isRemove = string.Equals(op.Op, "remove", StringComparison.OrdinalIgnoreCase);
        var isSet = string.Equals(op.Op, "replace", StringComparison.OrdinalIgnoreCase)
            || string.Equals(op.Op, "add", StringComparison.OrdinalIgnoreCase);

        var normalizedPath = op.Path?.Trim().ToLowerInvariant();
        if (isRemove)
        {
            switch (normalizedPath)
            {
                case "objectsid":
                case "urn:ietf:params:scim:schemas:extension:autheris:2.0:user:objectsid":
                    objectSid = null;
                    break;
                case "onpremisesimmutableid":
                case "urn:ietf:params:scim:schemas:extension:autheris:2.0:user:onpremisesimmutableid":
                    immutableId = null;
                    break;
                case "userprincipalname":
                case "urn:ietf:params:scim:schemas:extension:autheris:2.0:user:userprincipalname":
                    upn = null;
                    break;
                case "entraobjectid":
                case "urn:ietf:params:scim:schemas:extension:autheris:2.0:user:entraobjectid":
                    entraObjectId = null;
                    break;
            }
            return;
        }

        if (!isSet)
        {
            return;
        }

        switch (normalizedPath)
        {
            case "active":
                if (op.Value is bool b) activeStatus = b;
                else if (op.Value is JsonElement je && (je.ValueKind == JsonValueKind.True || je.ValueKind == JsonValueKind.False))
                    activeStatus = je.GetBoolean();
                else if (bool.TryParse(op.Value?.ToString(), out var parsed))
                    activeStatus = parsed;
                break;
            case "displayname":
                displayName = ExtractStringValue(op.Value);
                break;
            case "email":
                email = ExtractStringValue(op.Value);
                break;
            case "objectsid":
            case "urn:ietf:params:scim:schemas:extension:autheris:2.0:user:objectsid":
                objectSid = ExtractStringValue(op.Value);
                break;
            case "onpremisesimmutableid":
            case "urn:ietf:params:scim:schemas:extension:autheris:2.0:user:onpremisesimmutableid":
                immutableId = ExtractStringValue(op.Value);
                break;
            case "userprincipalname":
            case "urn:ietf:params:scim:schemas:extension:autheris:2.0:user:userprincipalname":
                upn = ExtractStringValue(op.Value);
                break;
            case "entraobjectid":
            case "urn:ietf:params:scim:schemas:extension:autheris:2.0:user:entraobjectid":
                entraObjectId = ExtractStringValue(op.Value);
                break;
            case null or "":
                if (op.Value is JsonElement valElem && valElem.ValueKind == JsonValueKind.Object)
                {
                    if (valElem.TryGetProperty("active", out var actElem)
                        && (actElem.ValueKind == JsonValueKind.True || actElem.ValueKind == JsonValueKind.False))
                    {
                        activeStatus = actElem.GetBoolean();
                    }
                    if (valElem.TryGetProperty("displayName", out var dnElem)) displayName = dnElem.GetString();
                    if (valElem.TryGetProperty("email", out var emElem)) email = emElem.GetString();
                    if (valElem.TryGetProperty(ScimSchemaUris.AutherisUserExtension, out var extElem)
                        && extElem.ValueKind == JsonValueKind.Object)
                    {
                        if (extElem.TryGetProperty("objectSid", out var sidEl)) objectSid = sidEl.GetString();
                        if (extElem.TryGetProperty("onPremisesImmutableId", out var immEl)) immutableId = immEl.GetString();
                        if (extElem.TryGetProperty("userPrincipalName", out var upnEl)) upn = upnEl.GetString();
                        if (extElem.TryGetProperty("entraObjectId", out var oidEl)) entraObjectId = oidEl.GetString();
                    }
                }
                break;
        }
    }

    private static bool IsGroupObjectSidPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var trimmed = path.Trim();
        return string.Equals(trimmed, "objectSid", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, $"{ScimSchemaUris.AutherisGroupExtension}:objectSid", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExtractStringValue(object? value) => value switch
    {
        null => null,
        string s => s,
        JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString(),
        JsonElement je when je.ValueKind == JsonValueKind.Null => null,
        _ => value.ToString()
    };

    private static void ExtractMembers(object? value, string? defaultType, List<(string Value, string? ExplicitType)> target)
    {
        if (value is JsonElement arr && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                if (item.TryGetProperty("value", out var valProp) && valProp.ValueKind == JsonValueKind.String)
                {
                    var val = valProp.GetString();
                    if (string.IsNullOrWhiteSpace(val)) continue;
                    string? explicitType = defaultType;
                    if (item.TryGetProperty("type", out var typeProp) && typeProp.ValueKind == JsonValueKind.String)
                    {
                        explicitType = ScimRequestValidator.NormalizeMemberType(typeProp.GetString());
                    }

                    target.Add((val, explicitType));
                }
            }
        }
        else if (value is IEnumerable<ScimGroupMember> memberList)
        {
            foreach (var m in memberList)
            {
                if (!string.IsNullOrWhiteSpace(m.Value))
                {
                    var explicitType = m.Type is null ? defaultType : ScimRequestValidator.NormalizeMemberType(m.Type);
                    target.Add((m.Value, explicitType));
                }
            }
        }
    }

    private static void ExtractMembersToAdd(object? value, List<(string Value, string Type)> target)
    {
        var raw = new List<(string Value, string? ExplicitType)>();
        ExtractMembers(value, ScimMemberTypes.User, raw);
        foreach (var (val, explicitType) in raw)
        {
            target.Add((val, explicitType ?? ScimMemberTypes.User));
        }
    }
}
