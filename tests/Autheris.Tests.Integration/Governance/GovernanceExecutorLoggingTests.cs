using System;
using System.Collections.Generic;
using System.Data.Common;
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
public sealed class GovernanceExecutorLoggingTests
{
    private sealed class CaptureLogger<T> : ILogger<T>
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
    public async Task Executor_logs_fragment_id_and_duration_only_never_parameter_values()
    {
        var logger = new CaptureLogger<GovernanceRepository>();
        var dialect = Substitute.For<IGovernanceSqlDialect>();
        var repo = new GovernanceRepository(dialect, logger: logger);

        var secretTenantValue = "tenant-super-secret-12345";
        var secretReason = "secret-hr-reason-confidential";

        var command = Substitute.For<DbCommand>();
        var parameters = Substitute.For<DbParameterCollection>();
        command.Parameters.Returns(parameters);

        var param1 = Substitute.For<DbParameter>();
        param1.ParameterName = "@tenant";
        param1.Value = secretTenantValue;

        var param2 = Substitute.For<DbParameter>();
        param2.ParameterName = "@reason";
        param2.Value = secretReason;

        command.CommandText = "SELECT COUNT(*) FROM CONSENTS WHERE tenant_id = @tenant AND reason = @reason";
        command.ExecuteScalarAsync(Arg.Any<System.Threading.CancellationToken>()).Returns(Task.FromResult<object?>(1L));
        command.ExecuteNonQueryAsync(Arg.Any<System.Threading.CancellationToken>()).Returns(Task.FromResult(1));

        await repo.ExecuteScalarAsync(command, "Consent.CountSecret", default);
        await repo.ExecuteNonQueryAsync(command, "Consent.UpdateSecret", default);

        logger.Messages.Count.ShouldBe(2);

        foreach (var msg in logger.Messages)
        {
            // Must contain fragment ID and duration
            msg.ShouldContain("Executed Consent.");
            msg.ShouldContain("ms");

            // SEC-SIMPL-25: NEVER leak parameter values or full SQL queries in the executor log
            msg.ShouldNotContain(secretTenantValue);
            msg.ShouldNotContain(secretReason);
            msg.ShouldNotContain("SELECT COUNT(*)");
        }
    }

    [Fact]
    public async Task Executor_logs_failure_with_fragment_id_and_duration_without_parameters()
    {
        var logger = new CaptureLogger<GovernanceRepository>();
        var dialect = Substitute.For<IGovernanceSqlDialect>();
        var repo = new GovernanceRepository(dialect, logger: logger);

        var secretValue = "secret-injection-attempt-value";
        var command = Substitute.For<DbCommand>();
        command.CommandText = "SELECT * FROM TABLES WHERE table_name = @secret";
        command.ExecuteScalarAsync(Arg.Any<System.Threading.CancellationToken>())
            .Returns(Task.FromException<object?>(new InvalidOperationException("Db failure")));

        await Should.ThrowAsync<InvalidOperationException>(() =>
            repo.ExecuteScalarAsync(command, "Consent.FailingFragment", default));

        logger.Messages.ShouldHaveSingleItem();
        var log = logger.Messages[0];
        log.ShouldContain("Execution failed for Consent.FailingFragment");
        log.ShouldContain("ms");
        log.ShouldNotContain(secretValue);
        log.ShouldNotContain("SELECT * FROM TABLES");
    }
}
