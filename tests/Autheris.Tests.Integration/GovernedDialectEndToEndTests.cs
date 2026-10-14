namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using TrinoSqlEngine.Ast.Emit;
using Xunit;

/// <summary>
/// WP-X1 (M-7): the governed WebSQL pipeline end to end on every admitted dialect, through the real DI container
/// (<c>WebApplicationFactory&lt;Program&gt;</c>): real catalog reader of the dialect, session pinning, compiler, binder, checked executor and
/// error sanitizer against a real database. One scenario set, four dialects (PostgreSQL, SQL Server, Oracle, SQLite): tenant isolation
/// (an exact tenant comparison, <c>ACME</c> is another tenant than <c>acme</c>), client parameters bound by name, masks computed in the
/// database, a provider error reduced to a stable code, and DML that is tenant scoped, limited by <c>MaxAffectedRows</c> and rolled back.
/// </summary>
public abstract class GovernedDialectEndToEndBase : IDisposable
{
    protected const string Source = "x1";
    protected const string TableLabel = "x1_orders";
    private const long MaxAffectedRows = 2;

    private WebApplicationFactory<Program>? _host;

    /// <summary>The target database; null when it is not available (a developer machine without Docker; fails under CI=true).</summary>
    protected abstract Task<DialectTarget?> StartAsync();

    protected abstract DialectTarget? Target { get; }

    protected sealed record DialectTarget(string Provider, string SourceType, string ConnectionString, string? DefaultSchema, string Schema, string TableName, bool ConversionFails);

