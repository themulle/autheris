namespace Autheris.Tests.Unit.Procedures;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Procedures.Services;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Options.DataSources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

/// <summary>The directory re-scan (replaces the file watcher) cannot load an unvalidated definition.</summary>
public sealed class ProcedureRescanTests : IDisposable
{
    private const string Minimal = "name: p\nprocedure: api.usp_X\n";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "autheris-rescan-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryProcedureRegistry _registry = new();
    private readonly List<string> _logged = [];
    private readonly IOptions<GatewayOptions> _options;
    private readonly ProcedureDefinitionLoader _loader;

    public ProcedureRescanTests()
    {
        Directory.CreateDirectory(_dir);
        var sources = ProcedureTestData.SqlServerSources("procedures");
        sources.Connections["ora"] = new DataSourceConnectionOptions { Provider = "Oracle", ConnectionString = "x" };
        _options = Options.Create(new GatewayOptions
        {
            DataSources = sources,
            SqlEndpoints = new SqlEndpointsOptions
            {
                Procedures = new ProcedureEndpointsOptions
                {
                    Enabled = true, Directory = _dir, AllowedSchemas = ["api"], AllowedDataSources = ["ora"],
                    AllowDeclaredValidation = true, RequireDdlHashForDeclared = false
                }
            }
        });
        _loader = new ProcedureDefinitionLoader(_registry, _options, null, new ListLogger(_logged));
    }

    public void Dispose() => Directory.Delete(_dir, true);

    private string Write(string file, string content)
    {
        string path = Path.Combine(_dir, file);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Rescan_NewDefinition_PendingUntilValidated_SEC_PROC_08()
    {
        _loader.LoadFromDirectory().ShouldBe(0);
        Write("p.proc.yaml", Minimal);

        _loader.Rescan().ShouldBe(1);

        _registry.TryGet("p", out var registered).ShouldBeTrue();
        registered!.State.ShouldBe(ProcedureState.Pending);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Rescan_ChangedFile_ReturnsToPending_UnchangedFileKeepsState_SEC_PROC_08()
    {
        string file = Write("p.proc.yaml", Minimal);
        _loader.LoadFromDirectory();
        _registry.MarkActive("p", new ProcedureValidationResult(true, [], [], [], new Dictionary<string, string>()));

        _loader.Rescan();
        _registry.TryGet("p", out var unchanged);
        unchanged!.State.ShouldBe(ProcedureState.Active);

        File.WriteAllText(file, Minimal + "summary: changed\n");
        _loader.Rescan();
        _registry.TryGet("p", out var changed);
        changed!.State.ShouldBe(ProcedureState.Pending);
        changed.Definition.Summary.ShouldBe("changed");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Rescan_ChangedFileInvalid_EntryRemoved_SEC_PROC_08()
    {
        string file = Write("p.proc.yaml", Minimal);
        _loader.LoadFromDirectory();
        _registry.TryGet("p", out _).ShouldBeTrue();

        File.WriteAllText(file, Minimal + "bogus: 1\n");
        _loader.Rescan();

        _registry.TryGet("p", out _).ShouldBeFalse();
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("mode: write\n")]
    [InlineData("data_source: ora\n")]
    public void Rescan_ModeWriteOrOracle_NotRegistered_SEC_PROC_08(string extra)
    {
        _loader.LoadFromDirectory();
        Write("p.proc.yaml", Minimal + extra);

        _loader.Rescan().ShouldBe(0);

        _registry.TryGet("p", out _).ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Rescan_DeletedFile_EntryRemoved_SEC_PROC_08()
    {
        string file = Write("p.proc.yaml", Minimal);
        _loader.LoadFromDirectory();

        File.Delete(file);
        _loader.Rescan();

        _registry.TryGet("p", out _).ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Rescan_SymlinkOutsideDirectory_Ignored_SEC_PROC_08()
    {
        string outside = Path.Combine(Path.GetTempPath(), "autheris-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            File.WriteAllText(Path.Combine(outside, "o.proc.yaml"), "name: outside\nprocedure: api.usp_O\n");
            File.CreateSymbolicLink(Path.Combine(_dir, "link.proc.yaml"), Path.Combine(outside, "o.proc.yaml"));
            Directory.CreateSymbolicLink(Path.Combine(_dir, "dir"), outside);

            _loader.Rescan().ShouldBe(0);

            _registry.TryGet("outside", out _).ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(outside, true);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Rescan_ProcSqlAppearsAtRuntime_RejectedAndLogged_SEC_PROC_08()
    {
        Write("ok.proc.yaml", Minimal);
        _loader.LoadFromDirectory().ShouldBe(1);
        Write("late.proc.sql", "-- @procedure api.usp_Late");

        Should.NotThrow(() => _loader.Rescan());

        _logged.ShouldContain(m => m.Contains("*.proc.sql", StringComparison.Ordinal));
        _registry.GetAll().Count.ShouldBe(1);
    }

    [Fact]
    public async Task Revalidation_RescansDirectory_PicksUpNewAndRemovedFiles()
    {
        string declared = "name: d\nprocedure: api.usp_D\nvalidation: declared\nresult_table: sales.orders\noutputs:\n  - order_id\n";
        string file = Write("d.proc.yaml", declared);
        _loader.LoadFromDirectory().ShouldBe(1);

        await NewService().ValidateDueAsync(CancellationToken.None);
        _registry.TryGet("d", out var first);
        first!.State.ShouldBe(ProcedureState.Active);

        File.Delete(file);
        Write("e.proc.yaml", declared.Replace("name: d", "name: e", StringComparison.Ordinal));
        await NewService().ValidateDueAsync(CancellationToken.None);

        _registry.TryGet("d", out _).ShouldBeFalse();
        _registry.TryGet("e", out var added).ShouldBeTrue();
        added!.State.ShouldBe(ProcedureState.Active);
    }

    private ProcedureRegistrationService NewService()
    {
        var factory = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return new ProcedureRegistrationService(_loader, _registry, factory, _options);
    }

    private sealed class ListLogger(List<string> sink) : ILogger<ProcedureDefinitionLoader>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            sink.Add(formatter(state, exception));
    }
}
