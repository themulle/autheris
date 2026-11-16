namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Contracts;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Policy.Services;
using Autheris.Application.Services;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class TableWritePermissionTests
{
    private static readonly TableIdentifier OrdersTable = new("sales", "dbo", "orders");
    private static readonly Sid AliceSid = new("S-1-5-21-ALICE");
    private static readonly TenantId TenantA = new("tenant-a");

    [Fact]
    public async Task AccessDecisionPoint_Denies_Write_With_ReadOnly_Allow_Consent()
    {
        var consentRepo = Substitute.For<IConsentRepository>();
        var allowConsent = new Consent
        {
            TableIdentifier = OrdersTable,
            TenantId = TenantA,
            GranteeType = GranteeType.User,
            GranteeSid = AliceSid,
            Effect = ConsentEffect.Allow,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1),
            ColumnRules = [new ConsentColumnRule { ColumnName = "amount", AccessLevel = ColumnAccessLevel.Clear }]
        };
        consentRepo.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<IReadOnlyList<Consent>>(new[] { allowConsent }.Where(c => c.TableIdentifier == ci.ArgAt<TableIdentifier>(1)).ToList()));

        var pdp = new AccessDecisionPoint(
            Options.Create(new GatewayOptions()),
            new ConsentResolutionService(),
            consentRepo,
            NullConsentCacheService.Instance,
            mandatoryFilters: NullMandatoryRowFilterResolver.Instance);

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, AliceSid.Value),
            new Claim("sub", AliceSid.Value),
            new Claim("tenant_id", TenantA.Value)
        ], "TestAuth");
        var user = Autheris.Tests.Unit.TestSupport.PipelinePrincipals.MarkComplete(new ClaimsPrincipal(identity));

        var tableMeta = new TableMetadata
        {
            Identifier = OrdersTable,
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "amount", DataType = "decimal" }
            ]
        };

        var subject = SecurityPrincipalContext.FromPrincipal(user, TenantA);
        var decision = await pdp.DecideAsync(new AccessRequest(subject, tableMeta, AccessOperation.Update, AccessTransport.WebSql, Columns: ["amount"]));
        decision.IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public async Task AccessDecisionPoint_Write_Enforces_SchemaContract_And_WriteGrant()
    {
        var consentRepo = Substitute.For<IConsentRepository>();
        var writeConsent = new Consent
        {
            TableIdentifier = OrdersTable,
            TenantId = TenantA,
            GranteeType = GranteeType.User,
            GranteeSid = AliceSid,
            Effect = ConsentEffect.Allow,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1),
            WriteRules = [new WriteRule(Guid.NewGuid(), WriteOperation.Update, ["id"], WriteRowScope.AllRows)]
        };
        consentRepo.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>([writeConsent]));

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, AliceSid.Value),
            new Claim("sub", AliceSid.Value),
            new Claim("tenant_id", TenantA.Value)
        ], "TestAuth");
        var user = Autheris.Tests.Unit.TestSupport.PipelinePrincipals.MarkComplete(new ClaimsPrincipal(identity));
        var tableMeta = new TableMetadata
        {
            Identifier = OrdersTable,
            Table = new Table { TableName = "orders", SchemaName = "dbo" },
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }]
        };

        var excludingContractManager = new FixedSchemaContractManager("customers");

        var pdpDisallowed = new AccessDecisionPoint(
            Options.Create(new GatewayOptions()),
            new ConsentResolutionService(),
            consentRepo,
            NullConsentCacheService.Instance,
            mandatoryFilters: NullMandatoryRowFilterResolver.Instance,
            contractManager: excludingContractManager);

        var subject = SecurityPrincipalContext.FromPrincipal(user, TenantA);
        var disallowed = await pdpDisallowed.DecideAsync(new AccessRequest(subject, tableMeta, AccessOperation.Update, AccessTransport.WebSql, Columns: ["id"]));
        disallowed.IsAllowed.ShouldBeFalse();

        var allowingContractManager = new FixedSchemaContractManager("orders");

        var pdpAllowed = new AccessDecisionPoint(
            Options.Create(new GatewayOptions()),
            new ConsentResolutionService(),
            consentRepo,
            NullConsentCacheService.Instance,
            mandatoryFilters: NullMandatoryRowFilterResolver.Instance,
            contractManager: allowingContractManager);

        var allowed = await pdpAllowed.DecideAsync(new AccessRequest(subject, tableMeta, AccessOperation.Update, AccessTransport.WebSql, Columns: ["id"]));
        allowed.IsAllowed.ShouldBeTrue();
        allowed.WriteGrant.ShouldNotBeNull();
        allowed.WriteGrant!.WritableColumns.ShouldContain("id");
    }

    private sealed class FixedSchemaContractManager(string allowedTable) : ISchemaContractManager
    {
        public bool IsEnabled => true;
        public string DefaultContract => "partner";
        public bool HasContract(string contractName) => string.Equals(contractName, "partner", StringComparison.Ordinal);
        public IReadOnlyList<string> GetAvailableContracts() => ["partner"];
        public SchemaContractDefinition? GetContract(string contractName) =>
            HasContract(contractName) ? new SchemaContractDefinition("partner", allowedTables: [allowedTable]) : null;
        public string FilterSchemaSdl(string originalSdl, SchemaContractDefinition contract) => originalSdl;
    }
}
