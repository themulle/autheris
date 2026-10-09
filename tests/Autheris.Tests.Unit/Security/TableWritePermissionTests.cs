namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Services;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class TableWritePermissionTests
{
    private static readonly TableIdentifier OrdersTable = new("sales", "dbo", "orders");
    private static readonly Sid AliceSid = new("S-1-5-21-ALICE");
    private static readonly TenantId TenantA = new("tenant-a");

    private static SecurityEvaluationContext CreateContext(TableIdentifier table, string action)
    {
        return new SecurityEvaluationContext(
            UserSid: AliceSid,
            GroupSids: Array.Empty<Sid>(),
            Tenant: TenantA,
            TargetTable: table,
            RequestedColumns: new[] { "id", "amount" },
            ClientIp: IPAddress.Loopback,
            Timestamp: DateTimeOffset.UtcNow,
            PurposeId: null,
            Attributes: new Dictionary<string, object?>
            {
                ["action"] = action,
                ["gql.action"] = action
            });
    }

    [Fact]
    public async Task User_With_Only_Read_Policy_Is_Denied_For_Write_And_Allowed_For_Read()
    {
        using var casbin = new CasbinEnforcementService();
        casbin.AddPolicy(TenantA, AliceSid.Value, OrdersTable.ToString(), "read", "true", "allow");

        var writeDecision = await casbin.EvaluatePolicyAsync(CreateContext(OrdersTable, "write"));
        writeDecision.IsAllowed.ShouldBeFalse();

        var readDecision = await casbin.EvaluatePolicyAsync(CreateContext(OrdersTable, "read"));
        readDecision.IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public async Task User_With_Only_Write_Policy_Is_Allowed_For_Write_And_Denied_For_Read()
    {
        using var casbin = new CasbinEnforcementService();
        casbin.AddPolicy(TenantA, AliceSid.Value, OrdersTable.ToString(), "write", "true", "allow");

        var writeDecision = await casbin.EvaluatePolicyAsync(CreateContext(OrdersTable, "write"));
        writeDecision.IsAllowed.ShouldBeTrue();

        var readDecision = await casbin.EvaluatePolicyAsync(CreateContext(OrdersTable, "read"));
        readDecision.IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public async Task User_With_Wildcard_Action_Is_Allowed_For_Both_Read_And_Write()
    {
        using var casbin = new CasbinEnforcementService();
        casbin.AddPolicy(TenantA, AliceSid.Value, OrdersTable.ToString(), "*", "true", "allow");

        var writeDecision = await casbin.EvaluatePolicyAsync(CreateContext(OrdersTable, "write"));
        writeDecision.IsAllowed.ShouldBeTrue();

        var readDecision = await casbin.EvaluatePolicyAsync(CreateContext(OrdersTable, "read"));
        readDecision.IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public async Task User_With_Explicit_Deny_For_Write_Is_Denied_For_Write_While_Read_Remains_Allowed()
    {
        using var casbin = new CasbinEnforcementService();
        casbin.AddPolicy(TenantA, AliceSid.Value, OrdersTable.ToString(), "*", "true", "allow");
        casbin.AddPolicy(TenantA, AliceSid.Value, OrdersTable.ToString(), "write", "true", "deny");

        var writeDecision = await casbin.EvaluatePolicyAsync(CreateContext(OrdersTable, "write"));
        writeDecision.IsAllowed.ShouldBeFalse();

        var readDecision = await casbin.EvaluatePolicyAsync(CreateContext(OrdersTable, "read"));
        readDecision.IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public async Task TableAccessPolicy_CanWriteTableAsync_Evaluates_Granular_Write_Permission()
    {
        using var casbin = new CasbinEnforcementService();
        casbin.AddPolicy(TenantA, AliceSid.Value, OrdersTable.ToString(), "read", "true", "allow");

        var consentRepo = Substitute.For<IConsentRepository>();
        var allowConsent = new Consent
        {
            TableIdentifier = OrdersTable,
            TenantId = TenantA,
            GranteeType = GranteeType.User,
            GranteeSid = AliceSid,
            Effect = ConsentEffect.Allow,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1)
        };
        consentRepo.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<IReadOnlyList<Consent>>(new[] { allowConsent }.Where(c => c.TableIdentifier == ci.ArgAt<TableIdentifier>(1)).ToList()));

        var tableAccessPolicy = new TableAccessPolicy(
            consentRepo,
            new ConsentResolutionService(),
            cacheService: null,
            policyEnforcementService: casbin,
            rebacEvaluator: null,
            clientIpResolver: null,
            options: new GatewayOptions(),
            mandatoryFilters: NullMandatoryRowFilterResolver.Instance);

        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.PrimarySid, AliceSid.Value),
            new Claim("sub", AliceSid.Value)
        }, "TestAuth");
        var user = new ClaimsPrincipal(identity);

        var tableMeta = new TableMetadata
        {
            Identifier = OrdersTable,
            Columns = new[]
            {
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "amount", DataType = "decimal" }
            }
        };

        // When user only has "read" in Casbin:
        var canWriteInitial = await tableAccessPolicy.CanWriteTableAsync(user, TenantA, tableMeta);
        canWriteInitial.ShouldBeFalse();

        // Grant explicit "write" permission in Casbin:
        casbin.AddPolicy(TenantA, AliceSid.Value, OrdersTable.ToString(), "write", "true", "allow");

        var canWriteAfterGrant = await tableAccessPolicy.CanWriteTableAsync(user, TenantA, tableMeta);
        canWriteAfterGrant.ShouldBeTrue();
    }

    [Fact]
    public async Task TableAccessPolicy_CanWriteTableAsync_WithContract_EnforcesAllowedTables()
    {
        using var casbin = new CasbinEnforcementService();
        casbin.AddPolicy(TenantA, AliceSid.Value, OrdersTable.ToString(), "write", "true", "allow");

        var consentRepo = Substitute.For<IConsentRepository>();
        var allowConsent = new Consent
        {
            TableIdentifier = OrdersTable,
            TenantId = TenantA,
            GranteeType = GranteeType.User,
            GranteeSid = AliceSid,
            Effect = ConsentEffect.Allow,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1)
        };
        consentRepo.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<IReadOnlyList<Consent>>(new[] { allowConsent }));

        var tableAccessPolicy = new TableAccessPolicy(
            consentRepo,
            new ConsentResolutionService(),
            cacheService: null,
            policyEnforcementService: casbin,
            rebacEvaluator: null,
            clientIpResolver: null,
            options: new GatewayOptions(),
            mandatoryFilters: NullMandatoryRowFilterResolver.Instance);

        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.PrimarySid, AliceSid.Value),
            new Claim("sub", AliceSid.Value)
        }, "TestAuth");
        var user = new ClaimsPrincipal(identity);

        var tableMeta = new TableMetadata
        {
            Identifier = OrdersTable,
            Table = new Table { TableName = "orders", SchemaName = "dbo" },
            Columns = new[]
            {
                new TableColumn { ColumnName = "id", DataType = "int" }
            }
        };

        // When contract allows only "customers"
        var contractExcluding = new Autheris.Application.Governance.Contracts.SchemaContractDefinition("partner", allowedTables: ["customers"]);
        var canWriteDisallowed = await tableAccessPolicy.CanWriteTableAsync(user, TenantA, tableMeta, contractExcluding, default);
        canWriteDisallowed.ShouldBeFalse();

        // When contract allows "orders"
        var contractAllowing = new Autheris.Application.Governance.Contracts.SchemaContractDefinition("partner", allowedTables: ["orders"]);
        var canWriteAllowed = await tableAccessPolicy.CanWriteTableAsync(user, TenantA, tableMeta, contractAllowing, default);
        canWriteAllowed.ShouldBeTrue();
    }
}
