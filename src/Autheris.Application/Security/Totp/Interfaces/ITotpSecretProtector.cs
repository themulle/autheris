namespace Autheris.Application.Security.Totp.Interfaces;

/// <summary>
/// SR-L-05: encrypts TOTP seeds before they are persisted in the (cluster) state store.
/// </summary>
public interface ITotpSecretProtector
{
    string Protect(string plaintext);

    string Unprotect(string protectedValue);
}
