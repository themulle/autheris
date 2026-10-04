namespace Autheris.Application.Events.Services;

using System;
using Autheris.Application.Events.Interfaces;
using Autheris.Domain.Model;

/// <summary>
/// F-EVT-01: Standardized transformation of CDC events to CloudEvents v1.0 specification.
/// </summary>
public sealed class CloudEventTransformer : ICloudEventTransformer
{
    public CloudEventEnvelope Transform(CdcEvent cdcEvent, object? maskedData)
    {
        ArgumentNullException.ThrowIfNull(cdcEvent);

        var operationType = cdcEvent.Operation switch
        {
            CdcOperation.Insert => "insert",
            CdcOperation.Update => "update",
            CdcOperation.Delete => "delete",
            CdcOperation.Snapshot => "snapshot",
            _ => "change"
        };

        var source = $"/autheris/cdc/{cdcEvent.Table.Domain}/{cdcEvent.Table.Schema}/{cdcEvent.Table.TableName}";
        var type = $"autheris.cdc.{operationType}";

        return new CloudEventEnvelope(
            SpecVersion: "1.0",
            Id: cdcEvent.EventId,
            Source: source,
            Type: type,
            Time: cdcEvent.Timestamp,
            DataContentType: "application/json",
            TenantId: cdcEvent.TenantId,
            Data: maskedData,
            Subject: cdcEvent.Table.TableName
        );
    }
}
