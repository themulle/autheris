namespace Autheris.Domain.Model;

public class InvoiceRecord
{
    public string Id { get; init; } = string.Empty;
    public decimal Amount { get; init; }
    public string Vendor { get; init; } = string.Empty;
    public string? Email { get; init; }
}

public class InvoiceItemRecord
{
    public string Id { get; init; } = string.Empty;
    public string InvoiceId { get; init; } = string.Empty;
    public string? ProductName { get; init; }
    public decimal Price { get; init; }
    public string? SensitiveNote { get; init; }
}
