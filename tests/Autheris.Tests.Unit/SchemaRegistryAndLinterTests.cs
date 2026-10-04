namespace Autheris.Tests.Unit;

using Autheris.Application.SchemaRegistry;
using Autheris.Application.SchemaRegistry.Validation;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

public class SchemaRegistryAndLinterTests
{
    private readonly SchemaLinter _linter = new();

    [Fact]
    public void SchemaLinter_IdenticalSchemas_ReturnsZeroChanges()
    {
        var sdl = """
            type Query {
                user(id: ID!): User
            }
            type User {
                id: ID!
                name: String!
            }
            """;

        var diff = _linter.Compare(sdl, sdl);

        diff.Changes.Count.ShouldBe(0);
        diff.HasBreakingChanges.ShouldBeFalse();
        diff.IsCompatible.ShouldBeTrue();
    }

    [Fact]
    public void SchemaLinter_RemovedField_ReportsBreakingChange()
    {
        var baseline = """
            type User {
                id: ID!
                name: String!
                email: String
            }
            """;

        var target = """
            type User {
                id: ID!
                name: String!
            }
            """;

        var diff = _linter.Compare(baseline, target);

        diff.HasBreakingChanges.ShouldBeTrue();
        diff.BreakingCount.ShouldBe(1);
        var change = diff.Changes[0];
        change.Code.ShouldBe("FIELD_REMOVED");
        change.Path.ShouldBe("User.email");
        change.IsBreaking.ShouldBeTrue();
    }

    [Fact]
    public void SchemaLinter_AddedOutputField_ReportsSafeChange()
    {
        var baseline = """
            type User {
                id: ID!
            }
            """;

        var target = """
            type User {
                id: ID!
                displayName: String
            }
            """;

        var diff = _linter.Compare(baseline, target);

        diff.HasBreakingChanges.ShouldBeFalse();
        diff.SafeCount.ShouldBe(1);
        diff.Changes[0].Code.ShouldBe("FIELD_ADDED");
        diff.Changes[0].Path.ShouldBe("User.displayName");
    }

    [Fact]
    public void SchemaLinter_DeprecatedField_ReportsDangerousChange()
    {
        var baseline = """
            type User {
                id: ID!
                oldField: String
            }
            """;

        var target = """
            type User {
                id: ID!
                oldField: String @deprecated(reason: "Use newField instead")
            }
            """;

        var diff = _linter.Compare(baseline, target);

        diff.HasBreakingChanges.ShouldBeFalse();
        diff.DangerousCount.ShouldBe(1);
        diff.Changes[0].Code.ShouldBe("FIELD_DEPRECATED");
    }

    [Fact]
    public void SchemaLinter_FieldTypeChanged_ReportsBreakingChange()
    {
        var baseline = """
            type Product {
                price: Float!
            }
            """;

        var target = """
            type Product {
                price: String!
            }
            """;

        var diff = _linter.Compare(baseline, target);

        diff.HasBreakingChanges.ShouldBeTrue();
        diff.Changes[0].Code.ShouldBe("FIELD_TYPE_CHANGED");
    }

    [Fact]
    public void SchemaLinter_RemovedType_ReportsBreakingChange()
    {
        var baseline = """
            type User { id: ID! }
            type Organization { id: ID! }
            """;

        var target = """
            type User { id: ID! }
            """;

        var diff = _linter.Compare(baseline, target);

        diff.HasBreakingChanges.ShouldBeTrue();
        diff.Changes[0].Code.ShouldBe("TYPE_REMOVED");
        diff.Changes[0].Path.ShouldBe("Organization");
    }

    [Fact]
    public void SchemaLinter_RequiredInputFieldAdded_ReportsBreakingChange()
    {
        var baseline = """
            input CreateUserInput {
                name: String!
            }
            """;

        var target = """
            input CreateUserInput {
                name: String!
                taxId: String!
            }
            """;

        var diff = _linter.Compare(baseline, target);

        diff.HasBreakingChanges.ShouldBeTrue();
        diff.Changes[0].Code.ShouldBe("REQUIRED_INPUT_FIELD_ADDED");
    }

    [Fact]
    public void SchemaLinter_OptionalInputFieldAdded_ReportsSafeChange()
    {
        var baseline = """
            input CreateUserInput {
                name: String!
            }
            """;

        var target = """
            input CreateUserInput {
                name: String!
                bio: String
            }
            """;

        var diff = _linter.Compare(baseline, target);

        diff.HasBreakingChanges.ShouldBeFalse();
        diff.SafeCount.ShouldBe(1);
        diff.Changes[0].Code.ShouldBe("OPTIONAL_INPUT_FIELD_ADDED");
    }

