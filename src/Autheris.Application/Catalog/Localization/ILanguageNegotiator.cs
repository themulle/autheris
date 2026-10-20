namespace Autheris.Application.Catalog.Localization;

using System.Collections.Generic;

public sealed record LanguageNegotiationResult(
    string EffectiveLanguage,
    string? RequestedLanguage,
    bool IsFallback,
    IReadOnlyList<string> FallbackChain);

public interface ILanguageNegotiator
{
    LanguageNegotiationResult Negotiate(string? explicitLanguage, string? acceptLanguageHeader = null);

    string NegotiateLanguage(string? requestedLanguage, IEnumerable<string>? supportedLanguages = null, string? defaultLanguage = null);
}
