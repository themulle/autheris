namespace Autheris.Application.Common.Interfaces;

using Autheris.Domain.Security;

public interface ICallerContextProvider
{
    CallerContext? ResolveCallerContext();
}
