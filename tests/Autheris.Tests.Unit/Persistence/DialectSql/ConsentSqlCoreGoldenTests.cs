using System.Data;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Persistence.DialectSql;

public sealed class ConsentSqlCoreGoldenTests
{
    public static TheoryData<string> Dialects() => DialectKit.Names();

    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task ReadConsentByIdRowAsync_BindsConsentId(string dialect)
    {
        var kit = DialectKit.Of(dialect);
        using var conn = kit.NewConnection();
        var consentId = Guid.NewGuid();

        _ = await ConsentSqlCore.ReadConsentByIdRowAsync(conn, consentId, CancellationToken.None);

        conn.Commands.Count.ShouldBe(1);
        var cmd = conn.Commands[0];
        cmd.Parameters.Select(p => p.Name).ShouldBe(["@id"]);
        cmd.Parameters[0].Value.ShouldBe(consentId.ToString());
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task ReadExpiringConsentRowsAsync_BindsWindowAndLimit(string dialect)
    {
        var kit = DialectKit.Of(dialect);
        using var conn = kit.NewConnection();
        var now = new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
        var threshold = now.AddDays(7);

        _ = await ConsentSqlCore.ReadExpiringConsentRowsAsync(conn, "SELECT 1", now, threshold, 25, CancellationToken.None);

        conn.Commands.Count.ShouldBe(1);
        var cmd = conn.Commands[0];
        cmd.CommandText.ShouldBe("SELECT 1");
        cmd.Parameters.Select(p => p.Name).ShouldBe(["@now", "@threshold", "@maxResults"]);
        cmd.Parameters[0].Value.ShouldBe(now.ToString("O"));
        cmd.Parameters[1].Value.ShouldBe(threshold.ToString("O"));
        cmd.Parameters[2].Value.ShouldBe(25);
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task HydrateConsentsBatchAsync_QueriesColumnRulesAndRowFilters(string dialect)
    {
        var kit = DialectKit.Of(dialect);
        using var conn = kit.NewConnection();
        var c1 = new Consent
        {
            Id = Guid.NewGuid(),
            TableId = Guid.NewGuid(),
            TableIdentifier = new TableIdentifier("src", "dbo", "orders"),
            TenantId = new TenantId("tenant-a"),
            GranteeType = GranteeType.User,
            GranteeSid = new Sid("S-1-5-21-1"),
            ValidFrom = DateTimeOffset.UtcNow,
            ValidTo = DateTimeOffset.UtcNow.AddDays(1)
        };
        var list = new List<Consent> { c1 };

        await ConsentSqlCore.HydrateConsentsBatchAsync(conn, list, CancellationToken.None);

        conn.Commands.Count.ShouldBe(2);
        conn.Commands[0].CommandText.ShouldStartWith(ConsentSqlCore.SelectColumnRulesPrefix);
        conn.Commands[0].Parameters.Select(p => p.Name).ShouldBe(["@cid0"]);
        conn.Commands[0].Parameters[0].Value.ShouldBe(c1.Id.ToString());

        conn.Commands[1].CommandText.ShouldStartWith(ConsentSqlCore.SelectRowFiltersPrefix);
        conn.Commands[1].Parameters.Select(p => p.Name).ShouldBe(["@cid0"]);
        conn.Commands[1].Parameters[0].Value.ShouldBe(c1.Id.ToString());
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task ApplyGuardedStatusAsync_BindsStatusAndIdAndExpected(string dialect)
    {
        var kit = DialectKit.Of(dialect);
        using var conn = kit.NewConnection();
        conn.NonQueryResult = 1;
        var reqId = Guid.NewGuid();

        await ConsentSqlCore.ApplyGuardedStatusAsync(conn, null!, null, reqId, "APPROVED", "PENDING", CancellationToken.None);

        conn.Commands.Count.ShouldBe(1);
        var cmd = conn.Commands[0];
        cmd.CommandText.ShouldBe(ConsentApprovalPolicy.GuardedStatusUpdateSql);
        cmd.Parameters.Select(p => p.Name).ShouldBe(["@st", "@id", "@expected"]);
        cmd.Parameters[0].Value.ShouldBe("APPROVED");
        cmd.Parameters[1].Value.ShouldBe(reqId.ToString());
        cmd.Parameters[2].Value.ShouldBe("PENDING");
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task DeleteConsentRequestRowsAsync_DeletesApprovalStepsThenRequest(string dialect)
    {
        var kit = DialectKit.Of(dialect);
        using var conn = kit.NewConnection();
        var reqId = Guid.NewGuid();

        await ConsentSqlCore.DeleteConsentRequestRowsAsync(conn, null, reqId, CancellationToken.None);

        conn.Commands.Count.ShouldBe(2);
        conn.Commands[0].CommandText.ShouldBe("DELETE FROM APPROVAL_STEPS WHERE consent_request_id = @id");
        conn.Commands[0].Parameters[0].Value.ShouldBe(reqId.ToString());
        conn.Commands[1].CommandText.ShouldBe("DELETE FROM CONSENT_REQUESTS WHERE id = @id");
        conn.Commands[1].Parameters[0].Value.ShouldBe(reqId.ToString());
    }
}
