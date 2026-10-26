using System.Data;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Persistence.DialectSql;

/// <summary>ARCH-MNT-22 WP3 L2 runtime golden: the shared consent grant core issues the pre-refactor SQL with the pre-refactor binding, tenant predicates included.</summary>
public sealed class ConsentGrantSqlCoreGoldenTests
{
    private const string Area = "ConsentGrants";
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 8, 30, 0, TimeSpan.Zero);
    private static readonly TableIdentifier Table = new("Sales", "Crm", "Orders");
    private static readonly Sid Actor = new("S-1-5-21-ADMIN");
    private static readonly TenantId Tenant = new("tenant-a");

    public static TheoryData<string> Dialects() => DialectKit.Names();

    private static string ActiveSql(string dialect, bool withTenant) => dialect switch
    {
        "PostgreSql" => PostgreSqlGovernanceRepository.ConsentGrantSql.ActiveRoleConsents(withTenant),
        "SqlServer" => SqlServerGovernanceRepository.ConsentGrantSql.ActiveRoleConsents(withTenant),
        _ => SqliteGovernanceRepository.ConsentGrantSql.ActiveRoleConsents(withTenant)
    };

    private static string RevokeTargetSql(string dialect) => dialect switch
    {
        "PostgreSql" => PostgreSqlGovernanceRepository.ConsentGrantSql.RevokeTarget,
        "SqlServer" => SqlServerGovernanceRepository.ConsentGrantSql.RevokeTarget,
        _ => SqliteGovernanceRepository.ConsentGrantSql.RevokeTarget
    };

    private static string ExpectedActiveSql(DialectKit kit, bool withTenant)
    {
        var f = kit.Golden(Area).Of("GetActiveRoleConsentsAsync");
        var head = f.Single(x => x.Contains("SELECT c.id", StringComparison.Ordinal));
        var tail = f.Single(x => x.Contains("c.grantee_type = 'Role'", StringComparison.Ordinal));
        var tenant = f.Single(x => x == " AND c.tenant_id = @tenantId");
        return head + (withTenant ? tenant : string.Empty) + tail;
    }

    private static DataTable ConsentRows(DateTimeOffset validFrom, DateTimeOffset validTo, string? tenant)
    {
        var table = new DataTable();
        foreach (var (name, type) in new (string, Type)[]
                 {
                     ("id", typeof(string)), ("table_id", typeof(string)), ("consent_request_id", typeof(string)), ("effect", typeof(string)),
                     ("grantee_type", typeof(string)), ("grantee_sid", typeof(string)), ("role_id", typeof(string)), ("role_name", typeof(string)),
                     ("valid_from", typeof(string)), ("valid_to", typeof(string)), ("is_revoked", typeof(int)), ("tenant_id", typeof(string))
                 })
        {
            table.Columns.Add(name, type);
        }

        table.Rows.Add(Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), DBNull.Value, "Allow", "Role", DBNull.Value, DBNull.Value, "Analyst",
            validFrom.ToString("O"), validTo.ToString("O"), 0, (object?)tenant ?? DBNull.Value);
        return table;
    }

    [Trait("Category", "Security")]
    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task DialectGolden_ConsentGrants_RuntimeBinding_Is_Unchanged(string dialect)
    {
        // ARCH-MNT-22-T-02: text, binding order, values, DbType/provider type and Transaction assignment of every core statement.
        var kit = DialectKit.Of(dialect);
        using var conn = kit.NewConnection();
        await using var tx = await conn.BeginTransactionAsync();
        var clock = new FakeTimeProvider(Now);
        var consentId = Guid.Parse("11111111-2222-3333-4444-555555555555");

        await ConsentGrantSqlCore.ReadActiveRoleConsentsAsync(conn, ActiveSql(dialect, true), Table, "Analyst", Tenant, Now, CancellationToken.None);
        await ConsentGrantSqlCore.ReadRevokeTargetAsync(conn, tx, RevokeTargetSql(dialect), consentId, Actor, CancellationToken.None);
        await ConsentGrantSqlCore.MarkRevokedAsync(conn, tx, consentId, Actor, "because", clock, CancellationToken.None);
        conn.ScalarResult = Now.AddDays(1).ToString("O");
        var updated = await ConsentGrantSqlCore.UpdateDescriptionAsync(conn, tx, new ConsentDescriptionUpdate(consentId, Tenant, "desc"), clock, CancellationToken.None);

        updated.ShouldBeTrue();
        conn.Commands.Count.ShouldBe(5);
        var golden = kit.Golden(Area);

        conn.Commands[0].CommandText.ShouldBe(ExpectedActiveSql(kit, true));
        conn.Commands[0].TransactionSet.ShouldBeFalse();
        kit.ShouldBindExactly(conn.Commands[0], ("@domain", "Sales"), ("@schema", "Crm"), ("@table", "Orders"), ("@tenantId", "tenant-a"), ("@role", "Analyst"));

        conn.Commands[1].CommandText.ShouldBe(golden.Fragment("RevokeConsentInTransactionAsync", "SELECT t.source_name"));
        conn.Commands[1].TransactionSet.ShouldBeTrue();
        kit.ShouldBindExactly(conn.Commands[1], ("@id", consentId.ToString()));

        conn.Commands[2].CommandText.ShouldBe(golden.Fragment("RevokeConsentInTransactionAsync", "UPDATE CONSENTS SET is_revoked = 1"));
        conn.Commands[2].TransactionSet.ShouldBeTrue();
        kit.ShouldBindExactly(
            conn.Commands[2], ("@id", consentId.ToString()), ("@by", Actor.Value), ("@now", Now.ToString("O")), ("@reason", "because"));

        conn.Commands[3].CommandText.ShouldBe(golden.Fragment("UpdateConsentDescriptionInTransactionAsync", "SELECT valid_to"));
        conn.Commands[3].TransactionSet.ShouldBeTrue();
        kit.ShouldBindExactly(conn.Commands[3], ("@id", consentId.ToString()), ("@t", "tenant-a"));

        conn.Commands[4].CommandText.ShouldBe(golden.Fragment("UpdateConsentDescriptionInTransactionAsync", "UPDATE CONSENTS SET description"));
        conn.Commands[4].TransactionSet.ShouldBeTrue();
        kit.ShouldBindExactly(conn.Commands[4], ("@d", "desc"), ("@id", consentId.ToString()), ("@t", "tenant-a"));
        conn.TransactionSetCount.ShouldBe(4);
    }

    [Trait("Category", "Security")]
    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task ReadActiveRoleConsents_WithTenant_BindsTenantPredicate(string dialect)
    {
        var kit = DialectKit.Of(dialect);
        using var conn = kit.NewConnection();

        await ConsentGrantSqlCore.ReadActiveRoleConsentsAsync(conn, ActiveSql(dialect, true), Table, "Analyst", Tenant, Now, CancellationToken.None);

        var command = conn.Commands.Single();
        command.CommandText.ShouldContain("c.tenant_id = @tenantId");
        command.Parameters.Single(p => p.Name == "@tenantId").Value.ShouldBe("tenant-a");
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task ReadActiveRoleConsents_WithoutTenant_OmitsPredicateAndBinding(string dialect)
    {
        var kit = DialectKit.Of(dialect);
        using var conn = kit.NewConnection();

        await ConsentGrantSqlCore.ReadActiveRoleConsentsAsync(conn, ActiveSql(dialect, false), Table, "Analyst", null, Now, CancellationToken.None);

        var command = conn.Commands.Single();
        command.CommandText.ShouldBe(ExpectedActiveSql(kit, false));
        command.CommandText.ShouldNotContain("@tenantId");
        kit.ShouldBindExactly(command, ("@domain", "Sales"), ("@schema", "Crm"), ("@table", "Orders"), ("@role", "Analyst"));
    }

    [Fact]
    public async Task ReadActiveRoleConsents_MapsRowsAndKeepsOnlyActive()
    {
        using var conn = DialectKit.Sqlite.NewConnection();
        conn.EnqueueReader(ConsentRows(Now.AddDays(-1), Now.AddDays(5), "tenant-a"));

        var active = await ConsentGrantSqlCore.ReadActiveRoleConsentsAsync(
            conn, SqliteGovernanceRepository.ConsentGrantSql.ActiveRoleConsents(true), Table, "Analyst", Tenant, Now, CancellationToken.None);

        var consent = active.Single();
        consent.TenantId.ShouldBe(Tenant);
        consent.RoleName.ShouldBe("Analyst");
        consent.TableIdentifier.ShouldBe(Table);

        using var conn2 = DialectKit.Sqlite.NewConnection();
        conn2.EnqueueReader(ConsentRows(Now.AddDays(-10), Now.AddDays(-1), null));
        (await ConsentGrantSqlCore.ReadActiveRoleConsentsAsync(
            conn2, SqliteGovernanceRepository.ConsentGrantSql.ActiveRoleConsents(false), Table, "Analyst", null, Now, CancellationToken.None)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("User", "s-1-5-21-admin", 0, true, false)]
    [InlineData("User", "S-1-5-21-OTHER", 1, false, true)]
    [InlineData("Role", "S-1-5-21-ADMIN", 0, false, false)]
    public async Task ReadRevokeTarget_MapsTheLockedRow_WithoutDecidingAnything(string granteeType, string granteeSid, int isRevoked, bool expectSelf, bool expectAlreadyRevoked)
    {
        using var conn = DialectKit.Sqlite.NewConnection();
        await using var tx = await conn.BeginTransactionAsync();
        var rows = new DataTable();
        foreach (var (name, type) in new (string, Type)[]
                 {
                     ("source_name", typeof(string)), ("schema_name", typeof(string)), ("table_name", typeof(string)), ("tenant_id", typeof(string)),
                     ("grantee_type", typeof(string)), ("grantee_sid", typeof(string)), ("is_revoked", typeof(int))
                 })
        {
            rows.Columns.Add(name, type);
        }

        rows.Rows.Add("Sales", "Crm", "Orders", "not-a-valid-tenant!", granteeType, granteeSid, isRevoked);
        conn.EnqueueReader(rows);

        var target = await ConsentGrantSqlCore.ReadRevokeTargetAsync(conn, tx, SqliteGovernanceRepository.ConsentGrantSql.RevokeTarget, Guid.NewGuid(), Actor, CancellationToken.None);

        target.ShouldNotBeNull();
        target.Value.Table.ShouldBe(Table);
        target.Value.TenantRaw.ShouldBe("not-a-valid-tenant!"); // unparsed: parsing (divergence D2) stays in the dialect repository
        target.Value.IsGranteeSelf.ShouldBe(expectSelf);
        target.Value.AlreadyRevoked.ShouldBe(expectAlreadyRevoked);
    }

    [Fact]
    public async Task ReadRevokeTarget_UnknownId_ReturnsNull()
    {
        using var conn = DialectKit.Sqlite.NewConnection();
        await using var tx = await conn.BeginTransactionAsync();

        (await ConsentGrantSqlCore.ReadRevokeTargetAsync(conn, tx, SqliteGovernanceRepository.ConsentGrantSql.RevokeTarget, Guid.NewGuid(), Actor, CancellationToken.None))
            .ShouldBeNull();
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task UpdateDescription_ExpiredConsent_ChangesNothing()
    {
        using var conn = DialectKit.Sqlite.NewConnection();
        await using var tx = await conn.BeginTransactionAsync();
        conn.ScalarResult = Now.AddSeconds(-1).ToString("O");

        var updated = await ConsentGrantSqlCore.UpdateDescriptionAsync(conn, tx, new ConsentDescriptionUpdate(Guid.NewGuid(), Tenant, "x"), new FakeTimeProvider(Now), CancellationToken.None);

        updated.ShouldBeFalse();
        conn.Commands.Count.ShouldBe(1); // only the tenant-scoped select ran; no update was issued
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task UpdateDescription_UnknownOrForeignTenantOrRevokedConsent_ChangesNothing()
    {
        using var conn = DialectKit.Sqlite.NewConnection();
        await using var tx = await conn.BeginTransactionAsync();
        conn.ScalarResult = null; // the select carries tenant_id = @t AND is_revoked = 0, so a foreign or revoked row yields no valid_to

        var updated = await ConsentGrantSqlCore.UpdateDescriptionAsync(conn, tx, new ConsentDescriptionUpdate(Guid.NewGuid(), Tenant, "x"), new FakeTimeProvider(Now), CancellationToken.None);

        updated.ShouldBeFalse();
        conn.Commands.Single().CommandText.ShouldContain("tenant_id = @t AND is_revoked = 0");
    }
}
