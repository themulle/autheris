namespace Autheris.Api.Security;

using Autheris.Application.Interfaces;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Http;

public sealed class HttpCallerContextProvider(IHttpContextAccessor? httpContextAccessor = null) : ICallerContextProvider
{
    public CallerContext? ResolveCallerContext()
    {
        var context = httpContextAccessor?.HttpContext;
        if (context?.Items.TryGetValue(SecurityPrincipalContext.ItemKey, out var secObj) == true && secObj is SecurityPrincipalContext secCtx)
        {
            return secCtx.Caller;
        }
        return null;
    }
}
