using Autheris.Tests.Common;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Net;
using System.Net.Sockets;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Governance;

public sealed class SecurityAttackVectorGovernanceTests
{
    [Fact]
    public async Task PostgreSqlGovernanceRepository_AuditHashChain_TamperingShouldBeDetected()
    {
        // Arrange
        using var repo = GovernanceTestDatabase.CreateRepository(new FakeEpochValidationService());

        // Insert valid consecutive audit records
        var entry1 = new AuditLogEntry
        {
            TenantId = Autheris.Tests.Common.TestTenants.Default,
            Id = Guid.NewGuid(),
            OccurredAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            ActorSid = new Sid("S-1-5-21-admin"),
            EventType = "DATA_READ",
            TargetTable = "crm.customers",
            Decision = "ALLOW",
            TraceId = "trace-001",
            DetailsJson = "{\"columns\":[\"id\",\"email\"],\"rows\":10}",
            PrevHash = "0000000000000000000000000000000000000000000000000000000000000000"
        };
        await repo.RecordAuditEventAsync(entry1);

        var entry2 = new AuditLogEntry
        {
            TenantId = Autheris.Tests.Common.TestTenants.Default,
            Id = Guid.NewGuid(),
            OccurredAt = DateTimeOffset.UtcNow.AddMinutes(-4),
            ActorSid = new Sid("S-1-5-21-analyst"),
            EventType = "DATA_READ",
            TargetTable = "crm.orders",
            Decision = "ALLOW",
            TraceId = "trace-002",
            DetailsJson = "{\"columns\":[\"order_id\",\"total\"],\"rows\":5}",
            PrevHash = entry1.EntryHash
        };
        await repo.RecordAuditEventAsync(entry2);

        // Act 1: Verify intact hash chain
        var isChainValid = await repo.VerifyAuditHashChainAsync();
        isChainValid.ShouldBeTrue();

        // Act 2: Tamper directly with entry1 row in database
        GovernanceTestDatabase.ExecuteRaw(GovernanceTestDatabase.ConnectionStringOf(repo), "UPDATE AUDIT_LOG_ENTRIES SET details_json = '{\"tampered\": true}' WHERE id = @id", bypassAppendOnlyTriggers: true, parameters: [("id", entry1.Id.ToString())]);

        // Act 3: Verification must now FAIL (tamper detection)
        var isChainValidAfterTampering = await repo.VerifyAuditHashChainAsync();
        isChainValidAfterTampering.ShouldBeFalse();
    }
}
