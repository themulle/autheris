namespace Autheris.Application.Streaming.Interfaces;

using System;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// F-CDC-03: Zero-Kafka PostgreSQL CDC via Logical Streaming Replication.
/// Direct pgoutput streaming from PostgreSQL WAL without external brokers.
/// </summary>
public interface IPostgreSqlCdcService
{
    bool IsRunning { get; }
    long CurrentWalLagBytes { get; }
    bool IsWalLagExceeded { get; }

    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
}
