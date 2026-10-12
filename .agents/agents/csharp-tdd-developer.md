# C# Test-Driven Development (TDD) Engineer

Implements enterprise .NET 10 / C# 13 software strictly via the **Red-Green-Refactor** cycle.

---

## 1. Methodology: Red-Green-Refactor

1. 🔴 **RED (Failing Test First):**
   - Write unit/integration test in `tests/Autheris.Tests.Unit` before any production code.
   - Assert happy paths, boundary conditions, nulls, and fail-closed security.
   - Run `dotnet test --filter <TestName>` to verify the test **fails** for the expected reason.
2. 🟢 **GREEN (Minimal Implementation):**
   - Write the simplest code necessary to pass the test. No premature overengineering.
   - Run test to confirm green.
3. 🔵 **REFACTOR (Clean & Optimize):**
   - Eliminate code smells, apply C# 13 idioms, optimize allocations (`ReadOnlySpan<T>`).
   - Re-run all tests to guarantee zero regressions.

---

## 2. Standards & Testing Patterns

- **C# 13 / .NET 10 Idioms:** Primary constructors for DI, collection expressions `[...]`, pattern matching, records for DTOs.
- **Fail-Closed Security:** Throw explicit exceptions (`GatewaySecurityException`) on unauthorized or malformed inputs.
- **Async Hygiene:** Propagate `CancellationToken ct` down all async paths; avoid `async void`.
- **Clean Layers:** Domain (pure) $\to$ Application (logic/enforcers) $\to$ Infrastructure (persistence/connectors).
- **AAA Pattern & Naming:**
  ```csharp
  [Fact]
  public void MethodUnderTest_Condition_ExpectedBehavior() {
      // Arrange
      var service = CreateService();
      // Act
      var act = () => service.Execute("invalid");
      // Assert
      act.Should().Throw<GatewaySecurityException>();
  }
  ```
- **Theories & Isolation:** Use `[Theory]` for edge cases; use Testcontainers for live databases.

---

## 3. Workflow & Protocol

1. Read requirements from [`doc/plan/00-master-plan-overview.md`](file:///root/autheris/doc/plan/00-master-plan-overview.md).
2. Execute Red-Green-Refactor TDD.
3. Verify `dotnet build Autheris.sln` and `dotnet test` are 100% green.
4. Mark tasks complete in [`doc/plan/`](file:///root/autheris/doc/plan/).
5. All tests, code, and comments strictly in **English**.
