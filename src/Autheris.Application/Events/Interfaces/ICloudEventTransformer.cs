namespace Autheris.Application.Events.Interfaces;

using Autheris.Domain.Model;

/// <summary>
/// F-EVT-01: Transforms governed CDC change events into CloudEvents v1.0 envelopes.
/// </summary>
public interface ICloudEventTransformer
{
    CloudEventEnvelope Transform(CdcEvent cdcEvent, object? maskedData);
}
