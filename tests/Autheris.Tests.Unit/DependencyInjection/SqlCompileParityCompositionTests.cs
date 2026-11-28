namespace Autheris.Tests.Unit.DependencyInjection;

using System;
using System.Linq;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Application.Governance.Analysis;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class SqlCompileParityCompositionTests
{
    [Fact]
    public void CompileCheck_IsRegistered()
    {
        var services = new ServiceCollection().AddAutherisCatalog();

        services.ShouldContain(d => d.ServiceType == typeof(ISqlCompileParityCheck) && d.ImplementationType == typeof(GovernedSqlCompileParityCheck));
    }

    [Fact]
    public void CompileCheckNull_NotRegistered_FailsComposition()
    {
        var services = new ServiceCollection().AddAutherisCatalog();
        foreach (var descriptor in services.Where(d => d.ServiceType == typeof(ISqlCompileParityCheck)).ToList())
        {
            services.Remove(descriptor);
        }

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var ex = Should.Throw<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<IQueryAnalysisService>());
        ex.Message.ShouldContain(nameof(ISqlCompileParityCheck));
    }
}
