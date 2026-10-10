namespace Autheris.Tests.Integration;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using Xunit;

/// <summary>
/// WP-A7 execution contract, run on every dialect that lists DML (SQL Server, PostgreSQL and Oracle containers, in-process
/// DuckDB, the Spark/Delta proxy). The scenarios are the acceptance criteria of the track: tenant A can neither update, delete nor
/// merge tenant B's rows, an inserted row carries the caller's tenant exactly (case variants are different tenants on a
/// case-insensitive column, B-1), a policy-column change is rejected, masked columns stay protected, an unfiltered UPDATE or
/// DELETE is rejected, and no MERGE source or target crosses a tenant. Every test starts from its own schema or database.
/// </summary>
public abstract class AstCompilerDmlContract
{
    protected FastSqlEngine Engine { get; } = new();

    protected Policies Policy { get; } = new();

    // ---- dialect plug-in ----

    protected abstract TargetSqlDialect Dialect { get; }

    /// <summary>False when the database is missing (an early return locally, a failure on CI).</summary>
    protected abstract bool Available();

    protected abstract InMemoryTableCatalog Catalog();
    protected abstract TableIdentity OrdersId { get; }
    protected abstract TableIdentity EntitlementsId { get; }

    /// <summary>The catalog spelling of a logical name (Id, TenantId, Region, Status, Amount, Email, OrderId).</summary>
    protected abstract string Canon(string logical);

    protected abstract string Quote(string identifier);

    /// <summary>How a test reads a column back (a provider may need a cast, for example PostgreSQL citext).</summary>
    protected virtual string ReadExpr(string logical) => Quote(Canon(logical));

    /// <summary>A reference to the logical table in a statement the test sends to the database directly.</summary>
    protected abstract string RawTable(string logical);

    /// <summary>A reference to the logical table in the user SQL (unquoted, schema-qualified).</summary>
    protected abstract string UserTable(string logical);

    protected abstract Task<int> ExecuteAsync(CompiledSql compiled);

    /// <summary>
    /// The row-count contract of CR-ADG-35 (the runtime executors adopt it at X1): run the statement in a transaction, compare the
    /// affected count with <c>ExpectedAffectedRows</c> and roll back on any difference with a typed error.
    /// </summary>
    protected abstract Task<int> ExecuteInTransactionAsync(CompiledSql compiled);

    /// <summary>
    /// The shared transaction harness for the ADO.NET dialects: the engine's checked executor (CR-ADG-43), the only path that may
    /// run a statement with a row-count check.
    /// </summary>
    protected static async Task<int> RunCheckedAsync(DbConnection connection, bool ownsConnection, DbCommandCompiledSqlBinder binder, CompiledSql compiled,
        System.Data.IsolationLevel isolation = System.Data.IsolationLevel.ReadCommitted)
    {
        try
        {
            return await new CheckedDmlExecutor(binder).ExecuteAsync(connection, compiled, new Dictionary<string, object?>(), isolation);
        }
        finally
        {
            if (ownsConnection) await connection.DisposeAsync();
        }
    }

    protected abstract Task<List<object?[]>> QueryAsync(string sql);

    protected virtual bool AllowExperimental => false;

    /// <summary>MERGE ... WHEN MATCHED THEN DELETE (Oracle only deletes rows it updated).</summary>
    protected virtual bool SupportsMergeDelete => true;

    protected virtual bool SupportsMerge => true;

    protected virtual bool HasUniqueKey => true;

    /// <summary>False where the engine refuses a subquery in the WHERE of UPDATE or DELETE (open-source Delta: DELTA_UNSUPPORTED_SUBQUERY).</summary>
    protected virtual bool SupportsSubqueryInDmlWhere => true;

    /// <summary>False where the engine returns no row count for INSERT (Delta returns an empty result).</summary>
    protected virtual bool ReportsInsertCount => true;

    // ---- data ----

    protected static readonly (int Id, string Tenant, string Region, string Status, decimal Amount, string? Email)[] OrderRows =
    {
        (1, "acme", "EU", "open", 10.50m, "alice.smith@acme.example"),
        (2, "acme", "US", "open", 20.00m, "bob.jones@acme.example"),
        (3, "ACME", "EU", "open", 30.00m, "upper.case@ACME.example"),
        (4, "ACME", "US", "closed", 40.00m, "upper.us@ACME.example"),
        (5, "other", "EU", "open", 50.00m, "carol@other.example"),
        (6, "acme", "EU", "closed", 60.00m, null)
    };

    protected static readonly (int Id, string Tenant, int OrderId)[] EntitlementRows =
    {
        (1, "acme", 1), (2, "other", 2), (3, "acme", 6), (4, "ACME", 3)
    };

    /// <summary>One single-row INSERT per seed row, with inline literals (the fixture data, not governed SQL).</summary>
    protected IEnumerable<string> SeedStatements()
    {
        string orderColumns = string.Join(", ", new[] { "Id", "TenantId", "Region", "Status", "Amount", "Email" }.Select(c => Quote(Canon(c))));
        foreach (var r in OrderRows)
        {
            string email = r.Email is null ? "NULL" : $"'{r.Email}'";
            yield return $"INSERT INTO {RawTable("Orders")} ({orderColumns}) VALUES ({r.Id}, '{r.Tenant}', '{r.Region}', '{r.Status}', {r.Amount.ToString(CultureInfo.InvariantCulture)}, {email})";
        }

        string entColumns = string.Join(", ", new[] { "Id", "TenantId", "OrderId" }.Select(c => Quote(Canon(c))));
        foreach (var r in EntitlementRows)
        {
            yield return $"INSERT INTO {RawTable("Entitlements")} ({entColumns}) VALUES ({r.Id}, '{r.Tenant}', {r.OrderId})";
        }
    }

