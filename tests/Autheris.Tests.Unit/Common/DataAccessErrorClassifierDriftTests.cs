using System.Data.Common;
using Autheris.Application.Common;
using Autheris.Application.Sql.Errors;
using Shouldly;

namespace Oracle.Fake
{
    /// <summary>Shape of OracleException: an int Number property, in a type whose full name starts with "Oracle.".</summary>
    internal sealed class FakeOracleException(int number) : DbException("ORA error")
    {
        public int Number { get; } = number;
    }
}

namespace Autheris.Tests.Unit.Common
{
    public sealed class DataAccessErrorClassifierDriftTests
    {
        private sealed class FakeSqlException(int number) : DbException("error")
        {
            public int Number { get; } = number;
        }

        private sealed class FakePostgresException(string sqlState) : DbException("error")
        {
            public override string? SqlState { get; } = sqlState;
        }

        private sealed class FakeDatabricksException(string errorClass) : Exception("The Databricks statement failed.")
        {
            public string ErrorClass { get; } = errorClass;
        }

        [Theory]
        [InlineData(207)]
        [InlineData(208)]
        [InlineData(2812)]
        [InlineData(201)]
        [InlineData(8144)]
        public void Classifies_SchemaMismatch_SqlServer(int number) =>
            DataAccessErrorClassifier.Classify(new FakeSqlException(number)).ShouldBe(DataAccessErrorKind.SourceSchemaMismatch);

        [Theory]
        [InlineData("42P01")]
        [InlineData("42703")]
        [InlineData("42883")]
        [InlineData("42804")]
        public void Classifies_SchemaMismatch_PostgreSql(string state) =>
            DataAccessErrorClassifier.Classify(new FakePostgresException(state)).ShouldBe(DataAccessErrorKind.SourceSchemaMismatch);

        [Theory]
        [InlineData(942)]
        [InlineData(904)]
        [InlineData(6550)]
        [InlineData(6553)]
        public void Classifies_SchemaMismatch_Oracle(int number) =>
            DataAccessErrorClassifier.Classify(new Oracle.Fake.FakeOracleException(number)).ShouldBe(DataAccessErrorKind.SourceSchemaMismatch);

        [Theory]
        [InlineData("TABLE_OR_VIEW_NOT_FOUND")]
        [InlineData("UNRESOLVED_COLUMN")]
        [InlineData("UNRESOLVED_ROUTINE")]
        public void Classifies_SchemaMismatch_Databricks(string errorClass) =>
            DataAccessErrorClassifier.Classify(new FakeDatabricksException(errorClass)).ShouldBe(DataAccessErrorKind.SourceSchemaMismatch);

        [Fact]
        public void Classifies_PermissionDenied_PerDialect()
        {
            foreach (var number in new[] { 229, 230, 262 })
            {
                DataAccessErrorClassifier.Classify(new FakeSqlException(number)).ShouldBe(DataAccessErrorKind.SourcePermissionDenied);
            }

            DataAccessErrorClassifier.Classify(new FakePostgresException("42501")).ShouldBe(DataAccessErrorKind.SourcePermissionDenied);
            DataAccessErrorClassifier.Classify(new Oracle.Fake.FakeOracleException(1031)).ShouldBe(DataAccessErrorKind.SourcePermissionDenied);
            DataAccessErrorClassifier.Classify(new FakeDatabricksException("PERMISSION_DENIED")).ShouldBe(DataAccessErrorKind.SourcePermissionDenied);
            DataAccessErrorClassifier.Classify(new FakeDatabricksException("INSUFFICIENT_PERMISSIONS")).ShouldBe(DataAccessErrorKind.SourcePermissionDenied);
        }

        [Fact]
        public void OracleNumber942_IsNotClassifiedAsSqlServerNumber_AndViceVersa()
        {
            // SQL Server 942 is "database offline" (not drift); Oracle numbers use the Oracle table only.
            DataAccessErrorClassifier.Classify(new FakeSqlException(942)).ShouldBe(DataAccessErrorKind.Other);
            DataAccessErrorClassifier.Classify(new Oracle.Fake.FakeOracleException(207)).ShouldBe(DataAccessErrorKind.Other);
        }

        [Fact]
        public void UnavailableAndTimeout_StillWinOverDrift()
        {
            var both = new AggregateException(new FakeSqlException(208), new FakeSqlException(1205));
            DataAccessErrorClassifier.Classify(both).ShouldBe(DataAccessErrorKind.Unavailable);
            DataAccessErrorClassifier.Classify(new FakeSqlException(-2)).ShouldBe(DataAccessErrorKind.Timeout);
        }

        [Trait("Category", "Security")]
        [Theory]
        [InlineData(208, "SOURCE_SCHEMA_MISMATCH")]
        [InlineData(229, "SOURCE_PERMISSION_DENIED")]
        public void SEC_SIMPL_146_Mapper_Returns502_GenericMessage_NoObjectNames(int number, string code)
        {
            var driver = new FakeSqlException(number);
            var wrapped = new InvalidOperationException("Invalid object name 'dbo.secret_table' near SELECT secret_column", driver);

            SqlCompilerErrorMapper.TryMap(wrapped, out var mapping).ShouldBeTrue();

            mapping.Status.ShouldBe(502);
            mapping.Code.ShouldBe(code);
            mapping.Title.ShouldNotContain("secret");
            mapping.Title.ShouldNotContain("dbo");
            mapping.Title.ShouldNotContain("SELECT");
        }
    }
}
