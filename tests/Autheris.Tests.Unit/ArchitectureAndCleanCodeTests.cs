namespace Autheris.Tests.Unit;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Procedures.Services;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public class ArchitectureAndCleanCodeTests
{
    [Fact]
    public void DefaultSqlSecurityValidator_ValidPredicate_Passes()
    {
        ISqlSecurityValidator validator = new DefaultSqlSecurityValidator();
        Should.NotThrow(() => validator.ValidatePredicateSql("status = 'ACTIVE' AND age >= 18", "testField"));
    }

    [Theory]
    [InlineData("1=1; DROP TABLE users;")]
    [InlineData("status = 'ACTIVE' -- comment")]
    [InlineData("status = 'ACTIVE' /* inline comment */")]
    [InlineData("status = @@version")]
    [InlineData("status = 'ACTIVE\0'")]
    public void DefaultSqlSecurityValidator_UnsafePredicate_ThrowsArgumentException(string unsafePredicate)
    {
        ISqlSecurityValidator validator = new DefaultSqlSecurityValidator();
        Should.Throw<ArgumentException>(() => validator.ValidatePredicateSql(unsafePredicate, "testField"));
    }

    [Theory]
    [InlineData("created_at DESC")]
    [InlineData("customer_id ASC, order_date DESC")]
    [InlineData("users.first_name ASC")]
    public void DefaultSqlSecurityValidator_ValidOrderBy_Passes(string validOrderBy)
    {
        ISqlSecurityValidator validator = new DefaultSqlSecurityValidator();
        Should.NotThrow(() => validator.ValidateOrderBySql(validOrderBy, "orderByField"));
    }

    [Theory]
    [InlineData("created_at; DROP TABLE users")]
    [InlineData("created_at -- comment")]
    [InlineData("created_at /* block */ DESC")]
    [InlineData("(SELECT password FROM users) ASC")]
    public void DefaultSqlSecurityValidator_UnsafeOrderBy_ThrowsArgumentException(string unsafeOrderBy)
    {
        ISqlSecurityValidator validator = new DefaultSqlSecurityValidator();
        Should.Throw<ArgumentException>(() => validator.ValidateOrderBySql(unsafeOrderBy, "orderByField"));
    }

    [Theory]
    [InlineData("p0")]
    [InlineData("@tenantId")]
    [InlineData("user_id_123")]
    public void DefaultSqlSecurityValidator_ValidParameterName_Passes(string paramName)
    {
        ISqlSecurityValidator validator = new DefaultSqlSecurityValidator();
        Should.NotThrow(() => validator.ValidateParameterName(paramName, "paramField"));
    }

    [Theory]
    [InlineData("@param;DROP TABLE")]
    [InlineData("param--comment")]
    [InlineData("123badStart")]
    [InlineData("@param with spaces")]
    public void DefaultSqlSecurityValidator_InvalidParameterName_ThrowsArgumentException(string invalidParam)
    {
        ISqlSecurityValidator validator = new DefaultSqlSecurityValidator();
        Should.Throw<ArgumentException>(() => validator.ValidateParameterName(invalidParam, "paramField"));
    }

    [Fact]
    public void DiContainer_ResolvesISqlSecurityValidator()
    {
        var services = new ServiceCollection();
        var options = new GatewayOptions
        {
            SqlEndpoints = new SqlEndpointsOptions
            {
                Procedures = new ProcedureEndpointsOptions
                {
                    Enabled = true,
                    AllowedSchemas = ["api"]
                }
            }
        };

        services.AddSingleton(Options.Create(options));
        services.AddLogging();
        services.AddSingleton<TrinoSqlEngine.ISqlEngine, TrinoSqlEngine.FastSqlEngine>();
        services.AddSingleton<ISqlSecurityValidator, DefaultSqlSecurityValidator>();

        var provider = services.BuildServiceProvider();

        provider.GetService<ISqlSecurityValidator>().ShouldNotBeNull();
    }

    private static string RepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Autheris.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Autheris.sln not found.");
    }

    private static IEnumerable<string> SourceFiles(string root)
    {
        string sep = Path.DirectorySeparatorChar.ToString();
        return Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{sep}obj{sep}") && !f.Contains($"{sep}bin{sep}"));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void DmlWritePermit_IsConstructedOnlyInTheEngine_UntilTheDecisionPointIsWired()
    {
        // SEC-SIMPL-120 / SEC-CIFIX-1: nothing outside src/TrinoSqlEngine may construct a write permit, so production derives none
        // from roles and every DML statement stays denied. WP-SIMPL-45 PR 4 adds the decision-point mapping as the only allowed entry.
        string root = RepoRoot();
        string engineDir = Path.Combine(root, "src", "TrinoSqlEngine") + Path.DirectorySeparatorChar;
        var allowList = Array.Empty<string>();
        // 'new DmlWritePermit(', object-initializer and target-typed forms ('Write = new(...)', 'DmlWritePermit? x = new(...)').
        var forms = new Regex(@"\bnew\s+DmlWritePermit\b|\bDmlWritePermit\b|\bWrite\s*=\s*new\s*[(\{]|\bWritePermit\s*=", RegexOptions.CultureInvariant);

        var offenders = SourceFiles(root)
            .Where(f => !f.StartsWith(engineDir, StringComparison.Ordinal))
            .Where(f => forms.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Where(f => !allowList.Contains(f))
            .ToList();

        offenders.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void TestWritePermitSqlEngine_IsReferencedOnlyByTests()
    {
        // SEC-CIFIX-1 / SEC-CIFIX-2: the permit-granting decorator and helpers exist only in the test assembly.
        string root = RepoRoot();

        var offenders = SourceFiles(root)
            .Where(f => File.ReadAllText(f).Contains("TestWritePermit", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .ToList();

        offenders.ShouldBeEmpty();
    }
}
