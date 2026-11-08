namespace Autheris.Tests.Integration.Governance;

using System;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>DT-SC-04 / DT-SC-16 (WP-DT-2a): no tenant fallback in stored values and writers of the PostgreSQL governance store.</summary>
[Trait("Category", "Security")]
public sealed class StoredTenantIntegrityPostgreSqlTests
{
    private static PostgreSqlGovernanceRepository CreateRepository(out string connectionString)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Test");
        return GovernanceTestDatabase.CreateRepository(new EpochValidationService(), out connectionString, environment: env);
    }

    private static AuditLogEntry Entry(TenantId tenant) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenant,
        EventType = AuditEventTypes.AuthSucceeded,
        ActorSid = new Sid("S-1-5-21-1"),
        TargetTable = "dbo.Users",
        Decision = "ALLOW",
        TraceId = "t",
        DetailsJson = "{}",
        OccurredAt = DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task DT_SC_04_PostgreSql_Audit_Write_Without_Tenant_Throws()
    {
        using var repo = CreateRepository(out _);
        await Should.ThrowAsync<ArgumentException>(() => repo.RecordAuditEventAsync(Entry(default)));
    }

    [Fact]
    public async Task DT_SC_04_PostgreSql_Consent_Write_Without_Tenant_Throws()
    {
        using var repo = CreateRepository(out _);
        var consent = new Consent { TableId = Guid.NewGuid(), GranteeSid = new Sid("S-1-5-21-1"), TenantId = default };
        await Should.ThrowAsync<ArgumentException>(() => repo.CreateConsentAsync(consent));
    }

    [Fact]
    public async Task DT_SC_16_PostgreSql_Audit_Reader_Invalid_Stored_Tenant_Fails_Closed()
    {
        using var repo = CreateRepository(out var cs);
        await repo.RecordAuditEventAsync(Entry(new TenantId("tenant-a")));
        await repo.FlushAuditChannelAsync();
        GovernanceTestDatabase.ExecuteRaw(cs, "UPDATE AUDIT_LOG_ENTRIES SET tenant_id = 'bad value'", bypassAppendOnlyTriggers: true);
        var ex = await Should.ThrowAsync<StoredTenantIntegrityException>(() => repo.GetAuditLogEntriesAsync(10));
        ex.ToString().ShouldNotContain("bad value");
    }

    [Fact]
    public async Task DT_SC_16_PostgreSql_Chain_Verification_Fails_For_Unparsable_Stored_Tenant()
    {
        using var repo = CreateRepository(out var cs);
        await repo.RecordAuditEventAsync(Entry(new TenantId("tenant-a")));
        await repo.FlushAuditChannelAsync();
        (await repo.VerifyAuditHashChainAsync()).ShouldBeTrue();
        GovernanceTestDatabase.ExecuteRaw(cs, "UPDATE AUDIT_LOG_ENTRIES SET tenant_id = 'bad value'", bypassAppendOnlyTriggers: true);
        (await repo.VerifyAuditHashChainAsync()).ShouldBeFalse();
    }
}
