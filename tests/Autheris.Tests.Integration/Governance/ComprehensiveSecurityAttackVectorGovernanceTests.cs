using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Security;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Middleware;
using Autheris.Application.Governance;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Security;
using Autheris.Tests.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Governance;

public sealed class ComprehensiveSecurityAttackVectorGovernanceTests
{
    [Fact]
    public async Task PostgreSqlGovernanceRepository_AuditTampering_ModifyingActorSid_BreaksHashChain()
    {
        using var repo = GovernanceTestDatabase.CreateRepository(new FakeEpochValidationService());

        var entry1 = new AuditLogEntry
        {
            TenantId = Autheris.Tests.Common.TestTenants.Default,
            Id = Guid.NewGuid(),
            OccurredAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            ActorSid = new Sid("S-1-5-21-LEGIT-USER"),
            EventType = "CONSENT_REQUEST",
            TargetTable = "crm.leads",
            Decision = "ALLOW",
            TraceId = "tr-001",
            DetailsJson = "{}",
            PrevHash = "0000000000000000000000000000000000000000000000000000000000000000"
        };
        await repo.RecordAuditEventAsync(entry1);

        // Chain is valid initially
        (await repo.VerifyAuditHashChainAsync()).ShouldBeTrue();

        // Attacker tampers with the ActorSid in the database
        GovernanceTestDatabase.ExecuteRaw(GovernanceTestDatabase.ConnectionStringOf(repo), "UPDATE AUDIT_LOG_ENTRIES SET actor_sid = 'S-1-5-21-IMPERSONATED' WHERE id = @id", bypassAppendOnlyTriggers: true, parameters: [("id", entry1.Id.ToString())]);

        // Verification must detect the tampering
        (await repo.VerifyAuditHashChainAsync()).ShouldBeFalse();
    }

    [Fact]
    public async Task PostgreSqlGovernanceRepository_AuditTampering_ModifyingDecision_BreaksHashChain()
    {
        using var repo = GovernanceTestDatabase.CreateRepository(new FakeEpochValidationService());

        var entry1 = new AuditLogEntry
        {
            TenantId = Autheris.Tests.Common.TestTenants.Default,
            Id = Guid.NewGuid(),
            OccurredAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            ActorSid = new Sid("S-1-5-21-LEGIT-USER"),
            EventType = "POLICY_EVALUATION",
            TargetTable = "finance.dbo.payroll",
            Decision = "DENY",
            TraceId = "tr-002",
            DetailsJson = "{}",
            PrevHash = "0000000000000000000000000000000000000000000000000000000000000000"
        };
        await repo.RecordAuditEventAsync(entry1);

        // Chain is valid initially
        (await repo.VerifyAuditHashChainAsync()).ShouldBeTrue();

        // Attacker attempts to change audit log from 'DENY' to 'ALLOW'
        GovernanceTestDatabase.ExecuteRaw(GovernanceTestDatabase.ConnectionStringOf(repo), "UPDATE AUDIT_LOG_ENTRIES SET decision = 'ALLOW' WHERE id = @id", bypassAppendOnlyTriggers: true, parameters: [("id", entry1.Id.ToString())]);

        // Verification must detect the tampering
        (await repo.VerifyAuditHashChainAsync()).ShouldBeFalse();
    }
}
