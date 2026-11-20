using System.Security.Claims;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

namespace Autheris.Tests.Common.Fakes;

/// <summary>
/// Configurable <see cref="IGatewayExecutionService"/> test double for unit tests that need fixed pages,
/// access checks, call inspection, or exception paths without constructing a mock per test.
/// </summary>
public sealed class StubGatewayExecutionService : IGatewayExecutionService
{
    public sealed record PageCall(ClaimsPrincipal? Principal, TableIdentifier Table, TablePageRequest Request);
    public sealed record MetadataAccessCheckCall(ClaimsPrincipal? Principal, TableMetadata Metadata, bool RecordAudit);

    private readonly List<PageCall> _pageCalls = [];
    private readonly List<MetadataAccessCheckCall> _metadataAccessCalls = [];

    public Func<ClaimsPrincipal?, TableIdentifier, TablePageRequest, CancellationToken, Task<TableQueryPage>>? OnExecutePage { get; set; }
    public Func<ClaimsPrincipal?, TableIdentifier, int?, int?, IReadOnlyDictionary<string, object?>?, IReadOnlyList<string>?, IReadOnlyDictionary<string, string[]>?, CancellationToken, Task<(IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, TableAccessDecision Decision)>>? OnExecuteQuery { get; set; }
    public Func<ClaimsPrincipal?, TableIdentifier, CancellationToken, Task<TableAccessDecision>>? OnCheckAccess { get; set; }
    public Func<ClaimsPrincipal?, TableMetadata, bool, CancellationToken, Task<TableAccessDecision>>? OnCheckMetadataAccess { get; set; }

    public IReadOnlyList<PageCall> PageCalls => _pageCalls;
    public IReadOnlyList<MetadataAccessCheckCall> MetadataAccessCalls => _metadataAccessCalls;
    public TablePageRequest? LastPageRequest => _pageCalls.Count > 0 ? _pageCalls[^1].Request : null;
    public TableIdentifier? LastPageTable => _pageCalls.Count > 0 ? _pageCalls[^1].Table : null;
    public int PageCallCount => _pageCalls.Count;

    public static StubGatewayExecutionService WithPage(TableQueryPage page)
        => new() { OnExecutePage = (_, _, _, _) => Task.FromResult(page) };

    public static StubGatewayExecutionService WithAccessDecision(Func<TableMetadata, TableAccessDecision> decide)
        => new() { OnCheckMetadataAccess = (_, metadata, _, _) => Task.FromResult(decide(metadata)) };

    public static StubGatewayExecutionService Throwing(Exception exception)
        => new()
        {
            OnExecutePage = (_, _, _, _) => Task.FromException<TableQueryPage>(exception),
            OnExecuteQuery = (_, _, _, _, _, _, _, _) => Task.FromException<(IReadOnlyList<IReadOnlyDictionary<string, object?>>, TableAccessDecision)>(exception),
            OnCheckAccess = (_, _, _) => Task.FromException<TableAccessDecision>(exception),
            OnCheckMetadataAccess = (_, _, _, _) => Task.FromException<TableAccessDecision>(exception)
        };

    public Task<(IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, TableAccessDecision Decision)> ExecuteTableQueryAsync(
        ClaimsPrincipal? principal,
        TableIdentifier table,
        int? first = null,
        int? after = null,
        CancellationToken ct = default)
        => ExecuteTableQueryAsync(principal, table, first, after, null, null, null, ct);

    public Task<(IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, TableAccessDecision Decision)> ExecuteTableQueryAsync(
        ClaimsPrincipal? principal,
        TableIdentifier table,
        int? first,
        int? after,
        IReadOnlyDictionary<string, object?>? queryArguments,
        IReadOnlyList<string>? requestedFields = null,
        IReadOnlyDictionary<string, string[]>? requestHeaders = null,
        CancellationToken ct = default)
    {
        if (OnExecuteQuery is not null)
        {
            return OnExecuteQuery(principal, table, first, after, queryArguments, requestedFields, requestHeaders, ct);
        }

        return Task.FromResult<(IReadOnlyList<IReadOnlyDictionary<string, object?>>, TableAccessDecision)>(
            (Array.Empty<IReadOnlyDictionary<string, object?>>(), TableAccessDecision.Denied(table, "Not configured")));
    }

    public Task<TableQueryPage> ExecuteTablePageAsync(
        ClaimsPrincipal? principal,
        TableIdentifier table,
        TablePageRequest request,
        CancellationToken ct = default)
    {
        _pageCalls.Add(new PageCall(principal, table, request));
        if (OnExecutePage is not null)
        {
            return OnExecutePage(principal, table, request, ct);
        }

        return Task.FromResult(new TableQueryPage([], TableAccessDecision.Denied(table, "Not configured"), TotalCount: null));
    }

    public Task<TableAccessDecision> CheckTableAccessAsync(
        ClaimsPrincipal? principal,
        TableIdentifier table,
        CancellationToken ct = default)
    {
        if (OnCheckAccess is not null)
        {
            return OnCheckAccess(principal, table, ct);
        }

        return Task.FromResult(TableAccessDecision.Denied(table, "Not configured"));
    }

    public Task<TableAccessDecision> CheckTableAccessAsync(
        ClaimsPrincipal? principal,
        TableMetadata metadata,
        bool recordAudit = true,
        CancellationToken ct = default)
    {
        _metadataAccessCalls.Add(new MetadataAccessCheckCall(principal, metadata, recordAudit));
        if (OnCheckMetadataAccess is not null)
        {
            return OnCheckMetadataAccess(principal, metadata, recordAudit, ct);
        }

        if (OnCheckAccess is not null)
        {
            return OnCheckAccess(principal, metadata.Identifier, ct);
        }

        return Task.FromResult<TableAccessDecision>(null!);
    }
}