    protected async Task<bool> EnsureHostAsync()
    {
        if (_host is not null)
        {
            return true;
        }

        var target = await StartAsync();
        if (target is null)
        {
            return false;
        }

        _host = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:Insecure:danger_allow_anonymous_access", "true");
            builder.UseSetting("Gateway:Insecure:danger_bypass_consent_checks", "true");
            builder.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", $"Data Source=gov-x1e2e-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
            builder.UseSetting("Gateway:WebSql:Enabled", "true");
            builder.UseSetting("Gateway:WebSql:DefaultDataSourceName", Source);
            builder.UseSetting("Gateway:WebSql:AllowDml", "true");
            builder.UseSetting("Gateway:WebSql:DmlWriterRoles:0", "Writer");
            builder.UseSetting("Gateway:WebSql:MaxAffectedRows", MaxAffectedRows.ToString());
            builder.UseSetting($"Gateway:DataSources:Connections:{Source}:Provider", target.Provider);
            builder.UseSetting($"Gateway:DataSources:Connections:{Source}:ConnectionString", target.ConnectionString);
            if (target.DefaultSchema is not null)
            {
                builder.UseSetting($"Gateway:DataSources:Connections:{Source}:DefaultSchema", target.DefaultSchema);
            }
        });

        await RegisterTableAsync(target, withMask: true);
        return true;
    }

    private async Task RegisterTableAsync(DialectTarget target, bool withMask)
    {
        using var scope = _host!.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITableMetadataRepository>();
        string Col(string name) => string.Equals(target.TableName, target.TableName.ToUpperInvariant(), StringComparison.Ordinal) ? name.ToUpperInvariant() : name;
        await repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Table = new Table { Id = Guid.NewGuid(), DisplayName = TableLabel, TableName = target.TableName, SchemaName = target.Schema, SourceName = Source, SourceType = target.SourceType },
            Identifier = new TableIdentifier(Source, target.Schema, target.TableName),
            Columns =
            [
                new TableColumn { ColumnName = Col("id"), DataType = "int" },
                new TableColumn { ColumnName = Col("tenant_id"), DataType = "varchar" },
                new TableColumn { ColumnName = Col("region"), DataType = "varchar" },
                new TableColumn { ColumnName = Col("email"), DataType = "varchar", IsSensitive = true }
            ],
            ColumnMaskingRules = withMask
                ? new Dictionary<string, MaskingRule> { [Col("email")] = new MaskingRule { RuleType = "REDACT" } }
                : new Dictionary<string, MaskingRule>()
        });
    }

    /// <summary>The schema-qualified name: it resolves under the data source name on every dialect (unqualified names use the legacy default domain).</summary>
    private string Ref => $"{Target!.Schema}.{Target.TableName}";

    private IServiceScope Scope() => _host!.Services.CreateScope();

    private static ClaimsPrincipal User(string tenant, params string[] roles) =>
        new(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.PrimarySid, "S-1-5-21-X1-E2E"), new Claim("tenant_id", tenant) }
                .Concat(roles.Select(r => new Claim(ClaimTypes.Role, r))), "Test"));

    private async Task<GovernedSqlResult> QueryAsync(string sql, string tenant, IReadOnlyDictionary<string, object?>? parameters = null, string[]? roles = null)
    {
        using var scope = Scope();
        var service = scope.ServiceProvider.GetRequiredService<IGovernedSqlExecutionService>();
        return await service.ExecuteQueryBufferedAsync(new GovernedSqlQueryRequest(sql, parameters), User(tenant, roles ?? []), new TenantId(tenant));
    }

    /// <summary>
    /// L-A2: proof of the path, not only of the outcome. Compile plus the executor write the compiler version and the keyed digest of
    /// the compiled text into the audit record of every statement; nothing else does.
    /// </summary>
    private async Task<int> CompiledAuditCountAsync()
    {
        using var scope = Scope();
        var repo = scope.ServiceProvider.GetRequiredService<IAuditLogRepository>();
        return (await repo.GetAuditLogEntriesAsync(1000)).Count(e =>
            e.DetailsJson != null && e.DetailsJson.Contains("\"compiler_version\"", StringComparison.Ordinal)
                                  && e.DetailsJson.Contains("\"sql.compiled_digest\"", StringComparison.Ordinal)
                                  && e.DetailsJson.Contains($"\"dialect\":\"{ExpectedDialect}\"", StringComparison.Ordinal));
    }

    private async Task AssertCompiledAuditAsync(int before, string what)
    {
        int after = before;
        for (var attempt = 0; attempt < 40 && after <= before; attempt++)
        {
            after = await CompiledAuditCountAsync();
            if (after <= before) await Task.Delay(250);
        }

        after.ShouldBeGreaterThan(before, $"{Target!.Provider} {what}: no audit record with compiler_version, dialect and sql.compiled_digest");
    }

    private string ExpectedDialect => Target!.Provider;

    private static List<long> Ids(GovernedSqlResult result) =>
        result.Rows.Select(r => Convert.ToInt64(r.First(kv => kv.Key.Equals("id", StringComparison.OrdinalIgnoreCase)).Value)).OrderBy(x => x).ToList();

    [Fact]
    public async Task Select_IsTenantIsolated_WithAnExactTenantComparison()
    {
        if (!await EnsureHostAsync()) return;
        int audited = await CompiledAuditCountAsync();

        Ids(await QueryAsync($"SELECT id FROM {Ref} ORDER BY id", "acme")).ShouldBe([1L, 2L, 3L]);
        Ids(await QueryAsync($"SELECT id FROM {Ref} ORDER BY id", "ACME")).ShouldBe([6L]);        // another tenant, not a case variant of acme
        Ids(await QueryAsync($"SELECT id FROM {Ref} ORDER BY id", "other")).ShouldBe([5L]);
        Ids(await QueryAsync($"SELECT id FROM {Ref} ORDER BY id", "nobody")).ShouldBeEmpty();
        await AssertCompiledAuditAsync(audited, "SELECT");
    }

    [Fact]
    public async Task ClientParameter_IsBoundByName_AndAHostileValueIsJustAValue()
    {
        if (!await EnsureHostAsync()) return;

        Ids(await QueryAsync($"SELECT id FROM {Ref} WHERE region = @r ORDER BY id", "acme", new Dictionary<string, object?> { ["r"] = "EU" })).ShouldBe([1L, 3L]);
        Ids(await QueryAsync($"SELECT id FROM {Ref} WHERE region = @r ORDER BY id", "acme", new Dictionary<string, object?> { ["r"] = "EU' OR '1'='1" })).ShouldBeEmpty();
    }

    [Fact]
    public async Task Mask_IsComputedInTheDatabase_AndTheRawValueNeverArrives()
    {
        if (!await EnsureHostAsync()) return;

        var result = await QueryAsync($"SELECT id, email FROM {Ref} ORDER BY id", "acme");

        result.Rows.Count.ShouldBe(3);
        result.Rows.Any(r => r.Values.Any(v => v is string text && text.Contains("@x.example"))).ShouldBeFalse();
    }

    [Fact]
    public async Task ProviderError_IsReducedToAStableCode_WithAFixedMessage()
    {
        if (!await EnsureHostAsync()) return;
        if (!Target!.ConversionFails) return;   // SQLite converts silently

        var ex = await Should.ThrowAsync<GovernedSqlException>(() => QueryAsync($"SELECT CAST(region AS integer) FROM {Ref}", "acme"));

        ex.Code.ShouldBeOneOf("SQL_DATA_ERROR", "SQL_PROVIDER_ERROR");
        ex.Message.ShouldBe(GovernedSqlErrorCodes.FixedMessage(ex.Code));   // no driver text, value or object name
        ex.InnerException.ShouldBeNull();
    }

    [Fact]
    public async Task Dml_IsTenantScoped_ThroughTheCheckedExecutor()
    {
        if (!await EnsureHostAsync()) return;

        int audited = await CompiledAuditCountAsync();
        await QueryAsync($"UPDATE {Ref} SET region = 'ZZ' WHERE id = 1", "acme", roles: ["Writer"]);
        await AssertCompiledAuditAsync(audited, "DML");

        (await QueryAsync($"SELECT id, region FROM {Ref} WHERE region = 'ZZ'", "acme")).Rows.Select(r => Convert.ToInt64(r.First(kv => kv.Key.Equals("id", StringComparison.OrdinalIgnoreCase)).Value)).ShouldBe([1L]);
        // the same statement as another tenant reaches none of acme's rows, and acme's other rows are untouched
        await QueryAsync($"UPDATE {Ref} SET region = 'ZZ' WHERE id = 5", "acme", roles: ["Writer"]);   // id 5 belongs to tenant 'other': zero rows, no error
        Ids(await QueryAsync($"SELECT id FROM {Ref} WHERE region = 'ZZ' ORDER BY id", "other")).ShouldBeEmpty();
        await QueryAsync($"UPDATE {Ref} SET region = 'EU' WHERE id = 1", "acme", roles: ["Writer"]);   // restore
    }

    [Fact]
    public async Task Dml_AboveMaxAffectedRows_IsRejectedAndRolledBack()
    {
        if (!await EnsureHostAsync()) return;

        // acme has three rows; the limit of this host is two
        var ex = await Should.ThrowAsync<DmlAffectedRowsLimitException>(() => QueryAsync($"UPDATE {Ref} SET region = 'QQ' WHERE id > 0", "acme", roles: ["Writer"]));

        ex.Code.ShouldBe("DML_AFFECTED_ROWS_LIMIT");
        Ids(await QueryAsync($"SELECT id FROM {Ref} WHERE region = 'QQ'", "acme")).ShouldBeEmpty();   // rolled back
    }

    [Fact]
    public async Task Dml_WithoutWriterRole_IsRejected()
    {
        if (!await EnsureHostAsync()) return;

        await Should.ThrowAsync<System.Security.SecurityException>(() => QueryAsync($"UPDATE {Ref} SET region = 'ZZ' WHERE id = 1", "acme"));
    }

    public virtual void Dispose()
    {
        _host?.Dispose();
        SqliteConnection.ClearAllPools();
    }
}

