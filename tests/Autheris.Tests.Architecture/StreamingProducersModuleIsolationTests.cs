namespace Autheris.Tests.Architecture;

using System;
using System.IO;
using System.Linq;
using Autheris.Infrastructure.Streaming.Producers;
using Shouldly;
using Xunit;

/// <summary>
/// The CDC producers open data-source connections and therefore live in core Infrastructure, never in a module.
/// </summary>
public sealed class StreamingProducersModuleIsolationTests
{
    [Fact]
    [Trait("Category", "Security")]
    public void Producers_namespace_has_no_module_reference()
    {
        var assembly = typeof(CdcProducerService).Assembly;

        var producerTypes = assembly.GetTypes()
            .Where(t => t.Namespace is not null && t.Namespace.StartsWith("Autheris.Infrastructure.Streaming.Producers", StringComparison.Ordinal))
            .ToList();
        producerTypes.ShouldNotBeEmpty();

        assembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ShouldNotContain(n => n.StartsWith("Autheris.Modules.", StringComparison.Ordinal)
                && !n.Equals("Autheris.Modules.Abstractions", StringComparison.Ordinal));

        var dir = Path.Combine(RepositoryScan.FindRepositoryRoot(), "src", "Autheris.Infrastructure", "Streaming", "Producers");
        Directory.Exists(dir).ShouldBeTrue();
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            File.ReadAllText(file).ShouldNotContain("Autheris.Modules.", customMessage: file);
        }
    }
}
