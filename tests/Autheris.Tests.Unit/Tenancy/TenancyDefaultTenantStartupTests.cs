namespace Autheris.Tests.Unit.Tenancy;

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Errors;
using Autheris.Api.Middleware;
using Autheris.Application.Audit;
using Autheris.Application.Tenancy;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>DT-A-8 / DT-SC-11 (WP-DT-1 part), DT-SC-01 arch rule and the governed codes of the two new denials.</summary>
[Trait("Category", "Security")]
public sealed class TenancyDefaultTenantStartupTests
{
    [Fact]
    public async Task Default_Tenant_Configured_Startup_Audit_Written()
    {
        var resolver = Substitute.For<ITenantResolver>();
        resolver.ConfiguredDefaultTenant.Returns(new TenantId("dflt"));
        var audit = Substitute.For<IAuditWriter>();
        var sut = new TenancyDefaultTenantStartupAuditor(resolver, audit, NullLogger<TenancyDefaultTenantStartupAuditor>.Instance);

        await sut.StartAsync(CancellationToken.None);

        await audit.Received(1).RecordAsync(
            Arg.Is<AuditLogEntry>(e =>
                e.EventType == AuditEventTypes.TenancyDefaultTenantConfigured &&
                e.TenantId == TenantId.Platform &&
                e.EffectiveDetailsJson.Contains("dflt")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task No_Default_Tenant_Writes_No_Audit_Row()
    {
        var resolver = Substitute.For<ITenantResolver>();
        resolver.ConfiguredDefaultTenant.Returns((TenantId?)null);
        var audit = Substitute.For<IAuditWriter>();
        var sut = new TenancyDefaultTenantStartupAuditor(resolver, audit, NullLogger<TenancyDefaultTenantStartupAuditor>.Instance);

        await sut.StartAsync(CancellationToken.None);

        await audit.DidNotReceiveWithAnyArgs().RecordAsync(default!, default);
    }

    [Fact]
    public void Startup_Audit_Event_Has_An_Explicit_Policy() =>
        AuditPolicyTable.All.ContainsKey(AuditEventTypes.TenancyDefaultTenantConfigured).ShouldBeTrue();

    [Fact]
    public void New_Denials_Map_To_403_With_Governed_Codes_And_Fixed_Messages()
    {
        var unresolved = new TenantUnresolvedException();
        var invalid = new TenantClaimInvalidException();

        GatewayDenialStatusMapper.Map(unresolved).Status.ShouldBe(403);
        GatewayDenialStatusMapper.Map(invalid).Status.ShouldBe(403);
        GatewayDenialStatusMapper.SpecificCode(unresolved).ShouldBe(ApiErrorCodes.TenantUnresolved);
        GatewayDenialStatusMapper.SpecificCode(invalid).ShouldBe(ApiErrorCodes.TenantClaimInvalid);
        GatewayDenialStatusMapper.SpecificCode(new GatewayForbiddenException()).ShouldBeNull();
        ApiErrorCodes.TenantUnresolved.ShouldBe("TENANT_UNRESOLVED");
        ApiErrorCodes.TenantClaimInvalid.ShouldBe("TENANT_CLAIM_INVALID");
    }

    [Fact]
    public void Default_Tenant_Option_Is_Read_Only_By_The_Resolver_And_Validation()
    {
        var root = RepoRoot();
        var allowed = new[] { "TenantResolver.cs", "GatewayCoreOptionsValidator.cs", "TenancyOptions.cs" };
        var offenders = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                        !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => !allowed.Contains(Path.GetFileName(f)))
            .Where(f =>
            {
                var text = File.ReadAllText(f);
                return text.Contains("Tenancy.DefaultTenantId", StringComparison.Ordinal) ||
                       text.Contains("TenancyOptions.DefaultTenantId", StringComparison.Ordinal);
            })
            .Select(Path.GetFileName)
            .ToList();

        offenders.ShouldBeEmpty("DT-SC-01: only TenantResolver (and option validation) may read Gateway:Tenancy:DefaultTenantId.");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
