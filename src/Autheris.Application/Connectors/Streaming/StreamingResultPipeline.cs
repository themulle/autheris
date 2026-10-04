using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Connectors;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Connectors;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

namespace Autheris.Application.Connectors.Streaming;

public interface IStreamingResultPipeline
{
    IAsyncEnumerable<IReadOnlyDictionary<string, object?>> StreamRowsAsync(
        TableMetadata metadata,
        ConnectorSessionContext session,
        IAutherisConnector connector,
        CancellationToken ct = default);
}

public sealed class StreamingResultPipeline : IStreamingResultPipeline
{
    private readonly IColumnMaskingProvider _maskingProvider;

    public StreamingResultPipeline(IColumnMaskingProvider maskingProvider)
    {
        _maskingProvider = maskingProvider ?? throw new ArgumentNullException(nameof(maskingProvider));
    }

    public async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> StreamRowsAsync(
        TableMetadata metadata,
        ConnectorSessionContext session,
        IAutherisConnector connector,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(connector);

        // Fail-closed security validation
        ConnectorSecurityPolicyEvaluator.EnforceSecurityPolicy(session, metadata);

        session.Items["TableMetadata"] = metadata;

        var splits = await connector.SplitManager.GetSplitsAsync(metadata, session, ct).ConfigureAwait(false);
        if (splits.Count == 0)
        {
            yield break;
        }

        foreach (var split in splits)
        {
            await foreach (var rawRow in connector.RecordSource.ReadSplitAsync(split, session, ct).ConfigureAwait(false))
            {
                // In-stream Zero-Trust Step: Column Masking & Deny Stripping (Zero-LOH individual record projection)
                var maskedRow = ConnectorRowMasker.MaskRow(rawRow, metadata, session.AccessDecision, _maskingProvider);
                yield return maskedRow;
            }
        }
    }
}
