namespace Autheris.Application.Catalog.Localization;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

/// <summary>
/// RFC 4647 language negotiation service. Resolves caller's requested language via explicit parameter,
/// HTTP Accept-Language header, or configured gateway default language.
/// Uses zero-allocation span parsing to defend against ReDoS and oversized header injections (SEC-CAT146-04, T-CAT146-05).
/// </summary>
public sealed class LanguageNegotiator : ILanguageNegotiator
{
    private const int MaxHeaderLength = 512;
    private const int MaxLanguageRanges = 8;

    private readonly string _defaultLanguage;
    private readonly HashSet<string> _supportedLanguages;
    private readonly Dictionary<string, string> _canonicalToConfiguredMap;

    public LanguageNegotiator() : this(Microsoft.Extensions.Options.Options.Create(new CatalogLocalizationOptions())) { }

    public LanguageNegotiator(IOptions<CatalogLocalizationOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var opts = options.Value ?? new CatalogLocalizationOptions();

        if (Bcp47LanguageTag.IsValid(opts.DefaultLanguage, out var canonicalDefault))
        {
            _defaultLanguage = canonicalDefault;
        }
        else
        {
            _defaultLanguage = "en";
        }

        _supportedLanguages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _canonicalToConfiguredMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (opts.SupportedLanguages != null)
        {
            foreach (var lang in opts.SupportedLanguages)
            {
                if (Bcp47LanguageTag.IsValid(lang, out var canonical))
                {
                    _supportedLanguages.Add(canonical);
                    _canonicalToConfiguredMap[canonical] = lang;
                }
            }
        }

        if (_supportedLanguages.Add(_defaultLanguage))
        {
            _canonicalToConfiguredMap[_defaultLanguage] = _defaultLanguage;
        }
    }

    public LanguageNegotiationResult Negotiate(string? explicitLanguage, string? acceptLanguageHeader = null)
    {
        // 1. Explicit parameter
        if (!string.IsNullOrWhiteSpace(explicitLanguage))
        {
            var trimmed = explicitLanguage.Trim();
            if (trimmed.Length <= Bcp47LanguageTag.MaxTagLength && Bcp47LanguageTag.IsValid(trimmed, out var canonicalTag))
            {
                // RFC 4647 subtag truncation lookup
                foreach (var candidate in Bcp47LanguageTag.GetFallbackChain(canonicalTag))
                {
                    if (_supportedLanguages.Contains(candidate))
                    {
                        var effective = candidate;
                        var fallbackChain = string.Equals(effective, _defaultLanguage, StringComparison.OrdinalIgnoreCase)
                            ? new[] { effective }
                            : new[] { effective, _defaultLanguage };

                        return new LanguageNegotiationResult(
                            EffectiveLanguage: effective,
                            RequestedLanguage: trimmed,
                            IsFallback: false,
                            FallbackChain: fallbackChain);
                    }
                }
            }

            // Explicit language was requested but unsupported or malformed -> fallback to default
            return new LanguageNegotiationResult(
                EffectiveLanguage: _defaultLanguage,
                RequestedLanguage: trimmed,
                IsFallback: true,
                FallbackChain: [_defaultLanguage]);
        }

        // 2. HTTP Accept-Language header
        if (!string.IsNullOrWhiteSpace(acceptLanguageHeader))
        {
            var headerSpan = acceptLanguageHeader.AsSpan().Trim();
            if (headerSpan.Length > MaxHeaderLength)
            {
                headerSpan = headerSpan[..MaxHeaderLength];
            }

            var ranges = ParseAcceptLanguageHeader(headerSpan);
            foreach (var range in ranges)
            {
                if (Bcp47LanguageTag.IsValid(range.Tag, out var canonicalTag))
                {
                    foreach (var candidate in Bcp47LanguageTag.GetFallbackChain(canonicalTag))
                    {
                        if (_supportedLanguages.Contains(candidate))
                        {
                            var effective = candidate;
                            var fallbackChain = string.Equals(effective, _defaultLanguage, StringComparison.OrdinalIgnoreCase)
                                ? new[] { effective }
                                : new[] { effective, _defaultLanguage };

                            return new LanguageNegotiationResult(
                                EffectiveLanguage: effective,
                                RequestedLanguage: range.Tag,
                                IsFallback: false,
                                FallbackChain: fallbackChain);
                        }
                    }
                }
            }
        }

        // 3. Default language fallback
        return new LanguageNegotiationResult(
            EffectiveLanguage: _defaultLanguage,
            RequestedLanguage: null,
            IsFallback: false,
            FallbackChain: [_defaultLanguage]);
    }

