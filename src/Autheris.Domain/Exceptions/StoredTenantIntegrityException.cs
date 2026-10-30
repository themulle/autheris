namespace Autheris.Domain.Exceptions;

/// <summary>
/// A stored tenant value is NULL, empty or not a valid tenant id (DT-A-9, DT-SC-16). The operation fails closed. The message is fixed and never
/// contains the stored value; there is no inner exception, so the value cannot leak through <see cref="Exception.ToString"/> or logs.
/// </summary>
public sealed class StoredTenantIntegrityException : GatewaySecurityException
{
    public const string Code = "STORED_TENANT_INVALID";

    private const string FixedMessage = "A stored tenant value is invalid; the operation was refused.";

    public StoredTenantIntegrityException()
        : base(FixedMessage, Code)
    {
    }
}
