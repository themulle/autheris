namespace Autheris.Application.Policy;

using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Issue #143 (PLAN-GOV-143 D-2, SEC-GOV143-06/11): startup validation of <see cref="RestrictedScopeOptions"/> (<c>ValidateOnStart</c>).
/// A null, blank, whitespace-containing, <c>/</c>-terminated or longer than 256 characters entry fails; more than 64 entries fail;
/// <c>Enabled=false</c> is only accepted in Development; a custom list without <c>Agent.Read</c> only logs a warning.
/// </summary>
public sealed class RestrictedScopeOptionsValidator(IHostEnvironment environment, ILogger<RestrictedScopeOptionsValidator>? logger = null)
    : IValidateOptions<RestrictedScopeOptions>
{
    private const string Path = RestrictedScopeOptions.SectionName;

    public ValidateOptionsResult Validate(string? name, RestrictedScopeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();

        if (!options.Enabled)
        {
            if (!environment.IsDevelopment())
            {
                failures.Add($"{Path}:Enabled=false is only allowed in the Development environment.");
            }
            else
            {
                logger?.LogInformation("Restricted scopes disabled ({Path}:Enabled=false, Development only).", Path);
            }
        }

        var scopes = options.Scopes;
        if (scopes is { Count: > RestrictedScopeOptions.MaxScopes })
        {
            failures.Add($"{Path}:Scopes must not contain more than {RestrictedScopeOptions.MaxScopes} entries.");
        }

        if (scopes is not null)
        {
            for (var i = 0; i < scopes.Count; i++)
            {
                var entry = scopes[i];
                if (string.IsNullOrWhiteSpace(entry)
                    || entry.Any(char.IsWhiteSpace)
                    || entry.EndsWith('/')
                    || entry.Length > RestrictedScopeOptions.MaxScopeLength)
                {
                    failures.Add($"{Path}:Scopes:{i} must be a non-empty scope name without whitespace, without a trailing '/' and at most {RestrictedScopeOptions.MaxScopeLength} characters.");
                }
            }
        }

        if (failures.Count > 0)
        {
            return ValidateOptionsResult.Fail(failures);
        }

        if (options.Enabled)
        {
            var effective = RestrictedScopePolicy.FromOptions(options);
            var names = string.Join(", ", effective.Scopes.Order(StringComparer.OrdinalIgnoreCase));
            if (!effective.Scopes.Contains(RestrictedScopeOptions.DefaultScope))
            {
                logger?.LogWarning(
                    "{Path}:Scopes does not contain '{Default}'; holders of '{Default}' are not restricted. Effective restricted scopes: {Scopes}.",
                    Path, RestrictedScopeOptions.DefaultScope, RestrictedScopeOptions.DefaultScope, names);
            }
            else
            {
                logger?.LogInformation("Restricted scopes: {Scopes}.", names);
            }
        }

        return ValidateOptionsResult.Success;
    }
}
