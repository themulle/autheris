using System.Security.Claims;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

namespace Autheris.Application.Interfaces;

public interface IDataSourceExecutor
{
    DataSourceType SupportedType { get; }

    Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteAsync(
        DataSourceExecutionContext context,
        CancellationToken ct = default);
}

public sealed record DataSourceExecutionContext(
    string SourceName,
    TableMetadata Metadata,
    ClaimsPrincipal Principal,
    TableAccessDecision AccessDecision,
    IReadOnlyDictionary<string, object?> Arguments,
    IReadOnlyList<string> RequestedFields,
    IReadOnlyDictionary<string, string[]>? RequestHeaders = null,
    int Limit = 1000,
    int Offset = 0,
    TenantId? Tenant = null,
    IDictionary<string, object?>? Items = null
)
{
    public IDictionary<string, object?> Items { get; init; } = Items ?? new Dictionary<string, object?>();
}
