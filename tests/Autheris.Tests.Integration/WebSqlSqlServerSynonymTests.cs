namespace Autheris.Tests.Integration;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.DataSources;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Options.DataSources;
using Autheris.Domain.Model;
using Autheris.Tests.Integration.Governance;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using TrinoSqlEngine.Ast.Emit;
using Xunit;

/// <summary>
/// Issue #669: a SQL Server synonym is governed like any other catalog object. Autheris reads no source metadata (OD-25), so it
/// does not resolve a synonym to its base object: the columns come from the governance catalog only, and SQL Server resolves the
/// synonym when the query runs, also for a base object in another database on the same server. An undeclared column of the base
/// object is never readable, and the base object is reachable only when it is registered itself. A synonym that cannot be used
/// names its reason in the log (not registered, no declared columns, inactive) while the caller receives the same denial for all.
/// </summary>
public sealed class WebSqlSqlServerSynonymTests : IAsyncLifetime, IDisposable
{
    private const string Source = "synsrc";
    private const string Tenant = "acme";

    private readonly ConcurrentQueue<string> _logs = new();
    private SqlServerTestDatabase? _baseDb;
    private SqlServerTestDatabase? _db;
    private WebApplicationFactory<Program>? _host;

    public async Task InitializeAsync()
    {
        _baseDb = await SqlServerTestDatabase.CreateAsync();
        _db = await SqlServerTestDatabase.CreateAsync();
        if (!_baseDb.IsAvailable || !_db.IsAvailable)
        {
            return;
        }

        var baseDbName = new SqlConnectionStringBuilder(_baseDb.ConnectionString).InitialCatalog;
        await ExecuteAsync(_baseDb.ConnectionString, """
            CREATE TABLE dbo.price_list (item_code nvarchar(20) NOT NULL, item_name nvarchar(100) NOT NULL, unit_price decimal(10,2) NOT NULL, internal_cost decimal(10,2) NOT NULL);
            INSERT dbo.price_list VALUES (N'A-1', N'Bolt', 1.50, 0.40), (N'A-2', N'Nut', 0.80, 0.20);
            """);
        await ExecuteAsync(_db.ConnectionString, $"""
            CREATE TABLE dbo.base_items (item_code nvarchar(20) NOT NULL, item_name nvarchar(100) NOT NULL, internal_note nvarchar(200) NULL);
            INSERT dbo.base_items VALUES (N'B-1', N'Washer', N'note one'), (N'B-2', N'Screw', N'note two');
            EXEC(N'CREATE SYNONYM dbo.items FOR dbo.base_items');
            EXEC(N'CREATE SYNONYM dbo.items_undeclared FOR dbo.base_items');
            EXEC(N'CREATE SYNONYM dbo.items_inactive FOR dbo.base_items');
            EXEC(N'CREATE SYNONYM dbo.price_list FOR [{baseDbName}].dbo.price_list');
            """);

        _host = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:Insecure:danger_allow_anonymous_access", "true");
            builder.UseSetting("Gateway:Insecure:danger_bypass_consent_checks", "true");
            builder.UseSetting("Gateway:GovernanceDb:Provider", "PostgreSql");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", GovernanceTestDatabase.Create(nameof(WebSqlSqlServerSynonymTests)));
            builder.UseSetting("Gateway:WebSql:Enabled", "true");
            builder.UseSetting("Gateway:WebSql:DefaultDataSourceName", Source);
            builder.UseSetting($"Gateway:DataSources:Connections:{Source}:Provider", "SqlServer");
            builder.UseSetting($"Gateway:DataSources:Connections:{Source}:ConnectionString", _db.ConnectionString);
            builder.ConfigureTestServices(services => services.AddSingleton<ILoggerProvider>(new CapturingLoggerProvider(_logs)));
        });

        await RegisterAsync("items", "item_code", "item_name");
        await RegisterAsync("price_list", "item_code", "item_name", "unit_price");
        await RegisterAsync("items_undeclared");
        await RegisterAsync("items_inactive", active: false, "item_code", "item_name");
    }

    public async Task DisposeAsync()
    {
        Dispose();
        if (_db is not null)
        {
            await _db.DisposeAsync();
        }

        if (_baseDb is not null)
        {
            await _baseDb.DisposeAsync();
        }
    }

    public void Dispose()
    {
        _host?.Dispose();
        _host = null;
    }

    [Fact]
    public async Task Query_SynonymWithDeclaredColumns_ReturnsRows()
    {
        if (_host is null) return;

        var result = await QueryAsync("SELECT item_code, item_name FROM dbo.items ORDER BY item_code");

        result.Rows.Select(r => Value(r, "item_code")).ShouldBe(["B-1", "B-2"]);
        result.Rows.Select(r => Value(r, "item_name")).ShouldBe(["Washer", "Screw"]);
    }

    [Fact]
    public async Task Query_CrossDatabaseSynonym_ReturnsRows()
    {
        if (_host is null) return;

        var result = await QueryAsync("SELECT item_code, unit_price FROM dbo.price_list ORDER BY item_code");

        result.Rows.Select(r => Value(r, "item_code")).ShouldBe(["A-1", "A-2"]);
        result.Rows.Select(r => Convert.ToDecimal(r.First(kv => kv.Key.Equals("unit_price", StringComparison.OrdinalIgnoreCase)).Value, System.Globalization.CultureInfo.InvariantCulture))
            .ShouldBe([1.50m, 0.80m]);
    }

    [Fact]
    public async Task Query_SynonymWithoutDeclaredColumns_DeniedWithNoColumnsReason()
    {
        if (_host is null) return;

        await Should.ThrowAsync<WebSqlTableDeniedException>(() => QueryAsync("SELECT item_code FROM dbo.items_undeclared"));

        _logs.ShouldContain(l => l.Contains("items_undeclared", StringComparison.Ordinal) &&
                                 l.Contains("the catalog entry declares no columns", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Query_SynonymColumnNotDeclared_DeniedByCatalog()
    {
        if (_host is null) return;

        // The base table has internal_note and the base object of price_list has internal_cost; neither is declared.
        // The table is sent as a derived table of its declared columns, so SQL Server itself rejects the undeclared column
        // ("Invalid column name") and the caller receives the policy denial; no value of the column is ever read.
        (await Should.ThrowAsync<GovernedSqlException>(() => QueryAsync("SELECT item_code, internal_note FROM dbo.items")))
            .Code.ShouldBe("SQL_POLICY_DENIED");
        (await Should.ThrowAsync<GovernedSqlException>(() => QueryAsync("SELECT item_code, internal_cost FROM dbo.price_list")))
            .Code.ShouldBe("SQL_POLICY_DENIED");

        var star = await QueryAsync("SELECT * FROM dbo.items");
        star.Rows.ShouldNotBeEmpty();
        star.Rows.ShouldAllBe(r => !r.Keys.Any(k => k.Equals("internal_note", StringComparison.OrdinalIgnoreCase)));
        star.Rows.SelectMany(r => r.Values).ShouldNotContain("note one");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Query_BaseTableNotRegistered_DirectAccessDenied()
    {
        if (_host is null) return;

        await Should.ThrowAsync<WebSqlTableDeniedException>(() => QueryAsync("SELECT item_code FROM dbo.base_items"));

        _logs.ShouldContain(l => l.Contains("base_items", StringComparison.Ordinal) &&
                                 l.Contains("no catalog metadata is registered", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Query_InactiveSynonym_DeniedWithInactiveReason()
    {
        if (_host is null) return;

        await Should.ThrowAsync<WebSqlTableDeniedException>(() => QueryAsync("SELECT item_code FROM dbo.items_inactive"));

        _logs.ShouldContain(l => l.Contains("items_inactive", StringComparison.Ordinal) &&
                                 l.Contains("the catalog entry is inactive", StringComparison.Ordinal));
    }

    /// <summary>The log names the reason; the caller still receives one answer for every unusable table (no catalog oracle).</summary>
    [Fact]
    [Trait("Category", "Security")]
    public async Task Query_SynonymWithoutColumnsOrInactive_AnswersLikeAnUnregisteredTable()
    {
        if (_host is null) return;

        var unknown = await Should.ThrowAsync<WebSqlTableDeniedException>(() => QueryAsync("SELECT item_code FROM dbo.not_there"));
        var noColumns = await Should.ThrowAsync<WebSqlTableDeniedException>(() => QueryAsync("SELECT item_code FROM dbo.items_undeclared"));
        var inactive = await Should.ThrowAsync<WebSqlTableDeniedException>(() => QueryAsync("SELECT item_code FROM dbo.items_inactive"));

        noColumns.Message.Replace("items_undeclared", "not_there", StringComparison.Ordinal).ShouldBe(unknown.Message);
        inactive.Message.Replace("items_inactive", "not_there", StringComparison.Ordinal).ShouldBe(unknown.Message);
    }

    private Task RegisterAsync(string table, params string[] columns) => RegisterAsync(table, active: true, columns);

    private async Task RegisterAsync(string table, bool active, params string[] columns)
    {
        using var scope = _host!.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITableMetadataRepository>();
        await repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Table = new Table { Id = Guid.NewGuid(), DisplayName = table, TableName = table, SchemaName = "dbo", SourceName = Source, SourceType = "SqlServer", IsActive = active },
            Identifier = new TableIdentifier(Source, "dbo", table),
            Columns = columns.Select(c => new TableColumn { ColumnName = c, DataType = c == "unit_price" ? "decimal" : "varchar" }).ToList()
        });
    }

    private async Task<GovernedSqlResult> QueryAsync(string sql)
    {
        using var scope = _host!.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IIngressTransportContext>().Set(DataSourceTransportFlags.WebSql);
        var service = scope.ServiceProvider.GetRequiredService<IGovernedSqlExecutionService>();
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.PrimarySid, "S-1-5-21-SYN-E2E"), new Claim("tenant_id", Tenant)], "Test"));
        return await service.ExecuteQueryBufferedAsync(new GovernedSqlQueryRequest(sql), user, new TenantId(Tenant));
    }

    private static string? Value(IReadOnlyDictionary<string, object?> row, string column) =>
        row.First(kv => kv.Key.Equals(column, StringComparison.OrdinalIgnoreCase)).Value?.ToString();

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private sealed class CapturingLoggerProvider(ConcurrentQueue<string> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Capture(sink);

        public void Dispose()
        {
        }

        private sealed class Capture(ConcurrentQueue<string> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                sink.Enqueue(formatter(state, exception) + (exception is null ? string.Empty : $" [{exception.GetType().Name}: {exception.Message}]"));
        }
    }
}
