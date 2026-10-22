namespace Autheris.Domain.Interfaces;

public interface IColumnMaskingProvider
{
    object? MaskValue(string columnName, object? rawValue, MaskingRule rule);

    /// <summary>
    /// VF-12: the configured <c>DataMasking:HmacKeyId</c>, the key id every channel must use when it scopes an HMAC rule to a tenant,
    /// so one value yields the same pseudonym on every read path. <c>null</c> when the provider has no configuration.
    /// </summary>
    string? DefaultHmacKeyId => null;
}
