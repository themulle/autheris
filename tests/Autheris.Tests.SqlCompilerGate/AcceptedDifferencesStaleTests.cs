namespace Autheris.Tests.SqlCompilerGate;

using System.Security;
using Autheris.Tests.SqlCompilerGate.Classification;
using Autheris.Tests.SqlCompilerGate.Harness;
using Autheris.Tests.SqlCompilerGate.Model;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Security;

/// <summary>An accepted tightening of issue #847 must still be real: every listed statement is refused by the expression allowlist (no database needed).</summary>
[Trait("Category", "Security")]
public sealed class AcceptedDifferencesStaleTests
{
    [Fact]
    public void Every_accepted_allowlist_statement_is_still_refused_on_its_dialect()
    {
        var accepted = AcceptedDifferences.Parse(File.ReadAllText(Path.Combine(CorpusLoader.CorpusDirectory, "accepted-differences.json")));
        var corpus = CorpusLoader.LoadAll().ToDictionary(s => s.Id, StringComparer.Ordinal);
        var engine = new FastSqlEngine();
        var stale = new List<string>();

        foreach (var entry in accepted.Entries.Where(e => e.Issue == "#847" && e.Reason == "SecurityException"))
        {
            foreach (var dialectName in entry.Dialects)
            {
                var gate = Enum.Parse<GateDialect>(dialectName);
                string schema = gate switch { GateDialect.SqlServer => "dbo", GateDialect.Oracle => "GATE_APP", _ => "public" };
                foreach (var id in entry.Statements)
                {
                    var statement = corpus[id];
                    bool refused = false;
                    foreach (var scenario in new[] { PolicyScenario.Tenant, PolicyScenario.TenantConsentMask })
                    {
                        var request = new GatePolicies(gate, schema, scenario).AstRequest(GatePolicies.PermissionsOf(statement.Class), statement.Class, statement.Sql);
                        try
                        {
                            engine.Compile(statement.Sql.AsMemory(), request, CancellationToken.None);
                        }
                        catch (SecurityException ex) when (ex.Message == RowPolicyErrorGuard.Message)
                        {
                            refused = true;
                        }
                        catch (Exception)
                        {
                        }
                    }

                    if (!refused) stale.Add($"{dialectName} {id}");
                }
            }
        }

        stale.ShouldBeEmpty("stale #847 entries in accepted-differences.json");
    }
}
