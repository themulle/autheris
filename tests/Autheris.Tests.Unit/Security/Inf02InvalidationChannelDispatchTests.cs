namespace Autheris.Tests.Unit.Security;

using System.Threading.Tasks;
using Autheris.Infrastructure.Messaging;
using Shouldly;
using Xunit;

/// <summary>
/// INF-02: the default consent invalidation channel must use the synchronous, never-dropped dispatch path.
/// </summary>
public sealed class Inf02InvalidationChannelDispatchTests
{
    [Theory]
    [InlineData("consent:invalidations")]
    [InlineData("rebac:invalidate")]
    [InlineData("epoch:bump")]
    public async Task INF_02_InvalidationChannels_AreDispatchedSynchronously(string channel)
    {
        await using var bus = new InProcessChannelEventBus();
        var received = 0;
        using var _ = bus.Subscribe<string>(channel, _ =>
        {
            received++;
            return Task.CompletedTask;
        });

        await bus.PublishAsync(channel, "x");

        // Synchronous dispatch: the handler has run by the time PublishAsync returns.
        received.ShouldBe(1);
    }
}
