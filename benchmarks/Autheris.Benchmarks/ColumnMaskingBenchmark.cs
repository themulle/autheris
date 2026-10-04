using Autheris.Application.Services;
using Autheris.Domain.Model;

namespace Autheris.Benchmarks;

public class ColumnMaskingBenchmark
{
    private readonly ColumnMaskingProvider _maskingProvider = new();
    private readonly MaskingRule _emailRule = new() { RuleType = "REGEX" };
    private readonly MaskingRule _ibanRule = new() { RuleType = "REGEX" };
    private readonly MaskingRule _hmacRule = new() { RuleType = "HMAC" };

    public object? MaskEmail()
    {
        return _maskingProvider.MaskValue("email_address", "john.doe.enterprise@company.global", _emailRule);
    }

    public object? MaskIban()
    {
        return _maskingProvider.MaskValue("iban", "DE89370400440532013000", _ibanRule);
    }

    public object? PseudonymizeHmac()
    {
        return _maskingProvider.MaskValue("social_security_number", "123-45-6789", _hmacRule);
    }
}
