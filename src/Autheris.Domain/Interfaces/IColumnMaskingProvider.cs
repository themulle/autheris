namespace Autheris.Domain.Interfaces;

public interface IColumnMaskingProvider
{
    object? MaskValue(string columnName, object? rawValue, MaskingRule rule);
}
