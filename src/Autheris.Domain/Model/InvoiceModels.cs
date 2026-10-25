namespace Autheris.Domain.Model;

public class InvoiceItemRecord
{
    public string? Id { get; init; }
    public string InvoiceId { get; init; } = string.Empty;
    public string? ProductName { get; init; }
    public decimal Price { get; init; }
    public string? SensitiveNote { get; init; }
}
