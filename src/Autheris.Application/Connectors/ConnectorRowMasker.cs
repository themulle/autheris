using System;
using System.Collections.Generic;
using Autheris.Domain.Common;
using Autheris.Domain.Connectors;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

namespace Autheris.Application.Connectors;

/// <summary>
/// Row masking and column stripping for one connector row; delegates to <see cref="GovernedConnectorReader.ProjectRow"/>
/// (Architecture 2), which callers reading whole tables should use through <see cref="GovernedConnectorReader.ReadAsync"/>.
/// </summary>
public static class ConnectorRowMasker
{
    public static Dictionary<string, object?> MaskRow(
        IReadOnlyDictionary<string, object?> rawRow,
        TableMetadata metadata,
        ConnectorSessionContext session,
        IColumnMaskingProvider maskingProvider,
        string? defaultHmacKeyId = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        bool alreadyMasked = session.Items.TryGetValue("InDbColumnMaskingExecuted", out var m) && m is true;
        return MaskRow(rawRow, metadata, session.AccessDecision, maskingProvider, session.Tenant?.Value, defaultHmacKeyId, alreadyMasked);
    }

    public static Dictionary<string, object?> MaskRow(
        IReadOnlyDictionary<string, object?> rawRow,
        TableMetadata metadata,
        TableAccessDecision decision,
        IColumnMaskingProvider maskingProvider,
        string? tenantId = null,
        string? defaultHmacKeyId = null,
        bool alreadyMasked = false)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(maskingProvider);
        return GovernedConnectorReader.ProjectRow(rawRow, metadata, decision, tenantId, new GovernedRowPolicy(maskingProvider, defaultHmacKeyId), alreadyMasked);
    }
}
