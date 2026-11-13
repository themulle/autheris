namespace Autheris.Tests.Unit.Sql;

using System;
using System.Collections.Generic;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using Xunit;

/// <summary>
/// WP-SIMPL-45 PR 3 CI repair, SEC-CIFIX-3: the write permit is part of the compile-cache key. A compilation produced for one
/// permit must never be served to a request that carries another (or none), so every permit member has to change the key.
/// </summary>
public sealed class CompileCacheKeyTests
{
    private const string Sql = "UPDATE orders SET amount = 1 WHERE id = 1";

    private static HashSet<string> Set(params string[] columns) => new(columns, StringComparer.OrdinalIgnoreCase);

    private static string Key(DmlWritePermit? permit)
    {
        var env = new TestCompileEnv(TargetSqlDialect.Sqlite).Table("orders", "tenant_id", "id:int", "amount:decimal", "tenant_id:varchar");
        var request = env.Request(statements: StatementPermissions.Update);
        request = request with { Policy = request.Policy with { Write = permit } };
        return CompileCacheKey.Material(request, Sql, DialectCapabilityTable.Default.Get(TargetSqlDialect.Sqlite));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Key_DiffersByWritePermitOperationWritableAndProtectedColumns()
    {
        var baseline = Key(new DmlWritePermit(DmlWriteOperation.Update, Set("id", "amount"), Set()));

        Key(new DmlWritePermit(DmlWriteOperation.Update, Set("id", "amount"), Set())).ShouldBe(baseline);   // control: equal permits share a key
        Key(new DmlWritePermit(DmlWriteOperation.Update, Set("amount", "id"), Set())).ShouldBe(baseline);   // enumeration order of the set does not matter
        Key(null).ShouldNotBe(baseline);                                                                      // null versus non-null
        Key(new DmlWritePermit(DmlWriteOperation.Delete, Set("id", "amount"), Set())).ShouldNotBe(baseline); // operation
        Key(new DmlWritePermit(DmlWriteOperation.Update, Set("id"), Set())).ShouldNotBe(baseline);           // writable set
        Key(new DmlWritePermit(DmlWriteOperation.Update, Set("id", "amount"), Set("amount"))).ShouldNotBe(baseline); // protected set
        Key(new DmlWritePermit(DmlWriteOperation.Update, Set(), Set())).ShouldNotBe(baseline);               // empty writable set
    }
}
