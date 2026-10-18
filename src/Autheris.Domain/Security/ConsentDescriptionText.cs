namespace Autheris.Domain.Security;

/// <summary>
/// PLAN-GOV-158-INC1 decision 5 (SEC-GOV158I1-11): the single output boundary for stored consent descriptions. The stored value is
/// re-validated with the current cap on every read; a violation (direct store write, lowered cap) yields <c>null</c> (fail closed).
/// The value is never logged.
/// </summary>
public static class ConsentDescriptionText
{
    public static string? ForOutput(string? stored, int maxLength)
    {
        if (stored is null)
        {
            return null;
        }

        var check = UntrustedText.Normalize(stored, maxLength, allowLineFeed: false);
        return check.IsValid ? check.Value : null;
    }

    /// <summary>
    /// SEC-GOV158I1-06: what an audit record may carry about a description instead of the text: its length and the first 8 hex characters of its
    /// SHA-256 (<c>null</c> hash for no description). Never the text, never the full hash.
    /// </summary>
    public static (int Length, string? HashPrefix) Fingerprint(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return (0, null);
        }

        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text));
        return (text.Length, Convert.ToHexString(hash, 0, 4).ToLowerInvariant());
    }
}
