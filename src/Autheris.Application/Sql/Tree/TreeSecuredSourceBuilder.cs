using System.Collections.Frozen;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Sql.Policy;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;

namespace Autheris.Application.Sql.Tree;

/// <summary>
/// SQLA-06 follow-up (SQLA06F-C1..C9): builds the secured source of one tree table whose typed row policy needs the engine pipeline (a
/// correlated consent subquery, a key-source virtual filter). It reuses the WebSQL pipeline: <see cref="GovernancePolicyFactory"/> admits the
/// table and its subquery dependents, the engine compiles <c>SELECT &lt;catalog columns&gt; FROM table</c> with the tenant wrapper, the policy
/// and the coverage proof. Everything is built per request from the request's tenant; nothing is cached here (C2). Every failure maps to the
/// fixed <c>TREE_ROW_POLICY_UNSUPPORTED</c> denial without an inner message (C7); the caller's cancellation propagates unchanged.
/// </summary>
public sealed class TreeSecuredSourceBuilder(
    GovernancePolicyFactory? policyFactory = null,
    ISqlEngine? engine = null,
    ILogger<TreeSecuredSourceBuilder>? logger = null)
{
    public async Task<TreeSecuredSource> BuildAsync(ResolvedTableAccess access, string dataSourceName, DatabaseDialect dialect, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(access);
        try
        {
            return await BuildCoreAsync(access, dataSourceName, dialect, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // C7: only the code and the table identity id are logged; no inner message, predicate text or value.
            logger?.LogDebug("Tree secured source denied ({Code}) for table {Table}.", TreeSecuredSourceMarkers.DeniedCode, access.Metadata.Identifier);
            throw TreeSecuredSourceMarkers.Denied();
        }
    }

    private async Task<TreeSecuredSource> BuildCoreAsync(ResolvedTableAccess access, string dataSourceName, DatabaseDialect dialect, CancellationToken ct)
    {
        // C8: no fallback to the text splice when the pipeline is not wired or the dialect is not one of the tree dialects.
        if (policyFactory is null || engine is null || string.IsNullOrWhiteSpace(dataSourceName))
        {
            throw TreeSecuredSourceMarkers.Denied();
        }

        if (!SqlDialectMapper.TryToTargetDialect(dialect, out var target)
            || target is not (TargetSqlDialect.Sqlite or TargetSqlDialect.SqlServer or TargetSqlDialect.PostgreSql))
        {
            throw TreeSecuredSourceMarkers.Denied();
        }

        var metadata = access.Metadata;
        var decision = access.Decision;
        if (decision.RowPolicy is not PolicyPredicateRowPolicy)
        {
            throw TreeSecuredSourceMarkers.Denied();
        }

        var identity = new TableIdentity(metadata.Table.SchemaName, metadata.Table.TableName);
        var sql = SelectAllColumns(metadata);

        var input = new GovernedPolicyInput(
            dataSourceName,
            access.Tenant,
            new Dictionary<TableIdentity, TableAccessDecision> { [identity] = decision },
            new HashSet<TableIdentity>(),
            GovernedSubqueryStrategy.Correlated)
        {
            Metadata = new Dictionary<TableIdentity, TableMetadata> { [identity] = metadata },
            KeySetStatement = null   // C6: no key-set stage on the tree (the EXISTS path stays byte-identical).
        };

        var assembly = await policyFactory.CreateAssemblyAsync(input, ct).ConfigureAwait(false);

        // C5/C6: masks stay in the tree's outer projection; the secured source applies tenant and policy only.
        var policy = assembly.Policy with { Masks = TreeNoMasks.Instance, TablesWithMaskedColumns = FrozenSet<string>.Empty };
        var request = CompileRequestFactory.Create(
            policy, target, new WebSqlOptions(), isDml: false, enforcedMaxRows: 0, SqlFunctionAllowlists.Build(target, null));

        var compiled = engine.Compile(sql.AsMemory(), request, ct);
        Validate(compiled, identity, target, access);

        // F4 at build time: a dry run of the remap proves the markers before the tree compiler ever sees the source.
        var dry = new Dictionary<string, string>(StringComparer.Ordinal);
        var counter = 0;
        foreach (var parameter in compiled.Parameters)
        {
            if (!dry.TryAdd(parameter.Marker, "@gq" + counter++))
            {
                throw TreeSecuredSourceMarkers.Denied();
            }
        }

        _ = TreeSecuredSourceMarkers.Remap(compiled.Sql, target, dry);
        return new TreeSecuredSource(compiled.Sql, compiled.Parameters, target);
    }

    private static void Validate(CompiledSql compiled, TableIdentity identity, TargetSqlDialect target, ResolvedTableAccess access)
    {
        if (compiled.StatementClass != SqlStatementClass.Select || compiled.Dialect != target)
        {
            throw TreeSecuredSourceMarkers.Denied();
        }

        var policyId = new SecurityPredicateId(identity.ToString(), 1);
        var tenantId = new SecurityPredicateId(identity.ToString(), 0);
        if (!compiled.AppliedPredicates.Contains(policyId)
            || (!string.IsNullOrWhiteSpace(access.Metadata.TenantColumnName) && !compiled.AppliedPredicates.Contains(tenantId)))
        {
            throw TreeSecuredSourceMarkers.Denied();
        }

        if (TreeSecuredSourceMarkers.HasRootRowLimit(compiled.Sql))
        {
            throw TreeSecuredSourceMarkers.Denied();
        }

        foreach (var parameter in compiled.Parameters)
        {
            if (parameter.ZoneArgument)
            {
                throw TreeSecuredSourceMarkers.Denied();
            }

            switch (parameter.Origin)
            {
                case ParameterOrigin.Tenant:
                    // C3: every tenant value is the tenant of this table's access (the outer tree tenant conjunct stays as well).
                    if (parameter.Value is not string tenant || !string.Equals(tenant, access.Tenant.Value, StringComparison.Ordinal))
                    {
                        throw TreeSecuredSourceMarkers.Denied();
                    }

                    break;
                case ParameterOrigin.Policy or ParameterOrigin.QueryLiteral:
                    break;
                default:
                    // ClientNamed, ClientPositional and Mask never occur in a gateway-generated source.
                    throw TreeSecuredSourceMarkers.Denied();
            }
        }
    }

    private static string SelectAllColumns(TableMetadata metadata)
    {
        if (metadata.Columns.Count == 0)
        {
            throw TreeSecuredSourceMarkers.Denied();
        }

        var columns = string.Join(", ", metadata.Columns.Select(static c => Quote(c.ColumnName)));
        var table = string.IsNullOrWhiteSpace(metadata.Table.SchemaName)
            ? Quote(metadata.Table.TableName)
            : Quote(metadata.Table.SchemaName) + "." + Quote(metadata.Table.TableName);
        return "SELECT " + columns + " FROM " + table;
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