    // ---- helpers ----

    protected sealed class Policies : IPolicyPredicateProvider, IColumnMaskProvider
    {
        public Dictionary<TableIdentity, PolicyPredicate> Predicates { get; } = new();
        public Dictionary<(TableIdentity, string), MaskSpec> Masks { get; } = new();
        public bool ShouldApplyPolicy(TableIdentity table) => Predicates.ContainsKey(table);
        public PolicyPredicate GetPredicate(TableIdentity table) => Predicates.TryGetValue(table, out var p) ? p : PolicyPredicate.DenyAll;
        public bool HasMask(TableIdentity table, string column) => Masks.ContainsKey((table, column.ToLowerInvariant()));
        public MaskSpec GetMask(TableIdentity table, string column) => Masks[(table, column.ToLowerInvariant())];
    }

    protected string O => UserTable("Orders");

    protected string E => UserTable("Entitlements");

    protected void RegionEuPolicy() => Policy.Predicates[OrdersId] = PolicyPredicate.Create(
        new BinaryExpression(
            new ColumnReference(new SqlQualifiedName(new[] { new SqlIdentifier(Canon("Region"), true) })),
            BinaryOperator.Equal, new PolicyParameterExpression("__pol_region", SqlParameterType.String)),
        new Dictionary<string, PolicyValue> { ["__pol_region"] = new("EU", SqlParameterType.String) });

    protected void MaskEmail() => Policy.Masks[(OrdersId, "email")] = new MaskSpec(
        MaskKind.Redact,
        new MaskArguments(Constant: new PolicyParameterExpression("__mask_email", SqlParameterType.String, ParameterOrigin.Mask)),
        new Dictionary<string, PolicyValue> { ["__mask_email"] = new("[REDACTED]", SqlParameterType.String) }.ToFrozenDictionary());

    protected CompileRequest Request(string tenant) => new()
    {
        TargetDialect = Dialect,
        AllowExperimentalDialect = AllowExperimental,
        TokenGuards = SqlTokenSecurityOptions.Strict,
        Statements = StatementPermissions.Insert | StatementPermissions.Update | StatementPermissions.Delete | StatementPermissions.Merge,
        Policy = new GovernancePolicy
        {
            RowFilters = Policy,
            Masks = Policy,
            Catalog = Catalog(),
            Tenant = new TenantBinding("__autheris_tenant", tenant, SqlParameterType.String)
        }
    };

    /// <summary>Compiles through the governed path and executes; returns the affected row count.</summary>
    protected async Task<int> ExecAsync(string sql, string tenant)
    {
        var compiled = Engine.Compile(sql.AsMemory(), Request(tenant), CancellationToken.None);
        return await ExecuteAsync(compiled);
    }

    /// <summary>Compiles through the governed path and executes with the row-count check (CR-ADG-35).</summary>
    protected async Task<int> ExecCheckedAsync(string sql, string tenant)
    {
        var compiled = Engine.Compile(sql.AsMemory(), Request(tenant), CancellationToken.None);
        compiled.RequiresRowCountCheck.ShouldBeTrue();
        return await ExecuteInTransactionAsync(compiled);
    }

    /// <summary>Runs an INSERT and checks the reported row count where the engine reports one.</summary>
    protected async Task InsertedAsync(string sql, string tenant, int expected)
    {
        int affected = await ExecAsync(sql, tenant);
        if (ReportsInsertCount) affected.ShouldBe(expected);
    }

    protected async Task<List<(int Id, string Tenant, string Status)>> OrdersAsync() =>
        (await QueryAsync($"SELECT {ReadExpr("Id")}, {ReadExpr("TenantId")}, {ReadExpr("Status")} FROM {RawTable("Orders")} ORDER BY {Quote(Canon("Id"))}"))
        .Select(r => (Convert.ToInt32(r[0], CultureInfo.InvariantCulture), (string)r[1]!, (string)r[2]!)).ToList();

    protected async Task<string> SnapshotAsync()
    {
        var columns = string.Join(", ", new[] { "Id", "TenantId", "Region", "Status", "Amount", "Email" }.Select(ReadExpr));
        return string.Join("|", (await QueryAsync($"SELECT {columns} FROM {RawTable("Orders")} ORDER BY {Quote(Canon("Id"))}"))
            .Select(r => string.Join(",", r.Select(v => v is IFormattable f ? f.ToString(null, CultureInfo.InvariantCulture) : v?.ToString()))));
    }

    /// <summary>The compile or the execution is refused (a security rejection before the database, or a database error).</summary>
    protected static Task RejectedAsync(Func<Task> action) => Should.ThrowAsync<Exception>(action);

    protected static Task SecurityRejectedAsync(Func<Task> action) => Should.ThrowAsync<SecurityException>(action);

    // ---- UPDATE ----

    [Fact]
    public async Task Update_AffectsOnlyTheCallersTenant_NotTheCaseVariantNorTheOtherTenant()
    {
        if (!Available()) return;
        (await ExecAsync($"UPDATE {O} SET status = 'changed' WHERE id > 0", "acme")).ShouldBe(3);   // 1, 2, 6

        var rows = await OrdersAsync();
        rows.Where(r => r.Status == "changed").Select(r => r.Id).ShouldBe(new[] { 1, 2, 6 });
        rows.Single(r => r.Id == 3).Status.ShouldBe("open");     // ACME is a different tenant (B-1)
        rows.Single(r => r.Id == 4).Status.ShouldBe("closed");
        rows.Single(r => r.Id == 5).Status.ShouldBe("open");     // other

        (await ExecAsync($"UPDATE {O} SET status = 'upper' WHERE id > 0", "ACME")).ShouldBe(2);   // 3, 4
        (await OrdersAsync()).Where(r => r.Status == "upper").Select(r => r.Id).ShouldBe(new[] { 3, 4 });
    }

