namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Localization;
using Autheris.Application.Catalog.Search;
using Autheris.Application.Catalog.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.AI;
using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Catalog;
using Autheris.Tests.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

/// <summary>
/// Security Verification Suite for Multilingual Catalog Texts, Language Negotiation & Search (#146).
/// Covers binding guardrails SEC-CAT146-01..07.
/// </summary>
public sealed class MultilingualCatalogSecurityTests
{
    /// <summary>
    /// SEC-CAT146-01: ReDoS safety in BCP 47 parser and negotiator.
    /// Parser must be zero-regex, linear time, bounded length, rejecting over-long input by length bound without unbounded work.
    /// </summary>
    [Fact]
    [Trait("Category", "Security")]
    public async Task T_CAT146_01_ReDoS_Safety_In_Bcp47Parser_And_Negotiator()
    {
        var negotiator = new LanguageNegotiator();
        var supported = new[] { "de", "en", "fr" };

        // Adversarial repeating patterns and long inputs
        var longMalformed = string.Join("-", Enumerable.Repeat("abcdefghij", 500));
        var adversarialHeader = string.Join(", ", Enumerable.Range(0, 200).Select(i => $"de-x-{new string('a', 50)};q={i / 200.0:F2}"));

        longMalformed.Length.ShouldBeGreaterThan(Bcp47LanguageTag.MaxTagLength);

        // Deterministic: over-long input is rejected by the length bound; the adversarial header falls back to the default.
        // The hang guard (30 s) is not a performance budget.
        await WaitHelper.RunGuarded(() =>
        {
            for (int i = 0; i < 50; i++)
            {
                Bcp47LanguageTag.TryParse(longMalformed, out _).ShouldBeFalse();
                Bcp47LanguageTag.TryCanonicalize(longMalformed, out _).ShouldBeFalse();
                negotiator.NegotiateLanguage(adversarialHeader, supported, "en").ShouldBe("en");
            }
        });
    }

