namespace Autheris.Tests.Unit.Security;

using System;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>PLAN-AST-DIALECT-GEN-16 WP-D4: no fail-open dialect defaults (F-1, SEC-ADG-14).</summary>
public sealed class FailClosedDialectDefaultsTests
{
    private static readonly TenantId Tenant = TenantId.LegacySingleTenant;

    [Theory]
    [InlineData("mysql")]
    [InlineData("nonsense")]
    [InlineData("99")]
    public void DbSessionContextInitializer_UnknownProvider_Throws(string provider)
    {
        Should.Throw<NotSupportedException>(() => DbSessionContextInitializer.ResolveDialect(provider));
    }

    [Fact]
    public void DbSessionContextInitializer_EmptyProvider_IsSqliteOptionDefault()
    {
        DbSessionContextInitializer.ResolveDialect(null).ShouldBe(DatabaseDialect.Sqlite);
    }

    [Theory]
    [InlineData((DatabaseDialect)0)]
    [InlineData((DatabaseDialect)99)]
    [InlineData(DatabaseDialect.Oracle)] // no session initialization until WP-F2: fail closed
    public async Task DbSessionContextInitializer_UnknownDialect_Throws(DatabaseDialect dialect)
    {
        var sut = new DbSessionContextInitializer();
        var connection = Substitute.For<DbConnection>();
        await Should.ThrowAsync<NotSupportedException>(() =>
            sut.InitializeSessionAsync(connection, dialect, Tenant, requireTransaction: false));
        await Should.ThrowAsync<NotSupportedException>(() =>
            sut.InitializeSessionAsync(connection, null, dialect, Tenant));
    }

    [Theory]
    [InlineData(DatabaseDialect.Sqlite)]
    [InlineData(DatabaseDialect.Databricks)]
    public async Task DbSessionContextInitializer_ExplicitNoSessionStateDialects_ReturnNull(DatabaseDialect dialect)
    {
        var sut = new DbSessionContextInitializer();
        var connection = Substitute.For<DbConnection>();
        (await sut.InitializeSessionAsync(connection, dialect, Tenant, requireTransaction: true)).ShouldBeNull();
        await sut.InitializeSessionAsync(connection, null, dialect, Tenant);
    }

    [Theory]
    [InlineData(DatabaseDialect.PostgreSql)]
    [InlineData(DatabaseDialect.SqlServer)]
    [InlineData(DatabaseDialect.Sqlite)]
    [InlineData(DatabaseDialect.Oracle)]
    public void MaskDialectMapping_MappedDialects_DoNotThrow(DatabaseDialect dialect)
    {
        Should.NotThrow(() => SqlDataSourceExecutor.ToMaskTargetDialect(dialect));
    }

    [Theory]
    [InlineData(DatabaseDialect.Databricks)]
    [InlineData((DatabaseDialect)0)]
    [InlineData((DatabaseDialect)99)]
    public void MaskDialectMapping_UnmappedDialect_Throws(DatabaseDialect dialect)
    {
        Should.Throw<NotSupportedException>(() => SqlDataSourceExecutor.ToMaskTargetDialect(dialect));
    }

    [Fact]
    public void NoDialectDefaultFallback_InSrc()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln")))
        {
            dir = dir.Parent;
        }

        dir.ShouldNotBeNull();
        var rx = new Regex(@"_\s*=>\s*(?:[A-Za-z.]*\.)?(DatabaseDialect|TargetSqlDialect)\.\w+", RegexOptions.Compiled);
        var offenders = Directory.EnumerateFiles(Path.Combine(dir!.FullName, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .SelectMany(f => File.ReadLines(f).Select((l, i) => (f, i, l)))
            .Where(x => rx.IsMatch(x.l))
            .Select(x => $"{x.f}:{x.i + 1}")
            .ToList();
        offenders.ShouldBeEmpty();
    }
}
