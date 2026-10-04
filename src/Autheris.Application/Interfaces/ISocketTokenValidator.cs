namespace Autheris.Application.Interfaces;

using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

public interface ISocketTokenValidator
{
    Task<(bool IsValid, ClaimsPrincipal? Principal)> ValidateTokenAsync(string token, CancellationToken cancellationToken = default);
}
