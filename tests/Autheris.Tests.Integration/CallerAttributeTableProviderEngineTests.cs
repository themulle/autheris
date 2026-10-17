namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Identity.Attributes;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// REQ-GOV-129 ST-129-12 / BC-129-11 on the real engines: the attribute table lookup returns only the rows of the caller's tenant, compares the
/// tenant ordinally (a case-insensitive collation must not match a tenant spelled differently), stops at Max + 1 rows, and never mixes two tenants
/// that hold the same subject key. Needs Docker; without it each test returns early.
/// </summary>
public abstract class CallerAttributeTableProviderEngineTests : IAsyncLifetime
{
    private const string Oid = "11111111-1111-1111-1111-111111111111";
    protected static readonly TenantId TenantA = new("tenant_a");
    protected bool Available { get; set; }

    protected abstract DatabaseDialect Dialect { get; }

    protected abstract string ProviderName { get; }

    protected abstract string ConnectionString { get; }

    protected abstract string? Schema { get; }

    protected abstract Task StartAsync();

    protected abstract Task StopAsync();

    protected abstract DbConnection NewConnection();

    protected abstract string CreateTableSql { get; }

    public async Task InitializeAsync()
    {
        await StartAsync();
        if (!Available)
        {
            return;
        }

        await using var connection = NewConnection();
        await connection.OpenAsync();
        await Exec(connection, CreateTableSql);
    }

    public Task DisposeAsync() => StopAsync();

    private sealed class Factory(Func<DbConnection> open) : ISqlConnectionFactory
    {
        public async Task<DbConnection> CreateOpenConnectionAsync(DataSourceConnectionOptions options, CancellationToken ct = default)
        {
            var connection = open();
            await connection.OpenAsync(ct);
            return connection;
        }
    }

    private static async Task Exec(DbConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value;
            cmd.Parameters.Add(p);
        }

        await cmd.ExecuteNonQueryAsync();
    }

    protected async Task InsertAsync(string key, string value, string tenant)
    {
        await using var connection = NewConnection();
        await connection.OpenAsync();
        string table = Schema is null ? "person_attributes" : $"{Schema}.person_attributes";
        await Exec(connection, $"INSERT INTO {table} (person_oid, cost_center, tenant_id) VALUES (@k, @v, @t)", ("@k", key), ("@v", value), ("@t", tenant));
    }

    private AttributeTableProvider Provider()
    {
        var options = Options.Create(new GatewayOptions
        {
            CallerAttributes = new CallerAttributeOptions { Enabled = true },
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["hr_db"] = new() { Provider = ProviderName, ConnectionString = ConnectionString, CallerAttributeLookupAllowed = true, CallerAttributeDedicatedLogin = true }
                }
            }
        });
        return new AttributeTableProvider(new Factory(NewConnection), options, Substitute.For<IHostEnvironment>());
    }

    private CallerAttributeDefinition Definition(int max = 5)
    {
        var config = new Dictionary<string, string>
        {
            ["dataSource"] = "hr_db", ["table"] = "person_attributes", ["subjectKeyKind"] = "EntraObjectId", ["subjectKeyColumn"] = "person_oid",
            ["valueColumn"] = "cost_center", ["tenantColumn"] = "tenant_id"
        };
        if (Schema != null) config["schema"] = Schema;
        return new CallerAttributeDefinition(Guid.NewGuid(), TenantA, "cost_center", CallerAttributeType.Text, CallerAttributeCardinality.List, max, 60,
            new CallerAttributeProviderBinding("attribute-table", config), null, FilterApprovalStatus.Active);
    }

    private static CallerSubject Subject(TenantId tenant) => new(tenant, CallerSubjectKind.User, null, [], "https://login.example/", "Bearer",
        System.Collections.Frozen.FrozenDictionary.ToFrozenDictionary(new Dictionary<SubjectKeyKind, string> { [SubjectKeyKind.EntraObjectId] = Oid }));

    private static string[] Values(CallerAttributeResult r) => Enumerable.Range(0, r.Value!.Count).Select(i => (string)r.Value[i]).Order().ToArray();

    [Fact]
    public async Task ST_129_12_TwoTenants_WithTheSameSubjectKey_NeverMix_AndTenantCaseIsOrdinal()
    {
        if (!Available) return;
        await InsertAsync(Oid, "100", "tenant_a");
        await InsertAsync(Oid, "200", "tenant_a");
        await InsertAsync(Oid, "777", "tenant_b");
        await InsertAsync(Oid, "888", "TENANT_A");   // differs only by case: a case-insensitive collation matches it in SQL, the ordinal check refuses it

        var result = await Provider().ResolveAsync(Subject(TenantA), Definition(), default);

        result.Outcome.ShouldBe(CallerAttributeOutcome.Resolved);
        Values(result).ShouldBe(["100", "200"]);
    }

    [Fact]
    public async Task ST_129_12_OverTheCap_IsOverCardinality_AndAnOtherSubjectHasNothing()
    {
        if (!Available) return;
        for (int i = 0; i < 6; i++) await InsertAsync(Oid, $"v{i}", "tenant_a");

        (await Provider().ResolveAsync(Subject(TenantA), Definition(max: 5), default)).Outcome.ShouldBe(CallerAttributeOutcome.OverCardinality);
        var other = new CallerSubject(TenantA, CallerSubjectKind.User, null, [], null, "Bearer",
            System.Collections.Frozen.FrozenDictionary.ToFrozenDictionary(new Dictionary<SubjectKeyKind, string> { [SubjectKeyKind.EntraObjectId] = "22222222-2222-2222-2222-222222222222" }));
        (await Provider().ResolveAsync(other, Definition(), default)).Outcome.ShouldBe(CallerAttributeOutcome.Missing);
    }

    [Fact]
    public async Task ST_129_12_ADatabaseError_IsAnInfrastructureFailure_WithoutTheDatabaseMessage()
    {
        if (!Available) return;
        var definition = Definition() with
        {
            Provider = new CallerAttributeProviderBinding("attribute-table", Definition().Provider.Config.ToDictionary(p => p.Key, p => p.Key == "table" ? "no_such_table_secret" : p.Value))
        };
        var ex = await Should.ThrowAsync<CallerAttributeSourceException>(() => Provider().ResolveAsync(Subject(TenantA), definition, default).AsTask());
        ex.Message.ShouldNotContain("no_such_table_secret");
    }
}

