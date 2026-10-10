namespace Autheris.Application.Security.Totp.Interfaces;

using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Persistent or cluster-wide storage for user TOTP Base32 shared secrets.
/// </summary>
public interface ITotpSecretStore
{
    Task SetSecretAsync(string userSid, string secretBase32, CancellationToken ct = default);

    Task<string?> GetSecretAsync(string userSid, CancellationToken ct = default);

    Task<bool> HasSecretAsync(string userSid, CancellationToken ct = default);

    Task RemoveSecretAsync(string userSid, CancellationToken ct = default);
}
