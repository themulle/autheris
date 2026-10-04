namespace Autheris.Application.Interfaces;

using System.Net;

public interface IClientIpResolver
{
    IPAddress ResolveClientIp();
}