    [Fact]
    public async Task Update_OfAnotherTenantsRowById_ChangesNothing()
    {
        if (!Available()) return;
        var before = await SnapshotAsync();
        (await ExecAsync($"UPDATE {O} SET status = 'hijacked' WHERE id = 5", "acme")).ShouldBe(0);
        (await SnapshotAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task Update_WithAnOrTautology_StillOnlyAffectsTheCallersRows()
    {
        if (!Available()) return;
        (await ExecAsync($"UPDATE {O} SET status = 'x' WHERE id = 5 OR status = 'open' OR status = 'closed'", "acme")).ShouldBe(3);
        (await OrdersAsync()).Single(r => r.Id == 5).Status.ShouldBe("open");
    }

    [Fact]
    public async Task Update_RowPolicy_IsAndedOntoTheTenant()
    {
        if (!Available()) return;
        RegionEuPolicy();
        (await ExecAsync($"UPDATE {O} SET status = 'eu' WHERE id > 0", "acme")).ShouldBe(2);   // 1 and 6 (EU), not 2 (US)
        (await OrdersAsync()).Where(r => r.Status == "eu").Select(r => r.Id).ShouldBe(new[] { 1, 6 });
    }

    [Fact]
    public async Task Update_PolicyColumnAssignment_TenantAssignment_AndMaskedColumn_AreRejected_NothingChanges()
    {
        if (!Available()) return;
        RegionEuPolicy();
        MaskEmail();
        var before = await SnapshotAsync();
        await SecurityRejectedAsync(() => ExecAsync($"UPDATE {O} SET region = 'US' WHERE id = 1", "acme"));
        await SecurityRejectedAsync(() => ExecAsync($"UPDATE {O} SET tenantid = 'other' WHERE id = 1", "acme"));
        await SecurityRejectedAsync(() => ExecAsync($"UPDATE {O} SET email = 'x' WHERE id = 1", "acme"));
        await SecurityRejectedAsync(() => ExecAsync($"UPDATE {O} SET status = email WHERE id = 1", "acme"));
        await SecurityRejectedAsync(() => ExecAsync($"UPDATE {O} SET status = 'x' WHERE email LIKE 'alice%'", "acme"));
        (await SnapshotAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task Update_Unfiltered_IsRejected_NothingChanges()
    {
        if (!Available()) return;
        var before = await SnapshotAsync();
        await Should.ThrowAsync<UnfilteredDmlException>(() => ExecAsync($"UPDATE {O} SET status = 'x'", "acme"));
        await Should.ThrowAsync<UnfilteredDmlException>(() => ExecAsync($"UPDATE {O} SET status = 'x' WHERE 1 = 1", "acme"));
        await Should.ThrowAsync<UnfilteredDmlException>(() => ExecAsync($"UPDATE {O} SET status = 'x' WHERE id = 1 OR 1 = 1", "acme"));
        (await SnapshotAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task Update_SubqueryInWhere_NeverSeesTheOtherTenantsRows()
    {
        if (!Available()) return;
        if (!SupportsSubqueryInDmlWhere)
        {
            // the compiler refuses the statement with a typed error (CR-ADG-39), before the backend; nothing is written
            var before = await SnapshotAsync();
            await Should.ThrowAsync<SqlCompileNotSupportedException>(() => ExecAsync($"UPDATE {O} SET status = 'viaSub' WHERE id IN (SELECT orderid FROM {E})", "acme"));
            (await SnapshotAsync()).ShouldBe(before);
            return;
        }

        // entitlement 2 (tenant other) points at order 2 (tenant acme): acme must not be able to use it
        (await ExecAsync($"UPDATE {O} SET status = 'viaSub' WHERE id IN (SELECT orderid FROM {E})", "acme")).ShouldBe(2);   // orders 1 and 6
        (await OrdersAsync()).Where(r => r.Status == "viaSub").Select(r => r.Id).ShouldBe(new[] { 1, 6 });
    }

    // ---- DELETE ----

    [Fact]
    public async Task Delete_AffectsOnlyTheCallersTenant()
    {
        if (!Available()) return;
        (await ExecAsync($"DELETE FROM {O} WHERE id > 0", "acme")).ShouldBe(3);
        (await OrdersAsync()).Select(r => r.Id).ShouldBe(new[] { 3, 4, 5 });
        (await ExecAsync($"DELETE FROM {O} WHERE id = 5", "acme")).ShouldBe(0);
        (await ExecAsync($"DELETE FROM {O} WHERE id > 0", "ACME")).ShouldBe(2);
        (await OrdersAsync()).Select(r => r.Id).ShouldBe(new[] { 5 });
    }

    [Fact]
    public async Task Delete_PolicyAndSubquery_AreApplied_UnfilteredIsRejected()
    {
        if (!Available()) return;
        RegionEuPolicy();
        if (SupportsSubqueryInDmlWhere)
        {
            (await ExecAsync($"DELETE FROM {O} WHERE id IN (SELECT orderid FROM {E})", "acme")).ShouldBe(2);   // 1 and 6, both EU
        }
        else
        {
            (await ExecAsync($"DELETE FROM {O} WHERE id > 5", "acme")).ShouldBe(1);   // only order 6 is EU and acme's and above 5
        }

        var before = await SnapshotAsync();
        await Should.ThrowAsync<UnfilteredDmlException>(() => ExecAsync($"DELETE FROM {O}", "acme"));
        await Should.ThrowAsync<UnfilteredDmlException>(() => ExecAsync($"DELETE FROM {O} WHERE 1 = 1", "acme"));
        (await SnapshotAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task Delete_DenyAllPolicy_DeletesNothing()
    {
        if (!Available()) return;
        Policy.Predicates[OrdersId] = PolicyPredicate.DenyAll;
        var before = await SnapshotAsync();
        (await ExecAsync($"DELETE FROM {O} WHERE id > 0 OR status = 'open'", "acme")).ShouldBe(0);
        (await SnapshotAsync()).ShouldBe(before);
    }

    // ---- INSERT ----

    [Fact]
    public async Task Insert_AlwaysWritesTheCallersTenant_Exactly()
    {
        if (!Available()) return;
        await InsertedAsync($"INSERT INTO {O} (id, tenantid, region, status, amount) VALUES (10, 'acme', 'EU', 'new', 1.5), (11, 'acme', 'US', 'new', 2.5)", "acme", 2);
        var rows = await OrdersAsync();
        rows.Single(r => r.Id == 10).Tenant.ShouldBe("acme");
        rows.Single(r => r.Id == 11).Tenant.ShouldBe("acme");

        // the case variant is a different tenant: ACME can write ACME, and cannot write 'acme'
        await InsertedAsync($"INSERT INTO {O} (id, tenantid, region, status, amount) VALUES (12, 'ACME', 'EU', 'new', 1.5)", "ACME", 1);
        (await OrdersAsync()).Single(r => r.Id == 12).Tenant.ShouldBe("ACME");
        await SecurityRejectedAsync(() => ExecAsync($"INSERT INTO {O} (id, tenantid, region, status, amount) VALUES (13, 'acme', 'EU', 'new', 1.5)", "ACME"));
    }

    [Fact]
    public async Task Insert_ForeignTenantOrNonLiteralTenant_IsRejected_NothingIsWritten()
    {
        if (!Available()) return;
        var before = await SnapshotAsync();
        await SecurityRejectedAsync(() => ExecAsync($"INSERT INTO {O} (id, tenantid, region, status, amount) VALUES (20, 'other', 'EU', 'x', 1)", "acme"));
        await SecurityRejectedAsync(() => ExecAsync($"INSERT INTO {O} (id, tenantid, region, status, amount) VALUES (21, 'acme', 'EU', 'x', 1), (22, 'other', 'EU', 'x', 1)", "acme"));
        await SecurityRejectedAsync(() => ExecAsync($"INSERT INTO {O} (id, tenantid, region, status, amount) VALUES (23, upper('acme'), 'EU', 'x', 1)", "acme"));
        await SecurityRejectedAsync(() => ExecAsync($"INSERT INTO {O} (id, region, status, amount) VALUES (24, 'EU', 'x', 1)", "acme"));
        (await SnapshotAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task InsertSelect_CopiesOnlyTheCallersSourceRows_AsTheCallersTenant()
    {
        if (!Available()) return;
        await InsertedAsync(
            $"INSERT INTO {O} (id, tenantid, region, status, amount) SELECT id + 100, 'acme', 'EU', 'copied', 1 FROM {E}", "acme", 2);   // entitlements 1 and 3
        var rows = await OrdersAsync();
        rows.Where(r => r.Status == "copied").Select(r => (r.Id, r.Tenant)).ShouldBe(new[] { (101, "acme"), (103, "acme") });
    }

    [Fact]
    public async Task InsertSelect_SourceTenantColumnOrWildcard_IsRejected()
    {
        if (!Available()) return;
        var before = await SnapshotAsync();
        await SecurityRejectedAsync(() => ExecAsync($"INSERT INTO {O} (id, tenantid, region, status, amount) SELECT id + 100, tenantid, 'EU', 'x', 1 FROM {E}", "acme"));
        await SecurityRejectedAsync(() => ExecAsync($"INSERT INTO {O} (id, tenantid) SELECT * FROM {O}", "acme"));
        (await SnapshotAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task InsertSelect_MaskedSourceColumn_InsertsTheMaskedValueOnly()
    {
        if (!Available()) return;
        MaskEmail();
        // reading a masked column as the source of a written (unmasked) column yields the mask, never the clear text
        await InsertedAsync(
            $"INSERT INTO {O} (id, tenantid, region, status, amount) SELECT id + 300, 'acme', 'EU', email, 1 FROM {O} WHERE id = 1", "acme", 1);
        (await OrdersAsync()).Single(r => r.Id == 301).Status.ShouldBe("[REDACTED]");
        await SecurityRejectedAsync(() => ExecAsync($"INSERT INTO {O} (id, tenantid, region, status, amount, email) VALUES (400, 'acme', 'EU', 'x', 1, 'w@x.y')", "acme"));
    }

    // CR-ADG-35: INSERT into a table with an admin row policy has check-option semantics (Delta reports no count and stays rejected).
    private const string PolicyColumns = "(id, tenantid, region, status, amount)";

    [Fact]
    public async Task Insert_IntoAPolicyTable_ThatSatisfiesThePolicy_IsWritten()
    {
        if (!Available()) return;
        RegionEuPolicy();
        if (!ReportsInsertCount)
        {
            var ex = await Should.ThrowAsync<SqlCompileNotSupportedException>(() => ExecAsync($"INSERT INTO {O} {PolicyColumns} VALUES (10, 'acme', 'EU', 'new', 1)", "acme"));
            ex.Reason.ShouldBe(SqlCompileNotSupportedReason.Construct);
            return;
        }

        (await ExecCheckedAsync($"INSERT INTO {O} {PolicyColumns} VALUES (10, 'acme', 'EU', 'new', 1), (11, 'acme', 'EU', 'new', 2)", "acme")).ShouldBe(2);
        var rows = await OrdersAsync();
        rows.Where(r => r.Id >= 10).Select(r => (r.Id, r.Tenant)).ShouldBe(new[] { (10, "acme"), (11, "acme") });
    }

    [Fact]
    public async Task Insert_IntoAPolicyTable_ThatViolatesThePolicy_IsRolledBack_NothingIsWritten()
    {
        if (!Available() || !ReportsInsertCount) return;
        RegionEuPolicy();
        var before = await SnapshotAsync();
        var ex = await Should.ThrowAsync<DmlCheckOptionViolationException>(() => ExecCheckedAsync($"INSERT INTO {O} {PolicyColumns} VALUES (10, 'acme', 'US', 'new', 1)", "acme"));
        ex.Code.ShouldBe("DML_CHECK_OPTION_VIOLATION");
        ex.ToString().ShouldNotContain("US");
        (await SnapshotAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task Insert_CheckOptionStatement_CannotBeBoundOrRunOutsideTheCheckedExecutor()
    {
        if (!Available() || !ReportsInsertCount) return;
        RegionEuPolicy();
        var before = await SnapshotAsync();
        var compiled = Engine.Compile($"INSERT INTO {O} {PolicyColumns} VALUES (10, 'acme', 'US', 'new', 1)".AsMemory(), Request("acme"), CancellationToken.None);
        compiled.RequiresRowCountCheck.ShouldBeTrue();
        // the plain path (bind + execute) would silently filter the violating row out and report success: it fails closed instead
        var ex = await Should.ThrowAsync<CheckedExecutionRequiredException>(() => ExecuteAsync(compiled));
        ex.Code.ShouldBe("DML_CHECKED_EXECUTION_REQUIRED");
        (await SnapshotAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task Insert_MultiRow_WithOneViolatingRow_RollsBackAllRows()
    {
        if (!Available() || !ReportsInsertCount) return;
        RegionEuPolicy();
        var before = await SnapshotAsync();
        await Should.ThrowAsync<DmlCheckOptionViolationException>(() => ExecCheckedAsync(
            $"INSERT INTO {O} {PolicyColumns} VALUES (10, 'acme', 'EU', 'new', 1), (11, 'acme', 'US', 'new', 2), (12, 'acme', 'EU', 'new', 3)", "acme"));
        (await SnapshotAsync()).ShouldBe(before);   // the two passing rows are not left behind
        (await ExecCheckedAsync($"INSERT INTO {O} {PolicyColumns} VALUES (10, 'acme', 'EU', 'new', 1)", "acme")).ShouldBe(1);   // key 10 is still free
    }

    // ---- CR-ADG-42: the check runs on the stored value (cast to the column type), strings are compared byte-exact ----

    private ColumnReference PolicyColumn(string logical) => new(new SqlQualifiedName(new[] { new SqlIdentifier(Canon(logical), true) }));

    protected void AmountBelowPolicy() => Policy.Predicates[OrdersId] = PolicyPredicate.Create(
        new BinaryExpression(PolicyColumn("Amount"), BinaryOperator.LessThan, new PolicyParameterExpression("__pol_amount", SqlParameterType.Decimal)),
        new Dictionary<string, PolicyValue> { ["__pol_amount"] = new(100m, SqlParameterType.Decimal) });

    protected void DueAfterPolicy() => Policy.Predicates[OrdersId] = PolicyPredicate.Create(
        new BinaryExpression(PolicyColumn("Due"), BinaryOperator.GreaterThan, new PolicyParameterExpression("__pol_due", SqlParameterType.Date)),
        new Dictionary<string, PolicyValue> { ["__pol_due"] = new(new DateTime(2026, 1, 1), SqlParameterType.Date) });

    [Fact]
    public async Task Insert_DecimalRoundingAcrossThePolicyBoundary_IsRolledBack_AndTheRoundedInRangeValueIsWritten()
    {
        if (!Available() || !ReportsInsertCount) return;
        AmountBelowPolicy();
        var before = await SnapshotAsync();
        // 99.999 passes "Amount < 100" as a bound value but the column (scale 2) stores 100.00: the check sees the stored value
        var ex = await Should.ThrowAsync<DmlCheckOptionViolationException>(() => ExecCheckedAsync($"INSERT INTO {O} {PolicyColumns} VALUES (10, 'acme', 'EU', 'new', 99.999)", "acme"));
        ex.Code.ShouldBe("DML_CHECK_OPTION_VIOLATION");
        (await SnapshotAsync()).ShouldBe(before);

        (await ExecCheckedAsync($"INSERT INTO {O} {PolicyColumns} VALUES (10, 'acme', 'EU', 'new', 99.99)", "acme")).ShouldBe(1);
        var stored = await QueryAsync($"SELECT {ReadExpr("Amount")} FROM {RawTable("Orders")} WHERE {Quote(Canon("Id"))} = 10");
        Convert.ToDecimal(stored.Single()[0], CultureInfo.InvariantCulture).ShouldBe(99.99m);
        // a rounding that stays inside the policy (99.994 -> 99.99) is accepted: the stored value satisfies it
        (await ExecCheckedAsync($"INSERT INTO {O} {PolicyColumns} VALUES (11, 'acme', 'EU', 'new', 99.994)", "acme")).ShouldBe(1);
    }

    [Fact]
    public async Task Insert_DateTruncationAcrossThePolicyBoundary_IsRolledBack_AndAnInRangeDateIsWritten()
    {
        if (!Available() || !ReportsInsertCount) return;
        DueAfterPolicy();
        const string columns = "(id, tenantid, region, status, amount, due)";
        var before = await SnapshotAsync();
        // 2026-01-01 00:00:00.5 is "after 2026-01-01" as a timestamp, but the date column stores 2026-01-01
        await Should.ThrowAsync<DmlCheckOptionViolationException>(() => ExecCheckedAsync(
            $"INSERT INTO {O} {columns} VALUES (10, 'acme', 'EU', 'new', 1, TIMESTAMP '2026-01-01 00:00:00.500')", "acme"));
        (await SnapshotAsync()).ShouldBe(before);

        (await ExecCheckedAsync($"INSERT INTO {O} {columns} VALUES (10, 'acme', 'EU', 'new', 1, TIMESTAMP '2026-01-02 10:30:00')", "acme")).ShouldBe(1);
    }

    [Fact]
    public async Task Insert_ValueLongerThanTheColumn_NeverMatchesThePolicyByItsTruncatedPrefix()
    {
        if (!Available() || !ReportsInsertCount) return;
        RegionEuPolicy();
        var before = await SnapshotAsync();
        // 'EU' plus 30 characters: the dialects that truncate (SQL Server, PostgreSQL varchar) see the value they store; the others
        // fail on the length. Either way nothing is written and the policy value is not matched by a prefix.
        await Should.ThrowAsync<Exception>(() => ExecCheckedAsync(
            $"INSERT INTO {O} {PolicyColumns} VALUES (10, 'acme', 'EUxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx', 'new', 1)", "acme"));
        (await SnapshotAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task Insert_CaseVariantOfThePolicyValue_IsRejected_OnACaseSensitiveColumn()
    {
        if (!Available() || !ReportsInsertCount) return;
        RegionEuPolicy();   // Region = 'EU'; the Region column is case-sensitive on every engine here (SQL Server: an explicit _CS_ collation)
        var before = await SnapshotAsync();
        // a case-insensitive check would accept 'eu' and the case-sensitive column would store it, a value a reader with Region = 'eu' sees
        var ex = await Should.ThrowAsync<DmlCheckOptionViolationException>(() => ExecCheckedAsync($"INSERT INTO {O} {PolicyColumns} VALUES (10, 'acme', 'eu', 'new', 1)", "acme"));
        ex.Code.ShouldBe("DML_CHECK_OPTION_VIOLATION");
        (await SnapshotAsync()).ShouldBe(before);
        (await ExecCheckedAsync($"INSERT INTO {O} {PolicyColumns} VALUES (10, 'acme', 'EU', 'new', 1)", "acme")).ShouldBe(1);
    }

    [Fact]
    public async Task Insert_StringInPolicy_AcceptsEachListedValue_AndRejectsTheRest()
    {
        if (!Available() || !ReportsInsertCount) return;
        Policy.Predicates[OrdersId] = PolicyPredicate.Create(
            new InListExpression(PolicyColumn("Region"), new Expression[] { new PolicyParameterExpression("__pol_a", SqlParameterType.String), new PolicyParameterExpression("__pol_b", SqlParameterType.String) }, false),
            new Dictionary<string, PolicyValue> { ["__pol_a"] = new("EU", SqlParameterType.String), ["__pol_b"] = new("US", SqlParameterType.String) });
        (await ExecCheckedAsync($"INSERT INTO {O} {PolicyColumns} VALUES (10, 'acme', 'EU', 'new', 1), (11, 'acme', 'US', 'new', 2)", "acme")).ShouldBe(2);
        var before = await SnapshotAsync();
        await Should.ThrowAsync<DmlCheckOptionViolationException>(() => ExecCheckedAsync($"INSERT INTO {O} {PolicyColumns} VALUES (12, 'acme', 'eu', 'new', 1)", "acme"));
        await Should.ThrowAsync<DmlCheckOptionViolationException>(() => ExecCheckedAsync($"INSERT INTO {O} {PolicyColumns} VALUES (12, 'acme', 'APAC', 'new', 1)", "acme"));
        (await SnapshotAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task Insert_ThousandRows_AreWritten_OrFailWithTheTypedBindLimit()
    {
        if (!Available() || !ReportsInsertCount) return;
        RegionEuPolicy();
        string rows = string.Join(", ", Enumerable.Range(100, 1000).Select(i => $"({i}, 'acme', 'EU', 'n', 1)"));
        try
        {
            (await ExecCheckedAsync($"INSERT INTO {O} {PolicyColumns} VALUES {rows}", "acme")).ShouldBe(1000);
            (await OrdersAsync()).Count(r => r.Id >= 100).ShouldBe(1000);
        }
        catch (SqlLimitExceededException ex)
        {
            ex.Kind.ShouldBe(SqlLimitKind.BindParameters);   // never an AST depth error: the row set is a balanced tree
            (await OrdersAsync()).Count(r => r.Id >= 100).ShouldBe(0);
        }
    }

    [Fact]
    public async Task Insert_IntoAPolicyTable_SelectSourceOrMissingPolicyColumn_IsRejectedBeforeExecution()
    {
        if (!Available()) return;
        RegionEuPolicy();
        var before = await SnapshotAsync();
        await SecurityRejectedAsync(() => ExecAsync($"INSERT INTO {O} (id, tenantid, region, status, amount) SELECT 20, 'acme', 'EU', 'x', 1 FROM {E} WHERE id = 1", "acme"));
        await SecurityRejectedAsync(() => ExecAsync($"INSERT INTO {O} (id, tenantid, status, amount) VALUES (21, 'acme', 'x', 1)", "acme"));
        (await SnapshotAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task Insert_DuplicateKeyOfAnotherTenant_IsADmlConstraintViolation_WithoutKeyValueOrConstraintName()
    {
        if (!Available() || !HasUniqueKey) return;
        // R-12: the key of tenant other's row is visible only as a constraint violation; the existing row is never changed.
        var before = await SnapshotAsync();
        var raw = await Should.ThrowAsync<Exception>(() => ExecAsync($"INSERT INTO {O} (id, tenantid, region, status, amount) VALUES (5, 'acme', 'EU', 'x', 1)", "acme"));
        var mapped = DmlErrorSanitizer.TryMap(Dialect, raw);
        mapped.ShouldNotBeNull();
        mapped.Kind.ShouldBe(DmlConstraintKind.Unique);
        mapped.InnerException.ShouldBeNull();
        mapped.Message.ShouldBe("The statement violated a data constraint.");
        mapped.ToString().ShouldNotContain("ORDERS", Case.Insensitive);
        mapped.ToString().ShouldNotContain("PK", Case.Sensitive);
        (await SnapshotAsync()).ShouldBe(before);
    }

    // ---- MERGE ----

    private async Task<bool> MergeAvailableAsync()
    {
        if (!Available()) return false;
        if (SupportsMerge) return true;
        // fail closed where the engine has no MERGE: the compiler refuses, nothing reaches the database
        await Should.ThrowAsync<SqlCompileNotSupportedException>(() => ExecAsync($"MERGE INTO {O} t USING {E} s ON t.id = s.orderid WHEN MATCHED THEN DELETE", "acme"));
        return false;
    }

    [Fact]
    public async Task Merge_OtherTenantRowWithSameKey_IsNeverMatched_AndNeverDeletedOrUpdated()
    {
        if (!await MergeAvailableAsync()) return;
        // entitlement 2 belongs to tenant other and points at order 2, which belongs to acme.
        var before = await SnapshotAsync();
        if (SupportsMergeDelete)
        {
            (await ExecAsync($"MERGE INTO {O} t USING {E} s ON t.id = s.orderid WHEN MATCHED THEN DELETE", "other")).ShouldBe(0);
        }

        (await ExecAsync($"MERGE INTO {O} t USING {E} s ON t.id = s.orderid WHEN MATCHED THEN UPDATE SET status = 'merged'", "other")).ShouldBe(0);
        (await SnapshotAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task Merge_UpdateAndDelete_TouchOnlyTheCallersRows_AndSourceIsSecured()
    {
        if (!await MergeAvailableAsync()) return;
        // as acme: source entitlements are 1 (order 1) and 3 (order 6); entitlement 2 of tenant other is invisible
        string deleteClause = SupportsMergeDelete ? " WHEN MATCHED AND s.id = 3 THEN DELETE" : string.Empty;
        int expected = SupportsMergeDelete ? 2 : 1;
        (await ExecAsync($"MERGE INTO {O} t USING {E} s ON t.id = s.orderid WHEN MATCHED AND s.id = 1 THEN UPDATE SET status = 'merged'{deleteClause}", "acme")).ShouldBe(expected);
        var rows = await OrdersAsync();
        rows.Single(r => r.Id == 1).Status.ShouldBe("merged");
        if (SupportsMergeDelete) rows.Any(r => r.Id == 6).ShouldBeFalse();
        rows.Single(r => r.Id == 2).Status.ShouldBe("open");   // matched only through other's entitlement: untouched
        rows.Single(r => r.Id == 3).Status.ShouldBe("open");   // ACME: untouched
    }

    [Fact]
    public async Task Merge_SourceAliasEqualToTheTargetAlias_IsRejectedByTheCompiler_BeforeAnyExecution()
    {
        if (!await MergeAvailableAsync()) return;   // CR-ADG-33
        var before = await SnapshotAsync();
        foreach (var source in new[] { $"(SELECT * FROM {E}) t", $"(SELECT * FROM {E}) T", $"{E} t" })
        {
            // a SecurityException comes from the compiler; a provider error would be a DbException, so nothing reached the database
            await SecurityRejectedAsync(() => ExecAsync($"MERGE INTO {O} t USING {source} ON t.id = 1 WHEN MATCHED THEN UPDATE SET status = 'merged'", "acme"));
        }

        (await SnapshotAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task Merge_Insert_WritesTheCallersTenant_NeverTheSourceTenant()
    {
        if (!await MergeAvailableAsync()) return;
        (await ExecAsync(
            $"MERGE INTO {O} t USING (SELECT id + 500 AS nid FROM {E}) s ON t.id = s.nid " +
            "WHEN NOT MATCHED THEN INSERT (id, tenantid, region, status, amount) VALUES (s.nid, 'acme', 'EU', 'inserted', 1)", "acme")).ShouldBe(2);   // entitlements 1 and 3 of acme
        var rows = await OrdersAsync();
        rows.Where(r => r.Status == "inserted").Select(r => (r.Id, r.Tenant)).ShouldBe(new[] { (501, "acme"), (503, "acme") });
        await SecurityRejectedAsync(() => ExecAsync(
            $"MERGE INTO {O} t USING {E} s ON t.id = s.orderid " +
            "WHEN NOT MATCHED THEN INSERT (id, tenantid, region, status, amount) VALUES (s.id + 600, s.tenantid, 'EU', 'x', 1)", "acme"));
    }

    [Fact]
    public async Task Merge_PolicyColumn_TenantColumn_MaskedColumn_AndTrivialOn_AreRejected_NothingChanges()
    {
        if (!await MergeAvailableAsync()) return;
        RegionEuPolicy();
        MaskEmail();
        var before = await SnapshotAsync();
        await SecurityRejectedAsync(() => ExecAsync($"MERGE INTO {O} t USING {E} s ON t.id = s.orderid WHEN MATCHED THEN UPDATE SET region = 'US'", "acme"));
        await SecurityRejectedAsync(() => ExecAsync($"MERGE INTO {O} t USING {E} s ON t.id = s.orderid WHEN MATCHED THEN UPDATE SET tenantid = 'other'", "acme"));
        await SecurityRejectedAsync(() => ExecAsync($"MERGE INTO {O} t USING {E} s ON t.id = s.orderid WHEN MATCHED THEN UPDATE SET status = t.email", "acme"));
        await SecurityRejectedAsync(() => ExecAsync($"MERGE INTO {O} t USING {E} s ON t.email = 'x' WHEN MATCHED THEN UPDATE SET status = 'x'", "acme"));
        await Should.ThrowAsync<UnfilteredDmlException>(() => ExecAsync($"MERGE INTO {O} t USING {E} s ON 1 = 1 WHEN MATCHED THEN UPDATE SET status = 'x'", "acme"));
        (await SnapshotAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task Merge_RowPolicy_IsPartOfTheOnCondition()
    {
        if (!await MergeAvailableAsync()) return;
        RegionEuPolicy();
        // acme sees orders 1, 2 and 6 through its entitlements 1 and 3: only the EU rows 1 and 6 can match
        (await ExecAsync($"MERGE INTO {O} t USING {E} s ON t.id = s.orderid WHEN MATCHED THEN UPDATE SET status = 'eu'", "acme")).ShouldBe(2);
        (await OrdersAsync()).Where(r => r.Status == "eu").Select(r => r.Id).ShouldBe(new[] { 1, 6 });
    }

    [Fact]
    public async Task Merge_NotMatchedBySource_CannotBeExpressed_SoOtherTenantsRowsSurvive()
    {
        if (!Available()) return;
        var before = await SnapshotAsync();
        await RejectedAsync(() => ExecAsync($"MERGE INTO {O} t USING {E} s ON t.id = s.orderid WHEN NOT MATCHED BY SOURCE THEN DELETE", "acme"));
        (await SnapshotAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task Merge_InsertOfAnotherTenantsKey_IsADmlConstraintViolation_AndChangesNothing()
    {
        if (!await MergeAvailableAsync() || !HasUniqueKey) return;
        // as tenant other: entitlement 2 points at order 2 (tenant acme). The target RLS hides it, so the row is "not matched"
        // and the INSERT clause collides with the primary key; the existing row is never touched.
        var before = await SnapshotAsync();
        var raw = await Should.ThrowAsync<Exception>(() => ExecAsync(
            $"MERGE INTO {O} t USING {E} s ON t.id = s.orderid " +
            "WHEN NOT MATCHED THEN INSERT (id, tenantid, region, status, amount) VALUES (s.orderid, 'other', 'EU', 'x', 1)", "other"));
        DmlErrorSanitizer.TryMap(Dialect, raw)!.Kind.ShouldBe(DmlConstraintKind.Unique);
        (await SnapshotAsync()).ShouldBe(before);
    }

    // ---- shared ----

    [Fact]
    public async Task ReturningAndOutput_AreRejectedBeforeAnyStatementReachesTheDatabase()
    {
        if (!Available()) return;
        var before = await SnapshotAsync();
        await RejectedAsync(() => ExecAsync($"DELETE FROM {O} WHERE id = 1 RETURNING *", "acme"));
        await RejectedAsync(() => ExecAsync($"DELETE FROM {O} OUTPUT deleted.* WHERE id = 1", "acme"));
        await RejectedAsync(() => ExecAsync($"UPDATE {O} SET status = 'x' OUTPUT inserted.* WHERE id = 1", "acme"));
        (await SnapshotAsync()).ShouldBe(before);
    }

    [Theory]
    [InlineData("acme'; DELETE FROM x; --")]
    [InlineData("acme ")]
    [InlineData("%")]
    [InlineData("' OR 1=1 --")]
    public async Task HostileTenantValues_AreBound_NeverInterpreted_AndMatchNoRow(string tenant)
    {
        if (!Available()) return;
        var before = await SnapshotAsync();
        (await ExecAsync($"UPDATE {O} SET status = 'x' WHERE id > 0", tenant)).ShouldBe(0);
        (await ExecAsync($"DELETE FROM {O} WHERE id > 0", tenant)).ShouldBe(0);
        if (SupportsMerge)
        {
            (await ExecAsync($"MERGE INTO {O} t USING {E} s ON t.id = s.orderid WHEN MATCHED THEN UPDATE SET status = 'x'", tenant)).ShouldBe(0);
        }

        (await SnapshotAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task PlanCache_RebindsTheTenantOnADmlHit()
    {
        if (!Available()) return;
        (await ExecAsync($"UPDATE {O} SET status = 'c' WHERE id > 0", "acme")).ShouldBe(3);
        (await ExecAsync($"UPDATE {O} SET status = 'c' WHERE id > 0", "other")).ShouldBe(1);
        (await ExecAsync($"UPDATE {O} SET status = 'c' WHERE id > 0", "ACME")).ShouldBe(2);
        Engine.CompileCache.Stats.Hits.ShouldBe(2);
    }

    [Fact]
    public void DmlStatementMatrix_ListsAllFourClasses()
    {
        var statements = DialectCapabilityTable.Default.Get(Dialect).DmlStatements;
        statements.HasFlag(StatementPermissions.Insert).ShouldBeTrue();
        statements.HasFlag(StatementPermissions.Update).ShouldBeTrue();
        statements.HasFlag(StatementPermissions.Delete).ShouldBeTrue();
        statements.HasFlag(StatementPermissions.Merge).ShouldBe(SupportsMerge);
    }
}