    /// <summary>
    /// SEC-CAT146-02: Hidden column translations pruned by ShapeHit in CatalogDiscoveryService.
    /// Hits caused solely by hidden columns with localized text are never disclosed.
    /// </summary>
    [Fact]
    [Trait("Category", "Security")]
    public void T_CAT146_02_Hidden_Column_Translations_Pruned_By_ShapeHit()
    {
        var id = new TableIdentifier("sales", "dbo", "orders");
        var visibleTable = new TableMetadata
        {
            Identifier = id,
            CatalogView = CatalogView.Full,
            Table = new Table { TableName = id.TableName, SchemaName = id.Schema, SourceName = id.Domain, DisplayName = "Orders" },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "integer" }
            ]
        };

        var allTable = new TableMetadata
        {
            Identifier = id,
            CatalogView = CatalogView.Full,
            Table = new Table { TableName = id.TableName, SchemaName = id.Schema, SourceName = id.Domain, DisplayName = "Orders" },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "integer" },
                new TableColumn
                {
                    ColumnName = "secret_tax_id",
                    DataType = "varchar",
                    Texts = new LocalizedTexts(new Dictionary<string, LocalizedTextEntry>
                    {
                        ["de"] = new("Steueridentifikationsnummer", "Geheime Steuer-ID", null)
                    })
                }
            ]
        };

        var permittedById = new Dictionary<TableIdentifier, TableMetadata> { [id] = visibleTable };
        var allById = new Dictionary<TableIdentifier, TableMetadata> { [id] = allTable };

        // Hit came from matching "steuer-id" on the hidden column
        var hit = new CatalogSearchHit(
            TableIdentifier: id,
            DisplayName: id.TableName,
            Description: "Orders",
            Domain: id.Domain,
            Sensitivity: "Internal",
            CombinedScore: 0.9,
            Bm25Score: 0.9,
            VectorScore: null,
            MatchedTerms: ["steuer-id"],
            RelevantColumns: ["secret_tax_id"],
            RelatedJoinPaths: [],
            SuggestedGraphQlField: null);

        var shaped = CatalogDiscoveryService.ShapeHit(hit, permittedById, allById, ["steuer-id"]);

        // When the query term matches only the hidden column, the hit is pruned completely (null)
        shaped.ShouldBeNull();
    }

    /// <summary>
    /// SEC-CAT146-03: LocalizedTextsValidator rejects scripts/bidi without leaking raw payload in exception message.
    /// </summary>
    [Fact]
    [Trait("Category", "Security")]
    public void T_CAT146_03_LocalizedTextsValidator_Rejects_Scripts_And_Bidi_Without_Leaking_Payload()
    {
        const string secretPayload = "TOP_SECRET_CANARY_VALUE_12345";
        var xssInput = $"<script>{secretPayload}</script>";
        var bidiInput = $"Title with \u202E {secretPayload}";

        var xssTexts = new LocalizedTexts(new Dictionary<string, LocalizedTextEntry>
        {
            ["en"] = new(xssInput, "Description", null)
        });

        var xssEx = Should.Throw<ValidationException>(() => LocalizedTextsValidator.Validate(xssTexts));
        xssEx.Message.ShouldNotContain(secretPayload);
        xssEx.Message.ShouldContain("HTML tags are not permitted");

        var bidiTexts = new LocalizedTexts(new Dictionary<string, LocalizedTextEntry>
        {
            ["en"] = new("Title", bidiInput, null)
        });

        var bidiEx = Should.Throw<ValidationException>(() => LocalizedTextsValidator.Validate(bidiTexts));
        bidiEx.Message.ShouldNotContain(secretPayload);
        bidiEx.Message.ShouldContain("BiDi control characters are not permitted");
    }

    /// <summary>
    /// SEC-CAT146-04: Secrets scrubbed from translated texts in search index.
    /// </summary>
    [Fact]
    [Trait("Category", "Security")]
    public async Task T_CAT146_04_Secrets_Scrubbed_From_Translated_Texts_In_Search_Index()
    {
        var embedder = new LocalDeterministicEmbeddingGenerator();
        var options = Options.Create(new CatalogSearchOptions());
        var engine = new CatalogSearchEngine(embedder, options, NullLogger<CatalogSearchEngine>.Instance);

        var id = new TableIdentifier("corp", "dbo", "config");
        var secretAwsKey = "AKIAIOSFODNN7EXAMPLE";
        var table = new TableMetadata
        {
            Identifier = id,
            CatalogView = CatalogView.Full,
            Table = new Table
            {
                TableName = id.TableName,
                SchemaName = id.Schema,
                SourceName = id.Domain,
                Texts = new LocalizedTexts(new Dictionary<string, LocalizedTextEntry>
                {
                    ["de"] = new("Konfiguration", $"Enthält AWS Schlüssel {secretAwsKey} für Backup", null)
                })
            },
            Columns = [new TableColumn { ColumnName = "id", DataType = "integer" }]
        };

        await engine.RebuildIndexAsync([table]);

        // Search for the exact raw secret key must return zero hits because SecretScrubber redacted it
        var hits = engine.Search(new CatalogSearchQuery(secretAwsKey), _ => true);
        hits.ShouldBeEmpty();
    }

    /// <summary>
    /// SEC-CAT146-05: Corrupt texts_json fails closed to LocalizedTexts.Empty and logs only TargetId and exception type name.
    /// </summary>
    [Fact]
    [Trait("Category", "Security")]
    public void T_CAT146_05_Corrupt_TextsJson_Fails_Closed_And_Logs_Only_TargetId_And_ExType()
    {
        var testSink = new TestLogSink();
        var logger = new TestLogger<object>(testSink);

        const string corruptPayload = "{ \"de\": { \"title\": \"Incomplete... UNCLOSED_SECRET_VALUE_99999\" ";
        const string targetId = "table:sales.dbo.orders";

        var result = CatalogLocalizationPersistence.ParseLocalizedTexts(corruptPayload, targetId, logger);

        // Fail-closed to empty
        result.ShouldBeSameAs(LocalizedTexts.Empty);
        result.IsEmpty.ShouldBeTrue();

        // Must log a warning
        testSink.Messages.ShouldNotBeEmpty();
        var logMessage = testSink.Messages.First();
        logMessage.ShouldContain(targetId);
        logMessage.ShouldContain(nameof(System.Text.Json.JsonException));
        // MUST NOT contain the raw corrupt payload or secret canary
        logMessage.ShouldNotContain("UNCLOSED_SECRET_VALUE_99999");
        logMessage.ShouldNotContain("Incomplete");
    }

    /// <summary>
    /// SEC-CAT146-06: Text-only updates bump content_version, not structure_version.
    /// </summary>
    [Fact]
    [Trait("Category", "Security")]
    public async Task T_CAT146_06_TextOnly_Updates_Bump_ContentVersion_Not_StructureVersion()
    {
        using var repo = GovernanceStoreFactory.CreateSqlite(new EpochValidationService());

        var id = new TableIdentifier("sales", "dbo", "customers");
        var initial = new TableMetadata
        {
            Identifier = id,
            CatalogView = CatalogView.Full,
            Table = new Table
            {
                Id = Guid.NewGuid(),
                TableName = id.TableName,
                SchemaName = id.Schema,
                SourceName = id.Domain,
                DisplayName = "Customers",
                Description = "Customer records",
                SourceType = "sqlite"
            },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "integer" },
                new TableColumn { ColumnName = "name", DataType = "varchar" }
            ]
        };

        await repo.UpsertTableMetadataAsync(initial);

        var v1 = await repo.GetCatalogVersionAsync();
        v1.StructureVersion.ShouldBe(2); // Initial insert is structural, bumps 1 -> 2
        v1.ContentVersion.ShouldBe(1);

        // Update ONLY text metadata (DisplayName, Texts)
        var updatedTexts = new TableMetadata
        {
            Identifier = id,
            CatalogView = CatalogView.Full,
            Table = new Table
            {
                Id = initial.Table.Id,
                TableName = id.TableName,
                SchemaName = id.Schema,
                SourceName = id.Domain,
                DisplayName = "Kundenstamm",
                Description = "Kundenstammdaten und Adressen",
                SourceType = "sqlite",
                Texts = new LocalizedTexts(new Dictionary<string, LocalizedTextEntry>
                {
                    ["de"] = new("Kundenstamm", "Kundenstammdaten", null)
                })
            },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "integer" },
                new TableColumn { ColumnName = "name", DataType = "varchar" }
            ]
        };

        await repo.UpsertTableMetadataAsync(updatedTexts);

        var v2 = await repo.GetCatalogVersionAsync();
        v2.StructureVersion.ShouldBe(2); // Structure did NOT bump! (Still 2)
        v2.ContentVersion.ShouldBe(2);   // Content DID bump! (1 -> 2)

        // Now update structure: add a new column
        var updatedStructure = new TableMetadata
        {
            Identifier = id,
            CatalogView = CatalogView.Full,
            Table = updatedTexts.Table,
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "integer" },
                new TableColumn { ColumnName = "name", DataType = "varchar" },
                new TableColumn { ColumnName = "email", DataType = "varchar" }
            ]
        };

        await repo.UpsertTableMetadataAsync(updatedStructure);

        var v3 = await repo.GetCatalogVersionAsync();
        v3.StructureVersion.ShouldBe(3); // Structure DID bump! (2 -> 3)
        v3.ContentVersion.ShouldBe(2);   // Content did NOT bump! (Still 2)
    }

    /// <summary>
    /// SEC-CAT146-07: Accept-Language header bounded parsing (clamp > 512 chars, > 8 ranges).
    /// </summary>
    [Fact]
    [Trait("Category", "Security")]
    public void T_CAT146_07_AcceptLanguage_Bounded_Parsing_Clamps_Length_And_Ranges()
    {
        var negotiator = new LanguageNegotiator();
        var supported = new[] { "de", "en", "fr" };

        // Construct a huge header: 60 ranges, > 1,000 characters
        var ranges = Enumerable.Range(0, 60).Select(i => $"language-subtag-{i};q=0.9").ToList();
        ranges.Insert(0, "es;q=0.5");
        // Place a valid supported language late in the list (e.g. at position 15)
        ranges.Insert(15, "de;q=0.99");
        var hugeHeader = string.Join(", ", ranges);

        hugeHeader.Length.ShouldBeGreaterThan(512);

        // Clamping to first 512 chars and max 8 ranges ensures that range #15 ('de') is never reached;
        // fallback to default occurs because none of the first 8 ranges matches supported languages.
        var result = negotiator.NegotiateLanguage(hugeHeader, supported, "en");
        result.ShouldBe("en");
    }

    private sealed class TestLogSink
    {
        public List<string> Messages { get; } = [];
    }

    private sealed class TestLogger<T>(TestLogSink sink) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            sink.Messages.Add(formatter(state, exception));
        }
    }
}
