namespace Autheris.Application.Identity.Attributes;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

/// <summary>
/// The one implementation of <see cref="IConsentMaterializationGuard"/> (GOV-12). The registry and the table repository are resolved
/// lazily through factories because the governance repositories (which depend on this guard) are themselves the backing store of both.
/// </summary>
public sealed class CallerAttributeConsentGuard : IConsentMaterializationGuard
{
    private readonly Func<ICallerAttributeRegistry> _registry;
    private readonly Func<ITableMetadataRepository> _tables;
    private readonly CallerAttributeOptions _options;

    public CallerAttributeConsentGuard(
        Func<ICallerAttributeRegistry> registry, Func<ITableMetadataRepository> tables, IOptions<GatewayOptions> options)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _tables = tables ?? throw new ArgumentNullException(nameof(tables));
        _options = options?.Value?.CallerAttributes ?? new CallerAttributeOptions();
    }

    public async ValueTask EnsureMayMaterializeAsync(
        TenantId tenant, TableIdentifier table, IReadOnlyList<ConsentRowFilter> filters, CancellationToken ct = default)
    {
        filters ??= Array.Empty<ConsentRowFilter>();
        ConsentRowFilterAttributeValidator.ValidateShape(filters);
        bool hasAttribute = filters.Any(f => string.Equals(f.ValueSource, ConsentRowFilterAttributeValidator.UserAttributeSource, StringComparison.OrdinalIgnoreCase));

        // A registry that cannot be read throws here: nothing is stored when the A-10 rule cannot be evaluated (fail closed).
        var snapshot = _options.Enabled || hasAttribute ? await _registry().GetAsync(ct).ConfigureAwait(false) : CallerAttributeSnapshot.Empty;
        if (_options.Enabled && ConsentRowFilterAttributeValidator.IsAttributeSource(table, snapshot, _options))
        {
            throw new ConsentOnAttributeSourceException();
        }

        if (hasAttribute)
        {
            var metadata = await _tables().GetTableMetadataAsync(table, ct).ConfigureAwait(false);
            ConsentRowFilterAttributeValidator.ValidateAgainstRegistry(tenant, filters, snapshot, _options, metadata);
        }
    }
}
