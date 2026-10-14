namespace Autheris.Tests.Unit.Events;

using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class EventBusOptionsValidatorTests
{
    private static EventBusOptionsValidator Validator(string env)
    {
        var host = Substitute.For<IHostEnvironment>();
        host.EnvironmentName.Returns(env);
        return new EventBusOptionsValidator(host);
    }

    [Fact]
    public void Default_Options_AreValid_InProduction() =>
        Validator(Environments.Production).Validate(null, new EventBusOptions()).Succeeded.ShouldBeTrue();

    [Fact]
    public void Inline_InProduction_IsRejected() =>
        Validator(Environments.Production)
            .Validate(null, new EventBusOptions { DeliveryMode = EventBusDeliveryMode.Inline }).Failed.ShouldBeTrue();

    [Fact]
    public void Inline_InDevelopment_IsAccepted() =>
        Validator(Environments.Development)
            .Validate(null, new EventBusOptions { DeliveryMode = EventBusDeliveryMode.Inline }).Succeeded.ShouldBeTrue();

    [Fact]
    public void NonPositiveCapacity_IsRejected() =>
        Validator(Environments.Development)
            .Validate(null, new EventBusOptions { SubscriberQueueCapacity = 0 }).Failed.ShouldBeTrue();
}
