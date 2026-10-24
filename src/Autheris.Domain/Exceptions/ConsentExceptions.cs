namespace Autheris.Domain.Exceptions;

using System;

/// <summary>
/// PERS-04: an expiry extension was rejected because the consent does not exist, is revoked, is already expired (server time) or the new
/// expiry is not later than the current one. The message is fixed and carries no id, state or timestamp (SEC-P4R-6);
/// <see cref="ConsentId"/> is for structured logs only.
/// </summary>
public sealed class ConsentNotExtendableException(Guid consentId)
    : InvalidOperationException("The consent cannot be extended. Create a new consent.")
{
    public const string ErrorCode = "CONSENT_NOT_EXTENDABLE";

    public string Code => ErrorCode;

    public Guid ConsentId { get; } = consentId;
}
