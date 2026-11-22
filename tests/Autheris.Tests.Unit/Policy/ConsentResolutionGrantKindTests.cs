using System;
using System.Collections.Generic;
using System.Data;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Policy;

[Trait("Category", "Security")]
public sealed class ConsentResolutionGrantKindTests
{
    private static readonly TableIdentifier Table = new("finance", "dbo", "invoices");
    private static readonly Sid UserSid = new("S-1-5-21-1001");
    private static readonly ConsentResolutionService Service = new();

    private static Consent BuildConsent(PermissionGrantKind kind) => new()
    {
        TenantId = Autheris.Tests.Common.TestTenants.Default,
        TableIdentifier = Table,
        Effect = ConsentEffect.Allow,
        GranteeType = GranteeType.User,
        GranteeSid = UserSid,
        ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
        ValidTo = DateTimeOffset.UtcNow.AddDays(30),
        IsRevoked = false,
        GrantKind = kind
    };

    [Fact]
    public void Catalog_and_unmask_never_give_rows()
    {
        var unmaskDecision = Service.ResolveAccess(
            UserSid,
            new HashSet<Sid>(),
            new HashSet<string>(),
            Table,
            [BuildConsent(PermissionGrantKind.Unmask)]);
        unmaskDecision.IsAllowed.ShouldBeFalse();

        var catalogDecision = Service.ResolveAccess(
            UserSid,
            new HashSet<Sid>(),
            new HashSet<string>(),
            Table,
            [BuildConsent(PermissionGrantKind.Catalog)]);
        catalogDecision.IsAllowed.ShouldBeFalse();

        var dataDecision = Service.ResolveAccess(
            UserSid,
            new HashSet<Sid>(),
            new HashSet<string>(),
            Table,
            [BuildConsent(PermissionGrantKind.Data)]);
        dataDecision.IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public void Unknown_kind_grants_nothing()
    {
        var decision = Service.ResolveAccess(
            UserSid,
            new HashSet<Sid>(),
            new HashSet<string>(),
            Table,
            [BuildConsent(PermissionGrantKind.Unknown)]);
        decision.IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public void Null_kind_grants_nothing()
    {
        ConsentWorkflowSqlMapping.ParseGrantKindOrUnknown(null).ShouldBe(PermissionGrantKind.Unknown);
        ConsentWorkflowSqlMapping.ParseGrantKindOrUnknown("").ShouldBe(PermissionGrantKind.Unknown);
        ConsentWorkflowSqlMapping.ParseGrantKindOrUnknown("NOT_A_KIND").ShouldBe(PermissionGrantKind.Unknown);

        using var table = new DataTable();
        table.Columns.Add("grant_kind", typeof(string));
        table.Rows.Add(DBNull.Value);
        using var reader = table.CreateDataReader();
        reader.Read().ShouldBeTrue();
        var parsedKind = ConsentWorkflowSql.ReadGrantKind(reader, 0);
        parsedKind.ShouldBe(PermissionGrantKind.Unknown);

        var decision = Service.ResolveAccess(
            UserSid,
            new HashSet<Sid>(),
            new HashSet<string>(),
            Table,
            [BuildConsent(parsedKind)]);
        decision.IsAllowed.ShouldBeFalse();
    }
}
