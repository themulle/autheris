using System.Security.Cryptography;
using System.Text;
using Autheris.Application.Interfaces;
using Autheris.Application.OpenMetadata.Interfaces;
using Autheris.Application.OpenMetadata.Models;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.OpenMetadata;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Extensions.Tests;

public sealed class OpenMetadataSyncServiceTests
{
    private readonly IOpenMetadataClient _client = Substitute.For<IOpenMetadataClient>();
    private readonly ITableMetadataRepository _metadataRepo = Substitute.For<ITableMetadataRepository>();
    private readonly IConsentRepository _consentRepo = Substitute.For<IConsentRepository>();
    private readonly IPolicyEpochRepository _epochRepo = Substitute.For<IPolicyEpochRepository>();

    private GatewayOptions CreateOptions(string? webhookSecret = "om-test-secret-42")
    {
        return new GatewayOptions
        {
            DataMasking = new DataMaskingOptions { HmacKeyId = "test-hmac-key" },
            OpenMetadata = new OpenMetadataOptions
            {
                Enabled = true,
                WebhookSecret = webhookSecret ?? string.Empty,
                TagToMaskingRuleMap = new Dictionary<string, string>
                {
                    ["PII.Sensitive"] = "REDACT",
                    ["PII.Email"] = "MASK_EMAIL",
                    ["PII.Pseudonym"] = "HMAC_SHA256"
                },
                RoleToGatewayRoleMap = new Dictionary<string, string>
                {
                    ["HrSpecialist"] = "HrSpecialist"
                },
                TeamToGroupSidMap = new Dictionary<string, string>
                {
                    ["FinanceTeam"] = "S-1-5-21-FINANCE-GROUP"
                },
                UserToUserSidMap = new Dictionary<string, string>
                {
                    ["alice@corp.local"] = "S-1-5-21-ALICE-SID",
                    ["bob@corp.local"] = "S-1-5-21-BOB-SID"
                }
            }
        };
    }

