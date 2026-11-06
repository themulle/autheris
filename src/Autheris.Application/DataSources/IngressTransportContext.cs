namespace Autheris.Application.DataSources;

using System;
using System.Numerics;
using Autheris.Domain.Options.DataSources;

/// <summary>
/// Issue #447: the ingress transport of the current request or background scope. Set only by server code
/// (endpoint metadata middleware or an explicit non-HTTP entry point), never from client input. The default is
/// <see cref="DataSourceTransportFlags.None"/>, which the transport gate denies.
/// </summary>
public interface IIngressTransportContext
{
    /// <summary>The stamped transport, or <see cref="DataSourceTransportFlags.None"/> when nothing was stamped.</summary>
    DataSourceTransportFlags Current { get; }

    /// <summary>
    /// Stamps the transport once. The same value again is a no-op; <see cref="DataSourceTransportFlags.None"/>, a multi-bit or undefined
    /// value, or a different second value throws <see cref="InvalidOperationException"/> (no fallback).
    /// </summary>
    void Set(DataSourceTransportFlags transport);
}

/// <summary>Scoped, set-once implementation of <see cref="IIngressTransportContext"/>.</summary>
public sealed class IngressTransportContext : IIngressTransportContext
{
    /// <summary>Key under which a caller stores its ingress in <c>ConnectorSessionContext.Items</c>so a connector that opens a new scope can copy it.</summary>
    public const string SessionItemKey = "__IngressTransport";

    private readonly object _gate = new();
    private DataSourceTransportFlags _current = DataSourceTransportFlags.None;

    /// <inheritdoc />
    public DataSourceTransportFlags Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <inheritdoc />
    public void Set(DataSourceTransportFlags transport)
    {
        if (!IsSingleKnownFlag(transport))
        {
            throw new InvalidOperationException("The ingress transport must be exactly one defined transport.");
        }

        lock (_gate)
        {
            if (_current == DataSourceTransportFlags.None)
            {
                _current = transport;
                return;
            }

            if (_current != transport)
            {
                throw new InvalidOperationException("The ingress transport of this scope is already set to a different value.");
            }
        }
    }

    /// <summary>True when <paramref name="transport"/> is exactly one bit and that bit is a defined transport.</summary>
    public static bool IsSingleKnownFlag(DataSourceTransportFlags transport)
    {
        int value = (int)transport;
        return value != 0
            && (transport & ~DataSourceTransportFlags.All) == 0
            && BitOperations.PopCount((uint)value) == 1;
    }
}
