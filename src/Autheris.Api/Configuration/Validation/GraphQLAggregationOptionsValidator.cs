using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

namespace Autheris.Api.Configuration.Validation;

/// <summary>
/// SEC-112-19: <c>Gateway:GraphQL:Aggregation:MaxFieldsPerOperation</c> must be 1..10. An invalid value is a start-up failure; it is never
/// clamped to a larger value.
/// </summary>
public sealed class GraphQLAggregationOptionsValidator : IValidateOptions<GatewayOptions>
{
    public ValidateOptionsResult Validate(string? name, GatewayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var aggregation = options.GraphQL?.Aggregation;
        if (aggregation is null)
        {
            return ValidateOptionsResult.Fail("Gateway:GraphQL:Aggregation must not be null.");
        }

        return aggregation.MaxFieldsPerOperation is < GraphQLAggregationOptions.MinFieldsPerOperation or > GraphQLAggregationOptions.MaxFieldsPerOperationUpperBound
            ? ValidateOptionsResult.Fail(
                $"Gateway:GraphQL:Aggregation:MaxFieldsPerOperation must be between {GraphQLAggregationOptions.MinFieldsPerOperation} and {GraphQLAggregationOptions.MaxFieldsPerOperationUpperBound}.")
            : ValidateOptionsResult.Success;
    }
}
