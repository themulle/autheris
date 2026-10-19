namespace Autheris.Domain.Security;

/// <summary>
/// Scope name normalisation (REQ-GOV-143, SEC-GOV143-05). A <c>scp</c> claim carries the bare scope name while configuration
/// may use the full form <c>api://app/Agent.Read</c>; both sides are compared by their bare name.
/// </summary>
public static class ScopeNames
{
    /// <summary>
    /// The part after the last <c>/</c> of <paramref name="scope"/>; the value itself when it has no <c>/</c> or ends with one.
    /// Nothing is trimmed here (callers decide), and the result may be empty (an empty bare name never matches a restricted scope).
    /// </summary>
    public static string ToBareName(string scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var slash = scope.LastIndexOf('/');
        return slash >= 0 && slash < scope.Length - 1 ? scope[(slash + 1)..] : scope;
    }
}
