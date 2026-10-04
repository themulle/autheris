namespace Autheris.Application.Extensibility.Interceptors;

using System.Security.Cryptography;
using System.Text;
using Autheris.Application.Extensibility;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

public sealed class AuditLineageEgressInterceptor : IEgressInterceptor
{
    private readonly GatewayOptions _options;

    public AuditLineageEgressInterceptor(IOptions<GatewayOptions> options)
    {
        _options = options.Value;
    }

    public int Order => 100;

    public ValueTask<EgressResult> OnEgressAsync(EgressContext context, CancellationToken cancellationToken = default)
    {
        if (!_options.Extensibility.Enabled)
        {
            return ValueTask.FromResult(EgressResult.Unmodified());
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Governance-Status"] = "Evaluated"
        };

        if (context.Items.ContainsKey("IsBreakGlass"))
        {
            headers["X-Governance-Mode"] = "Break-Glass-Active";
            if (context.Items.TryGetValue("AccessJustification", out var just) && just != null)
            {
                headers["X-Audit-Ticket"] = just.ToString()!;
            }
        }

        // F-OPS-02: Resolve distributed W3C TraceId for correlating APM traces with WORM audit
        var traceId = Autheris.Application.Common.TraceContextResolver.GetCurrentTraceId();
        headers["X-Trace-Id"] = traceId;

        // Compute SHA-256 hash for tamper-evident data lineage bound to TraceId
        byte[] payloadBytes;
        if (context.ResponseBytes.HasValue && !context.ResponseBytes.Value.IsEmpty)
        {
            var raw = context.ResponseBytes.Value.ToArray();
            var prefix = Encoding.UTF8.GetBytes($"{traceId}:");
            payloadBytes = new byte[prefix.Length + raw.Length];
            Buffer.BlockCopy(prefix, 0, payloadBytes, 0, prefix.Length);
            Buffer.BlockCopy(raw, 0, payloadBytes, prefix.Length, raw.Length);
        }
        else if (!string.IsNullOrEmpty(context.ResponseBodyText))
        {
            payloadBytes = Encoding.UTF8.GetBytes($"{traceId}:{context.ResponseBodyText}");
        }
        else
        {
            payloadBytes = Encoding.UTF8.GetBytes($"{traceId}:{context.IngressContext.Path}:{context.StatusCode}");
        }

        var hash = SHA256.HashData(payloadBytes);
        headers["X-Audit-Lineage-Hash"] = Convert.ToHexStringLower(hash);

        return ValueTask.FromResult(new EgressResult
        {
            Handled = false,
            AdditionalHeaders = headers
        });
    }
}
