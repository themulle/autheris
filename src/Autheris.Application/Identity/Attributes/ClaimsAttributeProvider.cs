namespace Autheris.Application.Identity.Attributes;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using TrinoSqlEngine.Ast.Emit;

/// <summary>A provider whose result is never kept in the value cache (it reads the live token of the request).</summary>
public interface INonCachingCallerAttributeProvider
{
}

/// <summary>
/// Reads an attribute from claims of the caller's validated bearer token (PLAN-GOV-129 A-5.1, BC-129-04). Per attribute: one claim type and
/// one trusted issuer; the claim must be of that issuer and belong to an identity of an allowed scheme (header-derived, Basic, Test and
/// normalizer-added claims never qualify). Two different values for a scalar attribute are <c>Failed(Ambiguous)</c>; size and count are
/// bounded before anything is parsed; a JSON array claim is parsed strictly (depth 1, no objects, no nulls, no comments).
/// Values are returned as raw strings; the resolver normalizes them. Staleness equals the token lifetime unless the definition sets
/// <c>maxTokenAgeSeconds</c>.
/// </summary>
public sealed class ClaimsAttributeProvider : ICallerAttributeProvider, INonCachingCallerAttributeProvider
{
    public const string ClaimKey = "claim";
    public const string IssuerKey = "issuer";
    public const string MaxTokenAgeKey = "maxTokenAgeSeconds";
    public const string IdpControlledAckKey = "idpControlledAck";

    private static readonly JsonDocumentOptions StrictJson = new() { MaxDepth = 2, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow };

    private readonly CallerAttributeClaimsOptions _options;
    private readonly TimeProvider _time;

    public ClaimsAttributeProvider(IOptions<GatewayOptions> options, TimeProvider? time = null)
    {
        _options = options?.Value?.CallerAttributes?.Claims ?? new CallerAttributeClaimsOptions();
        _time = time ?? TimeProvider.System;
    }

    public string Kind => CallerAttributeProviderBinding.Claims;

    public ValueTask<CallerAttributeResult> ResolveAsync(CallerSubject subject, CallerAttributeDefinition definition, CancellationToken ct) =>
        ValueTask.FromResult(Resolve(subject, definition));

    private CallerAttributeResult Resolve(CallerSubject subject, CallerAttributeDefinition definition)
    {
        if (subject.Kind != CallerSubjectKind.User)
        {
            return CallerAttributeResult.Missing("NotAUser");
        }

        var config = definition.Provider.Config;
        if (!config.TryGetValue(ClaimKey, out var claimType) || string.IsNullOrWhiteSpace(claimType) ||
            !config.TryGetValue(IssuerKey, out var issuer) || string.IsNullOrWhiteSpace(issuer))
        {
            return CallerAttributeResult.Failed("Misconfigured");
        }

        if (!_options.TrustedIssuers.Any(t => string.Equals(t.Issuer, issuer, StringComparison.Ordinal)))
        {
            return CallerAttributeResult.Failed("IssuerNotTrusted");
        }

        if (config.TryGetValue(MaxTokenAgeKey, out var maxAgeText))
        {
            if (!long.TryParse(maxAgeText, NumberStyles.None, CultureInfo.InvariantCulture, out var maxAge) || maxAge <= 0)
            {
                return CallerAttributeResult.Failed("Misconfigured");
            }

            var iat = subject.Claims.FirstOrDefault(c => c.Type == "iat" && IsAllowed(c));
            if (iat is null || !long.TryParse(iat.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var issuedAt) ||
                _time.GetUtcNow().ToUnixTimeSeconds() - issuedAt > maxAge)
            {
                return CallerAttributeResult.Missing("TokenTooOld");
            }
        }

        var matching = subject.Claims
            .Where(c => string.Equals(c.Type, claimType, StringComparison.Ordinal) && string.Equals(c.Issuer, issuer, StringComparison.Ordinal) && IsAllowed(c))
            .ToList();
        if (matching.Count == 0)
        {
            return CallerAttributeResult.Missing();
        }

        // Bounds before parsing (BC-129-04): count, per value and in total.
        if (matching.Count > definition.MaxValues ||
            matching.Any(c => c.Value.Length > _options.MaxClaimValueLength) ||
            matching.Sum(c => (long)Encoding.UTF8.GetByteCount(c.Value)) > _options.MaxTotalClaimBytes)
        {
            return CallerAttributeResult.Failed("ClaimTooLarge");
        }

        var values = new List<string>();
        foreach (var claim in matching)
        {
            var text = claim.Value.Trim();
            if (text.StartsWith('['))
            {
                if (!TryParseArray(text, values))
                {
                    return CallerAttributeResult.Failed("ClaimMalformed");
                }
            }
            else if (text.Length > 0)
            {
                values.Add(text);
            }
        }

        var distinct = values.Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count == 0)
        {
            return CallerAttributeResult.Missing();
        }

