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

    private static string RevokeTargetSql(string dialect) => dialect switch
    {
        "PostgreSql" => PostgreSqlGovernanceRepository.ConsentGrantSql.RevokeTarget,
        "SqlServer" => SqlServerGovernanceRepository.ConsentGrantSql.RevokeTarget,
        _ => throw new ArgumentOutOfRangeException(nameof(dialect))
    };

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

        await ConsentGrantSqlCore.ReadRevokeTargetAsync(conn, tx, RevokeTargetSql(dialect), consentId, Actor, CancellationToken.None);
        await ConsentGrantSqlCore.MarkRevokedAsync(conn, tx, consentId, Actor, "because", clock, CancellationToken.None);
        conn.ScalarResult = Now.AddDays(1).ToString("O");
        var updated = await ConsentGrantSqlCore.UpdateDescriptionAsync(conn, tx, new ConsentDescriptionUpdate(consentId, Tenant, "desc"), clock, CancellationToken.None);

        updated.ShouldBeTrue();
        conn.Commands.Count.ShouldBe(4);
        var golden = kit.Golden(Area);

        conn.Commands[0].CommandText.ShouldBe(golden.Of("RevokeConsentInTransactionAsync").Single(f => f.Contains("SELECT t.source_name", StringComparison.Ordinal) && f.Contains("UPDLOCK", StringComparison.Ordinal) == (dialect == "SqlServer")));
        conn.Commands[0].TransactionSet.ShouldBeTrue();
        kit.ShouldBindExactly(conn.Commands[0], ("@id", consentId.ToString()));

        conn.Commands[1].CommandText.ShouldBe(golden.Fragment("RevokeConsentInTransactionAsync", "UPDATE CONSENTS SET is_revoked = 1"));
        conn.Commands[1].TransactionSet.ShouldBeTrue();
        kit.ShouldBindExactly(
            conn.Commands[1], ("@id", consentId.ToString()), ("@by", Actor.Value), ("@now", Now.ToString("O")), ("@reason", "because"));

        conn.Commands[2].CommandText.ShouldBe(golden.Fragment("UpdateConsentDescriptionInTransactionAsync", "SELECT valid_to"));
        conn.Commands[2].TransactionSet.ShouldBeTrue();
        kit.ShouldBindExactly(conn.Commands[2], ("@id", consentId.ToString()), ("@t", "tenant-a"));

        conn.Commands[3].CommandText.ShouldBe(golden.Fragment("UpdateConsentDescriptionInTransactionAsync", "UPDATE CONSENTS SET description"));
        conn.Commands[3].TransactionSet.ShouldBeTrue();
        kit.ShouldBindExactly(conn.Commands[3], ("@d", "desc"), ("@id", consentId.ToString()), ("@t", "tenant-a"));
        conn.TransactionSetCount.ShouldBe(4);
    }

    [Theory]
    [InlineData("User", "s-1-5-21-admin", 0, true, false)]
    [InlineData("User", "S-1-5-21-OTHER", 1, false, true)]
    [InlineData("Group", "S-1-5-21-ADMIN", 0, false, false)]
    public async Task ReadRevokeTarget_MapsTheLockedRow_WithoutDecidingAnything(string granteeType, string granteeSid, int isRevoked, bool expectSelf, bool expectAlreadyRevoked)
    {
        using var conn = DialectKit.PostgreSql.NewConnection();
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

        var target = await ConsentGrantSqlCore.ReadRevokeTargetAsync(conn, tx, PostgreSqlGovernanceRepository.ConsentGrantSql.RevokeTarget, Guid.NewGuid(), Actor, CancellationToken.None);

        target.ShouldNotBeNull();
        target.Value.Table.ShouldBe(Table);
        target.Value.TenantRaw.ShouldBe("not-a-valid-tenant!"); // unparsed: parsing (divergence D2) stays in the dialect repository
        target.Value.IsGranteeSelf.ShouldBe(expectSelf);
        target.Value.AlreadyRevoked.ShouldBe(expectAlreadyRevoked);
    }

    [Fact]
    public async Task ReadRevokeTarget_UnknownId_ReturnsNull()
    {
        using var conn = DialectKit.PostgreSql.NewConnection();
        await using var tx = await conn.BeginTransactionAsync();

        (await ConsentGrantSqlCore.ReadRevokeTargetAsync(conn, tx, PostgreSqlGovernanceRepository.ConsentGrantSql.RevokeTarget, Guid.NewGuid(), Actor, CancellationToken.None))
            .ShouldBeNull();
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task UpdateDescription_ExpiredConsent_ChangesNothing()
    {
        using var conn = DialectKit.PostgreSql.NewConnection();
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
        using var conn = DialectKit.PostgreSql.NewConnection();
        await using var tx = await conn.BeginTransactionAsync();
        conn.ScalarResult = null; // the select carries tenant_id = @t AND is_revoked = 0, so a foreign or revoked row yields no valid_to

        var updated = await ConsentGrantSqlCore.UpdateDescriptionAsync(conn, tx, new ConsentDescriptionUpdate(Guid.NewGuid(), Tenant, "x"), new FakeTimeProvider(Now), CancellationToken.None);

        updated.ShouldBeFalse();
        conn.Commands.Single().CommandText.ShouldContain("tenant_id = @t AND is_revoked = 0");
    }
}
