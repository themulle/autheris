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
}
