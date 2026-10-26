using System.Collections.Generic;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using TrinoSqlEngine.Parsing;

namespace Autheris.Api.Configuration.Validation;

/// <summary>
/// ARCH-MNT-15-SC-04: the ANTLR cache bounds must each be greater than zero and below their upper bound; a violation fails start-up. Messages
/// name option paths only, never values.
/// </summary>
public sealed class SqlParserCacheOptionsValidator : IValidateOptions<GatewayOptions>
{
    public ValidateOptionsResult Validate(string? name, GatewayOptions options)
    {
        System.ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        foreach (var failure in ToEngineOptions(options.SqlParserCache).Validate())
        {
            failures.Add($"Gateway:SqlParserCache:{failure}");
        }

        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }

    internal static AntlrCacheOptions ToEngineOptions(SqlParserCacheOptions options) => new()
    {
        MaxStatesPerGeneration = options.MaxStatesPerGeneration,
        GenerationCheckInterval = options.GenerationCheckInterval,
        MinParsesPerGeneration = options.MinParsesPerGeneration,
        HardCapFactor = options.HardCapFactor
    };
}
