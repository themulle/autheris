namespace Autheris.Application.Identity.Attributes;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TrinoSqlEngine.Ast.Emit;

/// <summary>The attribute source could not be read. The database message is only the inner exception; it never reaches a log or a response.</summary>
public sealed class CallerAttributeSourceException : Exception
{
    public const string ErrorCode = "CALLER_ATTRIBUTE_SOURCE_UNAVAILABLE";

    public CallerAttributeSourceException(Exception inner) : base("The caller attribute source is unavailable.", inner)
    {
    }
}

/// <summary>
/// A fixed, privileged lookup (PLAN-GOV-129 A-5.2, BC-129-03, BC-129-09, BC-129-11): the gateway builds the statement from validated
/// identifiers (<see cref="AttributeTableQuery"/>), binds subject, tenant and issuer, reads at most <c>Max + 1</c> rows on the governed
/// read connection (PostgreSQL: read-only transaction, never committed), with a tenant-only session context. The connection must be
/// marked <c>CallerAttributeLookupAllowed</c> and use a dedicated read-only login (an acknowledgement is honoured in Development only).
/// A table without a tenant column is refused unless it is in <c>DataSources:TenantColumnExemptTables</c> <em>and</em> keyed by an issuer
/// column. Tenant and issuer are additionally compared ordinally on the rows read, so a case-insensitive collation can never match a
/// tenant spelled differently. Trust root: write access to the attribute table (documented assumption).
/// </summary>
public sealed class AttributeTableProvider : ICallerAttributeProvider
{
    private readonly ISqlConnectionFactory _connections;
    private readonly IOptions<GatewayOptions> _options;
    private readonly IHostEnvironment? _environment;
    private readonly ILogger<AttributeTableProvider>? _logger;
    private readonly IDbSessionContextInitializer _session;

    public AttributeTableProvider(
        ISqlConnectionFactory connections,
        IOptions<GatewayOptions> options,
        IHostEnvironment? environment = null,
        ILogger<AttributeTableProvider>? logger = null,
        IDbSessionContextInitializer? sessionInitializer = null)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _environment = environment;
        _logger = logger;
        _session = sessionInitializer ?? new DbSessionContextInitializer();
    }

    public string Kind => CallerAttributeProviderBinding.AttributeTable;

    public async ValueTask<CallerAttributeResult> ResolveAsync(CallerSubject subject, CallerAttributeDefinition definition, CancellationToken ct)
    {
        if (subject.Kind != CallerSubjectKind.User)
        {
            return CallerAttributeResult.Missing();
        }

        if (!AttributeTableSpec.TryParse(definition.Provider.Config, out var spec, out _))
        {
            return CallerAttributeResult.Failed("Misconfigured");
        }

        var key = subject.KeyFor(spec!.KeyKind);
        if (key is null)
        {
            return CallerAttributeResult.Missing("NoKey");
        }

        var dataSources = _options.Value.DataSources;
        if (dataSources?.Connections is null || !dataSources.Connections.TryGetValue(spec.DataSource, out var connection) ||
            !connection.CallerAttributeLookupAllowed || string.IsNullOrWhiteSpace(connection.ConnectionString) ||
            !DataSourceProvider.TryResolveDialect(connection.Provider, out var dialect) ||
            dialect is not (DatabaseDialect.SqlServer or DatabaseDialect.PostgreSql or DatabaseDialect.Sqlite) ||
            !HasDedicatedLogin(connection))
        {
            return CallerAttributeResult.Failed("SourceNotAllowed");
        }

        if (spec.TenantColumn is null && !(IsTenantExempt(dataSources.TenantColumnExemptTables, spec) && spec.IssuerColumn is not null))
        {
            return CallerAttributeResult.Failed("TenantColumnRequired");
        }

        if (spec.IssuerColumn is not null && string.IsNullOrEmpty(subject.Issuer))
        {
            return CallerAttributeResult.Missing("NoIssuer");
        }

        try
        {
            var statement = AttributeTableQuery.Build(spec, dialect, subject.Tenant.Value, key, subject.Issuer, definition.MaxValues);
            var values = await ReadAsync(connection, dialect, statement, subject, definition.MaxValues, ct).ConfigureAwait(false);
            if (values is null)
            {
                return CallerAttributeResult.OverCardinality();
            }

            return values.Count == 0
                ? CallerAttributeResult.Missing()
                : CallerAttributeResult.Resolved(new CallerAttributeValue(values.Cast<object>().ToList(), SqlParameterType.String));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The database message can quote data; only the type is logged and the exception stays an inner exception.
            _logger?.LogWarning("Caller attribute source read failed ({ExceptionType}).", ex.GetType().Name);
            throw new CallerAttributeSourceException(ex);
        }
    }

    private bool HasDedicatedLogin(DataSourceConnectionOptions connection) =>
        connection.CallerAttributeDedicatedLogin ||
        (connection.CallerAttributeSharedLoginAcknowledged && _environment?.IsDevelopment() == true);

    private static bool IsTenantExempt(IEnumerable<string>? exempt, AttributeTableSpec spec) =>
        exempt != null && exempt.Any(e =>
            string.Equals(e, spec.QualifiedName, StringComparison.OrdinalIgnoreCase) || string.Equals(e, spec.Table, StringComparison.OrdinalIgnoreCase));

    /// <summary>The values, or null when more than <paramref name="max"/> rows matched (never truncated).</summary>
    private async Task<List<string>?> ReadAsync(
        DataSourceConnectionOptions options, DatabaseDialect dialect, AttributeTableStatement statement, CallerSubject subject, int max, CancellationToken ct)
    {
        await using var connection = await _connections.CreateOpenConnectionAsync(options, ct).ConfigureAwait(false);
        await using DbTransaction? tx = dialect == DatabaseDialect.PostgreSql ? await connection.BeginTransactionAsync(ct).ConfigureAwait(false) : null;
        if (tx != null)
        {
            await using var readOnly = connection.CreateCommand();
            readOnly.Transaction = tx;
            readOnly.CommandText = "SET TRANSACTION READ ONLY";
            await readOnly.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // Principal-independent: the session carries the tenant only.
        await _session.InitializeSessionAsync(connection, tx, dialect, subject.Tenant, userSid: null, purpose: null, ct: ct).ConfigureAwait(false);

        await using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandType = CommandType.Text;
        cmd.CommandTimeout = Math.Max(1, (int)Math.Ceiling(_options.Value.CallerAttributes.ProviderTimeoutMilliseconds / 1000.0));
        cmd.CommandText = statement.Sql;
        foreach (var (name, value) in statement.Parameters)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value;
            cmd.Parameters.Add(p);
        }

        var values = new List<string>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (statement.TenantOrdinal is { } t && !string.Equals(reader.GetString(t), subject.Tenant.Value, StringComparison.Ordinal))
                {
                    continue;   // ordinal tenant comparison, whatever the collation of the source says
                }

                if (statement.IssuerOrdinal is { } i && !string.Equals(reader.GetString(i), subject.Issuer, StringComparison.Ordinal))
                {
                    continue;
                }

                var text = Convert.ToString(reader.GetValue(statement.ValueOrdinal), CultureInfo.InvariantCulture);
                if (!string.IsNullOrEmpty(text))
                {
                    values.Add(text);
                }

                if (values.Count > max)
                {
                    return null;   // stop at Max + 1 rows (BC-129-09)
                }
            }
        }

        if (tx != null)
        {
            await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }

        return values;
    }
}

