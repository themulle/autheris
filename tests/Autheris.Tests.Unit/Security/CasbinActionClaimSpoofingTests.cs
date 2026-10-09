namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class CasbinActionClaimSpoofingTests
{
    private const string Tenant = "tenant_a";
    private static readonly TableIdentifier Table = new("sales", "public", "orders");

    private static TableMetadata Meta() => new()
    {
        Identifier = Table,
        Table = new Table { TableName = "orders", SchemaName = "public", SourceType = "PostgreSQL" },
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "int" },
            new TableColumn { ColumnName = "tenant_id", DataType = "varchar" }
        ]
    };

    private static ClaimsPrincipal User(params Claim[] extra)
    {
        var claims = new List<Claim> { new(ClaimTypes.PrimarySid, "S-1-5-21-USER"), new("tenant_id", Tenant) };
        claims.AddRange(extra);
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
    }

    private static IConsentRepository NoConsents()
    {
        var repo = Substitute.For<IConsentRepository>();
        repo.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>(Array.Empty<Consent>()));
        return repo;
    }

    private static IConsentResolutionService ResolvesTo(bool allowed)
    {
        var resolution = Substitute.For<IConsentResolutionService>();
        resolution.ResolveAccess(Arg.Any<Sid>(), Arg.Any<IReadOnlySet<Sid>>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<Consent>>(), Arg.Any<DatabaseDialect>())
            .Returns(allowed
                ? TableAccessDecision.Allowed(Table, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true)
                : TableAccessDecision.Denied(Table, "no consent"));
        return resolution;
    }

    [Fact]
    public async Task Client_Token_Action_Claim_Is_Filtered_From_EvaluationContext()
    {
        var casbinMock = Substitute.For<IPolicyEnforcementService>();
        casbinMock.HasPolicies(Arg.Any<TenantId>()).Returns(true);
        SecurityEvaluationContext? capturedContext = null;

        casbinMock.EvaluatePolicyAsync(Arg.Any<SecurityEvaluationContext>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedContext = callInfo.Arg<SecurityEvaluationContext>();
                return ValueTask.FromResult(TableAccessDecision.Allowed(Table, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true));
            });

        var policy = new TableAccessPolicy(
            NoConsents(),
            ResolvesTo(true),
            cacheService: null,
            casbinMock,
            rebacEvaluator: null,
            clientIpResolver: null,
            options: new GatewayOptions(),
            mandatoryFilters: NullMandatoryRowFilterResolver.Instance);

        var user = User(new Claim("action", "custom_action"), new Claim("gql.action", "write"));
        var query = TableAccessQuery.ForPrincipal(user, new Sid("S-1-5-21-USER"), new TenantId(Tenant), Meta());

        await policy.DecideAsync(query, CancellationToken.None);

        capturedContext.ShouldNotBeNull();
        capturedContext.Attributes.ShouldNotBeNull();
        capturedContext.Attributes.ContainsKey("action").ShouldBeFalse();
        capturedContext.Attributes.ContainsKey("gql.action").ShouldBeFalse();
    }

    [Fact]
    public async Task Server_ExtraAttributes_Action_Is_Preserved()
    {
        var casbinMock = Substitute.For<IPolicyEnforcementService>();
        casbinMock.HasPolicies(Arg.Any<TenantId>()).Returns(true);
        SecurityEvaluationContext? capturedContext = null;

        casbinMock.EvaluatePolicyAsync(Arg.Any<SecurityEvaluationContext>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedContext = callInfo.Arg<SecurityEvaluationContext>();
                return ValueTask.FromResult(TableAccessDecision.Allowed(Table, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true));
            });

        var policy = new TableAccessPolicy(
            NoConsents(),
            ResolvesTo(true),
            cacheService: null,
            casbinMock,
            rebacEvaluator: null,
            clientIpResolver: null,
            options: new GatewayOptions(),
            mandatoryFilters: NullMandatoryRowFilterResolver.Instance);

        var query = TableAccessQuery.ForPrincipal(
            User(),
            new Sid("S-1-5-21-USER"),
            new TenantId(Tenant),
            Meta(),
            extraAttributes: new Dictionary<string, object?> { ["action"] = "write" });

        await policy.DecideAsync(query, CancellationToken.None);

        capturedContext.ShouldNotBeNull();
        capturedContext.Attributes.ShouldNotBeNull();
        capturedContext.Attributes.TryGetValue("action", out var action).ShouldBeTrue();
        action.ShouldBe("write");
    }

    [Fact]
    public async Task CasbinEnforcementService_Deny_Read_Also_Blocks_Write()
    {
        using var service = new CasbinEnforcementService();

        // Add deny rule for "read" and allow for wildcard
        service.AddPolicy(new TenantId(Tenant), "S-1-5-21-RESTRICTED", Table.ToString(), "read", "true", "deny");
        service.AddPolicy(new TenantId(Tenant), "S-1-5-21-RESTRICTED", Table.ToString(), "*", "true", "allow");

        var contextWrite = new SecurityEvaluationContext(
            UserSid: new Sid("S-1-5-21-RESTRICTED"),
            GroupSids: [],
            Tenant: new TenantId(Tenant),
            TargetTable: Table,
            RequestedColumns: ["id"],
            ClientIp: IPAddress.Loopback,
            Timestamp: DateTimeOffset.UtcNow,
            PurposeId: null,
            Attributes: new Dictionary<string, object?> { ["action"] = "write" },
            TargetDialect: DatabaseDialect.PostgreSql);

        var decision = await service.EvaluatePolicyAsync(contextWrite);

        decision.IsAllowed.ShouldBeFalse();
        decision.DeniedReasons.ShouldContain(r => r.Contains("denied", StringComparison.OrdinalIgnoreCase));
    }
}
