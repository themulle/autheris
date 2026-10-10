namespace Autheris.Tests.Integration;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using Xunit;

/// <summary>
/// WP-A7 execution evidence on a real SQL Server (Testcontainers or AUTHERIS_TEST_MSSQL): INSERT, UPDATE, DELETE and MERGE
/// compiled by <c>ISqlEngine.Compile</c> never reach another tenant's rows, always write the caller's tenant (byte-exact on a
/// case-insensitive column collation, B-1), keep policy and masked columns protected and reject unfiltered writes. Every test
/// runs in its own schema, so the data mutation of one test is invisible to the others.
/// </summary>
public sealed class AstCompilerSqlServerDmlExecutionTests : IClassFixture<AstCompilerSqlServerFixture>, IAsyncLifetime
{
    private readonly SqlServerTestDatabase _db;
    private readonly string _schema = "dml_" + Guid.NewGuid().ToString("N")[..12];
    private readonly FastSqlEngine _engine = new();
    private readonly SqlServerCompiledSqlBinder _binder = new();
    private readonly Policies _policies = new();
    private TableIdentity _orders;
    private TableIdentity _entitlements;

    public AstCompilerSqlServerDmlExecutionTests(AstCompilerSqlServerFixture fixture)
    {
        _db = fixture.Db!;
        _orders = new TableIdentity(_schema, "Orders");
        _entitlements = new TableIdentity(_schema, "Entitlements");
    }

