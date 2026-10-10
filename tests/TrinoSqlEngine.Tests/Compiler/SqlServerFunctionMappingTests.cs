using Xunit;
using static TrinoSqlEngine.Tests.Compiler.CompilerTestHelpers;

namespace TrinoSqlEngine.Tests.Compiler;

public class SqlServerFunctionMappingTests
{
    [Fact]
    public void Length_MapsToLen() => Assert.Contains("LEN([a])", GenerateSqlServer("SELECT length(a) FROM t").Sql);

    [Fact]
    public void CharLength_MapsToLen() => Assert.Contains("LEN([a])", GenerateSqlServer("SELECT char_length(a) FROM t").Sql);

    [Fact]
    public void Ceil_MapsToCeiling() => Assert.Contains("CEILING([a])", GenerateSqlServer("SELECT ceil(a) FROM t").Sql);

    [Fact]
    public void Strpos_MapsToCharindex_WithSwappedArguments() =>
        Assert.Contains("CHARINDEX(@p0, [a])", GenerateSqlServer("SELECT strpos(a, 'x') FROM t").Sql);

    [Fact]
    public void UnmappedFunctions_PassThroughUnchanged() => Assert.Contains("UPPER([a])", GenerateSqlServer("SELECT upper(a) FROM t").Sql);
}