public sealed class SqliteGovernedEndToEndTests : GovernedDialectEndToEndBase
{
    private string? _dir;
    private DialectTarget? _target;
    protected override DialectTarget? Target => _target;

    protected override Task<DialectTarget?> StartAsync()
    {
        _dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "autheris-x1sqlite-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(_dir);
        var connectionString = $"Data Source={System.IO.Path.Combine(_dir, "x1.db")}";
        using (var conn = new SqliteConnection(connectionString))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE x1_orders (id INTEGER, tenant_id TEXT, region TEXT, email TEXT);
                INSERT INTO x1_orders VALUES (1,'acme','EU','alice@x.example'),(2,'acme','US','bob@x.example'),(3,'acme','EU','cara@x.example'),(5,'other','EU','dan@x.example'),(6,'ACME','EU','eve@x.example');
                """;
            cmd.ExecuteNonQuery();
        }

        _target = new DialectTarget("Sqlite", "Sqlite", connectionString, null, "main", "x1_orders", ConversionFails: false);
        return Task.FromResult<DialectTarget?>(_target);
    }

    public override void Dispose()
    {
        base.Dispose();
        try { if (_dir is not null) System.IO.Directory.Delete(_dir, true); } catch { /* best effort */ }
    }
}

public sealed class PostgreSqlGovernedEndToEndTests : GovernedDialectEndToEndBase, IAsyncLifetime
{
    private PostgreSqlTestDatabase? _db;
    private DialectTarget? _target;
    protected override DialectTarget? Target => _target;

    public async Task InitializeAsync()
    {
        try
        {
            _db = await PostgreSqlTestDatabase.CreateAsync();
            if (!_db.IsAvailable)
            {
                return;
            }

            await using var conn = new NpgsqlConnection(_db.ConnectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE public.x1_orders (id integer, tenant_id text, region text, email text);
                INSERT INTO public.x1_orders VALUES (1,'acme','EU','alice@x.example'),(2,'acme','US','bob@x.example'),(3,'acme','EU','cara@x.example'),(5,'other','EU','dan@x.example'),(6,'ACME','EU','eve@x.example');
                """;
            await cmd.ExecuteNonQueryAsync();
            _target = new DialectTarget("PostgreSql", "PostgreSql", _db.ConnectionString, "public", "public", "x1_orders", ConversionFails: true);
        }
        catch (Exception ex)
        {
            ContainerStartup.FailIfRequired(ex, "The PostgreSQL test container");
        }
    }

    protected override Task<DialectTarget?> StartAsync() => Task.FromResult(_target);

    public async Task DisposeAsync()
    {
        Dispose();
        if (_db is not null)
        {
            await _db.DisposeAsync();
        }
    }
}

