namespace Autheris.Application.Streaming.Interfaces;

using System.Collections.Generic;
using System.Threading;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>Node-local shared tail reader per stream; decrypts each event once (PLAN-CDC-99 D-A13, D-B7).</summary>
public interface ICdcStreamTailHub
{
    IAsyncEnumerable<CdcStreamDelivery> AttachAsync(CdcStreamId id, long? fromOffset, CancellationToken ct);
}

/// <summary>A decoded stream event handed to in-process consumers; exactly one of Change/Control is set.</summary>
public sealed record CdcStreamDelivery(CdcStreamEventRecord Record, CdcChangePayload? Change, CdcControlPayload? Control, TableIdentifier Table);
