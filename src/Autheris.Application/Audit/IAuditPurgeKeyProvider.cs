namespace Autheris.Application.Audit;

/// <summary>
/// Provides access to the cryptographic HMAC key used for audit chain and purge checkpoint signing.
/// </summary>
public interface IAuditPurgeKeyProvider
{
    byte[]? GetAuditHmacKey();
}
