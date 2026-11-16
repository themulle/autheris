namespace Autheris.Tests.Unit.Governance.Classification;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Policy;
using Autheris.Application.Policy.Classification;
using Autheris.Application.Policy.RowScopes;
using Autheris.Application.Services;
using Autheris.Application.Sql.Masking;
using Autheris.Application.Sql.Policy;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class ClassificationAuditTests
{
    private static readonly TenantId Tenant = new("tenant-alpha");
    private static readonly Sid UserSid = new("S-1-5-21-USER-1");
    private static readonly TableIdentifier CustomersTable = new("crm", "public", "customers");
    private static readonly DateTimeOffset FixedNow = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Masked_column_audit_lists_classifications_function_keyRef_and_grant_levels_without_key_material()
    {
        var classRepo = new TestInMemoryClassificationStore(Tenant.Value);
        await classRepo.UpsertClassificationMaskRuleAsync(new ClassificationMaskRule
        {
            Scope = ClassificationScopes.Global,
            Code = ClassificationScopes.PiiCode,
            MaskFunction = MaskFunction.Hash,
            KeyRef = "test-hmac-key",
            ParamsJson = "{\"constant\":\"SECRET_CONSTANT_LITERAL\"}",
            Version = 1,
            UpdatedBy = "admin",
            UpdatedAt = FixedNow
        });

        var emailColId = Guid.NewGuid();
        var ssnColId = Guid.NewGuid();
        var meta = new TableMetadata
        {
            Identifier = CustomersTable,
            OwnerTenantId = Tenant.Value,
            Table = new Table { Id = Guid.NewGuid(), SourceName = "crm", SchemaName = "public", TableName = "customers", IsActive = true },
            Columns =
            [
                new TableColumn { Id = emailColId, ColumnName = "email", DataType = "varchar", IsSensitive = false },
                new TableColumn { Id = ssnColId, ColumnName = "ssn", DataType = "varchar", IsSensitive = false }
            ],
            ColumnClassifications = new Dictionary<string, IReadOnlyList<ColumnClassification>>(StringComparer.OrdinalIgnoreCase)
            {
                ["email"] = [new ColumnClassification { Scope = ClassificationScopes.Global, TableColumnId = emailColId.ToString(), Code = ClassificationScopes.PiiCode, Source = "OWNER", AssignedAt = FixedNow }],
                ["ssn"] =
                [
                    new ColumnClassification { Scope = ClassificationScopes.Global, TableColumnId = ssnColId.ToString(), Code = ClassificationScopes.PiiCode, Source = "OWNER", AssignedAt = FixedNow },
                    new ColumnClassification { Scope = ClassificationScopes.Global, TableColumnId = ssnColId.ToString(), Code = ClassificationScopes.PiiSpecialCode, Source = "OWNER", AssignedAt = FixedNow }
                ]
            }
        };

        var grantId = Guid.NewGuid();
        var consent = new Consent
        {
            Id = grantId,
            TenantId = Tenant,
            TableIdentifier = CustomersTable,
            GranteeType = GranteeType.User,
            GranteeSid = UserSid,
            Effect = ConsentEffect.Allow,
            ValidFrom = FixedNow.AddDays(-1),
            ValidTo = FixedNow.AddDays(30),
            ColumnRules =
            [
                new ConsentColumnRule { ColumnName = "email", AccessLevel = ColumnAccessLevel.Mask },
                new ConsentColumnRule { ColumnName = "ssn", AccessLevel = ColumnAccessLevel.Clear }
            ],
            ClassificationAccess =
            [
                new ConsentClassificationAccess { TenantId = Tenant.Value, ConsentId = grantId, Code = ClassificationScopes.PiiCode, AccessLevel = ColumnAccessLevel.Mask },
                new ConsentClassificationAccess { TenantId = Tenant.Value, ConsentId = grantId, Code = ClassificationScopes.PiiSpecialCode, AccessLevel = ColumnAccessLevel.Mask }
            ]
        };

        var time = new AdjustableClassificationTimeProvider(FixedNow);
        var options = new GatewayOptions();
        var maskProvider = new SqlDataMaskingProvider(Options.Create(options), new FakeClassificationSecretProvider(), timeProvider: time);
        var resolver = new ClassificationAccessResolver(maskProvider, classRepo, time);
        var rowFactory = new RowPolicyFactory(new PolicyPredicateRenderer());
        var policy = new TableAccessPolicy(
            new TestInMemoryConsentRepository([consent], filterWindow: false),
            new ConsentResolutionService(logger: NullLogger<ConsentResolutionService>.Instance),
            new TestNoOpConsentCache(),
            options,
            new NoOpMandatoryRowFilterResolver(),
            rowPolicyFactory: rowFactory,
            maskPolicyFactory: new ColumnMaskPolicyFactory(maskProvider),
            rowScopeCompiler: new RowScopeCompiler(rowPolicyFactory: rowFactory, classificationResolver: resolver, timeProvider: time),
            timeProvider: time,
            classificationResolver: resolver);

        var decision = await policy.DecideAsync(new TableAccessQuery(UserSid, Tenant, new HashSet<Sid>(), new HashSet<string>(), meta), CancellationToken.None);
        decision.IsAllowed.ShouldBeTrue();
        decision.MaskedColumnsAudit.ShouldNotBeEmpty();

        var emailEntry = decision.MaskedColumnsAudit.Single(e => e.Column == "email");
        emailEntry.Classifications.ShouldContain(ClassificationScopes.PiiCode);
        emailEntry.MaskFunction.ShouldBe("HASH");
        emailEntry.KeyRef.ShouldBe("test-hmac-key");

        var ssnEntry = decision.MaskedColumnsAudit.Single(e => e.Column == "ssn");
        ssnEntry.Classifications.ShouldContain(ClassificationScopes.PiiCode);
        ssnEntry.Classifications.ShouldContain(ClassificationScopes.PiiSpecialCode);

        var grantEntry = decision.GrantClassificationsAudit.Single(g => g.GrantId == grantId.ToString("D"));
        grantEntry.ClassificationLevels[ClassificationScopes.PiiCode].ShouldBe("MASK");
        grantEntry.ClassificationLevels[ClassificationScopes.PiiSpecialCode].ShouldBe("MASK");

        var details = GovernedQueryStages.DecisionDetails(decision, path: "websql");
        var serialized = JsonSerializer.Serialize(details);
        var redacted = AuditDetailsBuilder.RedactJson(serialized);

        redacted.ShouldContain("\"masked_columns\"");
        redacted.ShouldContain("\"grant_classifications\"");
        redacted.ShouldContain("\"test-hmac-key\"");
        redacted.ShouldNotContain("SECRET_CONSTANT_LITERAL");
        redacted.ShouldNotContain("keyBytes");
    }
}