public sealed class SqlServerGovernedEndToEndTests : GovernedDialectEndToEndBase, IAsyncLifetime
{
    private SqlServerTestDatabase? _db;
    private DialectTarget? _target;
    protected override DialectTarget? Target => _target;

    public async Task InitializeAsync()
    {
        _db = await SqlServerTestDatabase.CreateAsync();
        if (!_db.IsAvailable)
        {
            return;
        }

        await using var conn = new SqlConnection(_db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE dbo.x1_orders (id int NOT NULL, tenant_id nvarchar(64) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL, region nvarchar(20) NOT NULL, email nvarchar(200) NULL);
            INSERT dbo.x1_orders VALUES (1,N'acme',N'EU',N'alice@x.example'),(2,N'acme',N'US',N'bob@x.example'),(3,N'acme',N'EU',N'cara@x.example'),(5,N'other',N'EU',N'dan@x.example'),(6,N'ACME',N'EU',N'eve@x.example');
            """;
        await cmd.ExecuteNonQueryAsync();
        _target = new DialectTarget("SqlServer", "SqlServer", _db.ConnectionString, null, "dbo", "x1_orders", ConversionFails: true);
    }

    protected override Task<DialectTarget?> StartAsync() => Task.FromResult(_target);

    public async Task DisposeAsync()
    {
        Dispose();
        if (_db is not null)
        {
            await _db.DisposeAsync();
        }
    }
}

[Collection(OracleCollection.Name)]
public sealed class OracleGovernedEndToEndTests(AstCompilerOracleFixture fixture) : GovernedDialectEndToEndBase
{
    protected override DialectTarget? Target { get; } = new("Oracle", "Oracle", fixture.AppConnectionString, AstCompilerOracleFixture.AppUser, AstCompilerOracleFixture.AppUser, "X1_ORDERS", ConversionFails: true);

    protected override Task<DialectTarget?> StartAsync() => Task.FromResult(Target);
}
