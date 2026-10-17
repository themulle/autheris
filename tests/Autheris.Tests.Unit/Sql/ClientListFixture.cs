namespace Autheris.Tests.Unit.Sql;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

/// <summary>Records every formatted log message and exception text, to prove that no list value reaches a log (SEC-128-07).</summary>
internal sealed class ListCapturingLogger<T> : ILogger<T>
{
    public List<string> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Add(formatter(state, exception) + (exception is null ? string.Empty : " | " + exception));
}

/// <summary>
/// The governed pipeline against an in-memory SQLite database (orders: id, status, amount, tenant_id; tenant_a ids 1-5, tenant_b ids 6-7)
/// or against a recording connection, for the client list tests of REQ-SQL-128.
/// </summary>
internal sealed class ClientListFixture : IAsyncDisposable
{
    public const string TenantA = "tenant_a";
    public const string TenantB = "tenant_b";
    public const string WriterRole = "WebSqlWriter";

    private readonly SqliteConnection? _keepAlive;

    private ClientListFixture(SqliteConnection? keepAlive, string connectionString, GovernedSqlExecutionService service, List<AuditLogEntry> entries)
    {
        _entries = entries;
        _keepAlive = keepAlive;
        ConnectionString = connectionString;
        Service = service;
    }

    public string ConnectionString { get; }

    public GovernedSqlExecutionService Service { get; }

