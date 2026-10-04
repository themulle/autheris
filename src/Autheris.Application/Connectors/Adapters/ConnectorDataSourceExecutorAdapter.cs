using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Connectors;
using Autheris.Domain.Model;

namespace Autheris.Application.Connectors.Adapters;

/// <summary>
/// Allows any IAutherisConnector to be used as an IDataSourceExecutor.
/// </summary>
public sealed class ConnectorDataSourceExecutorAdapter : IDataSourceExecutor
{
    private readonly IAutherisConnector _connector;

    public DataSourceType SupportedType { get; }

    public ConnectorDataSourceExecutorAdapter(IAutherisConnector connector, DataSourceType supportedType = DataSourceType.Sql)
    {
        _connector = connector ?? throw new ArgumentNullException(nameof(connector));
        SupportedType = supportedType;
    }

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteAsync(
        DataSourceExecutionContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var session = new ConnectorSessionContext(
            Principal: context.Principal,
            Tenant: context.Tenant,
            AccessDecision: context.AccessDecision,
            ProjectedColumns: context.RequestedFields,
            Arguments: context.Arguments,
            PushdownFilterSql: context.AccessDecision.CombinedRowFilterSql,
            Limit: context.Limit,
            Offset: context.Offset,
            RequestHeaders: context.RequestHeaders,
            Items: context.Items);

        session.Items["TableMetadata"] = context.Metadata;

        var splits = await _connector.SplitManager.GetSplitsAsync(context.Metadata, session, ct);
        if (splits.Count == 0)
        {
            return Array.Empty<IReadOnlyDictionary<string, object?>>();
        }

        var results = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var split in splits)
        {
            var batch = await _connector.RecordSource.ReadBatchAsync(split, session, ct);
            results.AddRange(batch);
        }

        return results;
    }
}
