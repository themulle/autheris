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

        // Architecture 2: raw rows only; GatewayExecutionService applies the governed pipeline (filter, masking, byte cap)
        // using the flags the connector leaves in the shared Items.
        return await GovernedConnectorReader.ReadRawAsync(_connector, session, context.Metadata, maxRows: null, ct).ConfigureAwait(false);
    }
}