        if (definition.Cardinality == CallerAttributeCardinality.Scalar && distinct.Count > 1)
        {
            return CallerAttributeResult.Failed("Ambiguous");
        }

        return CallerAttributeResult.Resolved(new CallerAttributeValue(distinct.Cast<object>().ToList(), SqlParameterType.String));
    }

    private bool IsAllowed(Claim claim) =>
        claim.Subject?.AuthenticationType is { Length: > 0 } scheme && _options.AllowedSchemes.Contains(scheme, StringComparer.OrdinalIgnoreCase);

    private static bool TryParseArray(string text, List<string> into)
    {
        try
        {
            using var document = JsonDocument.Parse(text, StrictJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var parsed = new List<string>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                switch (element.ValueKind)
                {
                    case JsonValueKind.String:
                        parsed.Add(element.GetString() ?? string.Empty);
                        break;
                    case JsonValueKind.Number:
                        parsed.Add(element.GetRawText());
                        break;
                    case JsonValueKind.True:
                        parsed.Add("true");
                        break;
                    case JsonValueKind.False:
                        parsed.Add("false");
                        break;
                    default:
                        return false;   // object, nested array, null
                }
            }

            into.AddRange(parsed);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>Save-time rules of a claims attribute (SEC-129-04 a): a claim, a trusted issuer, the acknowledgement that the claim is IdP-controlled, and a sane token age.</summary>
public sealed class ClaimsAttributeDefinitionValidator : ICallerAttributeDefinitionValidator
{
    private readonly CallerAttributeClaimsOptions _options;

    public ClaimsAttributeDefinitionValidator(IOptions<GatewayOptions> options)
    {
        _options = options?.Value?.CallerAttributes?.Claims ?? new CallerAttributeClaimsOptions();
    }

    public string Kind => CallerAttributeProviderBinding.Claims;

    public void Validate(CallerAttributeDefinition definition)
    {
        var config = definition.Provider.Config;
        if (!config.TryGetValue(ClaimsAttributeProvider.ClaimKey, out var claim) || string.IsNullOrWhiteSpace(claim) || claim.Length > 256)
        {
            throw new CallerAttributeAdminException("InvalidProvider", "A claims attribute needs a claim type.");
        }

        if (!config.TryGetValue(ClaimsAttributeProvider.IssuerKey, out var issuer) ||
            !_options.TrustedIssuers.Any(t => string.Equals(t.Issuer, issuer, StringComparison.Ordinal)))
        {
            throw new CallerAttributeAdminException("InvalidProvider", "The issuer of a claims attribute must be a configured trusted issuer.");
        }

        if (!config.TryGetValue(ClaimsAttributeProvider.IdpControlledAckKey, out var ack) || !string.Equals(ack, "true", StringComparison.Ordinal))
        {
            throw new CallerAttributeAdminException("InvalidProvider", "The definition must acknowledge that the claim is controlled by the identity provider and not editable by the user.");
        }

        if (config.TryGetValue(ClaimsAttributeProvider.MaxTokenAgeKey, out var age) &&
            (!long.TryParse(age, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds <= 0))
        {
            throw new CallerAttributeAdminException("InvalidProvider", "maxTokenAgeSeconds must be a positive number.");
        }
    }
}
