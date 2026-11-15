namespace Autheris.Tests.Unit.Catalog.Discovery;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Security;
using Autheris.Domain.Audit;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Common;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class DiscoveryArtifactUploadTests
{
    private sealed class RecordingArtifactStore : IDiscoveryArtifactStore
    {
        public Dictionary<Guid, DiscoveryArtifacts> Items { get; } = [];
        public List<(Guid TableId, string Actor)> Upserts { get; } = [];
        public List<(Guid TableId, string Actor)> Deletes { get; } = [];

        public Task<DiscoveryArtifacts?> GetAsync(Guid tableId, CancellationToken ct = default)
            => Task.FromResult(Items.TryGetValue(tableId, out var a) ? a : null);

        public Task<bool> UpsertAsync(DiscoveryArtifacts artifacts, Domain.Common.Sid actor, CancellationToken ct = default)
        {
            Items[artifacts.TableId] = artifacts;
            Upserts.Add((artifacts.TableId, actor.Value));
            return Task.FromResult(true);
        }

        public Task<bool> DeleteAsync(Guid tableId, Domain.Common.Sid actor, CancellationToken ct = default)
        {
            var removed = Items.Remove(tableId);
            Deletes.Add((tableId, actor.Value));
            return Task.FromResult(removed);
        }
    }

    private static (DiscoveryArtifactService Service, FakeDiscoveryTableRepository Tables, RecordingArtifactStore Store, FakeDiscoveryOwnershipRepository Owners, FakeAuditWriter Audit) CreateHarness(
        Action<CatalogDiscoverySettings>? configureDiscovery = null)
    {
        var tables = new FakeDiscoveryTableRepository();
        var orders = DiscoveryTestBuilder.MakeTable(
            "sales",
            "orders",
            ownerTenantId: "tenant-a",
            columns:
            [
                ("id", false, false),
                ("amount", false, false),
                ("secret_col", true, true)
            ]);
        tables.Tables.Add(orders);

        var store = new RecordingArtifactStore();
        var owners = new FakeDiscoveryOwnershipRepository();
        var audit = new FakeAuditWriter();
        var settings = new CatalogDiscoverySettings();
        configureDiscovery?.Invoke(settings);

        var service = new DiscoveryArtifactService(
            tables,
            store,
            owners,
            audit,
            new GatewayRoleEvaluator(),
            Options.Create(new GatewayOptions { Tenancy = TenancyShape.Multi.Options() }),
            Options.Create(new CatalogDiscoveryOptions { Discovery = settings }));

        return (service, tables, store, owners, audit);
    }

    private static ReadOnlyMemory<byte> Utf8(string json) => Encoding.UTF8.GetBytes(json);

    [Fact]
    public async Task Upsert_ValidArtifact_SucceedsForUploaderRole_Owner_AndAdmin_AndAuditsHashOnly()
    {
        var (service, tables, store, owners, audit) = CreateHarness();
        var tableId = tables.Tables[0].Identifier;
        var payload = Utf8("""
        {
          "schemaVersion": 1,
          "samples": [
            { "id": 1, "amount": 42.5 }
          ],
          "statistics": {
            "table": {
              "approxRowCount": 100,
              "partitionColumns": ["id"]
            },
            "columns": {
              "amount": {
                "nullRatio": 0.1,
                "minValue": "1.0",
                "maxValue": "99.0",
                "approxDistinctCount": 50,
                "maxLength": 10,
                "topValues": ["42.5", "10.0"]
              }
            }
          }
        }
        """);

        // 1. Tenant-scoped CatalogArtifactUploader succeeds
        var uploader = DiscoveryTestBuilder.User("S-UPLOADER", "tenant-a", ["CatalogArtifactUploader"]);
        var r1 = await service.UpsertAsync(uploader, tableId.ToString(), payload, "api");
        r1.Outcome.ShouldBe(DiscoveryArtifactOutcome.Ok);
        r1.SampleRowCount.ShouldBe(1);
        r1.StatisticsColumnCount.ShouldBe(1);
        r1.ContentSha256.ShouldNotBeNullOrWhiteSpace();
        store.Items[tables.Tables[0].Table.Id].UploadedBy.ShouldBe("S-UPLOADER");

        // 2. Audit carries hash & counts, never sample or stat values
        audit.Entries.Count.ShouldBe(1);
        var entry = audit.Entries[0];
        entry.EventType.ShouldBe(AuditEventTypes.CatalogDiscoveryArtifactsChanged);
        entry.DetailsJson.ShouldNotBeNull();
        entry.DetailsJson!.ShouldContain(r1.ContentSha256!);
        entry.DetailsJson.ShouldNotContain("42.5");

        // 3. Data owner succeeds without CatalogArtifactUploader role
        owners.OwnedTables.Add(tableId);
        var owner = DiscoveryTestBuilder.User("S-OWNER", "tenant-a");
        var r2 = await service.UpsertAsync(owner, tableId.ToString(), payload, "api");
        r2.Outcome.ShouldBe(DiscoveryArtifactOutcome.Ok);

        // 4. GovernanceAdmin succeeds
        var admin = DiscoveryTestBuilder.User("S-ADMIN", "tenant-a", ["GovernanceAdmin"]);
        var r3 = await service.UpsertAsync(admin, tableId.ToString(), payload, "api");
        r3.Outcome.ShouldBe(DiscoveryArtifactOutcome.Ok);
    }

    [Fact]
    public async Task Upsert_UnauthorizedInTenantCaller_ReturnsForbidden()
    {
        var (service, tables, store, _, _) = CreateHarness();
        var caller = DiscoveryTestBuilder.User("S-USER", "tenant-a", ["AiAgent"]);

        var result = await service.UpsertAsync(
            caller,
            tables.Tables[0].Identifier.ToString(),
            Utf8("""{"schemaVersion":1,"samples":[{"id":1}]}"""),
            "api");

        result.Outcome.ShouldBe(DiscoveryArtifactOutcome.Forbidden);
        store.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task UpsertAndDelete_CrossTenantAndHiddenNonAdmin_MatchUnknownNotFound()
    {
        var (service, tables, store, _, _) = CreateHarness();
        var tableId = tables.Tables[0].Identifier.ToString();
        var body = Utf8("""{"schemaVersion":1,"samples":[{"id":1}]}""");

        // Cross-tenant uploader gets indistinguishable NotFound
        var foreignUploader = DiscoveryTestBuilder.User("S-OTHER", "tenant-b", ["CatalogArtifactUploader"]);
        var crossTenant = await service.UpsertAsync(foreignUploader, tableId, body, "api");
        var unknown = await service.UpsertAsync(foreignUploader, "sales.dbo.non_existent", body, "api");
        crossTenant.Outcome.ShouldBe(DiscoveryArtifactOutcome.NotFound);
        unknown.Outcome.ShouldBe(DiscoveryArtifactOutcome.NotFound);
        crossTenant.Message.ShouldBe(unknown.Message);

        // Hidden table for non-admin/non-owner uploader gets same NotFound
        tables.Tables[0] = tables.Tables[0] with { Table = tables.Tables[0].Table with { DiscoveryHidden = true } };
        var sameTenantUploader = DiscoveryTestBuilder.User("S-UP", "tenant-a", ["CatalogArtifactUploader"]);
        var hiddenPut = await service.UpsertAsync(sameTenantUploader, tableId, body, "api");
        var hiddenDel = await service.DeleteAsync(sameTenantUploader, tableId, "api");
        var unknownDel = await service.DeleteAsync(sameTenantUploader, "sales.dbo.non_existent", "api");
        hiddenPut.Outcome.ShouldBe(DiscoveryArtifactOutcome.NotFound);
        hiddenPut.Message.ShouldBe(unknown.Message);
        hiddenDel.Outcome.ShouldBe(DiscoveryArtifactOutcome.NotFound);
        hiddenDel.Message.ShouldBe(unknownDel.Message);
        store.Items.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("""{"schemaVersion":2,"samples":[{"id":1}]}""")]
    [InlineData("""{"schemaVersion":1,"samples":[{"id":1,"nonexistent":"x"}]}""")]
    [InlineData("""{"schemaVersion":1,"samples":[{"id":1,"secret_col":"x"}]}""")]
    [InlineData("""{"schemaVersion":1,"samples":[{"id":[1,2]}]}""")]
    [InlineData("""{"schemaVersion":1,"samples":[{"id":{"nested":1}}]}""")]
    [InlineData("""{"schemaVersion":1,"statistics":{"rowCount":-1}}""")]
    [InlineData("""{"schemaVersion":1,"statistics":{"table":{"approxRowCount":-1}}}""")]
    [InlineData("""{"schemaVersion":1,"statistics":{"columns":{"amount":{"nullRatio":1.5}}}}""")]
    [InlineData("""{"schemaVersion":1,"statistics":{"columns":{"amount":{"nullRatio":-0.1}}}}""")]
    [InlineData("""{"schemaVersion":1,"statistics":{"columns":{"amount":{"approxDistinctCount":-2}}}}""")]
    [InlineData("""{"schemaVersion":1,"statistics":{"columns":{"amount":{"maxLength":-1}}}}""")]
    [InlineData("""{"schemaVersion":1,"statistics":{"columns":{"secret_col":{"nullRatio":0.1}}}}""")]
    public async Task Upsert_InvalidArtifacts_RejectedBeforePersistence(string json)
    {
        var (service, tables, store, _, audit) = CreateHarness();
        var uploader = DiscoveryTestBuilder.User("S-UPLOADER", "tenant-a", ["CatalogArtifactUploader"]);

        var result = await service.UpsertAsync(uploader, tables.Tables[0].Identifier.ToString(), Utf8(json), "api");

        result.Outcome.ShouldBe(DiscoveryArtifactOutcome.InvalidArtifact);
        result.ViolationCode.ShouldNotBeNullOrWhiteSpace();
        store.Items.ShouldBeEmpty();
        audit.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Upsert_EnforcesByteSize_SampleRow_StringLength_AndTopValueCaps()
    {
        var (service, tables, store, _, _) = CreateHarness(s =>
        {
            s.MaxArtifactBytes = 200;
            s.MaxSampleRows = 2;
        });
        var uploader = DiscoveryTestBuilder.User("S-UPLOADER", "tenant-a", ["CatalogArtifactUploader"]);
        var tableId = tables.Tables[0].Identifier.ToString();

        // 1. Body size > MaxArtifactBytes -> BODY_TOO_LARGE
        var oversizedBody = Utf8($$"""{"schemaVersion":1,"samples":[{"amount":"{{new string('a', 250)}}"}]}""");
        var tooLarge = await service.UpsertAsync(uploader, tableId, oversizedBody, "api");
        tooLarge.Outcome.ShouldBe(DiscoveryArtifactOutcome.InvalidArtifact);
        tooLarge.ViolationCode.ShouldBe("BODY_TOO_LARGE");

        // 2. Sample row count > MaxSampleRows
        var (_, tables2, store2, _, _) = CreateHarness(s => s.MaxSampleRows = 2);
        var tooManyRows = await service.UpsertAsync(
            uploader,
            tables2.Tables[0].Identifier.ToString(),
            Utf8("""{"schemaVersion":1,"samples":[{"id":1},{"id":2},{"id":3}]}"""),
            "api");
        tooManyRows.Outcome.ShouldBe(DiscoveryArtifactOutcome.InvalidArtifact);
        tooManyRows.ViolationCode.ShouldBe("SAMPLE_ROW_LIMIT_EXCEEDED");

        // 3. String value > 1024 chars
        var (service3, tables3, store3, _, _) = CreateHarness();
        var longString = await service3.UpsertAsync(
            uploader,
            tables3.Tables[0].Identifier.ToString(),
            Utf8($$"""{"schemaVersion":1,"samples":[{"amount":"{{new string('z', 1025)}}"}]}"""),
            "api");
        longString.Outcome.ShouldBe(DiscoveryArtifactOutcome.InvalidArtifact);

        // 4. topValues > 20 entries
        var top21 = string.Join(",", Enumerable.Range(1, 21).Select(i => $"\"v{i}\""));
        var tooManyTop = await service3.UpsertAsync(
            uploader,
            tables3.Tables[0].Identifier.ToString(),
            Utf8("{\"schemaVersion\":1,\"statistics\":{\"columns\":{\"amount\":{\"topValues\":[" + top21 + "]}}}}"),
            "api");
        tooManyTop.Outcome.ShouldBe(DiscoveryArtifactOutcome.InvalidArtifact);

        store.Items.ShouldBeEmpty();
        store2.Items.ShouldBeEmpty();
        store3.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task AuditFailure_RollsBackUpsertAndDelete_AndDeleteIsIdempotent()
    {
        var (service, tables, store, _, audit) = CreateHarness();
        var uploader = DiscoveryTestBuilder.User("S-UPLOADER", "tenant-a", ["CatalogArtifactUploader"]);
        var table = tables.Tables[0];
        var tableKey = table.Identifier.ToString();

        // FailClosed audit failure on fresh insert rolls back (deletes new artifact)
        audit.FailWithUnavailable = true;
        var failedInsert = await service.UpsertAsync(uploader, tableKey, Utf8("""{"schemaVersion":1,"samples":[{"id":1}]}"""), "api");
        failedInsert.Outcome.ShouldBe(DiscoveryArtifactOutcome.Unavailable);
        store.Items.ShouldBeEmpty();

        // Seed initial artifact cleanly
        audit.FailWithUnavailable = false;
        var seed = await service.UpsertAsync(uploader, tableKey, Utf8("""{"schemaVersion":1,"samples":[{"id":10}]}"""), "api");
        seed.Outcome.ShouldBe(DiscoveryArtifactOutcome.Ok);
        var originalSha = store.Items[table.Table.Id].ContentSha256;

        // FailClosed audit failure on overwrite restores previous artifact
        audit.FailWithUnavailable = true;
        var failedOverwrite = await service.UpsertAsync(uploader, tableKey, Utf8("""{"schemaVersion":1,"samples":[{"id":20}]}"""), "api");
        failedOverwrite.Outcome.ShouldBe(DiscoveryArtifactOutcome.Unavailable);
        store.Items[table.Table.Id].ContentSha256.ShouldBe(originalSha);

        // FailClosed audit failure on delete restores deleted artifact
        var failedDelete = await service.DeleteAsync(uploader, tableKey, "api");
        failedDelete.Outcome.ShouldBe(DiscoveryArtifactOutcome.Unavailable);
        store.Items[table.Table.Id].ContentSha256.ShouldBe(originalSha);

        // Successful delete + idempotent second delete
        audit.FailWithUnavailable = false;
        var del1 = await service.DeleteAsync(uploader, tableKey, "api");
        var del2 = await service.DeleteAsync(uploader, tableKey, "api");
        del1.Outcome.ShouldBe(DiscoveryArtifactOutcome.NoContent);
        del2.Outcome.ShouldBe(DiscoveryArtifactOutcome.NoContent);
        store.Items.ShouldBeEmpty();
    }
}
