using System.Security.Claims;
using System.Text.Json;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Domains;
using Autheris.Application.Governance.Paging;
using Autheris.Application.Security;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Tests.Common.Fakes;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance.Protection;

[Trait("Category", "Security")]
public sealed class ProtectionAuditTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly Sid GovSid = new("S-1-5-21-1001-2002-3003-10");
    private static readonly Sid OwnerGroupSid = new("S-1-5-21-1001-2002-3003-512");
    private static readonly DateTimeOffset T0 = new(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);

    private static SecurityPrincipalContext GovCaller()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new(ClaimTypes.PrimarySid, GovSid.Value),
            new("tenant_id", TenantA.Value),
            new(ClaimTypes.Role, "GovernanceAdmin")
        ], "Bearer"));
        return SecurityPrincipalContext.FromPrincipal(principal, overrideTenant: TenantA) with
        {
            GroupResolutionStatus = GroupResolutionStatus.Complete
        };
    }

    private static (GovernanceDomainService Service, IGovernanceDomainRepository Domains, IConsentRepository Consents, InMemoryAuditWriter Audits) Build(
        Guid domainId,
        Guid tableId,
        TableIdentifier tableIdentifier,
        ProtectionClass domainDefault = ProtectionClass.Confidential)
    {
        var domains = Substitute.For<IGovernanceDomainRepository>();
        var tables = new InMemoryTableMetadataRepository(new TableMetadata
        {
            Identifier = tableIdentifier,
            OwnerTenantId = TenantA.Value,
            Table = new Table { Id = tableId, SourceName = tableIdentifier.Domain, SchemaName = tableIdentifier.Schema, TableName = tableIdentifier.TableName, IsActive = true },
            Columns = [new TableColumn { ColumnName = "id" }, new TableColumn { ColumnName = "ssn" }]
        });
        var consents = Substitute.For<IConsentRepository>();
        var audit = new InMemoryAuditWriter();

        domains.GetContainerForTableAsync(TenantA, tableId, Arg.Any<CancellationToken>())
            .Returns(new ContainerDefaults(new ContainerRef(ContainerKind.Domain, domainId), OwnerGroupSid, domainDefault, CatalogVisibilityMode.OnRequest, IsValid: true));

        var caps = new GovernanceCapabilities(domainRepository: domains);
        var service = new GovernanceDomainService(domains, tables, consents, audit, caps, timeProvider: new FakeTimeProvider(T0));
        return (service, domains, consents, audit);
    }

    [Fact]
    public async Task Table_looser_than_container_audited()
    {
        var domainId = Guid.NewGuid();
        var tableId = Guid.NewGuid();
        var tableRef = new TableIdentifier("crm", "public", "orders");
        var (service, domains, _, audits) = Build(domainId, tableId, tableRef, domainDefault: ProtectionClass.Confidential);

        domains.GetProtectionAsync(TenantA, tableId, Arg.Any<CancellationToken>())
            .Returns((null, new Dictionary<string, ObjectProtection>()));

        var res = await service.SetTableProtectionAsync(GovCaller(), TenantA, tableRef.ToString(), ProtectionClass.Public, null);

        res.Status.ShouldBe(DomainServiceStatus.Ok);
        res.Value!.LooserThanContainer.ShouldBeTrue();
        var entry = audits.Entries.Single(a => a.EventType == AuditEventTypes.ProtectionClassChanged);
        using var doc = JsonDocument.Parse(entry.DetailsJson);
        doc.RootElement.GetProperty("looserThanContainer").GetBoolean().ShouldBeTrue();
        doc.RootElement.GetProperty("afterClass").GetString().ShouldBe("PUBLIC");
        doc.RootElement.GetProperty("containerDefaultClass").GetString().ShouldBe("CONFIDENTIAL");
    }

    [Fact]
    public async Task Reclassify_reports_covering_grants()
    {
        var domainId = Guid.NewGuid();
        var tableId = Guid.NewGuid();
        var tableRef = new TableIdentifier("crm", "public", "employees");
        var (service, domains, consents, audits) = Build(domainId, tableId, tableRef, domainDefault: ProtectionClass.Internal);

        domains.GetProtectionAsync(TenantA, tableId, Arg.Any<CancellationToken>())
            .Returns((new ObjectProtection(ProtectionClass.Internal), new Dictionary<string, ObjectProtection>()));

        var grant1 = new Consent
        {
            Id = Guid.NewGuid(),
            TableIdentifier = tableRef,
            TenantId = TenantA,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid("S-1-5-21-50"),
            ValidFrom = T0.AddDays(-1),
            ValidTo = T0.AddDays(30)
        };
        var grant2 = new Consent
        {
            Id = Guid.NewGuid(),
            TableIdentifier = tableRef,
            TenantId = TenantA,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.Group,
            GranteeSid = new Sid("S-1-5-21-60"),
            ValidFrom = T0.AddDays(-1),
            ValidTo = T0.AddDays(30)
        };
        var columnDeniedGrant = new Consent
        {
            Id = Guid.NewGuid(),
            TableIdentifier = tableRef,
            TenantId = TenantA,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid("S-1-5-21-70"),
            ValidFrom = T0.AddDays(-1),
            ValidTo = T0.AddDays(30),
            ColumnRules = [new ConsentColumnRule { ColumnName = "ssn", AccessLevel = ColumnAccessLevel.Deny }]
        };

        consents.GetConsentPageAsync(Arg.Any<ConsentPageQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ConsentPage([grant1, grant2, columnDeniedGrant], NextAfterId: null));

        var res = await service.SetColumnProtectionAsync(GovCaller(), TenantA, tableRef.ToString(), "ssn", ProtectionClass.Secret, "PII_SSN");

        res.Status.ShouldBe(DomainServiceStatus.Ok);
        res.Value!.Effective.TableClass.ShouldBe(ProtectionClass.Secret);
        res.Value.CoveringGrantsCount.ShouldBe(2);

        var rollupAudit = audits.Entries.Single(a =>
            a.EventType == AuditEventTypes.ProtectionClassChanged && a.TargetColumn is null);
        using var doc = JsonDocument.Parse(rollupAudit.DetailsJson);
        doc.RootElement.GetProperty("reason").GetString().ShouldBe("column_rollup");
        doc.RootElement.GetProperty("coveringGrantsCount").GetInt32().ShouldBe(2);
        doc.RootElement.GetProperty("ownerGroupSid").GetString().ShouldBe(OwnerGroupSid.Value);
        doc.RootElement.GetProperty("afterClass").GetString().ShouldBe("SECRET");

        audits.Entries.ShouldContain(a => a.EventType == AuditEventTypes.MaskCategoryChanged && a.TargetColumn == "ssn");
    }
}
