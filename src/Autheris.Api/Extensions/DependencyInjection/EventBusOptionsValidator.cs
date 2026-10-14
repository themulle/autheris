namespace Autheris.Api.Extensions.DependencyInjection;

using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

/// <summary>
/// Validates <see cref="EventBusOptions"/>: the queue needs a positive capacity and the inline delivery mode
/// (publisher awaits handlers) is only permitted in Development.
/// </summary>
public sealed class EventBusOptionsValidator : IValidateOptions<EventBusOptions>
{
    private readonly IHostEnvironment _environment;

    public EventBusOptionsValidator(IHostEnvironment environment)
    {
        _environment = environment;
    }

    public ValidateOptionsResult Validate(string? name, EventBusOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.SubscriberQueueCapacity < 1)
        {
            return ValidateOptionsResult.Fail(
                $"{EventBusOptions.SectionName}:SubscriberQueueCapacity must be at least 1.");
        }

        if (options.DeliveryMode == EventBusDeliveryMode.Inline && !_environment.IsDevelopment())
        {
            return ValidateOptionsResult.Fail(
                $"{EventBusOptions.SectionName}:DeliveryMode 'Inline' is only permitted in Development; use 'Queued'.");
        }

        return ValidateOptionsResult.Success;
    }
}
