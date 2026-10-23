namespace Autheris.Application.Identity.Providers;

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Identity;

public interface ILdapDirectoryAdapter
{
    Task<IReadOnlyList<string>> QueryGroupsAsync(string sanitizedFilter, CancellationToken ct = default);
}

public sealed class LdapsRoleProvider : IRoleProvider
{
    public const string Id = "ldaps-ad";
    public string ProviderId => Id;

    private readonly ILdapDirectoryAdapter? _adapter;

    public LdapsRoleProvider(ILdapDirectoryAdapter? adapter = null)
    {
        _adapter = adapter;
    }

    public static string SanitizeLdapFilter(string input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;

        var sb = new StringBuilder(input.Length * 2);
        foreach (var c in input)
        {
            switch (c)
            {
                case '\\': sb.Append(@"\5c"); break;
                case '*': sb.Append(@"\2a"); break;
                case '(': sb.Append(@"\28"); break;
                case ')': sb.Append(@"\29"); break;
                case '\0': sb.Append(@"\00"); break;
                default:
                    if (char.IsControl(c))
                    {
                        sb.Append($@"\{((int)c):x2}");
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        return sb.ToString();
    }

    public async Task<ProviderRoleResult> GetRolesAsync(
        ResolvedIdentity identity,
        IdentityRealm realm,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);

        if (_adapter == null)
        {
            return ProviderRoleResult.FailedResult;
        }

        try
        {
            var sanitizedUserKey = SanitizeLdapFilter(identity.UserKey);
            var filter = $"(&(objectCategory=user)(sAMAccountName={sanitizedUserKey}))";
            var groups = await _adapter.QueryGroupsAsync(filter, cancellationToken).ConfigureAwait(false);

            return new ProviderRoleResult(
                new HashSet<string>(groups, StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            // SEC-SC-12: Fail closed
            return ProviderRoleResult.FailedResult;
        }
    }
}
