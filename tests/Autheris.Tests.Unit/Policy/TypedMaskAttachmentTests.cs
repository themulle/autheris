using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Services;
using Autheris.Application.Sql.Masking;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine.Governance;
using Xunit;

namespace Autheris.Tests.Unit.Policy;

/// <summary>WP-X0l: the live decision producer attaches typed column masks (the compiler path consumes <see cref="TableAccessDecision.ColumnMasks"/>).</summary>
public sealed class TypedMaskAttachmentTests
{
    private static readonly TenantId Tenant = new("acme");
    private static readonly Sid User = new("S-1");
    private static readonly TableIdentifier Orders = new("d", "main", "orders");

    private static TableMetadata Meta(params (string Name, bool Sensitive, MaskingRule? Rule)[] columns) => new()
    {
        Identifier = Orders,
        Table = new Table { SourceName = "ds", SchemaName = "main", TableName = "orders", SourceType = "SQLite" },
        Columns = columns.Select(c => new TableColumn { ColumnName = c.Name, DataType = "TEXT", IsSensitive = c.Sensitive }).ToList(),
        ColumnMaskingRules = columns.Where(c => c.Rule != null).ToDictionary(c => c.Name, c => c.Rule!)
    };

    private static ColumnMaskPolicyFactory Factory(GatewayOptions? options = null) =>
        new(new SqlDataMaskingProvider(Options.Create(options ?? new GatewayOptions())));

    private static TableAccessPolicy Policy(TableAccessDecision consentDecision, ColumnMaskPolicyFactory? masks)
    {
        var resolution = Substitute.For<IConsentResolutionService>();
        resolution.ResolveAccess(Arg.Any<Sid>(), Arg.Any<IReadOnlySet<Sid>>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<TableIdentifier>(),
            Arg.Any<IReadOnlyList<Consent>>(), Arg.Any<DatabaseDialect>(), Arg.Any<IReadOnlySet<Sid>?>()).Returns(consentDecision);
        var consents = Substitute.For<IConsentRepository>();
        consents.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<System.DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>([]));
        return new TableAccessPolicy(consents, resolution,
            Autheris.Application.Policy.Services.NullConsentCacheService.Instance,
            global::Autheris.Tests.Unit.TestDoubles.NullPolicyEnforcementService.Instance,
            Autheris.Application.Security.Rebac.Services.NullRebacEvaluator.Instance,
            clientIpResolver: null, new GatewayOptions(), NullMandatoryRowFilterResolver.Instance,
            maskPolicyFactory: masks);
    }

    private static TableAccessQuery Query(TableMetadata meta) => new(User, Tenant, new HashSet<Sid>(), new HashSet<string>(), meta, System.Array.Empty<System.Security.Claims.Claim>()); // a scope-less token (absent claims count as restricted, REQ-GOV-143)

    private static TableAccessDecision Allowed(params (string Column, ColumnAccessLevel Level)[] levels) =>
        TableAccessDecision.Allowed(Orders, levels.ToDictionary(l => l.Column, l => l.Level), hasUnconstrainedColumnAllow: true);

    [Fact]
    public async Task MaskedColumn_GetsATypedMask_FromItsCatalogRule()
    {
        var meta = Meta(("id", false, null), ("email", false, new MaskingRule { RuleType = "PARTIAL_MASK", KeepPrefix = 2, KeepSuffix = 1 }));

        var decision = await Policy(Allowed(("email", ColumnAccessLevel.Mask)), Factory()).DecideAsync(Query(meta), CancellationToken.None);

        decision.ColumnMasks.ShouldNotBeNull();
        decision.ColumnMasks!.HasMask("email").ShouldBeTrue();
        // SQLite: the typed partial mask is the constant-length redact of the legacy SQLite output.
        ((ColumnMaskPolicy)decision.ColumnMasks).Specs["email"].Kind.ShouldBe(MaskKind.Redact);
        decision.RequireTypedColumnMasks(meta).ShouldBeSameAs(decision);
    }

    [Fact]
    public async Task MaskedColumnWithoutCatalogRule_GetsTheDefaultRedactMask()
    {
        var meta = Meta(("id", false, null), ("email", false, null));

        var decision = await Policy(Allowed(("email", ColumnAccessLevel.Mask)), Factory()).DecideAsync(Query(meta), CancellationToken.None);

        ((ColumnMaskPolicy)decision.ColumnMasks!).Specs["email"].Kind.ShouldBe(MaskKind.Redact);
    }

    [Fact]
    public async Task CatalogSensitiveColumn_WithoutExplicitLevel_IsMaskedWithATypedMask()
    {
        var meta = Meta(("id", false, null), ("ssn", true, null));

        var decision = await Policy(Allowed(), Factory()).DecideAsync(Query(meta), CancellationToken.None);

        decision.ColumnMasks.ShouldNotBeNull();
        decision.ColumnMasks!.HasMask("ssn").ShouldBeTrue();
        decision.ColumnMasks.HasMask("id").ShouldBeFalse();
        decision.RequireTypedColumnMasks(meta).IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public async Task ExplicitClear_OverridesCatalogSensitivity_NoMask()
    {
        var meta = Meta(("ssn", true, null));

        var decision = await Policy(Allowed(("ssn", ColumnAccessLevel.Clear)), Factory()).DecideAsync(Query(meta), CancellationToken.None);

        decision.ColumnMasks.ShouldBeNull();
    }

    [Fact]
    public async Task DeniedColumn_HasNoTypedMask()
    {
        var meta = Meta(("email", false, null));

        var decision = await Policy(Allowed(("email", ColumnAccessLevel.Deny)), Factory()).DecideAsync(Query(meta), CancellationToken.None);

        decision.ColumnMasks.ShouldBeNull();
    }

    [Fact]
    public async Task WithoutAMaskFactory_TheDecisionStaysStringOnly_AndTheCompilerPathDeniesIt()
    {
        var meta = Meta(("email", false, null));

        var decision = await Policy(Allowed(("email", ColumnAccessLevel.Mask)), masks: null).DecideAsync(Query(meta), CancellationToken.None);

        decision.ColumnMasks.ShouldBeNull();
        decision.RequireTypedColumnMasks(meta).IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public async Task DeniedDecision_IsReturnedUnchanged()
    {
        var meta = Meta(("email", false, null));

        var decision = await Policy(TableAccessDecision.Denied(Orders, "no"), Factory()).DecideAsync(Query(meta), CancellationToken.None);

        decision.IsAllowed.ShouldBeFalse();
        decision.ColumnMasks.ShouldBeNull();
    }

    [Fact]
    public async Task HmacRule_WithoutAKey_DegradesToATypedRedact()
    {
        var meta = Meta(("email", false, new MaskingRule { RuleType = "HMAC_SHA256" }));

        var decision = await Policy(Allowed(("email", ColumnAccessLevel.Mask)), Factory()).DecideAsync(Query(meta), CancellationToken.None);

        ((ColumnMaskPolicy)decision.ColumnMasks!).Specs["email"].Kind.ShouldBe(MaskKind.Redact);
    }
}
