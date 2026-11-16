namespace Autheris.Tests.Integration.Identity.Attributes;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Identity.Attributes;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Autheris.Tests.Common;
using Autheris.Tests.Unit.Identity.Attributes;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Shouldly;
using Xunit;
using static Autheris.Tests.Unit.Identity.Attributes.CallerAttributeTestSupport;

/// <summary>PLAN-GOV-129 ST-129-12: PostgreSQL execution of the privileged attribute-table lookup, including binary collation enforcement.</summary>
[Trait("Category", "Security")]
public sealed class AttributeTableProviderIntegrationTests
{
    private static CallerAttributeDefinition Definition(string table = "person_attributes", string? tenant = "tenant_id", string? issuer = null, int max = 5)
    {
        var config = new List<(string, string)>
        {
            ("dataSource", "hr_db"),
            ("schema", "public"),
            ("table", table),
            ("subjectKeyKind", "EntraObjectId"),
            ("subjectKeyColumn", "person_oid"),
            ("valueColumn", "cost_center")
        };
        if (tenant is not null) config.Add(("tenantColumn", tenant));
        if (issuer is not null) config.Add(("issuerColumn", issuer));
        return CallerAttributeTestSupport.Definition("cost_center", "attribute-table", CallerAttributeType.Text, CallerAttributeCardinality.List, max, 60, config.ToArray());
    }

    private static AttributeTableProvider Provider(string connectionString, out List<string> logs)
    {
        var captured = new List<string>();
        logs = captured;
        var options = Microsoft.Extensions.Options.Options.Create(new GatewayOptions
        {
            CallerAttributes = Options(),
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["hr_db"] = new()
                    {
                        Provider = "PostgreSql",
                        DefaultSchema = "public",
                        ConnectionString = connectionString,
                        CallerAttributeLookupAllowed = true,
                        CallerAttributeDedicatedLogin = true
                    }
                }
            }
        });
        return new AttributeTableProvider(
            new SqlConnectionFactory(new StaticHostEnvironment("Production")),
            options,
            new StaticHostEnvironment("Production"),
            new CapturingLogger(captured));
    }

    private static string[] Values(CallerAttributeResult r) => Enumerable.Range(0, r.Value!.Count).Select(i => (string)r.Value[i]).ToArray();

    private static async Task<NpgsqlConnection> SeedAsync(PostgreSqlTestDatabase db)
    {
        var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE EXTENSION IF NOT EXISTS citext;
            CREATE TABLE public.person_attributes (person_oid TEXT, cost_center TEXT, tenant_id TEXT, issuer TEXT);
            CREATE TABLE public.nocase_attributes (person_oid TEXT, cost_center TEXT, tenant_id CITEXT, issuer TEXT);
            """;
        await cmd.ExecuteNonQueryAsync();
        return conn;
    }

    [RequiresContainerRuntimeFact]
    public async Task ST_129_12_OrdinalTenantMatch_RunsInTheDatabase_SoDifferentlyCasedRowsDoNotCrowdTheWindow()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;

        await using var conn = await SeedAsync(db);
        await using (var insert = conn.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO public.nocase_attributes VALUES
                    (@oid, 'x0', 'TENANT_A', 'iss'),
                    (@oid, 'x1', 'TENANT_A', 'iss'),
                    (@oid, 'x2', 'TENANT_A', 'iss'),
                    (@oid, 'real', 'tenant_a', 'iss');
                """;
            insert.Parameters.AddWithValue("oid", UserOid);
            await insert.ExecuteNonQueryAsync();
        }

        var result = await Provider(db.ConnectionString, out _).ResolveAsync(UserSubject(), Definition(table: "nocase_attributes", max: 2), default);

        result.Outcome.ShouldBe(CallerAttributeOutcome.Resolved);
        Values(result).ShouldBe(["real"]);
    }

    [RequiresContainerRuntimeFact]
    public async Task Resolves_TheValuesOfTheSubject_Only()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;

        await using var conn = await SeedAsync(db);
        await using (var insert = conn.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO public.person_attributes VALUES
                    (@u, '100', 'tenant_a', 'iss'),
                    (@u, '200', 'tenant_a', 'iss'),
                    (@o, '999', 'tenant_a', 'iss');
                """;
            insert.Parameters.AddWithValue("u", UserOid);
            insert.Parameters.AddWithValue("o", OtherOid);
            await insert.ExecuteNonQueryAsync();
        }

        var result = await Provider(db.ConnectionString, out _).ResolveAsync(UserSubject(), Definition(), default);

        result.Outcome.ShouldBe(CallerAttributeOutcome.Resolved);
        Values(result).Order().ShouldBe(["100", "200"]);
    }

    [RequiresContainerRuntimeFact]
    public async Task ST_129_12_TwoTenants_WithTheSameSubjectKey_NeverMix()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;

        await using var conn = await SeedAsync(db);
        await using (var insert = conn.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO public.person_attributes VALUES
                    (@u, '100', 'tenant_a', 'iss'),
                    (@u, '777', 'tenant_b', 'iss');
                """;
            insert.Parameters.AddWithValue("u", UserOid);
            await insert.ExecuteNonQueryAsync();
        }

        var result = await Provider(db.ConnectionString, out _).ResolveAsync(UserSubject(), Definition(), default);

        Values(result).ShouldBe(["100"]);
    }

    [RequiresContainerRuntimeFact]
    public async Task KeyValue_WithQuotes_IsBound_NotInterpolated()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;

        await using var conn = await SeedAsync(db);
        await using (var insert = conn.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO public.person_attributes VALUES
                    ('x'' OR ''1''=''1', 'leak', 'tenant_a', 'iss'),
                    (@u, 'mine', 'tenant_a', 'iss');
                """;
            insert.Parameters.AddWithValue("u", UserOid);
            await insert.ExecuteNonQueryAsync();
        }

        var subject = new CallerSubject(
            Tenant,
            CallerSubjectKind.User,
            null,
            [],
            TrustedIssuer,
            Bearer,
            System.Collections.Frozen.FrozenDictionary.ToFrozenDictionary(
                new Dictionary<SubjectKeyKind, string> { [SubjectKeyKind.EntraObjectId] = "x' OR '1'='1" }));

        var provider = Provider(db.ConnectionString, out _);
        Values(await provider.ResolveAsync(subject, Definition(), default)).ShouldBe(["leak"]);
        Values(await provider.ResolveAsync(UserSubject(), Definition(), default)).ShouldBe(["mine"]);
    }

    private sealed class CapturingLogger(List<string> logs) : ILogger<AttributeTableProvider>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            logs.Add(formatter(state, exception));
    }

    private sealed class StaticHostEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Autheris.Tests.Integration";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
