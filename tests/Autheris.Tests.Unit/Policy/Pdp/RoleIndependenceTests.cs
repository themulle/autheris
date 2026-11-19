namespace Autheris.Tests.Unit.Policy.Pdp;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autheris.Application.Policy;
using Autheris.Domain.Governance;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

/// <summary>Role grantees are removed: every entry point refuses them and an unknown grantee type is never skipped.</summary>
public sealed class RoleIndependenceTests
{
    private static readonly Sid User = new("S-1-5-21-ALICE-1001");
    private static readonly IReadOnlySet<Sid> NoGroups = new HashSet<Sid>();
    private static readonly IReadOnlySet<string> Roles = new HashSet<string>(["analysts"], StringComparer.OrdinalIgnoreCase);

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("Role")]
    [InlineData("role")]
    [InlineData(" ROLE ")]
    public void Security_RoleGranteeInput_Refused_AllEntryPoints(string literal)
    {
        // Shared parser: the retired literal is recognised and never parsed to a grantee type; numeric literals are not enum values.
        GranteeTypeParsing.IsRetiredRole(literal).ShouldBeTrue();
        GranteeTypeParsing.TryParse(literal, out _).ShouldBeFalse();
        GranteeTypeParsing.TryParse("3", out _).ShouldBeFalse();
        GranteeTypeParsing.TryParse("0", out _).ShouldBeFalse();
        GranteeTypeParsing.TryParse("Group", out var group).ShouldBeTrue();
        group.ShouldBe(GranteeType.Group);

        // Virtual filter profile body: a fixed refusal that never echoes the supplied grantee text.
        var body = new Autheris.Api.Endpoints.VirtualFilterEndpoints.ProfileBody
        {
            Scope = "a.b.c",
            Uncovered = "deny",
            Grantee = new Autheris.Api.Endpoints.VirtualFilterEndpoints.GranteeBody { Type = literal.Trim(), Sid = "S-1-5-21-X" }
        };
        var ex = Should.Throw<ArgumentException>(() => body.ToModel("p", new TenantId("t")));
        ex.Message.ShouldBe(GranteeTypeParsing.RoleRefusedMessage);

        // The bulk grant and the profile assignment entry points are covered by AccessProfileTests (Security_RoleGranteeInput_Refused_Bulk)
        // and ProfileAssignmentTests; the GraphQL consent types take no grantee type as input.
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Security_UnknownGranteeType_OnActiveRow_ThrowsAndDecisionDenies()
    {
        const GranteeType Retired = (GranteeType)3;
        var table = new TableIdentifier("d", "s", "t");
        var now = DateTimeOffset.UtcNow;

        // Restrictions never skip an unknown type: the check throws, so the decision that needed it cannot allow.
        Should.Throw<ArgumentOutOfRangeException>(() => GranteeMatcher.MatchesRestriction(Retired, new Sid("S-1-5-21-X"), User, NoGroups));
        GranteeMatcher.MatchesGrant(Retired, User, User, NoGroups).ShouldBeFalse("an unknown type never grants");

        var deny = new Consent
        {
            TenantId = Autheris.Tests.Common.TestTenants.Default,
            TableIdentifier = table,
            Effect = ConsentEffect.Deny,
            GranteeType = Retired,
            GranteeSid = User,
            ValidFrom = now.AddDays(-1),
            ValidTo = now.AddDays(1)
        };
        var allow = new Consent
        {
            TenantId = Autheris.Tests.Common.TestTenants.Default,
            TableIdentifier = table,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = User,
            ValidFrom = now.AddDays(-1),
            ValidTo = now.AddDays(1),
            ColumnRules = [new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear }]
        };
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new Autheris.Application.Services.ConsentResolutionService().ResolveAccess(User, NoGroups, Roles, table, [allow, deny]));

        // A persisted profile with an unknown grantee type fails the snapshot load; it is never skipped.
        var definition = "{\"GranteeType\":3,\"GranteeSid\":null,\"Scope\":\"a.b.c\",\"Uncovered\":0,\"Bindings\":[]}";
        var rows = new System.Data.DataTable();
        foreach (var column in new[] { "id", "tenant_id", "name", "definition_json", "definition_hash", "managed_path", "managed_commit", "updated_by", "updated_at" })
        {
            rows.Columns.Add(column, typeof(string));
        }

        rows.Rows.Add(Guid.NewGuid().ToString(), "t", "p", definition, "h", null, null, null, now.ToString("O"));
        using var reader = rows.CreateDataReader();
        reader.Read().ShouldBeTrue();
        var readProfile = typeof(Autheris.Infrastructure.Persistence.VirtualFilterStore).GetMethod("ReadProfile", BindingFlags.NonPublic | BindingFlags.Static)!;
        var thrown = Should.Throw<TargetInvocationException>(() => readProfile.Invoke(null, [reader]));
        thrown.InnerException.ShouldBeOfType<InvalidOperationException>();

        // A persisted consent row with the retired grantee text fails the load as well (the enum member is gone).
        Should.Throw<ArgumentException>(() => Enum.Parse<GranteeType>("Role", ignoreCase: true));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Security_ActiveRoleDenyRow_BeforeStep_DeniesTable()
    {
        // Before step 0029 runs, a stored role Deny row reaches the readers as text: the load throws (fail closed) instead of
        // dropping the row, so no decision on that table can allow.
        var rows = new System.Data.DataTable();
        foreach (var (name, type) in new (string, Type)[]
                 {
                     ("id", typeof(string)), ("table_id", typeof(string)), ("consent_request_id", typeof(string)), ("effect", typeof(string)),
                     ("grantee_type", typeof(string)), ("grantee_sid", typeof(string)), ("valid_from", typeof(string)), ("valid_to", typeof(string)),
                     ("is_revoked", typeof(int)), ("tenant_id", typeof(string))
                 })
        {
            rows.Columns.Add(name, type);
        }

        var now = DateTimeOffset.UtcNow;
        rows.Rows.Add(Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), null, "Deny", "Role", null, now.AddDays(-1).ToString("O"), now.AddDays(1).ToString("O"), 0, "t");
        using var reader = rows.CreateDataReader();
        var consents = new List<Consent>();

        Should.Throw<ArgumentException>(() => Autheris.Infrastructure.Persistence.ConsentSqlCore.ReadSubjectConsentRows(
            reader, new TableIdentifier("d", "s", "t"), now, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "x" }, new HashSet<Guid>(), consents));
        consents.ShouldBeEmpty();
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("1")]
    [InlineData("2")]
    [InlineData("3")]
    [InlineData("99")]
    [InlineData("-1")]
    [InlineData("Role")]
    [InlineData("")]
    public void Security_StoredGranteeType_RefusesNumericUndefinedAndRetiredValues(string stored)
    {
        Should.Throw<ArgumentException>(() => GranteeTypeParsing.ParseStored(stored));
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("user", GranteeType.User)]
    [InlineData("Group", GranteeType.Group)]
    [InlineData("SERVICEPRINCIPAL", GranteeType.ServicePrincipal)]
    public void StoredGranteeType_ParsesDefinedNamesIgnoringCase(string stored, GranteeType expected)
    {
        GranteeTypeParsing.ParseStored(stored).ShouldBe(expected);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void RoleAssignee_IsRejectedForProfileAssignments()
    {
        var profile = new AccessProfile { TenantId = new TenantId("t"), ProfileId = "p", Name = "p", MaskingMode = MaskingPolicyMode.Strict };
        var assignment = new AccessProfileAssignment { Subject = "analysts", SubjectType = AccessProfileSubjectType.Role };

        Should.Throw<AccessProfileAssignmentRejectedException>(() => AccessProfileAssignmentRules.ValidateForAssignment(profile, assignment, DateTimeOffset.UtcNow))
            .Code.ShouldBe(AccessProfileAssignmentRules.RoleSubjectUnsupported);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void No_data_plane_functional_rights_exist()
    {
        var declared = typeof(FunctionalRight).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(FunctionalRight))
            .Select(f => ((FunctionalRight)f.GetValue(null)!).Value)
            .ToList();

        declared.ShouldNotContain("Data.UnmaskSensitive");
        declared.ShouldNotContain("Data.ExportClearText");
        FunctionalRight.PrivilegedValues.ShouldNotContain("Data.UnmaskSensitive");
        FunctionalRight.PrivilegedValues.ShouldNotContain("Data.ExportClearText");
        ((FunctionalRight)"Data.UnmaskSensitive").IsPrivileged.ShouldBeFalse("an unknown persisted value grants and privileges nothing");
    }
}
