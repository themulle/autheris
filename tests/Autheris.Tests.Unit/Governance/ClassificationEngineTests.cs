namespace Autheris.Tests.Unit.Governance;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Governance.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

public sealed class ClassificationEngineTests
{
    private readonly IOptionsMonitor<GatewayOptions> _gatewayOptions;

    public ClassificationEngineTests()
    {
        var options = new GatewayOptions
        {
            Classification = new ClassificationOptions()
        };
        _gatewayOptions = new TestOptionsMonitor<GatewayOptions>(options);
    }

    [Fact]
    public async Task ClassifyTableAsync_DeterministicRegex_MatchesStandardPiiCategories()
    {
        var engine = new ClassificationEngine(_gatewayOptions, NullLogger<ClassificationEngine>.Instance);

        var context = new TableClassificationContext(
            new TableIdentifier("erp", "finance", "customer_accounts"),
            new List<ColumnMetadataContext>
            {
                new("customer_iban", "varchar(34)"),
                new("pan_credit_card", "varchar(16)"),
                new("billing_email", "varchar(255)"),
                new("support_phone", "varchar(50)"),
                new("client_ip", "varchar(45)"),
                new("dob", "date"),
                new("full_name", "varchar(100)"),
                new("geo_lat", "decimal(9,6)"),
                new("icd10_diagnosis", "varchar(50)"),
                new("description_notes", "text")
            });

        var proposal = await engine.ClassifyTableAsync(context);

        proposal.ShouldNotBeNull();
        proposal.Columns.Count.ShouldBe(10);

        // IBAN
        proposal.Columns[0].ColumnName.ShouldBe("customer_iban");
        proposal.Columns[0].DetectedPiiCategoryKey.ShouldBe("IBAN");
        proposal.Columns[0].ProposedSensitivityKey.ShouldBe("L3_CONFIDENTIAL");
        proposal.Columns[0].ProposedMaskingRule.ShouldBe("IBAN_STANDARD_4_4");
        proposal.Columns[0].IsDisputed.ShouldBeFalse();

        // Credit Card
        proposal.Columns[1].DetectedPiiCategoryKey.ShouldBe("CREDIT_CARD");
        proposal.Columns[1].ProposedSensitivityKey.ShouldBe("L4_STRICTLY_CONFIDENTIAL");
        proposal.Columns[1].ProposedMaskingRule.ShouldBe("CREDIT_CARD_LAST_4");

        // Email
        proposal.Columns[2].DetectedPiiCategoryKey.ShouldBe("EMAIL");
        proposal.Columns[2].ProposedMaskingRule.ShouldBe("EMAIL_DOMAIN_RETAIN");

        // Phone
        proposal.Columns[3].DetectedPiiCategoryKey.ShouldBe("PHONE_NUMBER");

        // IP
        proposal.Columns[4].DetectedPiiCategoryKey.ShouldBe("IP_ADDRESS");

        // Date of Birth
        proposal.Columns[5].DetectedPiiCategoryKey.ShouldBe("BIRTH_DATE");

        // Full Name
        proposal.Columns[6].DetectedPiiCategoryKey.ShouldBe("FULL_NAME");

        // Geo Location
        proposal.Columns[7].DetectedPiiCategoryKey.ShouldBe("GEO_LOCATION");

        // Health Data
        proposal.Columns[8].DetectedPiiCategoryKey.ShouldBe("HEALTH_DATA");
        proposal.Columns[8].ProposedSensitivityRank.ShouldBe(4);
        proposal.Columns[8].ProposedMaskingRule.ShouldBe("REDACT_COMPLETELY");

        // Default sensitivity for non-PII notes
        proposal.Columns[9].DetectedPiiCategoryKey.ShouldBeNull();
        proposal.Columns[9].ProposedSensitivityKey.ShouldBe("L2_INTERNAL");
        proposal.Columns[9].ProposedMaskingRule.ShouldBe("NONE");
    }

    [Fact]
    public async Task ClassifyTableAsync_ResolvesOwnerFromDbtMetadataTags()
    {
        var engine = new ClassificationEngine(_gatewayOptions, NullLogger<ClassificationEngine>.Instance);

        var context = new TableClassificationContext(
            new TableIdentifier("warehouse", "dbt_models", "dim_customers"),
            new List<ColumnMetadataContext> { new("id", "int") },
            MetadataTags: new Dictionary<string, string> { ["dbt_meta_owner"] = "data-team-alpha@corp.local" });

        var proposal = await engine.ClassifyTableAsync(context);

        proposal.DataOwnerSid.ShouldBe("data-team-alpha@corp.local");
        proposal.Status.ShouldBe("PENDING_OWNER_REVIEW");
    }

    [Fact]
    public async Task ClassifyTableAsync_ZeroAi_WorksCompletelyWithoutAiProvider()
    {
        var engine = new ClassificationEngine(_gatewayOptions, NullLogger<ClassificationEngine>.Instance, aiProvider: null);

        var context = new TableClassificationContext(
            new TableIdentifier("db", "schema", "lookup_countries"),
            new List<ColumnMetadataContext> { new("country_code", "char(2)") });

        var proposal = await engine.ClassifyTableAsync(context);

        proposal.ShouldNotBeNull();
        proposal.Columns[0].ProposedSensitivityKey.ShouldBe("L2_INTERNAL");
    }

    private sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public TestOptionsMonitor(T currentValue) => CurrentValue = currentValue;
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
