using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Application.Services;
using Autheris.Application.Sql.Policy;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Governance;
using Xunit;

namespace Autheris.Tests.Unit.Policy;

/// <summary>WP-X0e: access-profile row filters become a typed policy, parsed once, fail closed on invalid text.</summary>
public sealed class AccessProfileRowPolicyTests
{
    private static readonly TableIdentifier Table = new("telemetry", "tem", "gps_position");
    private static readonly TenantId Tenant = new("liebherr");

    private static TableMetadata Metadata() => new()
    {
        Identifier = Table,
        Table = new Table { SourceName = "telemetry", SchemaName = "tem", TableName = "gps_position", SourceType = "PostgreSql", IsActive = true },
        Columns =
        [
            new TableColumn { ColumnName = "crane_id", DataType = "VARCHAR(64)" },
            new TableColumn { ColumnName = "status", DataType = "VARCHAR(32)" }
        ]
    };

    private static AccessProfile Profile(string id, MaskingPolicyMode mode, string? predicate) => new()
    {
        ProfileId = id,
        Name = "profile",
        TenantId = Tenant,
        MaskingMode = mode,
        TargetTables = ["tem.*"],
        RowFilterPredicate = predicate,
        Assignments = TestAssign.Users("S-1-5-21-DAVID"),
        CreatedAt = DateTimeOffset.UtcNow,
        ValidTo = DateTimeOffset.UtcNow.AddDays(1),
        Justification = "test",
        CreatedBy = "admin"
    };

    private static Task<TableAccessPolicy> CreatePolicyAsync(string predicate, IPolicyExpressionParser? parser) =>
        CreatePolicyAsync(parser, Profile("p1", MaskingPolicyMode.Unmasked, predicate));

    private static async Task<TableAccessPolicy> CreatePolicyAsync(IPolicyExpressionParser? parser, params AccessProfile[] profiles)
    {
        var repo = new InMemoryAccessProfileRepository();
        foreach (var profile in profiles)
        {
            await repo.UpsertProfileAsync(profile);
        }

        // SR-M-07: an access profile never creates an allow; the underlying consent decision allows the table here.
        var resolution = Substitute.For<IConsentResolutionService>();
        resolution.ResolveAccess(default!, default!, default!, default, default!, default, default, default)
            .ReturnsForAnyArgs(ci => TableAccessDecision.Allowed(
                ci.ArgAt<TableIdentifier>(3), new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true));
        return new TableAccessPolicy(
            Substitute.For<IConsentRepository>(),
            resolution,
            Substitute.For<IConsentCacheService>(),
            Substitute.For<IPolicyEnforcementService>(),
            Substitute.For<IRebacEvaluator>(),
            Substitute.For<IClientIpResolver>(),
            new GatewayOptions(),
            NullMandatoryRowFilterResolver.Instance,
            accessProfileRepository: repo,
            rowPolicyFactory: new RowPolicyFactory(new PolicyPredicateRenderer(), parser));
    }

    private static TableAccessQuery Query() => new(
        UserSid: new Sid("S-1-5-21-DAVID"),
        Tenant: Tenant,
        GroupSids: new HashSet<Sid>(),
        Roles: new HashSet<string>(),
        Metadata: Metadata());

    [Fact]
    public async Task AccessProfilePredicate_ParsedOnce_FailClosedOnInvalid_ParsedOnce()
    {
        var parser = Substitute.For<IPolicyExpressionParser>();
        parser.Parse(Arg.Any<string>(), Arg.Any<PolicyParseContext>()).Returns(PolicyPredicate.DenyAll);
        var policy = await CreatePolicyAsync("status <> 'X'", parser);

        var decision = await policy.DecideAsync(Query(), CancellationToken.None);

        parser.Received(1).Parse("status <> 'X'", Arg.Any<PolicyParseContext>());
        decision.IsAllowed.ShouldBeTrue();
        decision.RowPolicy.ShouldNotBeNull();
        decision.RowPolicy!.IsDenyAll.ShouldBeTrue();
    }

