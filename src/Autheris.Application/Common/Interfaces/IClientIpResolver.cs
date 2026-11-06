namespace Autheris.Application.Common.Interfaces;

using System.Net;

public interface IClientIpResolver
{
    IPAddress ResolveClientIp();
}