    [Fact]
    public void SchemaLinter_EnumValues_DiffsCorrectly()
    {
        var baseline = """
            enum Status {
                ACTIVE
                INACTIVE
                PENDING
            }
            """;

        var target = """
            enum Status {
                ACTIVE
                INACTIVE
                ARCHIVED
            }
            """;

        var diff = _linter.Compare(baseline, target);

        diff.BreakingCount.ShouldBe(1); // PENDING removed
        diff.Changes.ShouldContain(c => c.Code == "ENUM_VALUE_REMOVED" && c.Path == "Status.PENDING");

        diff.DangerousCount.ShouldBe(1); // ARCHIVED added
        diff.Changes.ShouldContain(c => c.Code == "ENUM_VALUE_ADDED" && c.Path == "Status.ARCHIVED");
    }

    [Fact]
    public async Task SchemaRegistrationRequestValidator_ValidatesServiceAndSdlSyntax()
    {
        var validator = new SchemaRegistrationRequestValidator();

        var invalidServiceName = new SchemaRegistrationRequest
        {
            ServiceName = "service with spaces!",
            Sdl = "type Query { ping: String }"
        };
        var res1 = await validator.ValidateAsync(invalidServiceName);
        res1.IsValid.ShouldBeFalse();

        var invalidSdlSyntax = new SchemaRegistrationRequest
        {
            ServiceName = "orders-service",
            Sdl = "type Query { ping: }" // invalid syntax
        };
        var res2 = await validator.ValidateAsync(invalidSdlSyntax);
        res2.IsValid.ShouldBeFalse();
        res2.Errors.ShouldContain(e => e.PropertyName == "Sdl");

        var validRequest = new SchemaRegistrationRequest
        {
            ServiceName = "orders-service",
            Sdl = "type Query { orders: [Order!]! } type Order { id: ID! }"
        };
        var res3 = await validator.ValidateAsync(validRequest);
        res3.IsValid.ShouldBeTrue();
    }

    [Fact]
    public async Task SchemaRegistryService_FullLifecycle_EnforcesCompatibility()
    {
        var repo = new InMemorySchemaRegistryRepository();
        var validator = new SchemaRegistrationRequestValidator();
        var service = new SchemaRegistryService(repo, _linter, validator, NullLogger<SchemaRegistryService>.Instance);

        var v1Sdl = """
            type Query {
                users: [User!]!
            }
            type User {
                id: ID!
                name: String!
            }
            """;

        // 1. Initial Publish
        var pub1 = await service.RegisterSchemaAsync(new SchemaRegistrationRequest
        {
            ServiceName = "users-subgraph",
            Sdl = v1Sdl,
            Version = "1.0.0"
        });

        pub1.Success.ShouldBeTrue();
        pub1.Schema.ShouldNotBeNull();
        pub1.Schema.Version.ShouldBe("1.0.0");

        // 2. Compatible Update (Adding safe field)
        var v2Sdl = """
            type Query {
                users: [User!]!
            }
            type User {
                id: ID!
                name: String!
                email: String
            }
            """;

        var pub2 = await service.RegisterSchemaAsync(new SchemaRegistrationRequest
        {
            ServiceName = "users-subgraph",
            Sdl = v2Sdl,
            Version = "1.1.0"
        });

        pub2.Success.ShouldBeTrue();
        pub2.Diff.HasBreakingChanges.ShouldBeFalse();
        pub2.Diff.SafeCount.ShouldBe(1);

        // 3. Incompatible Update (Removing field)
        var v3Sdl = """
            type Query {
                users: [User!]!
            }
            type User {
                id: ID!
            }
            """;

        var pub3 = await service.RegisterSchemaAsync(new SchemaRegistrationRequest
        {
            ServiceName = "users-subgraph",
            Sdl = v3Sdl,
            Version = "2.0.0",
            ForceIfBreaking = false
        });

        pub3.Success.ShouldBeFalse();
        pub3.Diff.HasBreakingChanges.ShouldBeTrue();
        pub3.Diff.BreakingCount.ShouldBe(2); // name and email removed

        // 4. Incompatible Update with ForceIfBreaking = true
        var pub3Forced = await service.RegisterSchemaAsync(new SchemaRegistrationRequest
        {
            ServiceName = "users-subgraph",
            Sdl = v3Sdl,
            Version = "2.0.0",
            ForceIfBreaking = true
        });

        pub3Forced.Success.ShouldBeTrue();

        // 5. History check
        var history = await service.GetSchemaHistoryAsync("users-subgraph");
        history.Count.ShouldBe(3); // 1.0.0, 1.1.0, 2.0.0
    }
}
