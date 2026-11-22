#pragma warning disable CA2012 // NSubstitute .Returns(...) configures a ValueTask member; the instance is not consumed here.
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Contracts;
using Autheris.Application.Plugins.Auth;
using Autheris.Application.Policy;
using Autheris.Application.Policy.Services;
using Autheris.Application.Services;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Plugins.Auth;
using Autheris.Domain.Security;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Policy;

[Trait("Category", "Security")]
public sealed class SchemaContractProtectionClassTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly Sid UserSid = new("S-1-5-21-100-200-300-44");
    private static readonly TableIdentifier TableId = new("crm", "public", "customers");

    private static EffectiveProtection Protection(ProtectionClass cls) =>
        new(
            new ContainerRef(ContainerKind.Domain, Guid.NewGuid()),
            null,
            cls,
            cls,
            null,
            new Dictionary<string, ColumnProtection>(),
            CatalogVisibilityMode.OnRequest);

    [Fact]
    public void IncludedClasses_and_ExcludedClasses_filter_visibility_without_tags()
    {
        var pub = new TableMetadata
        {
            Identifier = new TableIdentifier("crm", "public", "pub_items"),
            Table = new Table { TableName = "pub_items", Tags = ["SECRET", "PII"] }
        };
        var sec = new TableMetadata
        {
            Identifier = new TableIdentifier("crm", "public", "sec_items"),
            Table = new Table { TableName = "sec_items", Tags = ["PUBLIC"] }
        };

        var classes = new Dictionary<TableIdentifier, ProtectionClass>
        {
            [pub.Identifier] = ProtectionClass.Public,
            [sec.Identifier] = ProtectionClass.Secret
        };

        var contract = new SchemaContractDefinition(
            "public-only",
            includedClasses: [ProtectionClass.Public],
            excludedClasses: [ProtectionClass.Secret]);

        var visible = CatalogVisibility.FilterByContract([pub, sec], contract, classes);

        visible.ShouldHaveSingleItem().Identifier.ShouldBe(pub.Identifier);
    }

    [Fact]
    public async Task Pdp_checks_contract_classes_and_passes_TableClass_to_authz_hooks()
    {
        var meta = new TableMetadata
        {
            Identifier = TableId,
            Table = new Table { Id = Guid.NewGuid(), SourceName = "crm", SchemaName = "public", TableName = "customers", IsActive = true, Tags = ["SECRET"] },
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }]
        };

        var consents = Substitute.For<IConsentRepository>();
        consents.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), TableId, Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns([
                new Consent
                {
                    TenantId = TenantA,
                    TableIdentifier = TableId,
                    GranteeType = GranteeType.User,
                    GranteeSid = UserSid,
                    Effect = ConsentEffect.Allow,
                    ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                    ValidTo = DateTimeOffset.UtcNow.AddDays(1)
                }
            ]);

        PluginAuthorizationContext? capturedHookCtx = null;
        var hooks = Substitute.For<IAuthPluginHost>();
        hooks.HasAuthorizationHooks.Returns(true);
        hooks.EvaluateAuthorizationHooksAsync(Arg.Any<PluginAuthorizationContext>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                capturedHookCtx = call.ArgAt<PluginAuthorizationContext>(0);
                return ValueTask.FromResult(PluginAuthorizationResult.Allow());
            });

        var policy = new TableAccessPolicy(
            consents,
            new ConsentResolutionService(),
            NullConsentCacheService.Instance,
            new GatewayOptions(),
            mandatoryFilters: NullMandatoryRowFilterResolver.Instance,
            authPluginHost: hooks);

        // IncludedClasses = [Internal]; table tags contain "SECRET" which must carry zero permission effect.
        var contract = new SchemaContractDefinition("internal-slice", includedClasses: [ProtectionClass.Internal]);

        var allowed = await policy.DecideAsync(
            new TableAccessQuery(UserSid, TenantA, new HashSet<Sid>(), new HashSet<string>(), meta, Contract: contract, Protection: Protection(ProtectionClass.Internal)),
            CancellationToken.None);
        allowed.IsAllowed.ShouldBeTrue();
        capturedHookCtx.ShouldNotBeNull();
        capturedHookCtx.TableClass.ShouldBe(ProtectionClass.Internal);

        var denied = await policy.DecideAsync(
            new TableAccessQuery(UserSid, TenantA, new HashSet<Sid>(), new HashSet<string>(), meta, Contract: contract, Protection: Protection(ProtectionClass.Confidential)),
            CancellationToken.None);
        denied.IsAllowed.ShouldBeFalse();
        denied.DeniedReasons.ShouldContain(r => r.Contains("Schema Contract Denial", StringComparison.Ordinal));
    }
}
