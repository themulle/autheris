namespace Autheris.Tests.Unit.Catalog.Search;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Application.Catalog.Search;
using Autheris.Application.Security;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.AI;
using Autheris.Tests.Common;
using Autheris.Tests.Unit.Catalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

/// <summary>
/// Catalog search must not reveal restricted tables or columns through presence, ranking, scores or text, and embedding
/// calls must follow the outbound egress allowlist (fail closed) and carry catalog metadata only.
/// </summary>
public sealed class CatalogSearchSecurityTests
{
    private const string Sid = "S-A-USER";

    private static readonly TableMetadata PaymentsWithDeniedColumn = Wp0.Table(
        "fin", "payments", "Payments for invoice settlement", ["id", "invoice_ref", "taxpayer_code"]) with
    {
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "varchar" },
            new TableColumn { ColumnName = "invoice_ref", DataType = "varchar" },
            new TableColumn { ColumnName = "taxpayer_code", DataType = "varchar", Description = "Confidential taxpayer invoice number" }
        ]
    };

    private static readonly TableMetadata Register = Wp0.Table("fin", "register", "Invoice register", ["id", "amount"]);
    private static readonly TableMetadata Customers = Wp0.Table("crm", "customers", "Customer master data", ["id", "name"]);
    private static readonly TableMetadata Restricted = Wp0.Table(
        "hr", "payroll_vault", "Confidential salaries and invoice bonus statements invoice invoice", ["id", "salary_invoice_total"]);

    private static async Task<IReadOnlyList<CatalogSearchHit>> SearchAsync(TableMetadata[] catalog, string term)
    {
        var engine = new CatalogSearchEngine(
            new LocalDeterministicEmbeddingGenerator(),
            Options.Create(new CatalogSearchOptions()),
            NullLogger<CatalogSearchEngine>.Instance);
        await engine.RebuildIndexAsync(catalog);

        var consents = new[]
        {
            Wp0.Allow(Register.Identifier, Sid, Wp0.TenantA),
            Wp0.Allow(Customers.Identifier, Sid, Wp0.TenantA),
            Wp0.Allow(PaymentsWithDeniedColumn.Identifier, Sid, Wp0.TenantA, "id", "invoice_ref")
        };
        var service = Wp0.Service(Wp0.Tables(catalog), Wp0.Consents(consents), Wp0.Options(TenancyShape.Multi), engine);
        var context = Wp0.Context(Wp0.User(Sid, "tenant-a"));
        return await service.SearchCatalogDetailedAsync(new CatalogSearchQuery(term, Mode: CatalogSearchMode.KeywordBm25, Limit: 10), context);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Restricted_tables_absent_from_results_scores_counts_snippets()
    {
        var withRestricted = await SearchAsync([Register, Customers, PaymentsWithDeniedColumn, Restricted], "invoice");
        var withoutRestricted = await SearchAsync([Register, Customers, PaymentsWithDeniedColumn], "invoice");

        // Presence and counts: only visible tables, same total as the catalog that never had the restricted table.
        withRestricted.Select(h => h.TableIdentifier.TableName).ShouldNotContain("payroll_vault");
        withRestricted.Count.ShouldBe(withoutRestricted.Count);
        withRestricted.GroupBy(h => h.Domain).ToDictionary(g => g.Key!, g => g.Count())
            .ShouldBe(withoutRestricted.GroupBy(h => h.Domain).ToDictionary(g => g.Key!, g => g.Count()));

        // Ranking and scores: identical to a catalog in which the restricted table does not exist (filter before rank).
        withRestricted.Select(h => h.TableIdentifier.TableName).ToArray()
            .ShouldBe(withoutRestricted.Select(h => h.TableIdentifier.TableName).ToArray());
        withRestricted.Select(h => h.CombinedScore).ToArray().ShouldBe(withoutRestricted.Select(h => h.CombinedScore).ToArray());
        withRestricted.Select(h => h.Bm25Score).ToArray().ShouldBe(withoutRestricted.Select(h => h.Bm25Score).ToArray());

        // Snippets, match reasons, descriptions: no text of the restricted table or the denied column.
        var everything = JsonSerializer.Serialize(withRestricted);
        foreach (var forbidden in new[] { "payroll_vault", "salaries", "salary_invoice_total", "taxpayer_code", "taxpayer", "Confidential" })
        {
            everything.ShouldNotContain(forbidden, Case.Insensitive);
        }

        withRestricted.SelectMany(h => h.RelevantColumns).ShouldNotContain("taxpayer_code");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Embedding_egress_refused_for_host_not_on_allowlist()
    {
        const string privateEndpoint = "https://10.1.2.3:11434";
        const string sampleSentinel = "SAMPLE-VALUE-4711";
        var table = Wp0.Table("fin", "payments", "Payments ledger", ["id", "amount"]) with
        {
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "varchar", Meta = new Dictionary<string, string> { ["sample_value"] = sampleSentinel } },
                new TableColumn { ColumnName = "amount", DataType = "decimal" }
            ]
        };

        // 1. Production wiring: the embeddings client resolved from DI is guarded by the egress policy (nothing allowlisted).
        using (var refused = BuildFactoryProvider(new OutboundEgressOptions()))
        {
            var client = refused.GetRequiredService<IHttpClientFactory>().CreateClient("CatalogEmbeddings");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Should.ThrowAsync<SecurityException>(async () =>
                await client.PostAsync($"{privateEndpoint}/api/embeddings", new StringContent("{}"), cts.Token));

            var generator = new OllamaEmbeddingGenerator(privateEndpoint, httpClient: client);
            var vectors = await generator.GenerateNormalizedEmbeddingsAsync(["Payments ledger"]);
            vectors.Count.ShouldBe(1);
            vectors[0].IsEmpty.ShouldBeTrue();
        }

        // 2. Allowlisted host: the request goes out and carries catalog metadata only (no sample values).
        var recorder = new RecordingHandler();
        var egress = new OutboundEgressOptions { TrustedInternalNetworks = ["10.1.0.0/16"], TrustedIntegrations = ["CatalogEmbeddings"] };
        var guard = new SsrfProtectionHandler(null, Options.Create(new GatewayOptions { Egress = egress }), "CatalogEmbeddings") { InnerHandler = recorder };
        using var allowedClient = new HttpClient(guard);
        var allowedGenerator = new OllamaEmbeddingGenerator(privateEndpoint, httpClient: allowedClient);
        var engine = new CatalogSearchEngine(
            allowedGenerator,
            Options.Create(new CatalogSearchOptions { EmbeddingProvider = CatalogEmbeddingProvider.Ollama, OllamaEndpoint = privateEndpoint }),
            NullLogger<CatalogSearchEngine>.Instance);
        await engine.RebuildIndexAsync([table]);

        recorder.Bodies.ShouldNotBeEmpty();
        var sent = string.Join('\n', recorder.Bodies);
        sent.ShouldContain("Payments ledger");
        sent.ShouldNotContain(sampleSentinel);
    }

    private static ServiceProvider BuildFactoryProvider(OutboundEgressOptions egress)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new GatewayOptions { Egress = egress }));
        services.AddAutherisCatalog();
        return services.BuildServiceProvider();
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError);
        }
    }
}
