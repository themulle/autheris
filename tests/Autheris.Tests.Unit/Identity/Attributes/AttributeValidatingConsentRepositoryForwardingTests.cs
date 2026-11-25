namespace Autheris.Tests.Unit.Identity.Attributes;

using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Identity.Attributes;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// #1009 A-3: <see cref="AttributeValidatingConsentRepository"/> is the registered <see cref="IConsentRepository"/>. Every interface
/// member must be implemented on the decorator itself: a member left to its interface default (for example "no unmask grants")
/// silently answers for the inner repository and changes access decisions in the real host.
/// </summary>
[Trait("Category", "Security")]
public sealed class AttributeValidatingConsentRepositoryForwardingTests
{
    [Theory]
    [InlineData(typeof(IConsentRepository))]
    [InlineData(typeof(IConsentDescriptionRepository))]
    public void Every_interface_member_is_implemented_by_the_decorator_not_by_an_interface_default(Type contract)
    {
        var map = typeof(AttributeValidatingConsentRepository).GetInterfaceMap(contract);
        var defaulted = map.TargetMethods
            .Where(m => m.DeclaringType != typeof(AttributeValidatingConsentRepository))
            .Select(m => m.Name)
            .ToList();

        defaulted.ShouldBeEmpty($"members of {contract.Name} fall back to their interface default instead of the inner repository");
    }

    [Fact]
    public async Task Unmask_grants_of_the_inner_repository_are_forwarded()
    {
        var inner = Substitute.For<IConsentRepository>();
        var table = new TableIdentifier("hr", "public", "employees");
        var tenant = new TenantId("tenant_a");
        var subjects = new[] { new Sid("S-1-5-21-1001") };
        var at = DateTimeOffset.UtcNow;
        var grant = new Consent { Id = Guid.NewGuid(), TenantId = tenant, TableIdentifier = table, GrantKind = PermissionGrantKind.Unmask };
        inner.GetActiveUnmaskGrantsForSubjectsAsync(subjects, table, at, tenant, default).Returns([grant]);
        IConsentRepository sut = new AttributeValidatingConsentRepository(inner, Substitute.For<IConsentMaterializationGuard>());

        var grants = await sut.GetActiveUnmaskGrantsForSubjectsAsync(subjects, table, at, tenant, default);

        grants.ShouldHaveSingleItem().Id.ShouldBe(grant.Id);
    }
}