    public static async Task<ClientListFixture> CreateAsync(
        int? maxListValues = null,
        int? maxLists = null,
        ListCapturingLogger<GovernedSqlExecutionService>? logger = null,
        Func<DbConnection>? openConnection = null,
        IFederatedQueryExecutionService? federated = null,
        bool crossSourceCatalog = false)
    {
        string connectionString = $"Data Source=list_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        var keepAlive = new SqliteConnection(connectionString);
        await keepAlive.OpenAsync();
        await using (var setup = keepAlive.CreateCommand())
        {
            setup.CommandText =
                "CREATE TABLE orders (id INTEGER, status TEXT, amount REAL, tenant_id TEXT);" +
                "INSERT INTO orders VALUES (1, 'new', 10, 'tenant_a'), (2, 'new', 20, 'tenant_a'), (3, 'paid', 30, 'tenant_a'), (4, 'paid', 40, 'tenant_a'), (5, 'void', 50, 'tenant_a');" +
                "INSERT INTO orders VALUES (6, 'new', 60, 'tenant_b'), (7, 'paid', 70, 'tenant_b');";
            await setup.ExecuteNonQueryAsync();
        }

        var webSql = new WebSqlOptions
        {
            MaxListParameterValues = maxListValues ?? 1000,
            MaxListParameters = maxLists ?? 8,
            Enabled = true,
            AllowDml = true,
            DmlWriterRoles = [WriterRole],
            DefaultMaxRows = 2000,
            MaxAllowedRows = 5000,
            MaxAffectedRows = 1000
        };
        var options = new GatewayOptions
        {
            WebSql = webSql,
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["default"] = new DataSourceConnectionOptions { Provider = "Sqlite", ConnectionString = connectionString }
                }
            }
        };

        var orders = new TableMetadata
        {
            Identifier = new TableIdentifier("default", "main", "orders"),
            Table = new Table { TableName = "orders", SchemaName = "main", SourceName = string.Empty, SourceType = "Sqlite" },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "status", DataType = "varchar" },
                new TableColumn { ColumnName = "amount", DataType = "decimal" },
                new TableColumn { ColumnName = "tenant_id", DataType = "varchar" }
            ]
        };

        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<TableMetadata?>(ci.Arg<TableIdentifier>().Equals(orders.Identifier) ? orders : null));

        var consentRepository = Substitute.For<IConsentRepository>();
        consentRepository.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>(Array.Empty<Consent>()));

        var consentResolution = Substitute.For<IConsentResolutionService>();
        consentResolution.ResolveAccess(Arg.Any<Sid>(), Arg.Any<IReadOnlySet<Sid>>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<Consent>>(), Arg.Any<DatabaseDialect>())
            .Returns(ci => TableAccessDecision.Allowed(ci.ArgAt<TableIdentifier>(3), new Dictionary<string, ColumnAccessLevel>(), null, hasUnconstrainedColumnAllow: true));

        var fixtureEntries = new List<AuditLogEntry>();
        var audit = Substitute.For<IAuditLogRepository>();
        audit.RecordAuditEventAsync(Arg.Do<AuditLogEntry>(e => fixtureEntries.Add(e)), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var connectionFactory = Substitute.For<ISqlConnectionFactory>();
        connectionFactory.CreateOpenConnectionAsync(Arg.Any<DataSourceConnectionOptions>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                if (openConnection is not null)
                {
                    return openConnection();
                }

                var connection = new SqliteConnection(connectionString);
                await connection.OpenAsync();
                return (DbConnection)connection;
            });

        var service = global::Autheris.Tests.Unit.TestDoubles.GovernedSqlExecutionServiceFactory.Create(
            Options.Create(options), executionGuard: GovernedTestSupport.Guard,
            policyEnforcement: global::Autheris.Tests.Unit.TestDoubles.NullPolicyEnforcementService.Instance,
            consentResolution: consentResolution,
            tableRepository: repo, policyFactory: global::Autheris.Tests.Unit.GovernedTestSupport.PolicyFactory(repo), typedPolicies: global::Autheris.Tests.Unit.GovernedTestSupport.TypedPolicies(),
            auditLogRepository: audit,
            connectionFactory: connectionFactory,
            clientIpResolver: null,
            environment: null,
            logger: (ILogger<GovernedSqlExecutionService>?)logger ?? NullLogger<GovernedSqlExecutionService>.Instance,
            consentRepository: consentRepository,
            secretProvider: null,
            federatedExecutionService: federated);
        return new ClientListFixture(keepAlive, connectionString, service, fixtureEntries);
    }

    private readonly List<AuditLogEntry> _entries;

    public IReadOnlyList<AuditLogEntry> Audit => _entries;

    public static ClaimsPrincipal User(string tenant = TenantA, bool writer = true) =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.PrimarySid, "S-1-5-21-LIST-USER"),
                new Claim("tenant_id", tenant),
                new Claim(ClaimTypes.Role, writer ? WriterRole : "Reader")
            ],
            "Test"));

    public static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    public static GovernedSqlQueryRequest Request(string sql, IReadOnlyDictionary<string, object?>? parameters, string? transport = SqlTransports.WebSql, bool planned = false) =>
        new(sql, parameters, Transport: transport, PlannedTableRead: planned);

    public async Task<GovernedSqlResult> QueryAsync(string sql, IReadOnlyDictionary<string, object?>? parameters, string tenant = TenantA, string? transport = SqlTransports.WebSql, bool planned = false) =>
        await Service.ExecuteQueryBufferedAsync(Request(sql, parameters, transport, planned), User(tenant), new TenantId(tenant));

    public async Task<long[]> IdsAsync(string sql, IReadOnlyDictionary<string, object?>? parameters, string tenant = TenantA) =>
        (await QueryAsync(sql, parameters, tenant)).Rows.Select(r => Convert.ToInt64(r["id"], System.Globalization.CultureInfo.InvariantCulture)).ToArray();

    public Task ExecuteAsync(string sql, IReadOnlyDictionary<string, object?>? parameters, string tenant = TenantA, string? transport = SqlTransports.WebSql) =>
        Service.ExecuteGovernedQueryAsync(Request(sql, parameters, transport), User(tenant), new TenantId(tenant), (_, _) => Task.CompletedTask);

    public async Task<long> ScalarAsync(string sql)
    {
        await using var cmd = _keepAlive!.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async ValueTask DisposeAsync()
    {
        if (_keepAlive is not null)
        {
            await _keepAlive.DisposeAsync();
        }
    }
}
