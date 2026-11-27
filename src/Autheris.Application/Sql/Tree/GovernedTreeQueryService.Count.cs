namespace Autheris.Application.Sql.Tree;

using System.Collections.Generic;
using System.Globalization;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Quota;
using Autheris.Application.Policy;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Options;

public sealed partial class GovernedTreeQueryService
{
    public Task<long> CountAsync(
        ClaimsPrincipal? principal,
        TreeQueryNode root,
        IReadOnlyDictionary<string, string[]>? requestHeaders,
        string operationId,
        CancellationToken ct = default)
    {
        if (!ScopeQueryExecutionGuard.TryGetCurrent(out var scopeLease) || !scopeLease.CancellationToken.CanBeCanceled)
        {
            return CountScopedAsync(principal, root, operationId, ct);
        }

        return CountLinkedAsync(principal, root, operationId, scopeLease.CancellationToken, ct);
    }

    private async Task<long> CountLinkedAsync(
        ClaimsPrincipal? principal,
        TreeQueryNode root,
        string operationId,
        CancellationToken scopeCt,
        CancellationToken ct)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, scopeCt);
        return await CountScopedAsync(principal, root, operationId, linkedCts.Token).ConfigureAwait(false);
    }

    private async Task<long> CountScopedAsync(
        ClaimsPrincipal? principal,
        TreeQueryNode root,
        string operationId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);

        var tenant = principal.GetTenantId().Value;
        var userSid = principal.GetUserSid()?.Value;
        if (string.IsNullOrWhiteSpace(userSid))
        {
            userSid = "anonymous";
        }

        // The count only reuses the decision the page statement of this operation was authorised with. It never asks the resolver again
        // and writes no audit entry or usage fact of its own.
        if (!_memos.TryGetValue($"{operationId}|{tenant}|{userSid}", out var memo)
            || !memo.AccessByTable.TryGetValue((root.Table, tenant, userSid), out var access)
            || !access.Decision.IsAllowed)
        {
            throw new GatewayForbiddenException("Access denied.");
        }

        var countRoot = root with { Relations = [], OrderBy = [], Offset = 0, Limit = 0, ExecutionLimit = null };

        if (access.Metadata.DataSourceType != DataSourceType.Sql)
        {
            throw new GatewayInvalidQueryException($"The table '{access.Metadata.Identifier}' is not a SQL table and cannot be counted.");
        }

        var sourceName = access.Metadata.Table.SourceName;
        var connections = _options.DataSources?.Connections;
        if (connections == null || !connections.TryGetValue(sourceName, out var connOptions) || !HasUsableConnection(connOptions))
        {
            _logger?.LogWarning("GovernedTreeQueryService: the data source '{SourceName}' has no database connection; the request is refused.", sourceName);
            throw new GatewayForbiddenException("Access denied.");
        }

        if (!DataSourceProvider.TryResolveDialect(connOptions.Provider, out var dialect))
        {
            throw new InvalidOperationException($"The provider '{connOptions.Provider}' of data source '{sourceName}' is not supported.");
        }
        if (access.Metadata.Dialect != dialect)
        {
            throw new InvalidOperationException($"Catalog dialect '{access.Metadata.Dialect}' of '{access.Metadata.Identifier}' does not match the provider '{connOptions.Provider}' of data source '{sourceName}'.");
        }

        var tenantColumn = TableMetadata.RequireTenantColumnOrThrow(
            access.Metadata,
            _options.DataSources?.RequireTenantColumn == true,
            _options.DataSources?.TenantColumnExemptTables);
        var treeAccess = new Dictionary<TableIdentifier, TreeTableAccess>
        {
            [root.Table] = new TreeTableAccess(access.Metadata, access.Decision, tenantColumn, tenantColumn == null ? null : access.Tenant.Value)
        };

        TreeSqlCompiler.Validate(countRoot, treeAccess, dialect, _options.IsColumnMaskingDisabled);

        if (TreeSqlCompiler.RowPolicyRequiresEnginePipeline(access.Decision)
            && access.Decision.RowPolicy is PolicyPredicateRowPolicy
            && _securedSourceBuilder is not null)
        {
            var securedSource = await _securedSourceBuilder.BuildAsync(access, sourceName, dialect, ct).ConfigureAwait(false);
            treeAccess[root.Table] = treeAccess[root.Table] with { SecuredSource = securedSource };
        }

        var compiled = TreeSqlCompiler.CompileCount(countRoot, treeAccess, dialect, _options.IsColumnMaskingDisabled);

        using var lease = AcquireLease(access);
        var text = await RunAsync(compiled, connOptions, dialect, access.Tenant, access.UserSid.Value, purpose: null, ct).ConfigureAwait(false);
        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) || count < 0)
        {
            throw new InvalidOperationException("The count statement returned a non-numeric result.");
        }

        return count;
    }
}
