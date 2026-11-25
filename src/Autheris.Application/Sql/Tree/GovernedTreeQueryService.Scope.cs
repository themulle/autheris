namespace Autheris.Application.Sql.Tree;

using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Quota;
using Autheris.Domain.Common;
using Autheris.Domain.Options;

public sealed partial class GovernedTreeQueryService
{
    public Task<JsonDocument> ExecuteAsync(
        ClaimsPrincipal? principal,
        TreeQueryNode root,
        IReadOnlyDictionary<string, string[]>? requestHeaders,
        string operationId,
        CancellationToken ct = default) => ExecuteAsync(principal, root, requestHeaders, operationId, observer: null, ct);

    public Task<JsonDocument> ExecuteAsync(
        ClaimsPrincipal? principal,
        TreeQueryNode root,
        IReadOnlyDictionary<string, string[]>? requestHeaders,
        string operationId,
        ITreeExecutionObserver? observer,
        CancellationToken ct = default)
    {
        if (!ScopeQueryExecutionGuard.TryGetCurrent(out var scopeLease) || !scopeLease.CancellationToken.CanBeCanceled)
        {
            return ExecuteScopedAsync(principal, root, requestHeaders, operationId, observer, ct);
        }

        return ExecuteLinkedAsync(principal, root, requestHeaders, operationId, observer, scopeLease.CancellationToken, ct);
    }

    private async Task<JsonDocument> ExecuteLinkedAsync(
        ClaimsPrincipal? principal,
        TreeQueryNode root,
        IReadOnlyDictionary<string, string[]>? requestHeaders,
        string operationId,
        ITreeExecutionObserver? observer,
        CancellationToken scopeCt,
        CancellationToken ct)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, scopeCt);
        return await ExecuteScopedAsync(principal, root, requestHeaders, operationId, observer, linkedCts.Token).ConfigureAwait(false);
    }

    private static int ClampScopeCommandTimeout(int configuredTimeoutSeconds) =>
        ScopeQueryExecutionGuard.TryGetCurrent(out var scopeLease)
            ? scopeLease.ClampTimeoutSeconds(configuredTimeoutSeconds)
            : configuredTimeoutSeconds;

    private static bool HasUsableConnection(DataSourceConnectionOptions? connOptions) =>
        connOptions != null &&
        (!string.IsNullOrWhiteSpace(connOptions.ConnectionString) ||
         (DataSourceProvider.Is(connOptions.Provider, DatabaseDialect.Databricks) && connOptions.Databricks is not null));

    internal static void ConfigureCommandParameter(DbParameter parameter, string name, object? value, DatabaseDialect dialect)
    {
        if (dialect != DatabaseDialect.Databricks)
        {
            parameter.ParameterName = name;
            parameter.Value = ToProviderValue(value, dialect) ?? DBNull.Value;
            return;
        }

        parameter.ParameterName = name.TrimStart(':', '@');
        var (dbType, mapped) = value switch
        {
            null or DBNull => (DbType.String, (object)DBNull.Value),
            bool b => (DbType.Boolean, b),
            int or short or byte or sbyte or ushort => (DbType.Int32, Convert.ToInt32(value, CultureInfo.InvariantCulture)),
            long or uint => (DbType.Int64, Convert.ToInt64(value, CultureInfo.InvariantCulture)),
            decimal m => (DbType.Decimal, m),
            double or float => (DbType.Double, Convert.ToDouble(value, CultureInfo.InvariantCulture)),
            DateOnly d => (DbType.Date, d.ToDateTime(TimeOnly.MinValue)),
            DateTime dt => (DbType.DateTime, dt),
            DateTimeOffset dto => (DbType.DateTimeOffset, dto),
            Guid g => (DbType.String, g.ToString("D")),
            string s => (DbType.String, s),
            _ => (DbType.String, Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty)
        };
        parameter.DbType = dbType;
        parameter.Value = mapped;
    }
}
