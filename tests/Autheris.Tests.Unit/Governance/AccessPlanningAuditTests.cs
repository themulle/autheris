namespace Autheris.Tests.Unit.Governance;

using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Governance.Services;
using Autheris.Application.Security.Totp.Interfaces;
using Autheris.Application.Security.Totp.Services;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>SEC-36-SP2-07: every administrative change is refused, without a state change, when its required audit write fails.</summary>
[Trait("Category", "Security")]
public sealed class AccessPlanningAuditTests
{
    private const string AdminSid = "S-1-5-21-ADMIN-500";
    private const string Tenant = "tenant-a";

    private readonly IAuditWriter _writer = Substitute.For<IAuditWriter>();
    private readonly ITableMetadataRepository _tableRepo = Substitute.For<ITableMetadataRepository>();
    private readonly IConsentRepository _consentRepo = Substitute.For<IConsentRepository>();
    private readonly IKeyVaultSecretProvider _secrets = Substitute.For<IKeyVaultSecretProvider>();
    private readonly ITotpSecretStore _totpSecrets = new InMemoryTotpSecretStore();
    private readonly ITotpVerificationService _totp = new TotpVerificationService();
    private readonly AccessPlanningService _service;

    public AccessPlanningAuditTests()
    {
        _service = new AccessPlanningService(_writer, _tableRepo, _consentRepo, _totp, _totpSecrets, _secrets);
        var id = Guid.NewGuid();
        _tableRepo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns(new TableMetadata
        {
            Identifier = new TableIdentifier("sales", "public", "orders"),
            Table = new Table { Id = id, SchemaName = "public", TableName = "orders", Sensitivity = "INTERNAL", IsActive = true },
            Columns = [new TableColumn { TableId = id, ColumnName = "id", DataType = "int", IsSensitive = false }],
            PrimaryKeyColumns = ["id"]
        });
    }

    private void AuditDown() =>
        _writer.RecordRequiredAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>()).Returns(Task.FromException(new AuditUnavailableException("down")));

    [Fact]
    public async Task Register_datasource_is_refused_when_audit_write_fails()
    {
        AuditDown();
        var request = new AdminRegisterDatasourceRequest("crane", "telemetry", Auth: new DatasourceAuthDto("apiKey", Secret: "s"));

        await Should.ThrowAsync<AuditUnavailableException>(() => _service.RegisterDatasourceAsync(request, AdminSid, Tenant, isClusterAdmin: true));

        _secrets.DidNotReceiveWithAnyArgs().SetSecret(default!, default!);
    }

    [Fact]
    public async Task Set_dataset_state_is_refused_when_audit_write_fails()
    {
        AuditDown();

        await Should.ThrowAsync<AuditUnavailableException>(() =>
            _service.SetDatasetStateAsync(new AdminSetDatasetStateRequest("sales.public.orders", "quarantined", "drift"), AdminSid, Tenant));

        await _tableRepo.DidNotReceiveWithAnyArgs().UpsertTableMetadataAsync(default!, default);
    }

    [Fact]
    public async Task Apply_access_is_refused_when_audit_write_fails()
    {
        var request = new AdminPlanAccessRequest(
            "sales.public.orders",
            [new PrincipalAccessGrant("S-1-5-21-ALICE-1001", new Dictionary<string, string> { ["id"] = "clear" }, null, DateTimeOffset.UtcNow.AddDays(30))],
            "access for report");
        var plan = await _service.PlanAccessAsync(request, AdminSid, Tenant);
        var enrollment = _totp.GenerateEnrollment(AdminSid, "admin@autheris.local");
        await _totpSecrets.SetSecretAsync(AdminSid, enrollment.SecretBase32);
        var confirm = await _service.ConfirmPlanAsync(plan.PlanId, _totp.GenerateTotpCode(enrollment.SecretBase32), AdminSid);
        AuditDown();

        await Should.ThrowAsync<AuditUnavailableException>(() =>
            _service.ApplyAccessAsync(new AdminApplyAccessRequest(plan.PlanId, confirm.ConfirmationToken), AdminSid, new TenantId(Tenant)));

        await _consentRepo.DidNotReceiveWithAnyArgs().CreateConsentAsync(default!, default);
    }

    [Fact]
    public async Task Admin_changes_write_their_record_before_the_change()
    {
        var order = new List<string>();
        _writer.RecordRequiredAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>()).Returns(c =>
        {
            order.Add(c.Arg<AuditLogEntry>().EventType);
            return Task.CompletedTask;
        });
        _tableRepo.UpsertTableMetadataAsync(Arg.Any<TableMetadata>(), Arg.Any<CancellationToken>()).Returns(c =>
        {
            order.Add("UPSERT");
            return Task.FromResult(c.Arg<TableMetadata>());
        });

        await _service.SetDatasetStateAsync(new AdminSetDatasetStateRequest("sales.public.orders", "quarantined", "drift"), AdminSid, Tenant);

        order.ShouldBe([AuditEventTypes.AdminSetDatasetState, "UPSERT"]);
    }
}
