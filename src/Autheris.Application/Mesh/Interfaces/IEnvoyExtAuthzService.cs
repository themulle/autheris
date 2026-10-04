namespace Autheris.Application.Mesh.Interfaces;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

/// <summary>
/// F-ARCH-11: Interface for Envoy External Authorization (ext_authz) & Istio mesh PDP evaluations.
/// </summary>
public interface IEnvoyExtAuthzService
{
    /// <summary>
    /// Evaluates Envoy CheckRequest JSON payload against Autheris PDP.
    /// </summary>
    ValueTask<EnvoyCheckResponse> CheckAsync(EnvoyCheckRequest request, CancellationToken ct = default);

    /// <summary>
    /// Evaluates direct Envoy HTTP check request with forwarded headers.
    /// </summary>
    ValueTask<EnvoyCheckResponse> CheckHttpAsync(
        string method,
        string path,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken ct = default);

    /// <summary>
    /// Exports ready-to-apply Istio EnvoyFilter Kubernetes CRD YAML pointing to this gateway.
    /// </summary>
    string GenerateIstioEnvoyFilterYaml(EnvoyFilterExportOptions? options = null);

    /// <summary>
    /// Exports ready-to-apply Istio WasmPlugin Kubernetes CRD YAML.
    /// </summary>
    string GenerateIstioWasmPluginYaml(EnvoyFilterExportOptions? options = null);
}
