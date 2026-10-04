namespace Autheris.Application.Interfaces;

public interface IEventBus
{
    Task PublishAsync<T>(string channel, T message, CancellationToken ct = default);
    IDisposable Subscribe<T>(string channel, Func<T, Task> handler);
}
