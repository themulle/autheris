---
name: csharp-code-reviewer
description: >-
  Code quality assurance and PR reviews for C#/.NET 10. Inspects modern idioms (Pattern Matching, Nullable
  Types, Primary Constructors), detects code smells, resource leaks, stack-trace preservation, and verifies tests.
---

# C# & .NET Code Reviewer

Guides structured, constructive, and precise code reviews for PRs, refactorings, and features in C#/.NET 10.

---

## 1. Modern C# Idioms & Best Practices

- **Nullable Reference Types & Guards:**
  - Avoid reckless null-forgiving operators (`!`); use only when null-safety is guaranteed by framework/assertion.
  - Throw early with standard helpers:
    ```csharp
    ArgumentNullException.ThrowIfNull(service);
    ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
    ```
- **Pattern Matching & Switch Expressions:**
  - Prefer `switch` expressions over nested `if/else`:
    ```csharp
    public decimal CalculateDiscount(Order order) => order switch {
        { Customer.IsVip: true, Total: > 1000m } => 0.20m,
        { Total: > 500m }                        => 0.10m,
        _                                        => 0.0m
    };
    ```
- **Collection Expressions & Primary Constructors:**
  - Use `[...]`: `int[] numbers = [1, 2, 3];` instead of `new int[] { 1, 2, 3 };`.
  - Primary constructors for DI:
    ```csharp
    public sealed class InvoiceService(IInvoiceRepository repo, ILogger<InvoiceService> logger) : IInvoiceService
    ```
  - Records for immutable DTOs and event models.

---

## 2. Code Smells & Hygiene

- **Exception Handling:**
  - ❌ **Never destroy stack traces:**
    ```csharp
    // BAD: Resets stack trace!
    catch (Exception ex) { logger.LogError(ex, "Error"); throw ex; }

    // GOOD: Preserves original stack trace
    catch (Exception ex) { logger.LogError(ex, "Error"); throw; }
    ```
  - ❌ No empty `catch { }` blocks without explicit rationale/logging.
  - Use exception filters: `catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)`.
- **Resource Management:**
  - Use `using var` for `IDisposable`.
  - Use `await using var` for `IAsyncDisposable` (streams, DbContexts, connections).

---

## 3. Testability & Test Quality

- **AAA Pattern:** Arrange, Act, Assert cleanly separated.
- **Naming Standard:** `MethodUnderTest_Condition_ExpectedBehavior` (e.g. `AuthenticateAsync_WhenTokenExpired_ReturnsFailure`).
- **Assertions:** Use `FluentAssertions` / `Shouldly` (`result.Succeeded.Should().BeTrue()`).
- **Mocking (NSubstitute):** Mock only external interfaces, never data records or value objects. Ensure `using NSubstitute;`.
- **Integration Tests (Testcontainers):** Use real containers for databases/queues instead of in-memory approximations:
  ```csharp
  public sealed class PostgresFixture : IAsyncLifetime {
      private readonly PostgreSqlContainer _container = new PostgreSqlBuilder().Build();
      public string ConnectionString => _container.GetConnectionString();
      public Task InitializeAsync() => _container.StartAsync();
      public Task DisposeAsync() => _container.DisposeAsync().AsTask();
  }
  ```

---

## 4. PR Review Template

1. 🔴 **Blocker (Must Fix):** Bugs, security flaws, race conditions, memory leaks, stack trace loss.
2. 🟡 **Recommendation:** Performance optimizations, idiomatic C# enhancements, missing test coverage.
3. 💡 **Nitpick:** Cosmetic styling, non-blocking naming tweaks.
4. 🌟 **Praise:** Clean design patterns and elegant solutions.
