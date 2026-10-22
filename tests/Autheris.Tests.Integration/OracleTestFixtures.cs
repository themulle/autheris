namespace Autheris.Tests.Integration;

using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Oracle.ManagedDataAccess.Client;
using Testcontainers.Oracle;
using Xunit;

/// <summary>All Oracle integration classes share one container (WP-X0z): the Oracle Free image is heavy.</summary>
[CollectionDefinition(Name)]
public sealed class OracleCollection : ICollectionFixture<AstCompilerOracleFixture>
{
    public const string Name = "Oracle Free";
}

public sealed class AstCompilerOracleFixture : IAsyncLifetime
{
    public const string AppUser = "AUTH_APP";
    public const string AppPassword = "App_Password_2026";

    // Pinned by digest (WP-F5, SEC-ADG-24, CR-ADG-08 d). Update together with the digest recorded in docs/plans (implementation log).
    public const string Image = "gvenzl/oracle-free:23-slim-faststart@sha256:d86d09794ae138a8951e97d7ee010778dcd43088e8d0f83a51002abab8c6d7fd";

    private readonly OracleContainer _container = new OracleBuilder(Image)
        .WithPassword("Oracle_Password_2026")
        .Build();

    public string AdminConnectionString => _container.GetConnectionString();

    public string AppConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        string dataSource = $"{_container.Hostname}:{_container.GetMappedPublicPort(1521)}/FREEPDB1";
        AppConnectionString = $"User Id={AppUser};Password={AppPassword};Data Source={dataSource};Pooling=true;Max Pool Size=1;Min Pool Size=0";

        // The builder default user is not a DBA; SYSTEM uses the ORACLE_PASSWORD of the image.
        await using var admin = new OracleConnection($"User Id=system;Password=Oracle_Password_2026;Data Source={dataSource}");
        await admin.OpenAsync();
        foreach (var statement in new[]
        {
            $"CREATE USER {AppUser} IDENTIFIED BY {AppPassword} QUOTA UNLIMITED ON USERS",
            $"GRANT CREATE SESSION, ALTER SESSION, CREATE TABLE, CREATE SEQUENCE TO {AppUser}",
            $"""
            CREATE TABLE {AppUser}.ORDERS (
                ID NUMBER(10) PRIMARY KEY,
                TENANT_ID VARCHAR2(64) NOT NULL,
                REGION VARCHAR2(20) NOT NULL,
                STATUS VARCHAR2(20) NOT NULL,
                AMOUNT NUMBER(18,2) NOT NULL,
                EMAIL VARCHAR2(200))
            """,
            $"CREATE TABLE {AppUser}.ENTITLEMENTS (ID NUMBER(10) PRIMARY KEY, TENANT_ID VARCHAR2(64) NOT NULL, ORDER_ID NUMBER(10) NOT NULL)",
            $"""
            INSERT ALL
                INTO {AppUser}.ORDERS VALUES (1, 'acme',  'EU', 'open',   10.50, 'alice.smith@acme.example')
                INTO {AppUser}.ORDERS VALUES (2, 'acme',  'US', 'open',   20.00, 'bob.jones@acme.example')
                INTO {AppUser}.ORDERS VALUES (3, 'ACME',  'EU', 'open',   30.00, 'upper.case@ACME.example')
                INTO {AppUser}.ORDERS VALUES (4, 'ACME',  'US', 'closed', 40.00, 'upper.us@ACME.example')
                INTO {AppUser}.ORDERS VALUES (5, 'other', 'EU', 'open',   50.00, 'carol@other.example')
                INTO {AppUser}.ORDERS VALUES (6, 'acme',  'EU', 'closed', 60.00, NULL)
            SELECT 1 FROM DUAL
            """,
            $"""
            INSERT ALL
                INTO {AppUser}.ENTITLEMENTS VALUES (1, 'acme', 1)
                INTO {AppUser}.ENTITLEMENTS VALUES (2, 'other', 2)
                INTO {AppUser}.ENTITLEMENTS VALUES (3, 'acme', 6)
                INTO {AppUser}.ENTITLEMENTS VALUES (4, 'ACME', 3)
            SELECT 1 FROM DUAL
            """,
            // Issue #277: a hierarchy (ORG) and a cyclic parent chain (CYC) for the recursive CTE tests.
            $"CREATE TABLE {AppUser}.ORG (ID NUMBER(10) PRIMARY KEY, TENANT_ID VARCHAR2(64) NOT NULL, PARENT_ID NUMBER(10))",
            $"""
            INSERT ALL
                INTO {AppUser}.ORG VALUES (1, 'acme', NULL)
                INTO {AppUser}.ORG VALUES (2, 'acme', 1)
                INTO {AppUser}.ORG VALUES (3, 'acme', 1)
                INTO {AppUser}.ORG VALUES (4, 'acme', 2)
                INTO {AppUser}.ORG VALUES (5, 'other', NULL)
                INTO {AppUser}.ORG VALUES (6, 'other', 5)
            SELECT 1 FROM DUAL
            """,
            $"CREATE TABLE {AppUser}.CYC (ID NUMBER(10) PRIMARY KEY, TENANT_ID VARCHAR2(64) NOT NULL, PARENT_ID NUMBER(10))",
            $"""
            INSERT ALL
                INTO {AppUser}.CYC VALUES (1, 'acme', 2)
                INTO {AppUser}.CYC VALUES (2, 'acme', 1)
            SELECT 1 FROM DUAL
            """,
            // X1: the dialect end-to-end scenarios (GovernedDialectEndToEndTests) run DML, so they own a table.
            $"CREATE TABLE {AppUser}.X1_ORDERS (ID NUMBER(10) PRIMARY KEY, TENANT_ID VARCHAR2(64) NOT NULL, REGION VARCHAR2(20) NOT NULL, EMAIL VARCHAR2(200))",
            $"""
            INSERT ALL
                INTO {AppUser}.X1_ORDERS VALUES (1, 'acme', 'EU', 'alice@x.example')
                INTO {AppUser}.X1_ORDERS VALUES (2, 'acme', 'US', 'bob@x.example')
                INTO {AppUser}.X1_ORDERS VALUES (3, 'acme', 'EU', 'cara@x.example')
                INTO {AppUser}.X1_ORDERS VALUES (5, 'other', 'EU', 'dan@x.example')
                INTO {AppUser}.X1_ORDERS VALUES (6, 'ACME', 'EU', 'eve@x.example')
            SELECT 1 FROM DUAL
            """,
            "COMMIT"
        })
        {
            await using var cmd = admin.CreateCommand();
            cmd.CommandText = statement;
            try { await cmd.ExecuteNonQueryAsync(); } catch (OracleException ex) { throw new InvalidOperationException("Fixture statement failed: " + statement.Split((char)10)[0] + " => " + ex.Message, ex); }
        }

