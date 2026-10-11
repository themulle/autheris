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

    private static async Task<TableAccessPolicy> CreatePolicyAsync(string predicate, IPolicyExpressionParser? parser)
    {
        var repo = new InMemoryAccessProfileRepository();
        await repo.UpsertProfileAsync(new AccessProfile
        {
            ProfileId = "p1",
            Name = "profile",
            TenantId = Tenant,
            MaskingMode = MaskingPolicyMode.Unmasked,
            TargetTables = ["tem.*"],
            RowFilterPredicate = predicate,
            AssignedSubjects = ["S-1-5-21-DAVID"],
            CreatedAt = DateTimeOffset.UtcNow,
            ValidTo = DateTimeOffset.UtcNow.AddDays(1),
            Justification = "test",
            CreatedBy = "admin"
        });
        return new TableAccessPolicy(
            Substitute.For<IConsentRepository>(),
            Substitute.For<IConsentResolutionService>(),
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
}
