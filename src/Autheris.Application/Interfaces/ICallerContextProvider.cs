namespace Autheris.Application.Interfaces;

using Autheris.Domain.Security;

public interface ICallerContextProvider
{
    CallerContext? ResolveCallerContext();
}
