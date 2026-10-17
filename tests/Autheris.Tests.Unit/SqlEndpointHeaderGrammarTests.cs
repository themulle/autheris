using Autheris.Application.SqlEndpoints.Services;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit;

/// <summary>REQ-SQL-128 WP-5: strict grammar of the colon-form <c>-- @param name: type[][!] [= default]</c> header (A-8, SEC-128-14).</summary>
public class SqlEndpointHeaderGrammarTests
{
    private static SqlEndpointDefinition Parse(string header, string sql = "SELECT id FROM t WHERE id IN (@ids)")
    {
        using var loader = new SqlEndpointLoader(new InMemorySqlEndpointRegistry());
        return loader.ParseSqlContent($"-- @name q\n{header}\n{sql}", "fallback");
    }

    private static SqlEndpointParameter Single(string header, string sql = "SELECT id FROM t WHERE id IN (@ids)") =>
        Parse(header, sql).Parameters.Single(p => p.Name == "ids");

    [Fact]
    public void Header_ListType_ParsedWithJsonDefault()
    {
        var p = Single("-- @param ids: string[] = [\"a\", \"b\"]");
        p.IsList.ShouldBeTrue();
        p.ClrType.ShouldBe(typeof(string));
        p.IsRequired.ShouldBeFalse();
        ((IReadOnlyList<object?>)p.DefaultValue!).ShouldBe(new object?[] { "a", "b" });
    }

    [Fact]
    public void Header_ListWithoutDefault_AndRequiredMarker()
    {
        var plain = Single("-- @param ids: int[]");
        plain.IsList.ShouldBeTrue();
        plain.ClrType.ShouldBe(typeof(int));
        plain.DefaultValue.ShouldBeNull();

        Single("-- @param ids: int[]!").IsRequired.ShouldBeTrue();
    }

    [Fact]
    public void Header_ListEmptyDefault_IsAnEmptyList()
    {
        var p = Single("-- @param ids: long[] = []");
        ((IReadOnlyList<object?>)p.DefaultValue!).ShouldBeEmpty();
    }

    [Fact]
    public void Header_ListDefault_IsTypedByTheDeclaredElementType()
    {
        ((IReadOnlyList<object?>)Single("-- @param ids: int[] = [3, 1, 3]").DefaultValue!).ShouldBe(new object?[] { 3, 1 });
        ((IReadOnlyList<object?>)Single("-- @param ids: guid[] = [\"0F8FAD5B-D9CB-469F-A165-70867728950E\"]").DefaultValue!)
            .ShouldBe(new object?[] { "0f8fad5b-d9cb-469f-a165-70867728950e" });
    }

    [Theory]
    [InlineData("-- @param ids: int[] = [1, \"a\"]")]            // element of the wrong type
    [InlineData("-- @param ids: int[] = [[1]]")]                 // nested
    [InlineData("-- @param ids: int[] = [1, null]")]             // null
    [InlineData("-- @param ids: int[] = 5")]                     // scalar default for a list
    [InlineData("-- @param ids: int[] = [1, 2")]                 // broken JSON
    [InlineData("-- @param ids: int[] = {\"a\":1}")]
    [Trait("Category", "Security")]
    public void Header_InvalidListDefault_RefusesFile(string header)
    {
        Should.Throw<FormatException>(() => Parse(header));
    }