    [Fact]
    public async Task SyncPermissionsAsync_SuccessfullySyncsTablesTagsAndConsents()
    {
        var baseOptions = CreateOptions();
        // SEC H-19: automatic consent creation is opt-in (default: proposals only)
        var options = new GatewayOptions
        {
            DataMasking = baseOptions.DataMasking,
            OpenMetadata = new OpenMetadataOptions
            {
                Enabled = true,
                WebhookSecret = baseOptions.OpenMetadata.WebhookSecret,
                TagToMaskingRuleMap = baseOptions.OpenMetadata.TagToMaskingRuleMap,
                RoleToGatewayRoleMap = baseOptions.OpenMetadata.RoleToGatewayRoleMap,
                TeamToGroupSidMap = baseOptions.OpenMetadata.TeamToGroupSidMap,
                UserToUserSidMap = baseOptions.OpenMetadata.UserToUserSidMap,
                AutoCreateConsents = true
            }
        };
        var tableGuid = Guid.NewGuid();

        var tables = new List<OpenMetadataTable>
        {
            new()
            {
                Id = tableGuid,
                Name = "employees",
                FullyQualifiedName = "hr_service.corp.dbo.employees",
                Service = new OpenMetadataEntityReference { Name = "hr_service" },
                DatabaseSchema = new OpenMetadataEntityReference { Name = "dbo" },
                Tags = [new OpenMetadataTag { TagFQN = "General" }],
                Columns =
                [
                    new OpenMetadataColumn
                    {
                        Name = "id",
                        DataType = "INT"
                    },
                    new OpenMetadataColumn
                    {
                        Name = "email",
                        DataType = "VARCHAR",
                        Tags = [new OpenMetadataTag { TagFQN = "PII.Email" }]
                    },
                    new OpenMetadataColumn
                    {
                        Name = "ssn",
                        DataType = "VARCHAR",
                        Tags = [new OpenMetadataTag { TagFQN = "PII.Sensitive" }]
                    },
                    new OpenMetadataColumn
                    {
                        Name = "account_no",
                        DataType = "VARCHAR",
                        Tags = [new OpenMetadataTag { TagFQN = "PII.Pseudonym" }]
                    }
                ]
            }
        };

        var policyGuid = Guid.NewGuid();
        var policies = new List<OpenMetadataPolicy>
        {
            new()
            {
                Id = policyGuid,
                Name = "HrPolicy",
                Enabled = true,
                Rules =
                [
                    new OpenMetadataRule
                    {
                        Name = "AllowEmployees",
                        Effect = "allow",
                        Resources = ["employees"],
                        // SEC H-19: only explicit data-read operations are mapped to consents
                        Operations = ["ViewAll"]
                    }
                ]
            }
        };

        var roles = new List<OpenMetadataRole>
        {
            new()
            {
                Name = "HrSpecialist",
                Policies = [new OpenMetadataEntityReference { Id = policyGuid, Name = "HrPolicy" }]
            }
        };

        var teams = new List<OpenMetadataTeam>
        {
            new()
            {
                Name = "FinanceTeam",
                Policies = [new OpenMetadataEntityReference { Id = policyGuid, Name = "HrPolicy" }]
            }
        };

        var users = new List<OpenMetadataUser>
        {
            new()
            {
                Name = "alice",
                Email = "alice@corp.local",
                Roles = [new OpenMetadataEntityReference { Name = "HrSpecialist" }]
            }
        };

        _client.GetTablesAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<OpenMetadataTable>>(tables));
        _client.GetPoliciesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<OpenMetadataPolicy>>(policies));
        _client.GetRolesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<OpenMetadataRole>>(roles));
        _client.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<OpenMetadataTeam>>(teams));
        _client.GetUsersAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<OpenMetadataUser>>(users));

        TableMetadata? capturedMetadata = null;
        _metadataRepo.UpsertTableMetadataAsync(Arg.Any<TableMetadata>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var meta = callInfo.Arg<TableMetadata>();
                capturedMetadata = meta;
                return Task.FromResult(meta);
            });

        var createdConsents = new List<Consent>();
        _consentRepo.CreateConsentAsync(Arg.Any<Consent>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var cons = callInfo.Arg<Consent>();
                createdConsents.Add(cons);
                return Task.FromResult(cons);
            });

        var syncService = new OpenMetadataSyncService(
            _client,
            _metadataRepo,
            _consentRepo,
            _epochRepo,
            Options.Create(options),
            NullLogger<OpenMetadataSyncService>.Instance);

        var result = await syncService.SyncPermissionsAsync(dryRun: false);

        result.Success.ShouldBeTrue();
        result.SyncedTables.ShouldBe(1);
        result.SyncedMaskingRules.ShouldBe(3);
        result.SyncedConsents.ShouldBeGreaterThanOrEqualTo(3);

        // Check table metadata
        capturedMetadata.ShouldNotBeNull();
        capturedMetadata.Identifier.Domain.ShouldBe("hr_service");
        capturedMetadata.Identifier.Schema.ShouldBe("dbo");
        capturedMetadata.Identifier.TableName.ShouldBe("employees");
        capturedMetadata.Table.Sensitivity.ShouldBe("NORMAL");
        capturedMetadata.ColumnMaskingRules.Count.ShouldBe(3);
        capturedMetadata.ColumnMaskingRules["email"].RuleType.ShouldBe("MASK_EMAIL");
        capturedMetadata.ColumnMaskingRules["ssn"].RuleType.ShouldBe("REDACT");
        capturedMetadata.ColumnMaskingRules["account_no"].RuleType.ShouldBe("HMAC_SHA256");
        capturedMetadata.ColumnMaskingRules["account_no"].HmacKeyId.ShouldBe("test-hmac-key");

        // Check consents – SEC E-06: the table contains PII-tagged (sensitive/masked) columns, therefore no allow consent
        // is created automatically even with AutoCreateConsents=true (the positive case for a non-sensitive table is
        // covered in Round4OpenMetadataTests.E07_TeamAndUserGrantees_ResolvedFromSeparateKeyMaps).
        capturedMetadata.Columns.Single(c => c.ColumnName == "email").IsSensitive.ShouldBeTrue();
        createdConsents.ShouldBeEmpty();

        // Check epoch bumped
        await _epochRepo.Received().IncrementTableEpochAsync(capturedMetadata.Identifier, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncPermissionsAsync_DryRun_DoesNotPersistChanges()
    {
        var options = CreateOptions();
        var tables = new List<OpenMetadataTable>
        {
            new()
            {
                Id = Guid.NewGuid(),
                Name = "orders",
                FullyQualifiedName = "corp.dbo.orders"
            }
        };

        _client.GetTablesAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<OpenMetadataTable>>(tables));
        _client.GetPoliciesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<OpenMetadataPolicy>>([]));
        _client.GetRolesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<OpenMetadataRole>>([]));
        _client.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<OpenMetadataTeam>>([]));
        _client.GetUsersAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<OpenMetadataUser>>([]));

        var syncService = new OpenMetadataSyncService(
            _client,
            _metadataRepo,
            _consentRepo,
            _epochRepo,
            Options.Create(options),
            NullLogger<OpenMetadataSyncService>.Instance);

        var result = await syncService.SyncPermissionsAsync(dryRun: true);

        result.Success.ShouldBeTrue();
        result.SyncedTables.ShouldBe(1);

        // Verify repos were NOT called
        await _metadataRepo.DidNotReceive().UpsertTableMetadataAsync(Arg.Any<TableMetadata>(), Arg.Any<CancellationToken>());
        await _consentRepo.DidNotReceive().CreateConsentAsync(Arg.Any<Consent>(), Arg.Any<CancellationToken>());
        await _epochRepo.DidNotReceive().IncrementTableEpochAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void VerifyWebhookSignature_ValidAndInvalidSignatures()
    {
        var secret = "my-om-secret";
        var payload = "{\"eventType\":\"entityUpdated\",\"entityType\":\"table\"}";

        var hashBytes = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload));
        var validHex = Convert.ToHexStringLower(hashBytes);

        // Valid signature without prefix
        OpenMetadataSyncService.VerifyWebhookSignature(payload, validHex, secret).ShouldBeTrue();

        // Valid signature with sha256= prefix
        OpenMetadataSyncService.VerifyWebhookSignature(payload, $"sha256={validHex}", secret).ShouldBeTrue();

        // Tampered payload
        OpenMetadataSyncService.VerifyWebhookSignature(payload + " ", validHex, secret).ShouldBeFalse();

        // Tampered secret
        OpenMetadataSyncService.VerifyWebhookSignature(payload, validHex, "wrong-secret").ShouldBeFalse();

        // Invalid signature format
        OpenMetadataSyncService.VerifyWebhookSignature(payload, "invalid_sig", secret).ShouldBeFalse();
    }

    [Fact]
    public async Task HandleWebhookEventAsync_ProcessesTableUpdate()
    {
        var options = CreateOptions("webhook-secret-123");
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var eventId = Guid.NewGuid();
        var payload = $"{{\"id\":\"{eventId}\",\"timestamp\":{nowMs},\"eventType\":\"entityUpdated\",\"entityType\":\"table\",\"entityFullyQualifiedName\":\"service.db.schema.products\"}}";

        var hash = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes("webhook-secret-123"), Encoding.UTF8.GetBytes(payload)));

        var table = new OpenMetadataTable
        {
            Id = Guid.NewGuid(),
            Name = "products",
            FullyQualifiedName = "service.db.schema.products",
            Columns =
            [
                new OpenMetadataColumn { Name = "id", DataType = "INT" },
                new OpenMetadataColumn { Name = "price", DataType = "DECIMAL" }
            ]
        };

        _client.GetTableByFqnAsync("service.db.schema.products", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OpenMetadataTable?>(table));

        var syncService = new OpenMetadataSyncService(
            _client,
            _metadataRepo,
            _consentRepo,
            _epochRepo,
            Options.Create(options),
            NullLogger<OpenMetadataSyncService>.Instance);

        var success = await syncService.HandleWebhookEventAsync(payload, $"sha256={hash}");

        success.ShouldBeTrue();
        await _metadataRepo.Received().UpsertTableMetadataAsync(Arg.Is<TableMetadata>(t => t.Identifier.TableName == "products"), Arg.Any<CancellationToken>());
        await _epochRepo.Received().IncrementTableEpochAsync(Arg.Is<TableIdentifier>(t => t.TableName == "products"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleWebhookEventAsync_RejectsReplayAttack_WhenTimestampExceedsWindow()
    {
        var options = CreateOptions("webhook-secret-123");
        var staleTimestamp = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeMilliseconds();
        var payload = $"{{\"id\":\"{Guid.NewGuid()}\",\"eventType\":\"entityUpdated\",\"entityType\":\"table\",\"entityFullyQualifiedName\":\"service.db.schema.products\",\"timestamp\":{staleTimestamp}}}";

        var hash = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes("webhook-secret-123"), Encoding.UTF8.GetBytes(payload)));

        var syncService = new OpenMetadataSyncService(
            _client,
            _metadataRepo,
            _consentRepo,
            _epochRepo,
            Options.Create(options),
            NullLogger<OpenMetadataSyncService>.Instance);

        var success = await syncService.HandleWebhookEventAsync(payload, $"sha256={hash}");

        // Stale timestamp outside 5-minute replay window must be rejected
        success.ShouldBeFalse();
        await _metadataRepo.DidNotReceiveWithAnyArgs().UpsertTableMetadataAsync(default!, default);
    }

    [Fact]
    public async Task HandleWebhookEventAsync_DeduplicatesEvents_WhenDuplicateEventIdReceived()
    {
        var options = CreateOptions("webhook-secret-123");
        var eventId = Guid.NewGuid();
        var validTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var payload = $"{{\"id\":\"{eventId}\",\"eventType\":\"entityUpdated\",\"entityType\":\"table\",\"entityFullyQualifiedName\":\"service.db.schema.dedup_table\",\"timestamp\":{validTimestamp}}}";

        var hash = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes("webhook-secret-123"), Encoding.UTF8.GetBytes(payload)));

        var table = new OpenMetadataTable
        {
            Id = Guid.NewGuid(),
            Name = "dedup_table",
            FullyQualifiedName = "service.db.schema.dedup_table",
            Columns = [new OpenMetadataColumn { Name = "id", DataType = "INT" }]
        };
        _client.GetTableByFqnAsync("service.db.schema.dedup_table", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OpenMetadataTable?>(table));

        var syncService = new OpenMetadataSyncService(
            _client,
            _metadataRepo,
            _consentRepo,
            _epochRepo,
            Options.Create(options),
            NullLogger<OpenMetadataSyncService>.Instance);

        // First delivery: processes successfully
        var firstResult = await syncService.HandleWebhookEventAsync(payload, $"sha256={hash}");
        firstResult.ShouldBeTrue();

        // Duplicate delivery with same event ID: idempotent skip without re-invoking repo
        _metadataRepo.ClearReceivedCalls();
        var duplicateResult = await syncService.HandleWebhookEventAsync(payload, $"sha256={hash}");
        duplicateResult.ShouldBeTrue();
        await _metadataRepo.DidNotReceiveWithAnyArgs().UpsertTableMetadataAsync(default!, default);
    }

    [Fact]
    public async Task EXT_6_SyncPermissionsAsync_WhenUserHasUnmappedRole_DoesNotGenerateConsentsForUser()
    {
        var options = new GatewayOptions
        {
            DataMasking = new DataMaskingOptions { HmacKeyId = "test-hmac-key" },
            OpenMetadata = new OpenMetadataOptions
            {
                Enabled = true,
                WebhookSecret = "test-secret",
                RoleToGatewayRoleMap = new Dictionary<string, string>
                {
                    ["ApprovedRole"] = "GatewayApprovedRole"
                    // "UnmappedRole" is NOT mapped!
                },
                UserToUserSidMap = new Dictionary<string, string>
                {
                    ["alice@corp.local"] = "S-1-5-21-ALICE-SID"
                },
                AutoCreateConsents = true
            }
        };

        var tableGuid = Guid.NewGuid();
        var table = new OpenMetadataTable
        {
            Id = tableGuid,
            Name = "classified_docs",
            FullyQualifiedName = "service.db.schema.classified_docs",
            Service = new OpenMetadataEntityReference { Name = "service" },
            DatabaseSchema = new OpenMetadataEntityReference { Name = "schema" },
            Columns = [new OpenMetadataColumn { Name = "id", DataType = "INT" }]
        };

        var policyGuid = Guid.NewGuid();
        var policy = new OpenMetadataPolicy
        {
            Id = policyGuid,
            Name = "UnmappedPolicy",
            Enabled = true,
            Rules =
            [
                new OpenMetadataRule
                {
                    Name = "Rule1",
                    Effect = "allow",
                    Resources = ["classified_docs"],
                    Operations = ["ViewAll"]
                }
            ]
        };

        var unmappedRoleGuid = Guid.NewGuid();
        var unmappedRole = new OpenMetadataRole
        {
            Id = unmappedRoleGuid,
            Name = "UnmappedRole",
            Policies = [new OpenMetadataEntityReference { Id = policyGuid, Name = "UnmappedPolicy" }]
        };

        var userGuid = Guid.NewGuid();
        var user = new OpenMetadataUser
        {
            Id = userGuid,
            Name = "alice",
            Email = "alice@corp.local",
            Roles = [new OpenMetadataEntityReference { Id = unmappedRoleGuid, Name = "UnmappedRole" }]
        };

        _client.GetTablesAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<OpenMetadataTable>>([table]));
        _client.GetPoliciesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<OpenMetadataPolicy>>([policy]));
        _client.GetRolesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<OpenMetadataRole>>([unmappedRole]));
        _client.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<OpenMetadataTeam>>([]));
        _client.GetUsersAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<OpenMetadataUser>>([user]));

        var createdConsents = new List<Consent>();
        _consentRepo.CreateConsentAsync(Arg.Any<Consent>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var cons = callInfo.Arg<Consent>();
                createdConsents.Add(cons);
                return Task.FromResult(cons);
            });

        var syncService = new OpenMetadataSyncService(
            _client,
            _metadataRepo,
            _consentRepo,
            _epochRepo,
            Options.Create(options),
            NullLogger<OpenMetadataSyncService>.Instance);

        await syncService.SyncPermissionsAsync(dryRun: false);

        createdConsents.ShouldNotContain(c => c.GranteeSid == new Sid("S-1-5-21-ALICE-SID"));
    }
}
