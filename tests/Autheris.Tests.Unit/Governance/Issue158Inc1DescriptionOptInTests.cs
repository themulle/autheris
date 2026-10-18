using System.Security.Claims;
using Autheris.Application.Governance;
using Autheris.Application.Governance.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance;

/// <summary>
/// PLAN-GOV-158-INC1 review finding: the stored description is an explicit opt-in load. The repository read methods return consents without it
/// (access-decision hot path); explain and myActiveConsents load it once for the consents they present.
/// </summary>
public sealed class Issue158Inc1DescriptionOptInTests
{
    private const string UserSid = "S-1-5-21-OPTIN-USER";
    private const string Group = "S-1-GRP-OPTIN";
    private const string TenantName = "tenant-optin";
    private static readonly TableIdentifier Orders = new("sales", "crm", "orders");
    private static readonly DateTimeOffset Start = DateTimeOffset.UtcNow;

    private static Consent Grant(GranteeType type, double validToDays, string sid) => new()
    {
        Id = Guid.NewGuid(), TableIdentifier = Orders, TenantId = new TenantId(TenantName), Effect = ConsentEffect.Allow, GranteeType = type,
        GranteeSid = new Sid(sid), ValidFrom = Start.AddDays(-1), ValidTo = Start.AddDays(validToDays),
        ColumnRules = [new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear }]
    };

    private static ClaimsPrincipal Caller(string[]? groups = null)
    {
        var claims = new List<Claim> { new(ClaimTypes.PrimarySid, UserSid), new("tenant_id", TenantName) };
        claims.AddRange((groups ?? []).Select(g => new Claim(ClaimTypes.GroupSid, g)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "MCP"));
    }

    /// <summary>A repository whose reads never carry a description; <c>LoadDescriptionsAsync</c> fills it from <paramref name="stored"/>.</summary>
    private static (IConsentRepository Repo, IConsentDescriptionRepository Descriptions) OptInRepo(List<Consent> consents, Dictionary<Guid, string> stored)
    {
        var repo = Substitute.For<IConsentRepository, IConsentDescriptionRepository>();
        repo.GetActiveConsentsForSubjectsAsync(default!, default, default, default, default)
            .ReturnsForAnyArgs(_ => Task.FromResult<IReadOnlyList<Consent>>(consents.ToList()));
        repo.GetAllActiveConsentsForSubjectsAsync(default!, default, default, default, default)
            .ReturnsForAnyArgs(_ => Task.FromResult<IReadOnlyList<Consent>>(consents.ToList()));
        var descriptions = (IConsentDescriptionRepository)repo;
        descriptions.LoadDescriptionsAsync(default!, default).ReturnsForAnyArgs(ci =>
        {
            foreach (var c in ci.Arg<IReadOnlyCollection<Consent>>()) if (stored.TryGetValue(c.Id, out var text)) c.Description = text;
            return Task.CompletedTask;
        });
        return (repo, descriptions);
    }

    private static (EffectiveAccessExplanationService Service, ITableMetadataRepository Tables) Service(IConsentRepository repo)
    {
        var table = new TableMetadata
        {
            Table = new Table { Id = Guid.NewGuid(), SourceName = "sales", SchemaName = "crm", TableName = "orders", IsActive = true },
            Identifier = Orders,
            Columns = [new TableColumn { ColumnName = "id", DataType = "INT" }]
        };
        var tables = Substitute.For<ITableMetadataRepository>();
        tables.GetTableMetadataAsync(Orders, Arg.Any<CancellationToken>()).Returns(table);
        tables.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<TableMetadata>>([table]));
        return (new EffectiveAccessExplanationService(tables, repo, new ConsentResolutionService()), tables);
    }

    [Fact]
    public async Task Explain_LoadsDescriptionsOnceThroughTheOptInSeam()
    {
        var grant = Grant(GranteeType.User, 30, UserSid);
        var (repo, descriptions) = OptInRepo([grant], new() { [grant.Id] = "Opt-in text" });

        var dto = await Service(repo).Service.ExplainMyAccessAsync(Caller(), Orders);

        dto.ConsentDescription.ShouldBe("Opt-in text");
        await descriptions.Received(1).LoadDescriptionsAsync(Arg.Is<IReadOnlyCollection<Consent>>(c => c.Count == 1 && c.Single().Id == grant.Id), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Explain_Denied_NeverLoadsDescriptions()
    {
        var (repo, descriptions) = OptInRepo([], []);

        await Service(repo).Service.ExplainMyAccessAsync(Caller(), Orders);

        await descriptions.DidNotReceiveWithAnyArgs().LoadDescriptionsAsync(default!, default);
    }

    [Fact]
    public async Task Explain_ConsentDescription_IsTheFirstEntryOfTheFilteredList_NotAnUntrustedMoreSpecificOne()
    {
        // The most specific grant carries an untrusted (bidi) value that ForOutput drops; the single description must be the first of the
        // returned list, never null while the list holds a value.
        var user = Grant(GranteeType.User, 30, UserSid);
        var group = Grant(GranteeType.Group, 30, Group);
        var (repo, _) = OptInRepo([user, group], new() { [user.Id] = "bad‮text", [group.Id] = "Group text" });

        var dto = await Service(repo).Service.ExplainMyAccessAsync(Caller([Group]), Orders);

        dto.ConsentDescriptions.ShouldBe(["Group text"]);
        dto.ConsentDescription.ShouldBe("Group text");
    }

    [Fact]
    public async Task MyActiveConsents_LoadsDescriptionsThroughTheOptInSeam()
    {
        var grant = Grant(GranteeType.User, 30, UserSid);
        var (repo, descriptions) = OptInRepo([grant], new() { [grant.Id] = "Mine" });
        var accessor = Substitute.For<Microsoft.AspNetCore.Http.IHttpContextAccessor>();
        accessor.HttpContext.Returns(new Microsoft.AspNetCore.Http.DefaultHttpContext { User = Caller() });

        var dtos = await new Autheris.GraphQL.Types.Query().MyActiveConsentsAsync(null, null, null, repo, accessor);

        dtos.Single().Description.ShouldBe("Mine");
        await descriptions.Received(1).LoadDescriptionsAsync(Arg.Any<IReadOnlyCollection<Consent>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Loader_RepositoryWithoutDescriptionSupport_LeavesConsentsUntouched()
    {
        var consent = Grant(GranteeType.User, 30, UserSid);

        await ConsentDescriptionLoader.LoadAsync(Substitute.For<IConsentRepository>(), [consent], CancellationToken.None);

        consent.Description.ShouldBeNull();
    }
}
