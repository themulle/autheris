namespace Autheris.Application.Policy;

using Microsoft.Extensions.Options;

/// <summary>
/// Issue #107 (PLAN-GOV-107 D-15, SEC-RF107-16): startup validation of <see cref="RowFilterKeySetCacheOptions"/>
/// (<c>ValidateOnStart</c>). WP-A owns the implementation; contract stub only (not registered in DI yet).
/// </summary>
public sealed class RowFilterKeySetCacheOptionsValidator : IValidateOptions<GatewayOptions>
{
    public ValidateOptionsResult Validate(string? name, GatewayOptions options)
        => throw new NotImplementedException("WP-A: RowFilterKeySetCacheOptionsValidator");
}