/// <summary>Save-time rules of an attribute-table attribute (BC-129-03, BC-129-11, F-9): valid identifiers, an existing connection that allows the lookup, a tenant column or (exempt table) an issuer column.</summary>
public sealed class AttributeTableDefinitionValidator : ICallerAttributeDefinitionValidator
{
    private readonly IOptions<GatewayOptions> _options;

    public AttributeTableDefinitionValidator(IOptions<GatewayOptions> options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public string Kind => CallerAttributeProviderBinding.AttributeTable;

    public void Validate(CallerAttributeDefinition definition)
    {
        if (!AttributeTableSpec.TryParse(definition.Provider.Config, out var spec, out var error))
        {
            throw new CallerAttributeAdminException("InvalidProvider", $"The attribute table configuration is not acceptable ({error}).");
        }

        var dataSources = _options.Value.DataSources;
        if (dataSources?.Connections is null || !dataSources.Connections.TryGetValue(spec!.DataSource, out var connection) ||
            !connection.CallerAttributeLookupAllowed ||
            !DataSourceProvider.TryResolveDialect(connection.Provider, out var dialect) ||
            dialect is not (DatabaseDialect.SqlServer or DatabaseDialect.PostgreSql or DatabaseDialect.Sqlite))
        {
            throw new CallerAttributeAdminException("InvalidProvider", "The data source does not allow the caller attribute lookup (CallerAttributeLookupAllowed, SQL Server, PostgreSQL or SQLite).");
        }

        if (spec.TenantColumn is null)
        {
            bool exempt = dataSources.TenantColumnExemptTables.Any(e =>
                string.Equals(e, spec.QualifiedName, StringComparison.OrdinalIgnoreCase) || string.Equals(e, spec.Table, StringComparison.OrdinalIgnoreCase));
            if (!exempt || spec.IssuerColumn is null)
            {
                throw new CallerAttributeAdminException("InvalidProvider", "An attribute table needs a tenant column; a tenant-exempt table needs an issuer column as part of the key.");
            }
        }
    }
}
