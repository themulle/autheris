namespace Autheris.Application.Plugins.Auth;

using System.Collections.Generic;
using Autheris.Domain.Options;
using Autheris.Domain.Options.Plugins;
using Microsoft.Extensions.Options;

public sealed class AuthPluginOptionsValidator : IValidateOptions<GatewayOptions>, IValidateOptions<AuthPluginOptions>
{
    public ValidateOptionsResult Validate(string? name, GatewayOptions options)
    {
        if (options?.Plugins?.Auth == null)
        {
            return ValidateOptionsResult.Fail("Plugins.Auth options section is required.");
        }

        return ValidateInternal(options.Plugins.Auth);
    }

    public ValidateOptionsResult Validate(string? name, AuthPluginOptions options)
    {
        if (options == null)
        {
            return ValidateOptionsResult.Fail("Auth plugin options are required.");
        }

        return ValidateInternal(options);
    }

    private static ValidateOptionsResult ValidateInternal(AuthPluginOptions options)
    {
        var failures = new List<string>();

        if (options.ExecutionTimeoutMilliseconds < 50 || options.ExecutionTimeoutMilliseconds > 5000)
        {
            failures.Add($"ExecutionTimeoutMilliseconds must be between 50 and 5000 (was {options.ExecutionTimeoutMilliseconds}).");
        }

        if (options.MaxEnrichedRolesPerCaller < 1 || options.MaxEnrichedRolesPerCaller > 256)
        {
            failures.Add($"MaxEnrichedRolesPerCaller must be between 1 and 256 (was {options.MaxEnrichedRolesPerCaller}).");
        }

        if (options.MaxEnrichedGroupSidsPerCaller < 1 || options.MaxEnrichedGroupSidsPerCaller > 1024)
        {
            failures.Add($"MaxEnrichedGroupSidsPerCaller must be between 1 and 1024 (was {options.MaxEnrichedGroupSidsPerCaller}).");
        }

        if (options.ProhibitedRoles == null || options.ProhibitedRoles.Count == 0)
        {
            failures.Add("ProhibitedRoles must not be empty.");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
