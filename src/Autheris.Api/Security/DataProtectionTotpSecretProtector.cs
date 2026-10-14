namespace Autheris.Api.Security;

using Autheris.Application.Security.Totp.Interfaces;
using Microsoft.AspNetCore.DataProtection;

/// <summary>
/// SR-L-05: ASP.NET Data Protection based <see cref="ITotpSecretProtector"/>.
/// </summary>
public sealed class DataProtectionTotpSecretProtector : ITotpSecretProtector
{
    private readonly IDataProtector _protector;

    public DataProtectionTotpSecretProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector("Autheris.Totp.Secret.v1");
    }

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public string Unprotect(string protectedValue) => _protector.Unprotect(protectedValue);
}