    public async Task InitializeAsync()
    {
        if (!Available()) return;
        await using var conn = new SqlConnection(_db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            CREATE SCHEMA [{_schema}];
            """;
        await cmd.ExecuteNonQueryAsync();
        cmd.CommandText = $"""
            CREATE TABLE [{_schema}].Orders (
                Id int NOT NULL PRIMARY KEY,
                TenantId nvarchar(64) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
                Region nvarchar(20) NOT NULL,
                Status nvarchar(20) NOT NULL,
                Amount decimal(18,2) NOT NULL,
                Email nvarchar(200) NULL);
            CREATE TABLE [{_schema}].Entitlements (
                Id int NOT NULL PRIMARY KEY,
                TenantId nvarchar(64) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
                OrderId int NOT NULL);
            INSERT [{_schema}].Orders VALUES
                (1, N'acme',  N'EU', N'open',   10.50, N'alice.smith@acme.example'),
                (2, N'acme',  N'US', N'open',   20.00, N'bob.jones@acme.example'),
                (3, N'ACME',  N'EU', N'open',   30.00, N'upper.case@ACME.example'),
                (4, N'ACME',  N'US', N'closed', 40.00, N'upper.us@ACME.example'),
                (5, N'other', N'EU', N'open',   50.00, N'carol@other.example'),
                (6, N'acme',  N'EU', N'closed', 60.00, NULL);
            INSERT [{_schema}].Entitlements VALUES (1, N'acme', 1), (2, N'other', 2), (3, N'acme', 6), (4, N'ACME', 3);
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private bool Available()
    {
        if (_db.IsAvailable) return true;
        if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
        {
            throw new Xunit.Sdk.XunitException("The SQL Server container is unavailable on CI; the DML execution evidence cannot be skipped.");
        }

        return false;
    }

    // ---- fixtures ----

    private InMemoryTableCatalog Catalog() => new(new[]
    {
        new TableCatalogEntry(_orders, ImmutableArray.Create(
            new CatalogColumn("Id", "int"), new CatalogColumn("TenantId", "nvarchar(64)"), new CatalogColumn("Region", "nvarchar(20)"),
            new CatalogColumn("Status", "nvarchar(20)"), new CatalogColumn("Amount", "decimal(18,2)"), new CatalogColumn("Email", "nvarchar(200)")),
            "TenantId", 1),
        new TableCatalogEntry(_entitlements, ImmutableArray.Create(
            new CatalogColumn("Id", "int"), new CatalogColumn("TenantId", "nvarchar(64)"), new CatalogColumn("OrderId", "int")),
            "TenantId", 1)
    }, _schema);

    private sealed class Policies : IPolicyPredicateProvider, IColumnMaskProvider
    {
        public Dictionary<TableIdentity, PolicyPredicate> Predicates { get; } = new();
        public Dictionary<(TableIdentity, string), MaskSpec> Masks { get; } = new();
        public bool ShouldApplyPolicy(TableIdentity table) => Predicates.ContainsKey(table);
        public PolicyPredicate GetPredicate(TableIdentity table) => Predicates.TryGetValue(table, out var p) ? p : PolicyPredicate.DenyAll;
        public bool HasMask(TableIdentity table, string column) => Masks.ContainsKey((table, column.ToLowerInvariant()));
        public MaskSpec GetMask(TableIdentity table, string column) => Masks[(table, column.ToLowerInvariant())];
    }

    private static PolicyPredicate RegionPolicy(string region) => PolicyPredicate.Create(
        new BinaryExpression(
            new ColumnReference(new SqlQualifiedName(new[] { new SqlIdentifier("Region", true) })),
            BinaryOperator.Equal, new PolicyParameterExpression("__pol_region", SqlParameterType.String)),
        new Dictionary<string, PolicyValue> { ["__pol_region"] = new(region, SqlParameterType.String) });

    private void MaskEmail() => _policies.Masks[(_orders, "email")] = new MaskSpec(
        MaskKind.Redact,
        new MaskArguments(Constant: new PolicyParameterExpression("__mask_email", SqlParameterType.String, ParameterOrigin.Mask)),
        new Dictionary<string, PolicyValue> { ["__mask_email"] = new("[REDACTED]", SqlParameterType.String) }.ToFrozenDictionary());

    private CompileRequest Request(string tenant) => new()
    {
        TargetDialect = TargetSqlDialect.SqlServer,
        TokenGuards = SqlTokenSecurityOptions.Strict,
        Statements = StatementPermissions.Insert | StatementPermissions.Update | StatementPermissions.Delete | StatementPermissions.Merge,
        Policy = new GovernancePolicy
        {
            RowFilters = _policies,
            Masks = _policies,
            Catalog = Catalog(),
            Tenant = new TenantBinding("__autheris_tenant", tenant, SqlParameterType.String)
        }
    };

    private string Q(string table) => $"{_schema}.{table}";

    /// <summary>Compiles through the governed path and runs the statement; returns the affected row count.</summary>
    private async Task<int> ExecAsync(string sql, string tenant)
    {
        var compiled = _engine.Compile(sql.AsMemory(), Request(tenant), CancellationToken.None);
        await using var conn = new SqlConnection(_db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        _binder.Bind(cmd, compiled, new Dictionary<string, object?>());
        return await cmd.ExecuteNonQueryAsync();
    }

    private async Task<List<object?[]>> RawAsync(string sql)
    {
        await using var conn = new SqlConnection(_db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var rows = new List<object?[]>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new object?[reader.FieldCount];
            reader.GetValues(row);
            rows.Add(row.Select(v => v is DBNull ? null : v).ToArray());
        }

        return rows;
    }

    /// <summary>Id, TenantId (compared as bytes, so 'acme' and 'ACME' differ), Status of every Orders row.</summary>
    private async Task<List<(int Id, string Tenant, string Status)>> OrdersAsync() =>
        (await RawAsync($"SELECT Id, CAST(TenantId AS varchar(64)) COLLATE Latin1_General_BIN, Status FROM {Q("Orders")} ORDER BY Id"))
        .Select(r => (Convert.ToInt32(r[0]), (string)r[1]!, (string)r[2]!)).ToList();

    private async Task<string> SnapshotAsync() =>
        string.Join("|", (await RawAsync($"SELECT Id, TenantId, Region, Status, Amount, Email FROM {Q("Orders")} ORDER BY Id"))
            .Select(r => string.Join(",", r)));

    // ---- UPDATE ----

    [Fact]
    public async Task Update_AffectsOnlyTheCallersTenant_NotTheCaseVariantNorTheOtherTenant()
    {
        if (!Available()) return;
        (await ExecAsync($"UPDATE {Q("Orders")} SET status = 'changed' WHERE id > 0", "acme")).ShouldBe(3);   // 1, 2, 6

        var rows = await OrdersAsync();
        rows.Where(r => r.Status == "changed").Select(r => r.Id).ShouldBe(new[] { 1, 2, 6 });
        rows.Single(r => r.Id == 3).Status.ShouldBe("open");     // ACME is a different tenant (B-1)
        rows.Single(r => r.Id == 4).Status.ShouldBe("closed");
        rows.Single(r => r.Id == 5).Status.ShouldBe("open");     // other

        (await ExecAsync($"UPDATE {Q("Orders")} SET status = 'upper' WHERE id > 0", "ACME")).ShouldBe(2);   // 3, 4
        (await OrdersAsync()).Where(r => r.Status == "upper").Select(r => r.Id).ShouldBe(new[] { 3, 4 });
    }

    [Fact]
    public async Task Update_OfAnotherTenantsRowById_ChangesNothing()
    {
        if (!Available()) return;
        var before = await SnapshotAsync();
        (await ExecAsync($"UPDATE {Q("Orders")} SET status = 'hijacked' WHERE id = 5", "acme")).ShouldBe(0);
        (await SnapshotAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task Update_WithAnOrTautology_StillOnlyAffectsTheCallersRows()
    {
        if (!Available()) return;
        (await ExecAsync($"UPDATE {Q("Orders")} SET status = 'x' WHERE id = 5 OR status = 'open' OR status = 'closed'", "acme")).ShouldBe(3);
        (await OrdersAsync()).Single(r => r.Id == 5).Status.ShouldBe("open");
    }

    [Fact]
    public async Task Update_RowPolicy_IsAndedOntoTheTenant()
    {
        if (!Available()) return;
        _policies.Predicates[_orders] = RegionPolicy("EU");
        (await ExecAsync($"UPDATE {Q("Orders")} SET status = 'eu' WHERE id > 0", "acme")).ShouldBe(2);   // 1 and 6 (EU), not 2 (US)
        (await OrdersAsync()).Where(r => r.Status == "eu").Select(r => r.Id).ShouldBe(new[] { 1, 6 });
    }

    [Fact]
    public async Task Update_PolicyColumnAssignment_TenantAssignment_AndMaskedColumn_AreRejected_NothingChanges()
    {
        if (!Available()) return;
        _policies.Predicates[_orders] = RegionPolicy("EU");
        MaskEmail();
        var before = await SnapshotAsync();
        await Should.ThrowAsync<SecurityException>(() => ExecAsync($"UPDATE {Q("Orders")} SET region = 'US' WHERE id = 1", "acme"));
        await Should.ThrowAsync<SecurityException>(() => ExecAsync($"UPDATE {Q("Orders")} SET tenantid = 'other' WHERE id = 1", "acme"));
        await Should.ThrowAsync<SecurityException>(() => ExecAsync($"UPDATE {Q("Orders")} SET email = 'x' WHERE id = 1", "acme"));
        await Should.ThrowAsync<SecurityException>(() => ExecAsync($"UPDATE {Q("Orders")} SET status = email WHERE id = 1", "acme"));
        await Should.ThrowAsync<SecurityException>(() => ExecAsync($"UPDATE {Q("Orders")} SET status = 'x' WHERE email LIKE 'alice%'", "acme"));
        (await SnapshotAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task Update_Unfiltered_IsRejected_NothingChanges()
    {
        if (!Available()) return;
        var before = await SnapshotAsync();
        await Should.ThrowAsync<UnfilteredDmlException>(() => ExecAsync($"UPDATE {Q("Orders")} SET status = 'x'", "acme"));
        await Should.ThrowAsync<UnfilteredDmlException>(() => ExecAsync($"UPDATE {Q("Orders")} SET status = 'x' WHERE 1 = 1", "acme"));
        await Should.ThrowAsync<UnfilteredDmlException>(() => ExecAsync($"UPDATE {Q("Orders")} SET status = 'x' WHERE id = 1 OR 1 = 1", "acme"));
        (await SnapshotAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task Update_SubqueryInWhere_NeverSeesTheOtherTenantsRows()
    {
        if (!Available()) return;
        // entitlement 2 (tenant other) points at order 2 (tenant acme): acme must not be able to use it
        (await ExecAsync($"UPDATE {Q("Orders")} SET status = 'viaSub' WHERE id IN (SELECT orderid FROM {Q("Entitlements")})", "acme")).ShouldBe(2);   // orders 1 and 6
        (await OrdersAsync()).Where(r => r.Status == "viaSub").Select(r => r.Id).ShouldBe(new[] { 1, 6 });
    }

    // ---- DELETE ----

    [Fact]
    public async Task Delete_AffectsOnlyTheCallersTenant()
    {
        if (!Available()) return;
        (await ExecAsync($"DELETE FROM {Q("Orders")} WHERE id > 0", "acme")).ShouldBe(3);
        (await OrdersAsync()).Select(r => r.Id).ShouldBe(new[] { 3, 4, 5 });
        (await ExecAsync($"DELETE FROM {Q("Orders")} WHERE id = 5", "acme")).ShouldBe(0);
        (await ExecAsync($"DELETE FROM {Q("Orders")} WHERE id > 0", "ACME")).ShouldBe(2);
        (await OrdersAsync()).Select(r => r.Id).ShouldBe(new[] { 5 });
    }

    [Fact]
    public async Task Delete_PolicyAndSubquery_AreApplied_UnfilteredIsRejected()
    {
        if (!Available()) return;
        _policies.Predicates[_orders] = RegionPolicy("EU");
        (await ExecAsync($"DELETE FROM {Q("Orders")} WHERE id IN (SELECT orderid FROM {Q("Entitlements")})", "acme")).ShouldBe(2);   // 1 and 6, both EU
        var before = await SnapshotAsync();
        await Should.ThrowAsync<UnfilteredDmlException>(() => ExecAsync($"DELETE FROM {Q("Orders")}", "acme"));
        await Should.ThrowAsync<UnfilteredDmlException>(() => ExecAsync($"DELETE FROM {Q("Orders")} WHERE 1 = 1", "acme"));
        (await SnapshotAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task Delete_DenyAllPolicy_DeletesNothing()
    {
        if (!Available()) return;
        _policies.Predicates[_orders] = PolicyPredicate.DenyAll;
        var before = await SnapshotAsync();
        (await ExecAsync($"DELETE FROM {Q("Orders")} WHERE id > 0 OR status = 'open'", "acme")).ShouldBe(0);
        (await SnapshotAsync()).ShouldBe(before);
    }

    // ---- INSERT ----

    [Fact]
    public async Task Insert_AlwaysWritesTheCallersTenant_Exactly()
    {
        if (!Available()) return;
        (await ExecAsync($"INSERT INTO {Q("Orders")} (id, tenantid, region, status, amount) VALUES (10, 'acme', 'EU', 'new', 1.5), (11, 'acme', 'US', 'new', 2.5)", "acme")).ShouldBe(2);
        var rows = await OrdersAsync();
        rows.Single(r => r.Id == 10).Tenant.ShouldBe("acme");
        rows.Single(r => r.Id == 11).Tenant.ShouldBe("acme");

        // the case variant is a different tenant: ACME can write ACME, and cannot write 'acme'
        (await ExecAsync($"INSERT INTO {Q("Orders")} (id, tenantid, region, status, amount) VALUES (12, 'ACME', 'EU', 'new', 1.5)", "ACME")).ShouldBe(1);
        (await OrdersAsync()).Single(r => r.Id == 12).Tenant.ShouldBe("ACME");
        await Should.ThrowAsync<SecurityException>(() => ExecAsync($"INSERT INTO {Q("Orders")} (id, tenantid, region, status, amount) VALUES (13, 'acme', 'EU', 'new', 1.5)", "ACME"));
    }

    [Fact]
    public async Task Insert_ForeignTenantOrNonLiteralTenant_IsRejected_NothingIsWritten()
    {
        if (!Available()) return;
        var before = await SnapshotAsync();
        await Should.ThrowAsync<SecurityException>(() => ExecAsync($"INSERT INTO {Q("Orders")} (id, tenantid, region, status, amount) VALUES (20, 'other', 'EU', 'x', 1)", "acme"));
        await Should.ThrowAsync<SecurityException>(() => ExecAsync($"INSERT INTO {Q("Orders")} (id, tenantid, region, status, amount) VALUES (21, 'acme', 'EU', 'x', 1), (22, 'other', 'EU', 'x', 1)", "acme"));
        await Should.ThrowAsync<SecurityException>(() => ExecAsync($"INSERT INTO {Q("Orders")} (id, tenantid, region, status, amount) VALUES (23, upper('acme'), 'EU', 'x', 1)", "acme"));
        await Should.ThrowAsync<SecurityException>(() => ExecAsync($"INSERT INTO {Q("Orders")} (id, region, status, amount) VALUES (24, 'EU', 'x', 1)", "acme"));
        (await SnapshotAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task InsertSelect_CopiesOnlyTheCallersSourceRows_AsTheCallersTenant()
    {
        if (!Available()) return;
        (await ExecAsync(
            $"INSERT INTO {Q("Orders")} (id, tenantid, region, status, amount) SELECT id + 100, 'acme', 'EU', 'copied', 1 FROM {Q("Entitlements")}", "acme")).ShouldBe(2);   // entitlements 1 and 3
        var rows = await OrdersAsync();
        rows.Where(r => r.Status == "copied").Select(r => (r.Id, r.Tenant)).ShouldBe(new[] { (101, "acme"), (103, "acme") });
    }

    [Fact]
    public async Task InsertSelect_SourceTenantColumnOrWildcard_IsRejected()
    {
        if (!Available()) return;
        var before = await SnapshotAsync();
        await Should.ThrowAsync<SecurityException>(() => ExecAsync($"INSERT INTO {Q("Orders")} (id, tenantid, region, status, amount) SELECT id + 100, tenantid, 'EU', 'x', 1 FROM {Q("Entitlements")}", "acme"));
        await Should.ThrowAsync<SecurityException>(() => ExecAsync($"INSERT INTO {Q("Orders")} (id, tenantid) SELECT * FROM {Q("Orders")}", "acme"));
        (await SnapshotAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task InsertSelect_MaskedSourceColumn_InsertsTheMaskedValueOnly()
    {
        if (!Available()) return;
        MaskEmail();
        // reading a masked column as the source of a written (unmasked) column yields the mask, never the clear text
        (await ExecAsync(
            $"INSERT INTO {Q("Orders")} (id, tenantid, region, status, amount) SELECT id + 300, 'acme', 'EU', email, 1 FROM {Q("Orders")} WHERE id = 1", "acme")).ShouldBe(1);
        (await OrdersAsync()).Single(r => r.Id == 301).Status.ShouldBe("[REDACTED]");
        await Should.ThrowAsync<SecurityException>(() => ExecAsync($"INSERT INTO {Q("Orders")} (id, tenantid, region, status, amount, email) VALUES (400, 'acme', 'EU', 'x', 1, 'w@x.y')", "acme"));
    }

    [Fact]
    public async Task Insert_IntoATableWithARowPolicy_IsRejected()
    {
        if (!Available()) return;
        _policies.Predicates[_orders] = RegionPolicy("EU");
        await Should.ThrowAsync<SecurityException>(() => ExecAsync($"INSERT INTO {Q("Orders")} (id, tenantid, region, status, amount) VALUES (30, 'acme', 'US', 'x', 1)", "acme"));
    }

    [Fact]
    public async Task Insert_DuplicateKeyOfAnotherTenant_FailsAsAnSqlError_WithoutBeingExecutedAsAnOverwrite()
    {
        if (!Available()) return;
        // R-12: the key of tenant other's row is visible only as a constraint violation; the existing row is never changed.
        var before = await SnapshotAsync();
        await Should.ThrowAsync<SqlException>(() => ExecAsync($"INSERT INTO {Q("Orders")} (id, tenantid, region, status, amount) VALUES (5, 'acme', 'EU', 'x', 1)", "acme"));
        (await SnapshotAsync()).ShouldBe(before);
    }

    // ---- MERGE ----

    [Fact]
    public async Task Merge_OtherTenantRowWithSameKey_IsNeverMatched_AndNeverDeletedOrUpdated()
    {
        if (!Available()) return;
        // entitlement 2 belongs to tenant other and points at order 2, which belongs to acme.
        var before = await SnapshotAsync();
        (await ExecAsync($"MERGE INTO {Q("Orders")} t USING {Q("Entitlements")} s ON t.id = s.orderid WHEN MATCHED THEN DELETE", "other")).ShouldBe(0);
        (await ExecAsync($"MERGE INTO {Q("Orders")} t USING {Q("Entitlements")} s ON t.id = s.orderid WHEN MATCHED THEN UPDATE SET status = 'merged'", "other")).ShouldBe(0);
        (await SnapshotAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task Merge_UpdateAndDelete_TouchOnlyTheCallersRows_AndSourceIsSecured()
    {
        if (!Available()) return;
        // as acme: source entitlements are 1 (order 1) and 3 (order 6); entitlement 2 of tenant other is invisible
        (await ExecAsync(
            $"MERGE INTO {Q("Orders")} t USING {Q("Entitlements")} s ON t.id = s.orderid " +
            "WHEN MATCHED AND s.id = 1 THEN UPDATE SET status = 'merged' WHEN MATCHED AND s.id = 3 THEN DELETE", "acme")).ShouldBe(2);
        var rows = await OrdersAsync();
        rows.Single(r => r.Id == 1).Status.ShouldBe("merged");
        rows.Any(r => r.Id == 6).ShouldBeFalse();
        rows.Single(r => r.Id == 2).Status.ShouldBe("open");   // matched only through other's entitlement: untouched
        rows.Single(r => r.Id == 3).Status.ShouldBe("open");   // ACME: untouched
    }

    [Fact]
    public async Task Merge_Insert_WritesTheCallersTenant_NeverTheSourceTenant()
    {
        if (!Available()) return;
        (await ExecAsync(
            $"MERGE INTO {Q("Orders")} t USING (SELECT id + 500 AS nid FROM {Q("Entitlements")}) s ON t.id = s.nid " +
            "WHEN NOT MATCHED THEN INSERT (id, tenantid, region, status, amount) VALUES (s.nid, 'acme', 'EU', 'inserted', 1)", "acme")).ShouldBe(2);   // entitlements 1 and 3 of acme
        var rows = await OrdersAsync();
        rows.Where(r => r.Status == "inserted").Select(r => (r.Id, r.Tenant)).ShouldBe(new[] { (501, "acme"), (503, "acme") });
        await Should.ThrowAsync<SecurityException>(() => ExecAsync(
            $"MERGE INTO {Q("Orders")} t USING {Q("Entitlements")} s ON t.id = s.orderid " +
            "WHEN NOT MATCHED THEN INSERT (id, tenantid, region, status, amount) VALUES (s.id + 600, s.tenantid, 'EU', 'x', 1)", "acme"));
    }

    [Fact]
    public async Task Merge_PolicyColumn_TenantColumn_MaskedColumn_AndTrivialOn_AreRejected_NothingChanges()
    {
        if (!Available()) return;
        _policies.Predicates[_orders] = RegionPolicy("EU");
        MaskEmail();
        var before = await SnapshotAsync();
        await Should.ThrowAsync<SecurityException>(() => ExecAsync($"MERGE INTO {Q("Orders")} t USING {Q("Entitlements")} s ON t.id = s.orderid WHEN MATCHED THEN UPDATE SET region = 'US'", "acme"));
        await Should.ThrowAsync<SecurityException>(() => ExecAsync($"MERGE INTO {Q("Orders")} t USING {Q("Entitlements")} s ON t.id = s.orderid WHEN MATCHED THEN UPDATE SET tenantid = 'other'", "acme"));
        await Should.ThrowAsync<SecurityException>(() => ExecAsync($"MERGE INTO {Q("Orders")} t USING {Q("Entitlements")} s ON t.id = s.orderid WHEN MATCHED THEN UPDATE SET status = t.email", "acme"));
        await Should.ThrowAsync<SecurityException>(() => ExecAsync($"MERGE INTO {Q("Orders")} t USING {Q("Entitlements")} s ON t.email = 'x' WHEN MATCHED THEN DELETE", "acme"));
        await Should.ThrowAsync<UnfilteredDmlException>(() => ExecAsync($"MERGE INTO {Q("Orders")} t USING {Q("Entitlements")} s ON 1 = 1 WHEN MATCHED THEN DELETE", "acme"));
        (await SnapshotAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task Merge_RowPolicy_IsPartOfTheOnCondition()
    {
        if (!Available()) return;
        _policies.Predicates[_orders] = RegionPolicy("EU");
        // acme sees orders 1, 2 and 6 through its entitlements 1 and 3: only the EU rows 1 and 6 can match
        (await ExecAsync($"MERGE INTO {Q("Orders")} t USING {Q("Entitlements")} s ON t.id = s.orderid WHEN MATCHED THEN UPDATE SET status = 'eu'", "acme")).ShouldBe(2);
        (await OrdersAsync()).Where(r => r.Status == "eu").Select(r => r.Id).ShouldBe(new[] { 1, 6 });
    }

    [Fact]
    public async Task Merge_NotMatchedBySource_CannotBeExpressed_SoOtherTenantsRowsSurvive()
    {
        if (!Available()) return;
        var before = await SnapshotAsync();
        await Should.ThrowAsync<Exception>(() => ExecAsync($"MERGE INTO {Q("Orders")} t USING {Q("Entitlements")} s ON t.id = s.orderid WHEN NOT MATCHED BY SOURCE THEN DELETE", "acme"));
        (await SnapshotAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task ReturningAndOutput_AreRejectedBeforeAnyStatementReachesTheDatabase()
    {
        if (!Available()) return;
        var before = await SnapshotAsync();
        await Should.ThrowAsync<Exception>(() => ExecAsync($"DELETE FROM {Q("Orders")} WHERE id = 1 RETURNING *", "acme"));
        await Should.ThrowAsync<Exception>(() => ExecAsync($"DELETE FROM {Q("Orders")} OUTPUT deleted.* WHERE id = 1", "acme"));
        await Should.ThrowAsync<Exception>(() => ExecAsync($"UPDATE {Q("Orders")} SET status = 'x' OUTPUT inserted.* WHERE id = 1", "acme"));
        (await SnapshotAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task PlanCache_RebindsTheTenantOnADmlHit()
    {
        if (!Available()) return;
        (await ExecAsync($"UPDATE {Q("Orders")} SET status = 'c' WHERE id > 0", "acme")).ShouldBe(3);
        (await ExecAsync($"UPDATE {Q("Orders")} SET status = 'c' WHERE id > 0", "other")).ShouldBe(1);
        (await ExecAsync($"UPDATE {Q("Orders")} SET status = 'c' WHERE id > 0", "ACME")).ShouldBe(2);
        _engine.CompileCache.Stats.Hits.ShouldBe(2);
    }
}
