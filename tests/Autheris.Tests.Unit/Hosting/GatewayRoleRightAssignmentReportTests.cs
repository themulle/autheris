namespace Autheris.Tests.Unit.Hosting;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Hosting;
using Autheris.Application.Identity;
using Autheris.Domain.Common;
using Autheris.Domain.Governance;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>NL-SC-02 / NL-T-03 (O-3): roles without a functional-right assignment are reported at Warning, names only, bounded, never blocking.</summary>
[Trait("Category", "Security")]
public sealed class GatewayRoleRightAssignmentReportTests
{
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    [Fact]
    public void Startup_Reports_Roles_Without_Right_Assignment_At_Warning_Bounded()
    {
        var logger = new CapturingLogger<GatewayRoleRightAssignmentReport>();
        var roles = Enumerable.Range(0, GatewayRoleRightAssignmentReport.MaxListedRoles + 7).Select(i => $"Role{i:D3}").Append("Assigned").ToList();

        GatewayRoleRightAssignmentReport.Report(roles, new HashSet<string>(["assigned"], StringComparer.OrdinalIgnoreCase), logger);

        var entry = logger.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Message.ShouldContain($"{GatewayRoleRightAssignmentReport.MaxListedRoles + 7} GatewayRole(s)");
        entry.Message.ShouldContain("Role000");
        entry.Message.ShouldNotContain($"Role{GatewayRoleRightAssignmentReport.MaxListedRoles:D3}");
        entry.Message.ShouldNotContain("Assigned");
        entry.Message.ShouldContain("and 7 more");
    }

    [Fact]
    public void Startup_Report_Is_Silent_When_Every_Role_Is_Assigned()
    {
        var logger = new CapturingLogger<GatewayRoleRightAssignmentReport>();
        GatewayRoleRightAssignmentReport.Report(["A", "B"], new HashSet<string>(["a", "b"], StringComparer.OrdinalIgnoreCase), logger);
        logger.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Startup_Report_Does_Not_Block_Startup_When_The_Store_Fails()
    {
        var repository = Substitute.For<IFunctionalRightRepository>();
        repository.GetMappingsAsync(Arg.Any<TenantId>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<FunctionalRightMapping>>>(_ => throw new InvalidOperationException("db down"));
        var registry = Substitute.For<IIdentityRealmRegistry>();
        registry.GetAllRealms().Returns([Realm("r1")]);
        var logger = new CapturingLogger<GatewayRoleRightAssignmentReport>();

        await new GatewayRoleRightAssignmentReport(repository, registry, logger).StartAsync(CancellationToken.None);

        logger.Entries.ShouldHaveSingleItem().Level.ShouldBe(LogLevel.Warning);
    }

    [Fact]
    public async Task Startup_Report_Lists_Only_Roles_Lacking_An_Approved_Assignment()
    {
        var repository = Substitute.For<IFunctionalRightRepository>();
        var approved = new FunctionalRightMapping(TenantId.LegacySingleTenant, "r1", "DataSteward", FunctionalRight.DataExport, "bob", DateTimeOffset.UtcNow, "Standard", "alice", DateTimeOffset.UtcNow);
        repository.GetMappingsAsync(Arg.Any<TenantId>(), "r1", Arg.Any<CancellationToken>()).Returns([approved]);
        var registry = Substitute.For<IIdentityRealmRegistry>();
        registry.GetAllRealms().Returns([Realm("r1")]);
        var logger = new CapturingLogger<GatewayRoleRightAssignmentReport>();

        await new GatewayRoleRightAssignmentReport(repository, registry, logger).StartAsync(CancellationToken.None);

        var entry = logger.Entries.ShouldHaveSingleItem();
        entry.Message.ShouldContain("ClusterAdmin");
        entry.Message.ShouldNotContain("DataSteward");
    }

    private static IdentityRealm Realm(string id)
    {
        return new IdentityRealm(id, IdentityRealmKind.EntraWorkforce, new TenantBindingSpecification(TenantBindingKind.Fixed, TenantId.LegacySingleTenant.Value));
    }
}
