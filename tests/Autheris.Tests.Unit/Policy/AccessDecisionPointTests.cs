namespace Autheris.Tests.Unit.Policy;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.DataSources;
using Autheris.Application.Governance;
using Autheris.Application.Policy;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class AccessDecisionPointTests
{
    private static readonly TableIdentifier OrdersId = new("tenant-1", "raw", "orders");
    private static readonly TenantId Tenant = new("tenant-1");

    [Fact]
    [Trait("Category", "Security")]
    public void Constructor_without_policy_service_throws()
    {
        var options = Options.Create(new GatewayOptions());

        var ex = Should.Throw<ArgumentNullException>(() =>
            new AccessDecisionPoint(
                options,
                policyEnforcementService: null!));

        ex.ParamName.ShouldBe("policyEnforcementService");
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "SEC-50-10")]
    public async Task Read_propagates_abac_row_filter()
    {
        var decision = await DecideAsync(AccessOperation.Read, Abac(rowFilter: "region = 'EU'"));

        decision.RowFilterSql.ShouldNotBeNull().ShouldContain("region = 'EU'");
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "SEC-50-10")]
    public async Task Write_propagates_abac_row_filter()
    {
        var decision = await DecideAsync(AccessOperation.Write, Abac(rowFilter: "region = 'EU'"));

        decision.RowFilterSql.ShouldNotBeNull().ShouldContain("region = 'EU'");
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "SEC-50-10")]
    public async Task Read_applies_abac_masking_rule()
    {
        var abac = Abac(columnAccess: new Dictionary<string, ColumnAccessLevel> { ["name"] = ColumnAccessLevel.Mask });

        var decision = await DecideAsync(AccessOperation.Read, abac);

        decision.IsAllowed.ShouldBeTrue();
        decision.ColumnAccess["name"].ShouldBe(ColumnAccessLevel.Mask);
        decision.ColumnAccess.GetValueOrDefault("id", ColumnAccessLevel.Clear).ShouldBe(ColumnAccessLevel.Clear);
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "SEC-50-10")]
    public void Combined_row_filter_parenthesizes_operands()
    {
        RowFilterCombiner.And("a OR b", "c").ShouldBe("(a OR b) AND (c)");
        RowFilterCombiner.And("a OR b", "   ").ShouldBe("(a OR b)");
        RowFilterCombiner.And(null, "c").ShouldBe("(c)");
        RowFilterCombiner.And(string.Empty, null).ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "SEC-50-10")]
    public async Task Masking_merge_keeps_most_restrictive_level()
    {
        // "secret" is catalog-sensitive (effective Mask for the consent) and the ABAC rule denies it: Deny wins.
        var abac = Abac(columnAccess: new Dictionary<string, ColumnAccessLevel>
        {
            ["secret"] = ColumnAccessLevel.Deny,
            ["name"] = ColumnAccessLevel.Mask,
            ["id"] = ColumnAccessLevel.Clear
        });

        var decision = await DecideAsync(AccessOperation.Read, abac);

        decision.ColumnAccess["secret"].ShouldBe(ColumnAccessLevel.Deny);
        decision.ColumnAccess["name"].ShouldBe(ColumnAccessLevel.Mask);
        decision.ColumnAccess.GetValueOrDefault("id", ColumnAccessLevel.Clear).ShouldBe(ColumnAccessLevel.Clear);
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "SEC-AZ-04")]
    public async Task No_sid_claim_denies()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "analyst@corp.com")], "Bearer"));

        var decision = await DecideAsync(AccessOperation.Read, abac: null, principal);

        decision.IsAllowed.ShouldBeFalse();
        decision.DeniedReasons.ShouldContain("missing subject identifier");
    }

    private static TableAccessDecision Abac(
        string? rowFilter = null,
        IReadOnlyDictionary<string, ColumnAccessLevel>? columnAccess = null) =>
        new(
            OrdersId,
            true,
            columnAccess ?? new Dictionary<string, ColumnAccessLevel>(),
            rowFilter,
            [],
            HasUnconstrainedColumnAllow: true);

    private static ClaimsPrincipal Analyst() => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, "analyst@corp.com"), new Claim(ClaimTypes.PrimarySid, "analyst@corp.com")],
        "Bearer"));

    private static async Task<AccessDecision> DecideAsync(
        AccessOperation operation,
        TableAccessDecision? abac,
        ClaimsPrincipal? principal = null)
    {
        var meta = new TableMetadata
        {
            Identifier = OrdersId,
            Table = new Table { SourceName = "tenant-1", SchemaName = "raw", TableName = "orders", DataSourceType = DataSourceType.LakehouseIceberg },
            Columns =
            [
                new TableColumn { ColumnName = "id" },
                new TableColumn { ColumnName = "name" },
                new TableColumn { ColumnName = "secret", IsSensitive = true }
            ]
        };

        var consent = new Consent
        {
            TableIdentifier = OrdersId,
            TenantId = Tenant,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid("analyst@corp.com"),
            Effect = ConsentEffect.Allow,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1),
            ColumnRules = Array.Empty<ConsentColumnRule>()
        };

        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        metadataRepo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TableMetadata?>(meta));

        var consentRepo = Substitute.For<IConsentRepository>();
        consentRepo.GetActiveConsentsForSubjectsAsync(
                Arg.Any<IReadOnlyList<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>([consent]));

        var policy = Substitute.For<IPolicyEnforcementService>();
        policy.HasPolicies(Arg.Any<TenantId>()).Returns(abac != null);
        if (abac != null)
        {
            policy.EvaluatePolicyAsync(Arg.Any<SecurityEvaluationContext>(), Arg.Any<CancellationToken>())
                .Returns(new ValueTask<TableAccessDecision>(abac));
        }

        var adp = new AccessDecisionPoint(
            Options.Create(new GatewayOptions { Tenancy = new TenancyOptions { DefaultTenantId = "default-tenant" } }),
            policy,
            new ConsentResolutionService(),
            consentRepo,
            metadataRepo: metadataRepo,
            transportGate: Substitute.For<IDataSourceTransportGate>());

        return await adp.DecideAsync(new AccessRequest(
            principal ?? Analyst(),
            Tenant,
            OrdersId,
            operation,
            ["id", "name", "secret"],
            AccessTransport.Lakehouse,
            TableMetadata: meta));
    }
}
