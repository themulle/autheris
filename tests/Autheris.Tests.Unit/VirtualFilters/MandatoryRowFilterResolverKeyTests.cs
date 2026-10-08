namespace Autheris.Tests.Unit.VirtualFilters;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autheris.Application.Policy;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class MandatoryRowFilterResolverKeyTests
{
    private static TableMetadata CreateTableMetadata(string tableName = "invoices") => new()
    {
        Identifier = new TableIdentifier("finance", "public", tableName),
        Table = new Table { TableName = tableName, SchemaName = "public", SourceName = "finance", SourceType = "PostgreSql" },
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "int" },
            new TableColumn { ColumnName = "tenant_id", DataType = "varchar" }
        ]
    };

    [Fact]
    public void VirtualFilterMemoKey_SameParameters_AreEqual_AndHaveSameHashCode()
    {
        var meta = CreateTableMetadata();
        var q1 = new MandatoryFilterQuery(
            new Sid("S-1-5-21-USER1"),
            new HashSet<Sid> { new("S-1-5-21-G1"), new("S-1-5-21-G2") },
            new HashSet<string> { "FinanceUser", "Viewer" },
            new TenantId("tenant_a"),
            meta);

        var q2 = new MandatoryFilterQuery(
            new Sid("S-1-5-21-USER1"),
            new HashSet<Sid> { new("S-1-5-21-G2"), new("S-1-5-21-G1") }, // different order
            new HashSet<string> { "Viewer", "FinanceUser" },               // different order
            new TenantId("tenant_a"),
            meta);

        var key1 = new VirtualFilterMemoKey(100, q1);
        var key2 = new VirtualFilterMemoKey(100, q2);

        key1.ShouldBe(key2);
        key1.GetHashCode().ShouldBe(key2.GetHashCode());
        (key1 == key2).ShouldBeTrue();
    }

    [Fact]
    public void VirtualFilterMemoKey_DifferentUser_AreNotEqual()
    {
        var meta = CreateTableMetadata();
        var q1 = new MandatoryFilterQuery(
            new Sid("S-1-5-21-USER1"),
            new HashSet<Sid>(),
            new HashSet<string> { "Viewer" },
            new TenantId("tenant_a"),
            meta);

        var q2 = new MandatoryFilterQuery(
            new Sid("S-1-5-21-USER2"),
            new HashSet<Sid>(),
            new HashSet<string> { "Viewer" },
            new TenantId("tenant_a"),
            meta);

        var key1 = new VirtualFilterMemoKey(100, q1);
        var key2 = new VirtualFilterMemoKey(100, q2);

        key1.ShouldNotBe(key2);
        (key1 != key2).ShouldBeTrue();
    }

    [Fact]
    public void VirtualFilterMemoKey_DifferentAllUserSids_AreNotEqual()
    {
        var meta = CreateTableMetadata();
        var q1 = new MandatoryFilterQuery(
            new Sid("S-1-5-21-USER1"),
            new HashSet<Sid>(),
            new HashSet<string> { "Viewer" },
            new TenantId("tenant_a"),
            meta,
            AllUserSids: new HashSet<Sid> { new("S-1-5-21-USER1"), new("oid-1") });

        var q2 = new MandatoryFilterQuery(
            new Sid("S-1-5-21-USER1"),
            new HashSet<Sid>(),
            new HashSet<string> { "Viewer" },
            new TenantId("tenant_a"),
            meta,
            AllUserSids: new HashSet<Sid> { new("S-1-5-21-USER1"), new("oid-2") });

        var key1 = new VirtualFilterMemoKey(100, q1);
        var key2 = new VirtualFilterMemoKey(100, q2);

        key1.ShouldNotBe(key2);
        (key1 != key2).ShouldBeTrue();
    }

    [Fact]
    public async Task MandatoryRowFilterResolver_UsesMemoKey_AndReturnsCachedOutcome()
    {
        var snapshotProvider = Substitute.For<IVirtualFilterSnapshotProvider>();
        var predicateBuilder = Substitute.For<IVirtualFilterPredicateBuilder>();

        var profile = new AccessProfile
        {
            Name = "prof1",
            TenantId = new TenantId("tenant_a"),
            Scope = "*.*.*",
            GranteeType = GranteeType.User,
            GranteeSid = new Sid("S-1-5-21-USER1"),
            Status = FilterApprovalStatus.Active,
            Bindings = [new FilterBinding { FilterName = "filter1", ObjectKinds = FilterObjectKinds.Relation }]
        };

        var filter = new VirtualFilter
        {
            Name = "filter1",
            TenantId = new TenantId("tenant_a"),
            Status = FilterApprovalStatus.Active,
            Source = "finance",
            KeyColumns = ["inv.tenant_id"]
        };

        var snapshot = new VirtualFilterSnapshot(
            Generation: 42,
            Filters: [filter],
            Profiles: [profile]);

        snapshotProvider.GetAsync(Arg.Any<System.Threading.CancellationToken>())
            .Returns(new ValueTask<VirtualFilterSnapshot>(snapshot));

        predicateBuilder.Build(Arg.Any<VirtualFilter>(), Arg.Any<FilterBinding>(), Arg.Any<TableMetadata>(), Arg.Any<DatabaseDialect>())
            .Returns("autheris_target.tenant_id = 'tenant_a'");

        var resolver = new MandatoryRowFilterResolver(snapshotProvider, predicateBuilder);

        var meta = CreateTableMetadata();
        var q = new MandatoryFilterQuery(
            new Sid("S-1-5-21-USER1"),
            new HashSet<Sid>(),
            new HashSet<string>(),
            new TenantId("tenant_a"),
            meta);

        var outcome1 = await resolver.ResolveAsync(q);
        var outcome2 = await resolver.ResolveAsync(q);

        outcome1.ShouldBe(outcome2);
        outcome1.PredicateSql.ShouldBe("autheris_target.tenant_id = 'tenant_a'");

        // PredicateBuilder should only be invoked ONCE due to memoization
        predicateBuilder.Received(1).Build(Arg.Any<VirtualFilter>(), Arg.Any<FilterBinding>(), Arg.Any<TableMetadata>(), Arg.Any<DatabaseDialect>());
    }
}
