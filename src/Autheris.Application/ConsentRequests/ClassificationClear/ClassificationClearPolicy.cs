namespace Autheris.Application.ConsentRequests.ClassificationClear;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Autheris.Application.Policy.Classification;
using Autheris.Domain.Model;

/// <summary>One requested per-classification Clear: the classification and the columns it should be cleared for.</summary>
public sealed record RequestedClearItem(string Classification, IReadOnlyList<string> Columns);

/// <summary>Stable error codes of per-classification Clear requests and approvals.</summary>
public static class ClearRequestErrorCodes
{
    public const string Invalid = "CLEAR_REQUEST_INVALID";
    public const string UnknownClassification = "CLEAR_UNKNOWN_CLASSIFICATION";
    public const string NotGrantable = "CLEAR_NOT_GRANTABLE";
    public const string ColumnNotClassified = "CLEAR_COLUMN_NOT_CLASSIFIED";
    public const string StewardRequired = "CLEAR_STEWARD_REQUIRED";
    public const string ApproverOrder = "CLEAR_APPROVER_ORDER";
    public const string Unavailable = "CLEAR_UNAVAILABLE";
}

/// <summary>
/// Persists the requested per-classification Clear inside the existing <c>context_json</c> payload of the consent request
/// (key <c>requestedClear</c>); no dedicated column is needed. The key is written by the server only and is re-validated
/// against the live classification vocabulary on every decision.
/// </summary>
public static class RequestedClearCodec
{
    public const string JsonKey = "requestedClear";

    public static string Merge(string? contextJson, IReadOnlyList<RequestedClearItem> items)
    {
        var root = TryParseObject(contextJson) ?? new JsonObject();
        root[JsonKey] = new JsonArray(items.Select(static i => (JsonNode)new JsonObject
        {
            ["classification"] = i.Classification,
            ["columns"] = new JsonArray(i.Columns.Select(static c => (JsonNode?)JsonValue.Create(c)).ToArray())
        }).ToArray());
        return root.ToJsonString();
    }

    /// <summary>Removes the server-owned key from client supplied context so a requester can never inject Clear items.</summary>
    public static string? Strip(string? contextJson)
    {
        var root = TryParseObject(contextJson);
        if (root is null)
        {
            return contextJson;
        }

        root.Remove(JsonKey);
        return root.ToJsonString();
    }

    /// <summary>Reads the requested Clear items; malformed content yields no items (masked, never widened).</summary>
    public static IReadOnlyList<RequestedClearItem> Parse(string? contextJson)
    {
        var root = TryParseObject(contextJson);
        if (root is null || !root.TryGetPropertyValue(JsonKey, out var node) || node is not JsonArray array)
        {
            return [];
        }

        var items = new List<RequestedClearItem>(array.Count);
        foreach (var entry in array)
        {
            if (entry is not JsonObject obj ||
                obj["classification"] is not JsonValue code ||
                !code.TryGetValue<string>(out var classification) ||
                string.IsNullOrWhiteSpace(classification))
            {
                continue;
            }

            var columns = new List<string>();
            if (obj["columns"] is JsonArray cols)
            {
                foreach (var col in cols)
                {
                    if (col is JsonValue v && v.TryGetValue<string>(out var name) && !string.IsNullOrWhiteSpace(name))
                    {
                        columns.Add(name);
                    }
                }
            }

            items.Add(new RequestedClearItem(classification, columns));
        }

        return items;
    }

    private static JsonObject? TryParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Approver routing derived from the requested classifications.</summary>
/// <param name="Items">Validated requested Clear items.</param>
/// <param name="StewardGroupSids">Steward groups of the requested classifications that need a steward approval.</param>
public sealed record ClearApprovalPlan(
    IReadOnlyList<RequestedClearItem> Items,
    IReadOnlyList<string> StewardGroupSids,
    ClassificationPolicyCatalog Catalog)
{
    /// <summary>A steward must approve after the data owner (sequential four-eyes).</summary>
    public bool NeedsSteward => StewardGroupSids.Count > 0;
}

/// <summary>Pure rules for per-classification Clear requests: validation, routing, narrowing and validity cap.</summary>
public static class ClassificationClearPolicy
{
    public static ClassificationPolicyCatalog ResolveCatalog(string tenant, IReadOnlyList<DataClassification> definitions) =>
        definitions.Count == 0
            ? ClassificationAccessResolver.BuiltInCatalog(tenant)
            : ClassificationAccessResolver.BuildCatalog(tenant, definitions, []);