    [Theory]
    [InlineData("-- @param ids: geography")]
    [InlineData("-- @param ids: int[")]
    [InlineData("-- @param ids: int]")]
    [InlineData("-- @param ids: int[][]")]
    [InlineData("-- @param ids: int [] ")]
    [InlineData("-- @param ids: varchar(50)")]
    [InlineData("-- @param ids: nvarchar(max)")]
    [InlineData("-- @param ids: ")]
    [InlineData("-- @param ids: filter by status")]
    [InlineData("-- @param ids: [] int")]
    [Trait("Category", "Security")]
    public void Header_UnknownType_MalformedBrackets_SizeSuffix_RefusesFile(string header)
    {
        Should.Throw<FormatException>(() => Parse(header));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Header_ColonDescriptionLine_RefusesFile_NoValueInDiagnostic()
    {
        var ex = Should.Throw<FormatException>(() => Parse("-- @param ids: super secret description text"));
        ex.Message.ShouldNotContain("secret");
        ex.Message.ShouldNotContain("description");
        ex.Message.ShouldContain("ids");           // the parameter name (regex-limited) and the file name are named
        ex.Message.ShouldContain("fallback");
    }

    [Theory]
    [InlineData("-- @param ids Department code, for example HR")]
    [InlineData("-- @param department Department code used to filter")]
    [InlineData("-- @param status filter by status: open or closed")]
    [InlineData("-- @param")]
    [Trait("Category", "Security")]
    public void Header_DescriptionOnlyForm_StaysIgnored(string header)
    {
        // The f-sql-01 example: no colon after the first token. The parameter keeps its string default type.
        var parsed = Parse(header, "SELECT id FROM t WHERE id = @ids");
        var p = parsed.Parameters.Single(x => x.Name == "ids");
        p.ClrType.ShouldBe(typeof(string));
        p.IsList.ShouldBeFalse();
    }

    [Theory]
    [InlineData("int", typeof(int))]
    [InlineData("integer", typeof(int))]
    [InlineData("int32", typeof(int))]
    [InlineData("smallint", typeof(int))]
    [InlineData("tinyint", typeof(int))]
    [InlineData("long", typeof(long))]
    [InlineData("bigint", typeof(long))]
    [InlineData("int64", typeof(long))]
    [InlineData("decimal", typeof(decimal))]
    [InlineData("numeric", typeof(decimal))]
    [InlineData("money", typeof(decimal))]
    [InlineData("float", typeof(double))]
    [InlineData("double", typeof(double))]
    [InlineData("real", typeof(double))]
    [InlineData("bool", typeof(bool))]
    [InlineData("boolean", typeof(bool))]
    [InlineData("bit", typeof(bool))]
    [InlineData("date", typeof(DateTimeOffset))]
    [InlineData("datetime", typeof(DateTimeOffset))]
    [InlineData("datetime2", typeof(DateTimeOffset))]
    [InlineData("smalldatetime", typeof(DateTimeOffset))]
    [InlineData("datetimeoffset", typeof(DateTimeOffset))]
    [InlineData("timestamp", typeof(DateTimeOffset))]
    [InlineData("guid", typeof(Guid))]
    [InlineData("uuid", typeof(Guid))]
    [InlineData("uniqueidentifier", typeof(Guid))]
    [InlineData("string", typeof(string))]
    [InlineData("text", typeof(string))]
    [InlineData("varchar", typeof(string))]
    [InlineData("nvarchar", typeof(string))]
    [InlineData("char", typeof(string))]
    [InlineData("nchar", typeof(string))]
    [InlineData("INT", typeof(int))]
    [InlineData("UniqueIdentifier", typeof(Guid))]
    public void Header_AliasList_MapsToExpectedClrTypes(string alias, Type expected)
    {
        Single($"-- @param ids: {alias}", "SELECT id FROM t WHERE id = @ids").ClrType.ShouldBe(expected);
        Single($"-- @param ids: {alias}[]").ClrType.ShouldBe(expected);
    }

    [Fact]
    public void Header_ExistingScalarDeclarations_AreUnchanged()
    {
        var parsed = Parse("-- @param offset: int = 0\n-- @param limit: int = 50\n-- @param ids: string!", "SELECT id FROM t WHERE id IN (@ids) ORDER BY id OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY");
        parsed.Parameters.Single(p => p.Name == "limit").DefaultValue.ShouldBe(50);
        parsed.Parameters.Single(p => p.Name == "limit").IsList.ShouldBeFalse();
        parsed.Parameters.Single(p => p.Name == "ids").IsRequired.ShouldBeTrue();
    }

    [Fact]
    public void Header_ListParameterInTheSqlWithoutDeclaration_IsAStringScalar()
    {
        var p = Parse("-- @name q", "SELECT id FROM t WHERE id IN (@ids)").Parameters.Single();
        p.IsList.ShouldBeFalse();
    }

    [Fact]
    public void Header_SyncDbtModel_WritesTheListSuffix()
    {
        string dir = Path.Combine(Path.GetTempPath(), "list-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var loader = new SqlEndpointLoader(new InMemorySqlEndpointRegistry());
            string file = loader.SyncDbtModelToFile(dir, "m", "SELECT id FROM t WHERE id IN (@ids)",
                parameters: [new SqlEndpointParameter("ids", "@ids", typeof(int), IsRequired: true, IsList: true)]);
            File.ReadAllText(file).ShouldContain("-- @param ids: int[]!");
            loader.ParseSqlContent(File.ReadAllText(file), "m").Parameters.Single(p => p.Name == "ids").IsList.ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