    [Fact]
    public async Task AccessProfilePredicate_FailClosedOnInvalid()
    {
        var parser = Substitute.For<IPolicyExpressionParser>();
        parser.Parse(Arg.Any<string>(), Arg.Any<PolicyParseContext>()).Returns(_ => throw new PolicyParseException("invalid"));
        var policy = await CreatePolicyAsync("status <> 'SECRET-LITERAL' )(", parser);

        var decision = await policy.DecideAsync(Query(), CancellationToken.None);

        decision.IsAllowed.ShouldBeFalse();
        string.Join(' ', decision.DeniedReasons).ShouldNotContain("SECRET-LITERAL");
    }

    [Fact]
    public async Task AccessProfilePredicate_RealParser_IsTypedAndBound()
    {
        var parser = new PolicyExpressionParser(new FastSqlEngine());
        var policy = await CreatePolicyAsync("status <> 'DELIVERED'", parser);

        var decision = await policy.DecideAsync(Query(), CancellationToken.None);

        decision.IsAllowed.ShouldBeTrue();
        decision.RowPolicy.ShouldNotBeNull();
        decision.CombinedRowFilterSql.ShouldNotBeNullOrWhiteSpace();
        decision.CombinedRowFilterSql.ShouldContain("status");
        decision.CombinedRowFilterSql.ShouldNotContain("DELIVERED");
        decision.RowFilterParameters.ShouldNotBeNull();
        decision.RowFilterParameters!.Values.ShouldContain("DELIVERED");
        decision.RequireTypedRowPolicy().IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public async Task AccessProfilePredicate_UnknownColumn_FailsClosed()
    {
        var policy = await CreatePolicyAsync("no_such_column = 1", new PolicyExpressionParser(new FastSqlEngine()));

        var decision = await policy.DecideAsync(Query(), CancellationToken.None);

        decision.IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public async Task AccessProfilePredicate_WithoutParser_StaysLegacyStringAndIsDeniedOnCompilerPath()
    {
        var policy = await CreatePolicyAsync("status <> 'DELIVERED'", parser: null);

        var decision = await policy.DecideAsync(Query(), CancellationToken.None);

        decision.IsAllowed.ShouldBeTrue();
        decision.RowPolicy.ShouldBeNull();
        decision.CombinedRowFilterSql.ShouldBe("status <> 'DELIVERED'");
        decision.RequireTypedRowPolicy().IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public async Task GOV06_MostRestrictiveProfileDoesNotDropTheRowFilterOfAnotherProfile()
    {
        // The Strict profile sorts first and has no row filter; the Default profile carries the data owner's row restriction.
        var policy = await CreatePolicyAsync(
            new PolicyExpressionParser(new FastSqlEngine()),
            Profile("a-strict", MaskingPolicyMode.Strict, predicate: null),
            Profile("b-default", MaskingPolicyMode.Default, "status <> 'DELIVERED'"));

        var decision = await policy.DecideAsync(Query(), CancellationToken.None);

        decision.IsAllowed.ShouldBeTrue();
        decision.CombinedRowFilterSql.ShouldNotBeNullOrWhiteSpace();
        decision.CombinedRowFilterSql.ShouldContain("status");
    }

    [Fact]
    public async Task GOV06_UnparsableRowFilterOfAnyMatchedProfile_DeniesTheTable()
    {
        var policy = await CreatePolicyAsync(
            new PolicyExpressionParser(new FastSqlEngine()),
            Profile("a-strict", MaskingPolicyMode.Strict, "status <> 'X'"),
            Profile("b-default", MaskingPolicyMode.Default, "no_such_column = 1"));

        var decision = await policy.DecideAsync(Query(), CancellationToken.None);

        decision.IsAllowed.ShouldBeFalse();
    }
}
