using Autheris.Tests.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Extensibility;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Audit;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Olap;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Plugins;
using Autheris.Infrastructure.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using StackExchange.Redis;
using Xunit;

namespace Autheris.Tests.Integration.Governance;

public sealed class AuditAnchorTruncationGovernanceTests : IDisposable
{
    private readonly string _tempDir;


    public AuditAnchorTruncationGovernanceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "autheris-audit-anchor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.GetFiles(_tempDir, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(f, FileAttributes.Normal);
            }
            Directory.Delete(_tempDir, true);
        }
        catch (IOException) { }
    }

    [Fact]
    public async Task H17_TailTruncation_IsDetectedAfterRestart()
    {
        var (options, _, anchorFile) = CreateFileAuditOptions();
        string connectionString;
        using (var repo = GovernanceTestDatabase.CreateRepository(new EpochValidationService(), out connectionString, options))
        {
            for (var i = 1; i <= 5; i++)
            {
                await repo.RecordAuditEventAsync(NewAuditEntry(i));
            }
            (await repo.VerifyAuditHashChainAsync()).ShouldBeTrue();

            // Attacker deletes the last two entries directly in the database.
            GovernanceTestDatabase.ExecuteRaw(connectionString, "DELETE FROM AUDIT_LOG_ENTRIES WHERE rowid IN (SELECT rowid FROM AUDIT_LOG_ENTRIES ORDER BY rowid DESC LIMIT 2)", bypassAppendOnlyTriggers: true);
        }

        File.Exists(anchorFile).ShouldBeTrue("The signed end anchor must be persisted outside the database.");

        using var restarted = GovernanceTestDatabase.CreateRepositoryOn(connectionString, new EpochValidationService(), options: options);
        (await restarted.VerifyAuditHashChainAsync()).ShouldBeFalse("Tail truncation must be detected against the external anchor, even after a restart.");
    }

    [Fact]
    public async Task H17_TotalDeletion_IsDetectedAfterRestart()
    {
        var (options, _, _) = CreateFileAuditOptions();
        string connectionString;
        using (var repo = GovernanceTestDatabase.CreateRepository(new EpochValidationService(), out connectionString, options))
        {
            for (var i = 1; i <= 3; i++)
            {
                await repo.RecordAuditEventAsync(NewAuditEntry(i));
            }

            GovernanceTestDatabase.ExecuteRaw(connectionString, "DELETE FROM AUDIT_LOG_ENTRIES", bypassAppendOnlyTriggers: true);
        }

        using var restarted = GovernanceTestDatabase.CreateRepositoryOn(connectionString, new EpochValidationService(), options: options);
        (await restarted.VerifyAuditHashChainAsync()).ShouldBeFalse("An emptied audit log must not be accepted as a fresh GENESIS chain.");
    }

    [Fact]
    public async Task H17_InProcessTruncation_IsNotSilentlyResynchronised()
    {
        var (options, _, _) = CreateFileAuditOptions();
        using var repo = GovernanceTestDatabase.CreateRepository(new EpochValidationService(), out var connectionString, options);
        for (var i = 1; i <= 3; i++)
        {
            await repo.RecordAuditEventAsync(NewAuditEntry(i));
        }

        GovernanceTestDatabase.ExecuteRaw(connectionString, "DELETE FROM AUDIT_LOG_ENTRIES WHERE rowid = (SELECT MAX(rowid) FROM AUDIT_LOG_ENTRIES)", bypassAppendOnlyTriggers: true);

        // Previously the next insert re-read the (truncated) DB tail and chained cleanly onto it.
        await repo.RecordAuditEventAsync(NewAuditEntry(4));

        (await repo.VerifyAuditHashChainAsync()).ShouldBeFalse();
    }

    [Fact]
    public async Task H17_SequenceNumbers_AreGapFreeAndBoundIntoTheHash()
    {
        var (options, _, _) = CreateFileAuditOptions();
        using var repo = GovernanceTestDatabase.CreateRepository(new EpochValidationService(), out var connectionString, options);
        for (var i = 1; i <= 3; i++)
        {
            await repo.RecordAuditEventAsync(NewAuditEntry(i));
        }

        var seqs = new List<long>();
        using (var cmd = repo.RawConnection().CreateCommand())
        {
            cmd.CommandText = "SELECT seq FROM AUDIT_LOG_ENTRIES ORDER BY rowid";
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                seqs.Add(reader.GetInt64(0));
            }
        }
        seqs.ShouldBe(new List<long> { 1, 2, 3 });
        (await repo.VerifyAuditHashChainAsync()).ShouldBeTrue();

        GovernanceTestDatabase.ExecuteRaw(connectionString, "UPDATE AUDIT_LOG_ENTRIES SET seq = 7 WHERE seq = 3", bypassAppendOnlyTriggers: true);
        (await repo.VerifyAuditHashChainAsync()).ShouldBeFalse();
    }

    [Fact]
    public async Task H17_ForgedAnchorFile_IsRejected()
    {
        var (options, _, anchorFile) = CreateFileAuditOptions();
        string connectionString;
        using (var repo = GovernanceTestDatabase.CreateRepository(new EpochValidationService(), out connectionString, options))
        {
            await repo.RecordAuditEventAsync(NewAuditEntry(1));
        }

        // Attacker rewrites the anchor to "legitimise" a shortened chain, but cannot compute the HMAC.
        File.WriteAllText(anchorFile, JsonSerializer.Serialize(new AuditChainAnchor(0, "GENESIS_0000000000000000000000000000000000000000000000000000000000000000", DateTimeOffset.UtcNow, "00")));

        using var restarted = GovernanceTestDatabase.CreateRepositoryOn(connectionString, new EpochValidationService(), options: options);
        (await restarted.VerifyAuditHashChainAsync()).ShouldBeFalse();
    }

    [Fact]
    public async Task M31_WormExport_ExportsMoreThan5000Entries_WithSequenceRangeInManifest()
    {
        const int total = 5100;
        using var repo = GovernanceTestDatabase.CreateRepository(new EpochValidationService(), Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions { SeedDemoData = false }
        }));

        for (var i = 1; i <= total; i++)
        {
            await repo.RecordAuditEventAsync(NewAuditEntry(i));
        }

        using var httpClient = new HttpClient(new OkHandler());
        var exporter = new AuditWormExportService(repo, WormOptions(), httpClient, NullLogger<AuditWormExportService>.Instance);

        var result = await exporter.ExportAuditSnapshotAsync(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1));

        result.Success.ShouldBeTrue(result.ErrorMessage);
        result.RecordCount.ShouldBe(total);
        using var manifest = JsonDocument.Parse(result.ManifestJson);
        manifest.RootElement.GetProperty("firstSequence").GetInt64().ShouldBe(1);
        manifest.RootElement.GetProperty("lastSequence").GetInt64().ShouldBe(total);
        manifest.RootElement.GetProperty("expectedRecordCount").GetInt64().ShouldBe(total);
        manifest.RootElement.GetProperty("chainAnchor").GetProperty("sequence").GetInt64().ShouldBe(total);
    }

    private sealed class OkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }

    private IOptions<GatewayOptions> WormOptions() => Options.Create(new GatewayOptions
    {
        Audit = new AuditOptions
        {
            Worm = new WormAuditOptions { Enabled = true, StorageType = "Local", ExportPath = Path.Combine(_tempDir, "worm"), RetentionDays = 30 }
        }
    });

    private (IOptions<GatewayOptions> Options, string DbFile, string AnchorFile) CreateFileAuditOptions()
    {
        var dbFile = Path.Combine(_tempDir, $"audit_{Guid.NewGuid():N}.db");
        var anchorFile = Path.Combine(_tempDir, "anchors", Path.GetFileName(dbFile) + ".anchor.json");
        var options = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions { SeedDemoData = false },
            Audit = new AuditOptions { ChainAnchorPath = anchorFile }
        });
        return (options, dbFile, anchorFile);
    }

    private static AuditLogEntry NewAuditEntry(int i) => new()
    {
        TenantId = Autheris.Tests.Common.TestTenants.Default,
        EventType = $"EVENT_{i}",
        ActorSid = new Sid($"S-1-5-21-ACTOR-{i}"),
        TargetTable = "hr.dbo.salaries",
        Decision = "ALLOW",
        TraceId = $"trace-{i}",
        DetailsJson = $"{{\"record\": {i}}}"
    };
}