        // Hostile database defaults: every session of the application user starts case-insensitive and linguistic. The trigger
        // owner needs the privileges directly (roles do not apply inside definer-rights PL/SQL).
        await using (var grant = admin.CreateCommand())
        {
            grant.CommandText = "GRANT ALTER SESSION, ADMINISTER DATABASE TRIGGER TO SYSTEM";
            try { await grant.ExecuteNonQueryAsync(); } catch (OracleException ex) { throw new InvalidOperationException("Fixture GRANT failed => " + ex.Message, ex); }
        }

        await using (var trigger = admin.CreateCommand())
        {
            trigger.CommandText = $"""
                CREATE OR REPLACE TRIGGER SYSTEM.AUTH_APP_HOSTILE_LOGON AFTER LOGON ON {AppUser}.SCHEMA
                BEGIN
                  EXECUTE IMMEDIATE 'ALTER SESSION SET NLS_COMP = LINGUISTIC';
                  EXECUTE IMMEDIATE 'ALTER SESSION SET NLS_SORT = BINARY_CI';
                END;
                """;
            try { await trigger.ExecuteNonQueryAsync(); } catch (OracleException ex) { throw new InvalidOperationException("Fixture TRIGGER failed => " + ex.Message, ex); }
        }
    }

    /// <summary>
    /// Opens a raw, unpooled session (the hostile logon trigger applies, no gateway pinning) and returns a scalar count. Raw driver
    /// use is limited to this fixture (architecture test), so tests can still prove that the database defaults are hostile.
    /// </summary>
    public async Task<int> RawHostileSessionCountAsync(string sql)
    {
        await using var raw = new OracleConnection(AppConnectionString + ";Pooling=false");
        await raw.OpenAsync();
        await using var probe = new OracleCommand(sql, raw);
        return Convert.ToInt32(await probe.ExecuteScalarAsync());
    }

    public async Task DisposeAsync()
    {
        OracleConnection.ClearAllPools();
        await _container.DisposeAsync();
    }
}

/// <summary>The real gateway host configured with the Oracle Free application account as data source <c>oracle</c>.</summary>
internal static class OracleGatewayHost
{
    public const string Source = "oracle";

    public static WebApplicationFactory<Program> Create(AstCompilerOracleFixture fixture, string tag) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:Insecure:danger_allow_anonymous_access", "true");
            builder.UseSetting("Gateway:Insecure:danger_bypass_consent_checks", "true");
            builder.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", $"Data Source=gov-{tag}-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
            builder.UseSetting("Gateway:WebSql:Enabled", "true");
            builder.UseSetting("Gateway:WebSql:DefaultDataSourceName", Source);
            builder.UseSetting($"Gateway:DataSources:Connections:{Source}:Provider", "Oracle");
            builder.UseSetting($"Gateway:DataSources:Connections:{Source}:ConnectionString", fixture.AppConnectionString);
            builder.UseSetting($"Gateway:DataSources:Connections:{Source}:DefaultSchema", AstCompilerOracleFixture.AppUser);
            builder.UseSetting($"Gateway:DataSources:Connections:{Source}:RequireDefaultSchema", "true");
        });
}
