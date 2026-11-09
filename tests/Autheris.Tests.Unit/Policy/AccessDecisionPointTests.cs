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
using Autheris.Tests.Unit.Governance;
using Autheris.Tests.Unit.TestDoubles;
using Microsoft.Extensions.Options;
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
        decision.RowFilterSql.ShouldBeNull();
        decision.ColumnAccess.ShouldBeEmpty();
    }

    [Fact]
    public void Column_access_levels_are_ordered_from_most_to_least_restrictive()
    {
        (ColumnAccessLevel.Deny < ColumnAccessLevel.Mask).ShouldBeTrue();
        (ColumnAccessLevel.Mask < ColumnAccessLevel.Clear).ShouldBeTrue();
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

        var consentRepo = new FakeConsentRepository();
        await consentRepo.CreateConsentAsync(consent);

        var adp = new AccessDecisionPoint(
            Options.Create(new GatewayOptions { Tenancy = new TenancyOptions { DefaultTenantId = "default-tenant" } }),
            new StubPolicyEnforcementService(abac),
            new ConsentResolutionService(),
            consentRepo,
            metadataRepo: new StubTableMetadataRepository(meta),
            transportGate: AllowAllTransportGate.Instance);

        return await adp.DecideAsync(new AccessRequest(
            principal ?? Analyst(),
            Tenant,
            OrdersId,
            operation,
            ["id", "name", "secret"],
            AccessTransport.Lakehouse,
            TableMetadata: meta));
    }

    private sealed class StubPolicyEnforcementService(TableAccessDecision? decision) : IPolicyEnforcementService
    {
        public ValueTask<TableAccessDecision> EvaluatePolicyAsync(SecurityEvaluationContext context, CancellationToken ct = default) =>
            decision is null ? throw new InvalidOperationException("No ABAC decision configured.") : ValueTask.FromResult(decision);

        public bool HasPolicies(TenantId tenant) => decision is not null;

        public Task ReloadPoliciesAsync(TenantId tenant, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubTableMetadataRepository(TableMetadata metadata) : ITableMetadataRepository
    {
        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default) =>
            Task.FromResult<TableMetadata?>(metadata);

        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TableMetadata>>([metadata]);

        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(
            TableIdentifier table,
            IReadOnlyList<string> primaryKey,
            IReadOnlyList<TableIndexDefinition> indexes,
            IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
