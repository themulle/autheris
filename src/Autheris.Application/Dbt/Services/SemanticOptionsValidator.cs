namespace Autheris.Application.Dbt.Services;

using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

/// <summary>Fail-fast ranges of <see cref="SemanticOptions"/> (start-up failure, no clamping).</summary>
public sealed class SemanticOptionsValidator : IValidateOptions<SemanticOptions>
{
    public ValidateOptionsResult Validate(string? name, SemanticOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        if (options.ManifestCacheSeconds is < 0 or > SemanticOptions.MaxManifestCacheSeconds)
        {
            failures.Add($"Semantic:ManifestCacheSeconds must be between 0 and {SemanticOptions.MaxManifestCacheSeconds}.");
        }

        if (options.MaxFilterTemplateLength is < SemanticOptions.MinFilterTemplateLength or > SemanticOptions.MaxFilterTemplateLengthUpperBound)
        {
            failures.Add(
                $"Semantic:MaxFilterTemplateLength must be between {SemanticOptions.MinFilterTemplateLength} and {SemanticOptions.MaxFilterTemplateLengthUpperBound}.");
        }

        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }
}