    /// <summary>
    /// Validates a Clear request against the vocabulary and the table. <c>CREDENTIALS</c> is never requestable, a classification
    /// must be active, clear-grantable and need a separate approval, and every column must exist and carry exactly that
    /// classification (no hierarchy inheritance: Clear for <c>PII</c> never covers <c>PII_SPECIAL</c>).
    /// </summary>
    /// <exception cref="ClearRequestException">The request is invalid.</exception>
    public static IReadOnlyList<RequestedClearItem> ValidateRequest(
        IReadOnlyList<RequestedClearItem> items,
        ClassificationPolicyCatalog catalog,
        TableMetadata meta,
        string tenant)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            throw new ClearRequestException(ClearRequestErrorCodes.Invalid, "A requested classification Clear needs at least one item.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var normalized = new List<RequestedClearItem>(items.Count);
        foreach (var item in items)
        {
            var code = item?.Classification;
            if (!ClassificationHierarchyValidator.IsValidClassificationCode(code) || !seen.Add(code!))
            {
                throw new ClearRequestException(ClearRequestErrorCodes.Invalid, "Each requested classification must be a valid, distinct code.");
            }

            if (string.Equals(code, ClassificationScopes.CredentialsCode, StringComparison.Ordinal))
            {
                throw new ClearRequestException(ClearRequestErrorCodes.NotGrantable, "Clear access can never be requested for credentials.");
            }

            if (!catalog.Definitions.TryGetValue(code!, out var def) || !def.IsActive)
            {
                throw new ClearRequestException(ClearRequestErrorCodes.UnknownClassification, "The requested classification is unknown or inactive.");
            }

            if (!def.ClearGrantable || !def.SeparateApproval)
            {
                throw new ClearRequestException(ClearRequestErrorCodes.NotGrantable, "Clear access is not grantable for the requested classification.");
            }

            var columns = item!.Columns?.Where(static c => !string.IsNullOrWhiteSpace(c)).Select(static c => c.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [];
            if (columns.Count == 0)
            {
                throw new ClearRequestException(ClearRequestErrorCodes.Invalid, "Each requested classification needs at least one column.");
            }

            var canonical = new List<string>(columns.Count);
            foreach (var name in columns)
            {
                var column = meta.GetColumn(name);
                if (column is null ||
                    !ClassificationMetadata.TryGetEffectiveColumnClassifications(meta, column.ColumnName, tenant, out var assigned) ||
                    !assigned.Any(a => string.Equals(a.Code, code, StringComparison.Ordinal)))
                {
                    throw new ClearRequestException(ClearRequestErrorCodes.ColumnNotClassified, "A requested column does not carry the requested classification.");
                }

                canonical.Add(column.ColumnName);
            }

            normalized.Add(new RequestedClearItem(code!, canonical));
        }

        return normalized;
    }

    /// <summary>Routes each requested classification to its approver mode (owner, steward, or owner then steward).</summary>
    public static ClearApprovalPlan BuildPlan(IReadOnlyList<RequestedClearItem> items, ClassificationPolicyCatalog catalog)
    {
        var stewards = new List<string>();
        foreach (var item in items)
        {
            if (!catalog.Definitions.TryGetValue(item.Classification, out var def))
            {
                continue;
            }

            if (def.ApproverMode is ApproverMode.Steward or ApproverMode.DataOwnerAndSteward)
            {
                // A steward mode without a configured group can never be satisfied: fail closed with a sentinel nobody holds.
                var sid = string.IsNullOrWhiteSpace(def.StewardGroupSid) ? "STEWARD_UNCONFIGURED" : def.StewardGroupSid;
                if (!stewards.Contains(sid, StringComparer.OrdinalIgnoreCase))
                {
                    stewards.Add(sid);
                }
            }
        }

        return new ClearApprovalPlan(items, stewards, catalog);
    }

    /// <summary>
    /// Applies an approver's selection. <c>null</c> keeps every requested item; a selection may only narrow, never add a
    /// classification that was not requested.
    /// </summary>
    /// <returns>The approved items, or <c>null</c> with <paramref name="exceeds"/> set when the selection widens the request.</returns>
    public static IReadOnlyList<RequestedClearItem>? Narrow(
        IReadOnlyList<RequestedClearItem> requested,
        IReadOnlyList<string>? approvedCodes,
        out bool exceeds)
    {
        exceeds = false;
        if (approvedCodes is null)
        {
            return requested;
        }

        var requestedSet = requested.Select(static r => r.Classification).ToHashSet(StringComparer.Ordinal);
        if (approvedCodes.Any(c => !requestedSet.Contains(c)))
        {
            exceeds = true;
            return null;
        }

        var approvedSet = approvedCodes.ToHashSet(StringComparer.Ordinal);
        return requested.Where(r => approvedSet.Contains(r.Classification)).ToList();
    }

    /// <summary>Clear lasts at most <c>min(grant valid_until, approved_at + max_clear_days)</c>.</summary>
    public static DateTimeOffset CapValidity(
        DateTimeOffset grantValidTo,
        DateTimeOffset approvedAt,
        DataClassification definition,
        int defaultMaxClearDays)
    {
        var days = definition.MaxClearDays is > 0 ? definition.MaxClearDays.Value : Math.Max(1, defaultMaxClearDays);
        var cap = approvedAt.AddDays(Math.Min(days, 36500));
        return cap < grantValidTo ? cap : grantValidTo;
    }
}

/// <summary>A Clear request violates the vocabulary or table rules.</summary>
public sealed class ClearRequestException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
