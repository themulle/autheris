namespace Autheris.Tests.Unit.VirtualFilters;

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Dbt.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Application.VirtualFilters;
using Autheris.Application.VirtualFilters.Federation;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.Dbt;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// The binding checks live in <see cref="VirtualFilterAdministrationService"/>, so every caller of
/// <c>SaveProfileAsync</c> and of the sync (HTTP API, file repository sync, dbt import) is covered, not only the endpoints.
/// </summary>
public sealed class VirtualFilterBindingServiceValidationTests : IDisposable
{
    private static readonly VirtualFilterActor DbtActor = new(new Sid("S-1-5-21-DBT-SYNC"), IsSync: true);

    private readonly SqliteGovernanceRepository _repository = new(
        Substitute.For<IEpochValidationService>(),
        Options.Create(new GatewayOptions { GovernanceDb = new GovernanceDbOptions { ConnectionString = $"Data Source=vfb_{Guid.NewGuid():N};Mode=Memory;Cache=Shared" } }));

    private readonly VirtualFilterAdministrationService _service;

    public VirtualFilterBindingServiceValidationTests()
    {
        var catalog = Substitute.For<ITableMetadataRepository>();
        catalog.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(new System.Collections.Generic.List<TableMetadata>
        {
            MandatoryRowFilterResolverTests.Table("conf", "client", "client_id", "crane_serial_number"),
            MandatoryRowFilterResolverTests.Table("md", "crane", "serial_number", "is_delivered")
        });
        var options = Options.Create(new GatewayOptions
        {
            VirtualFilters = new VirtualFilterOptions { Federation = new VirtualFilterFederationOptions { MaxPushdownKeys = 50 } }
        });
        _service = new VirtualFilterAdministrationService(_repository, Substitute.For<IAuditLogRepository>(), options, catalog: catalog);
    }

    public void Dispose() => _repository.Dispose();

    private static VirtualFilterAccessProfile ProfileWith(Func<FilterBinding, FilterBinding> change)
    {
        var profile = VirtualFilterModelTests.DavidProfile();
        return profile with { Bindings = profile.Bindings.Select(change).ToList() };
    }

    [Fact]
    public async Task SaveProfile_WithAnExplicitKeyBudgetAboveTheGatewayLimit_IsRejectedForAnyCaller()
    {
        await _service.SaveFilterAsync(VirtualFilterModelTests.DavidFilter(), DbtActor);

        var ex = await Should.ThrowAsync<VirtualFilterBindingException>(() =>
            _service.SaveProfileAsync(ProfileWith(b => b with { MaxPushdownKeys = 51 }), DbtActor));

        ex.Code.ShouldBe(VirtualFilterErrorCodes.PushdownKeyBudgetInvalid);
        (await _repository.LoadSnapshotAsync()).Profiles.ShouldBeEmpty();
    }

    [Fact]
    public async Task SaveProfile_WithAFormatNoEndpointCanSatisfy_IsRejectedForAnyCaller()
    {
        await _service.SaveFilterAsync(VirtualFilterModelTests.DavidFilter(), DbtActor);

        var ex = await Should.ThrowAsync<VirtualFilterBindingException>(() =>
            _service.SaveProfileAsync(ProfileWith(b => b with { PushdownFormat = PushdownParameterFormat.ODataIn }), DbtActor));

        ex.Code.ShouldBe(VirtualFilterErrorCodes.PushdownFormatUnsupported);
        (await _repository.LoadSnapshotAsync()).Profiles.ShouldBeEmpty();
    }

    [Fact]
    public async Task SaveProfile_WithTheModelDefaultBudget_IsNotCheckedAgainstALowGatewayLimit()
    {
        await _service.SaveFilterAsync(VirtualFilterModelTests.DavidFilter(), DbtActor);

        await _service.SaveProfileAsync(VirtualFilterModelTests.DavidProfile(), DbtActor);

        (await _repository.LoadSnapshotAsync()).Profiles.ShouldHaveSingleItem()
            .Bindings.ShouldHaveSingleItem().MaxPushdownKeys.ShouldBe(FilterBinding.DefaultMaxPushdownKeys);
    }

    [Fact]
    public async Task ApplySync_WithAnInvalidBinding_IsRejectedAndStoresNothing()
    {
        var request = new VirtualFilterSyncRequest(
            VirtualFilterModelTests.DavidProfile().TenantId,
            new ManagedBy("governance/access", "c1"),
            [VirtualFilterModelTests.DavidFilter()],
            [ProfileWith(b => b with { MaxPushdownKeys = 51 })]);

        await Should.ThrowAsync<VirtualFilterBindingException>(() => _service.ApplySyncAsync(request, DbtActor));
        await Should.ThrowAsync<VirtualFilterBindingException>(() => _service.PlanSyncAsync(request));

        (await _repository.LoadSnapshotAsync()).Profiles.ShouldBeEmpty();
    }

    [Fact]
    public async Task DbtImport_ThroughTheService_SavesAProfileWithDefaultBindingOptions_UnderALowGatewayLimit()
    {
        await _service.SaveFilterAsync(VirtualFilterModelTests.DavidFilter(), DbtActor);
        var dbt = new DbtMetadataIngestionService(
            Substitute.For<IDbtProposalRepository>(),
            Substitute.For<ITableMetadataRepository>(),
            Substitute.For<ILineageGraphStore>(),
            epochRepository: null,
            gatewayOptions: null,
            sqlEndpointLoader: null,
            relationRepository: null,
            virtualFilterAdmin: _service,
            logger: NullLogger<DbtMetadataIngestionService>.Instance);
        const string json = """
            {"access_profiles":{"david":{"tenant":"tenant_lwe","scope":"lwetem_prod.*.*","uncovered":"deny",
              "grantee":{"type":"user","sid":"S-1-5-21-LWE-DAVID"},
              "bindings":[{"filter":"nicht_ausgelieferte_krane","target":"lwetem_prod.*.*.client_id"}]}}}
            """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var result = await dbt.IngestGovernanceStreamAsync(stream);

        result.AccessProfilesCount.ShouldBe(1);
        result.Warnings.ShouldBeEmpty();
        (await _repository.LoadSnapshotAsync()).Profiles.ShouldHaveSingleItem();
    }
}
