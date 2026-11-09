using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Dialects;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Governance;

[Trait("Category", "Security")]
public sealed class GovernanceCoreSecurityTests
{
    private sealed class TestCaptureLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Messages)
            {
                Messages.Add(formatter(state, exception));
            }
        }
    }

    [Fact]
    public void Missing_tenant_id_throws_ArgumentException()
    {
        Should.Throw<ArgumentException>(() => TenantScope.RequireTenant((TenantId?)null, nameof(GovernanceTable.Consents)))
            .Message.ShouldContain("TenantId is required");

        Should.Throw<ArgumentException>(() => TenantScope.RequireTenant(default(TenantId), nameof(GovernanceTable.Consents)))
            .Message.ShouldContain("TenantId is required");

        Should.Throw<ArgumentException>(() => TenantScope.RequireTenantString(null, nameof(GovernanceTable.Consents)))
            .Message.ShouldContain("TenantId is required");

        Should.Throw<ArgumentException>(() => TenantScope.RequireTenantString("", nameof(GovernanceTable.Consents)))
            .Message.ShouldContain("TenantId is required");
    }

    [Theory]
    [InlineData("'; DROP TABLE CONSENTS; --")]
    [InlineData("1 OR 1=1")]
    [InlineData("\" UNION SELECT * FROM SENSITIVE_TABLE --")]
    public void Injection_payloads_in_table_name_are_validated_or_fail_closed(string injectionPayload)
    {
        // Global table check fails closed
        GovernanceGlobalTables.IsGlobal(injectionPayload).ShouldBeFalse();

        // Enforcing tenant scope for injection payload table throws if tenant missing
        Should.Throw<ArgumentException>(() => TenantScope.RequireTenant((TenantId?)null, injectionPayload));
    }

    [Fact]
    public async Task Logging_never_records_parameters_or_inline_sql_queries()
    {
        var logger = new TestCaptureLogger<GovernanceRepository>();
        var dialect = Substitute.For<IGovernanceSqlDialect>();
        var repo = new GovernanceRepository(dialect, logger: logger);

        var secretTenant = "tenant-secret-value-never-log";
        var secretSubject = "S-1-5-21-999999-999999-999999-secret";

        var cmd = Substitute.For<DbCommand>();
        var parameters = Substitute.For<DbParameterCollection>();
        cmd.Parameters.Returns(parameters);

        cmd.CommandText = "SELECT * FROM CONSENTS WHERE tenant_id = @tenant AND grantee_sid = @sid";
        cmd.ExecuteScalarAsync(Arg.Any<System.Threading.CancellationToken>()).Returns(Task.FromResult<object?>(42L));

        await repo.ExecuteScalarAsync(cmd, "Consent.FindActive", default);

        logger.Messages.ShouldHaveSingleItem();
        var entry = logger.Messages[0];

        // Logs only fragment id + duration
        entry.ShouldContain("Executed Consent.FindActive in");
        entry.ShouldContain("ms");

        // Never logs parameters or inlined SQL (SEC-SIMPL-25)
        entry.ShouldNotContain(secretTenant);
        entry.ShouldNotContain(secretSubject);
        entry.ShouldNotContain("SELECT * FROM CONSENTS");
    }

    [Fact]
    public void Catalog_statements_touch_only_global_tables()
    {
        // SEC-SIMPL-22: the moved catalog statements carry no tenant predicate, so every table they name must be a table without a tenant_id column.
        var sql = typeof(GovernanceRepository).GetNestedType("CatalogSql", BindingFlags.NonPublic)!;
        var statements = sql.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();
        statements.ShouldNotBeEmpty();

        var tables = statements
            .SelectMany(s => Regex.Matches(s, @"\b(?:FROM|JOIN|INTO|UPDATE|MERGE(?:\s+INTO)?)\s+(?:dbo\.)?([A-Za-z_]+)", RegexOptions.IgnoreCase).Select(m => m.Groups[1].Value.ToUpperInvariant()))
            .Where(n => n is not ("SELECT" or "SET" or "EXCLUDED" or "TABLE"))
            .Distinct()
            .ToList();

        tables.ShouldNotBeEmpty();
        tables.Where(n => !GovernanceGlobalTables.IsGlobal(n)).ShouldBeEmpty();
    }
}