public sealed class PostgreSqlCallerAttributeTableProviderTests : CallerAttributeTableProviderEngineTests
{
    private PostgreSqlTestDatabase? _db;

    protected override DatabaseDialect Dialect => DatabaseDialect.PostgreSql;

    protected override string ProviderName => "PostgreSql";

    protected override string ConnectionString => _db!.ConnectionString;

    protected override string? Schema => "hr";

    protected override string CreateTableSql => "CREATE SCHEMA hr; CREATE TABLE hr.person_attributes (person_oid TEXT, cost_center TEXT, tenant_id TEXT);";

    protected override DbConnection NewConnection() => new NpgsqlConnection(_db!.ConnectionString);

    protected override async Task StartAsync()
    {
        try
        {
            _db = await PostgreSqlTestDatabase.CreateAsync();
            Available = _db.IsAvailable;
        }
        catch (Exception ex)
        {
            Available = false;
            ContainerStartup.FailIfRequired(ex, "The PostgreSQL test container");
        }
    }

    protected override async Task StopAsync()
    {
        if (_db != null)
        {
            await _db.DisposeAsync();
        }
    }
}

public sealed class SqlServerCallerAttributeTableProviderTests : CallerAttributeTableProviderEngineTests
{
    private SqlServerTestDatabase? _db;

    protected override DatabaseDialect Dialect => DatabaseDialect.SqlServer;

    protected override string ProviderName => "SqlServer";

    protected override string ConnectionString => _db!.ConnectionString;

    protected override string? Schema => "dbo";

    protected override string CreateTableSql => "CREATE TABLE dbo.person_attributes (person_oid NVARCHAR(64), cost_center NVARCHAR(64), tenant_id NVARCHAR(64));";

    protected override DbConnection NewConnection() => new SqlConnection(_db!.ConnectionString);

    protected override async Task StartAsync()
    {
        _db = await SqlServerTestDatabase.CreateAsync();
        Available = _db.IsAvailable;
    }

    protected override async Task StopAsync()
    {
        if (_db != null)
        {
            await _db.DisposeAsync();
        }
    }
}
