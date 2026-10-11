namespace Autheris.Application.Policy;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Sql.Policy;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Security;
using TrinoSqlEngine.Governance;

/// <summary>
/// Builds typed <see cref="IRowPolicy"/> instances for the policy sources of <see cref="TableAccessPolicy"/> (WP-X0e). Every value is a
/// bound policy parameter (INV-5); text enters only through <see cref="IPolicyExpressionParser"/>, which fails closed.
/// </summary>
public sealed class RowPolicyFactory
{
    /// <summary>Parameter name of the tenant value (the renderer turns it into the marker <c>@__gql_tenant_id</c>).</summary>
    public const string TenantParameterName = "__gql_tenant_id";

    private readonly IPolicyPredicateRenderer _renderer;
    private readonly IPolicyExpressionParser? _parser;

    public RowPolicyFactory(IPolicyPredicateRenderer renderer, IPolicyExpressionParser? parser = null)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _parser = parser;
    }

    /// <summary>True when admin-authored predicates can be parsed (a parser is registered).</summary>
    public bool CanParseAdminPredicates => _parser != null;

    /// <summary>The tenant isolation predicate of <paramref name="tenantColumn"/>: the tenant is a bound parameter, never text.</summary>
    public IRowPolicy ForTenant(string tenantColumn, TenantId tenant, TargetSqlDialect dialect)
    {
        var capabilities = DialectCapabilityTable.Default.Get(dialect);
        var expression = TenantPredicateFactory.Build(capabilities, tenantColumn, TenantParameterName, SqlParameterType.String);
        var predicate = PolicyPredicate.Create(expression, new Dictionary<string, PolicyValue>
        {
            [TenantParameterName] = new PolicyValue(tenant.Value, SqlParameterType.String)
        });
        return new PolicyPredicateRowPolicy(predicate, dialect, _renderer);
    }

    /// <summary>
    /// Wraps an already typed predicate (for example a Virtual Filter predicate, WP-X0g) in a policy for the dialect of the protected
    /// table. The dialect mapping throws for an unknown dialect (no default).
    /// </summary>
    public IRowPolicy FromPredicate(PolicyPredicate predicate, DatabaseDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return new PolicyPredicateRowPolicy(predicate, PolicyParameterNamer.ToTargetDialect(dialect), _renderer);
    }

    /// <summary>
    /// Parses an access-profile row filter (canonical Trino syntax) once into a typed policy.
    /// </summary>
    /// <exception cref="PolicyParseException">The predicate is invalid (the caller denies; the message never echoes the text).</exception>
    /// <exception cref="InvalidOperationException">No parser is registered.</exception>
    public IRowPolicy FromAccessProfile(string predicate, TableMetadata metadata)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(predicate);
        ArgumentNullException.ThrowIfNull(metadata);
        var parser = _parser ?? throw new InvalidOperationException("No policy expression parser is registered.");

        var dialect = PolicyParameterNamer.ToTargetDialect(metadata.Dialect);
        var context = new PolicyParseContext(
            new TableIdentity(metadata.Identifier.Schema, metadata.Identifier.TableName),
            metadata.Columns.Select(c => c.ColumnName).ToList(),
            SqlFunctionAllowlists.Build(dialect, null),
            new Dictionary<string, PolicyValue>(),
            CatalogVersion: 0)
        {
            TargetDialect = dialect
        };
        return new PolicyPredicateRowPolicy(parser.Parse(predicate, context), dialect, _renderer);
    }
}
