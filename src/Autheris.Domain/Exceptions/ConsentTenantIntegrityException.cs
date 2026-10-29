namespace Autheris.Domain.Exceptions;

/// <summary>
/// A consent row carries a tenant value that is NULL, empty or not a valid tenant id (DEF-02, DEF-SC-01). The operation is refused
/// (fail closed, mapped to 403 by the governed denial mapper). The message is fixed and never contains the stored value; there is no
/// inner exception, so the value cannot leak through <see cref="Exception.ToString"/>, <see cref="Exception.Data"/> or logs.
/// </summary>
public sealed class ConsentTenantIntegrityException : GatewaySecurityException
{
    public const string Code = "CONSENT_TENANT_INVALID";

    private const string FixedMessage = "The consent tenant value is invalid; the operation was refused.";

    public ConsentTenantIntegrityException(Guid consentId, string? targetTable)
        : base(FixedMessage, Code)
    {
        ConsentId = consentId;
        TargetTable = targetTable;
    }

    /// <summary>The consent the refusal is about (an id, never the tenant value); used for the refusal audit row.</summary>
    public Guid ConsentId { get; }

    /// <summary>The table of the consent, if known.</summary>
    public string? TargetTable { get; }
}