    public string NegotiateLanguage(string? requestedLanguage, IEnumerable<string>? supportedLanguages = null, string? defaultLanguage = null)
    {
        if (supportedLanguages != null || defaultLanguage != null)
        {
            var def = defaultLanguage ?? _defaultLanguage;
            var supp = supportedLanguages != null ? new HashSet<string>(supportedLanguages, StringComparer.OrdinalIgnoreCase) : _supportedLanguages;
            if (string.IsNullOrWhiteSpace(requestedLanguage)) return def;

            if (requestedLanguage == "*") return def;

            var inputSpan = requestedLanguage.AsSpan().Trim();
            if (inputSpan.Length > MaxHeaderLength)
            {
                inputSpan = inputSpan[..MaxHeaderLength];
            }

            if (inputSpan.IndexOf(',') >= 0 || inputSpan.IndexOf(';') >= 0)
            {
                var ranges = ParseAcceptLanguageHeader(inputSpan);
                foreach (var r in ranges)
                {
                    if (Bcp47LanguageTag.IsValid(r.Tag, out var can))
                    {
                        foreach (var cand in Bcp47LanguageTag.GetFallbackChain(can))
                        {
                            if (supp.Contains(cand)) return cand;
                        }
                    }
                }
                return def;
            }

            var trimmed = inputSpan.ToString();
            if (Bcp47LanguageTag.IsValid(trimmed, out var canonical))
            {
                foreach (var cand in Bcp47LanguageTag.GetFallbackChain(canonical))
                {
                    if (supp.Contains(cand)) return cand;
                }
            }

            return def;
        }

        var isHeader = requestedLanguage != null && (requestedLanguage.Contains(',') || requestedLanguage.Contains(';'));
        var res = Negotiate(isHeader ? null : requestedLanguage, isHeader ? requestedLanguage : null);
        return res.EffectiveLanguage;
    }

    private static List<(string Tag, double Quality)> ParseAcceptLanguageHeader(ReadOnlySpan<char> header)
    {
        var result = new List<(string Tag, double Quality)>(MaxLanguageRanges);
        int start = 0;
        int count = 0;

        for (int i = 0; i <= header.Length; i++)
        {
            if (i == header.Length || header[i] == ',')
            {
                if (i > start)
                {
                    var item = header.Slice(start, i - start).Trim();
                    if (!item.IsEmpty)
                    {
                        int semiIdx = item.IndexOf(';');
                        string tag;
                        double q = 1.0;

                        if (semiIdx >= 0)
                        {
                            tag = item[..semiIdx].Trim().ToString();
                            var qPart = item[(semiIdx + 1)..].Trim();
                            if (qPart.StartsWith("q=", StringComparison.OrdinalIgnoreCase))
                            {
                                var qValSpan = qPart[2..].Trim();
                                if (double.TryParse(qValSpan, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsedQ))
                                {
                                    q = Math.Clamp(parsedQ, 0.0, 1.0);
                                }
                            }
                        }
                        else
                        {
                            tag = item.ToString();
                        }

                        if (tag.Length > 0 && tag.Length <= Bcp47LanguageTag.MaxTagLength && q > 0.0)
                        {
                            result.Add((tag, q));
                            count++;
                            if (count >= MaxLanguageRanges) break;
                        }
                    }
                }
                start = i + 1;
            }
        }

        return result.OrderByDescending(r => r.Quality).ToList();
    }
}
