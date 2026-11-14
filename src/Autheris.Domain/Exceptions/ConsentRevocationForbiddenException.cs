namespace Autheris.Domain.Exceptions;

/// <summary>
/// Typed 403 authorization refusal for unauthorized consent revocation attempts.
/// Carries a fixed message with zero actor SID or table identifier disclosure.
/// </summary>
public sealed class ConsentRevocationForbiddenException : GatewaySecurityException
{
    public const string FixedMessage = "You do not have permission to revoke this consent.";

    public ConsentRevocationForbiddenException()
        : base(FixedMessage, "ACCESS_DENIED")
    {
    }
}
