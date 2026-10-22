namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Security review (information_schema.columns): Deny columns are hidden for every subject the data path evaluates
/// (user SID, group SIDs, roles), loaded in one batch call instead of one consent query per table.
/// </summary>
public sealed class TrinoMetadataColumnDenyTests
{
    private const string UserSid = "S-1-5-21-USER";
    private const string GroupSid = "S-1-5-21-GROUP-FINANCE";
    private const string Role = "Analyst";

    private static readonly TenantId Tenant = Wp0.TenantA;

    private static Consent Grant(TableIdentifier table, GranteeType type, string? sid, string? roleName, params (string Column, ColumnAccessLevel Level)[] rules) => new()
    {
        TableIdentifier = table,
        TenantId = Tenant,
        Effect = ConsentEffect.Allow,
        GranteeType = type,
        GranteeSid = sid == null ? (Sid?)null : new Sid(sid),
        RoleName = roleName,
        ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
        ValidTo = DateTimeOffset.UtcNow.AddDays(1),
        ColumnRules = rules.Select(r => new ConsentColumnRule { ColumnName = r.Column, AccessLevel = r.Level }).ToList()
    };

    private static ClaimsPrincipal Caller() => Wp0.User(
        UserSid, Tenant.Value, [Role], "Bearer", new Claim(ClaimTypes.GroupSid, GroupSid));

    private static IConsentRepository Repository(params Consent[] consents)
    {
        var repo = Substitute.For<IConsentRepository>();
        repo.GetAllActiveConsentsForSubjectsAsync(
                Arg.Any<IEnumerable<Sid>>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>(consents));
        return repo;
    }

    private static async Task<string[]> ColumnsAsync(IConsentRepository consents, params TableMetadata[] tables)
    {
        var result = await TrinoMetadataQueryHandler.TryExecuteAsync(
            "SELECT * FROM information_schema.columns", Caller(), Tenant, Wp0.Tables(tables), consents, Wp0.Options());
        result.ShouldNotBeNull();
        return result.Rows.Select(r => r["column_name"]?.ToString() ?? string.Empty).ToArray();
    }

    [Fact]
    public async Task DenyColumns_FromGroupAndRoleConsents_AreHidden()
    {
        var table = Wp0.Table("hr", "employees", columns: ["id", "email", "salary"]);
        var consents = Repository(
            Grant(table.Identifier, GranteeType.User, UserSid, null, ("id", ColumnAccessLevel.Clear), ("email", ColumnAccessLevel.Clear), ("salary", ColumnAccessLevel.Clear)),
            Grant(table.Identifier, GranteeType.Group, GroupSid, null, ("salary", ColumnAccessLevel.Deny)),
            Grant(table.Identifier, GranteeType.Role, null, Role, ("email", ColumnAccessLevel.Deny)));

        var columns = await ColumnsAsync(consents, table);

        columns.ShouldBe(["id"]);
    }

    [Fact]
    public async Task ConsentsAreLoaded_OnceForAllSubjects_NotPerTable()
    {
        var first = Wp0.Table("hr", "employees", columns: ["id"]);
        var second = Wp0.Table("hr", "contracts", columns: ["id"]);
        var consents = Repository(
            Grant(first.Identifier, GranteeType.User, UserSid, null),
            Grant(second.Identifier, GranteeType.User, UserSid, null));

        await ColumnsAsync(consents, first, second);

        // Batch calls with the full subject set (user SID, group SIDs, roles); never the per-table query.
        await consents.Received().GetAllActiveConsentsForSubjectsAsync(
            Arg.Is<IEnumerable<Sid>>(s => s.Contains(new Sid(UserSid)) && s.Contains(new Sid(GroupSid))),
            Arg.Is<IEnumerable<string>?>(r => r != null && r.Contains(Role)),
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<TenantId?>(),
            Arg.Any<CancellationToken>());
        await consents.DidNotReceive().GetActiveConsentsForSubjectsAsync(
            Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ColumnsWithoutAnyDenyRule_StayListed()
    {
        var table = Wp0.Table("hr", "employees", columns: ["id", "email"]);
        var consents = Repository(Grant(table.Identifier, GranteeType.User, UserSid, null));

        var columns = await ColumnsAsync(consents, table);

        columns.ShouldBe(["id", "email"]);
    }
}
