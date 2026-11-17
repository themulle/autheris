using Autheris.Infrastructure.Persistence.Migrations;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Persistence;

public sealed class ConsentApiMigrationStep0014Tests
{
    [Trait("Category", "Security")]
    [Fact]
    public void Consents_GrantSource_Backfill_DefaultsTo_ConsentRequest_NeverDeclarative()
    {
        var step = SqlMigrationStep.LoadEmbedded("postgresql")
            .Single(s => s.Name == "consent_api");

        // Existing rows are backfilled by the column default: a legacy consent must never become a declarative grant.
        step.Sql.ShouldContain("ALTER TABLE CONSENTS ADD COLUMN IF NOT EXISTS grant_source VARCHAR(24) NOT NULL DEFAULT 'CONSENT_REQUEST'");
        step.Sql.ShouldNotContain("DEFAULT 'DECLARATIVE'");
        step.Sql.ShouldContain("ALTER TABLE CONSENT_REQUESTS ADD COLUMN IF NOT EXISTS workflow_mode VARCHAR(16) NOT NULL DEFAULT 'INTERNAL'");
    }
}
