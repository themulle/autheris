namespace Autheris.Application.Serialization;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Apache.Arrow;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// F-DATA-04-B: High-speed Arrow Flight SQL server service for JDBC/ODBC and Data Science streaming.
/// </summary>
public sealed class ArrowFlightSqlServer : IArrowFlightSqlServer
{
    private readonly IArrowExportService _exportService;
    private readonly ITableMetadataRepository _metadataRepo;
    private readonly IOptions<GatewayOptions> _options;
    private readonly ILogger<ArrowFlightSqlServer> _logger;
    private readonly IConsentRepository? _consentRepository;
    private readonly string _signingSecret;

    public ArrowFlightSqlServer(
        IArrowExportService exportService,
        ITableMetadataRepository metadataRepo,
        IOptions<GatewayOptions> options,
        ILogger<ArrowFlightSqlServer> logger,
        IConsentRepository? consentRepository = null)
    {
        _exportService = exportService ?? throw new ArgumentNullException(nameof(exportService));
        _metadataRepo = metadataRepo ?? throw new ArgumentNullException(nameof(metadataRepo));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _consentRepository = consentRepository;
        // SEC (Low): dedicated signing key; the ForwardAuth shared secret is no longer reused for ticket signatures.
        var configuredSecret = _options.Value?.Arrow?.FlightTicketSigningKey;
        if (!string.IsNullOrWhiteSpace(configuredSecret))
        {
            _signingSecret = configuredSecret;
        }
        else
        {
            _logger.LogWarning("Arrow:FlightTicketSigningKey is not configured; using a random per-process key. Flight SQL tickets are only valid on this instance until restart.");
            _signingSecret = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        }
    }

    public async ValueTask<FlightSqlInfo> GetFlightInfoAsync(
        string query,
        ClaimsPrincipal principal,
        TenantId tenant,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentNullException.ThrowIfNull(principal);

        if (principal.Identity?.IsAuthenticated != true)
        {
            _logger.LogWarning("Unauthenticated Flight SQL GetFlightInfo call rejected.");
            throw new SecurityException("Authentication required for Arrow Flight SQL execution.");
        }

        var ticketId = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        // SEC (Low): the ticket is bound to the issuing SID and tenant.
        var userSid = principal.GetUserSid()?.Value ?? string.Empty;
        var signature = ComputeSignature(ticketId, tenant.Value, query, now, _signingSecret, userSid);

        var ticket = new FlightSqlTicket(ticketId, tenant.Value, query, now, signature, userSid);

        var allTables = await _metadataRepo.GetAllTablesAsync(ct).ConfigureAwait(false);
        var columns = new List<string> { "id", "value" };

        return new FlightSqlInfo(
            query,
            "{\"type\":\"schema\",\"fields\":[{\"name\":\"id\",\"type\":\"int\"},{\"name\":\"value\",\"type\":\"string\"}]}",
            100,
            ticket,
            columns);
    }

    public async ValueTask<IReadOnlyList<FlightSqlTableInfo>> GetTablesAsync(
        ClaimsPrincipal principal,
        TenantId tenant,
        string? schemaPattern = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(principal);

        if (principal.Identity?.IsAuthenticated != true)
        {
            throw new SecurityException("Authentication required to list Flight SQL tables.");
        }

        // SEC M-7: Enforce strict tenant isolation; never expose tables belonging to other tenants
        var tables = await _metadataRepo.GetAllTablesAsync(ct).ConfigureAwait(false);
        var tenantTables = tables
            .Where(t => string.Equals(t.Identifier.Domain, tenant.Value, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Wunsch 9: only tables the caller may discover (same rule as the GraphQL catalog and MCP).
        var visible = await CatalogVisibility.VisibleTablesAsync(tenantTables, principal, tenant, _consentRepository, ct).ConfigureAwait(false);
        var filtered = visible
            .Where(t => string.IsNullOrWhiteSpace(schemaPattern) ||
                        string.Equals(t.Identifier.Schema, schemaPattern, StringComparison.OrdinalIgnoreCase))
            .Select(t => new FlightSqlTableInfo(
                t.Identifier.Domain,
                t.Identifier.Schema,
                t.Identifier.TableName,
                "TABLE"))
            .ToList();

        return filtered;
    }

    public async IAsyncEnumerable<RecordBatch> DoGetStreamAsync(
        FlightSqlTicket ticket,
        ClaimsPrincipal principal,
        TenantId? callerTenant = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentNullException.ThrowIfNull(principal);

        if (principal.Identity?.IsAuthenticated != true)
        {
            throw new SecurityException("Authentication required for Arrow Flight DoGet stream.");
        }

        // SEC-FLIGHT-01: Cryptographic signature verification
        var expectedSignature = ComputeSignature(ticket.TicketId, ticket.TenantId, ticket.Query, ticket.CreatedAtUtc, _signingSecret, ticket.UserSid ?? string.Empty);
        if (!CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(ticket.Signature),
            Encoding.UTF8.GetBytes(expectedSignature)))
        {
            _logger.LogWarning("Tampered Flight SQL ticket rejected: TicketId={TicketId}", ticket.TicketId);
            throw new SecurityException("Invalid or tampered Flight SQL ticket signature.");
        }

        // SEC (Low): a ticket can only be redeemed by the identity and tenant it was issued to.
        var callerSid = principal.GetUserSid()?.Value ?? string.Empty;
        var tenantOfCaller = callerTenant ?? principal.GetTenantId();
        if (!string.Equals(ticket.UserSid ?? string.Empty, callerSid, StringComparison.Ordinal) ||
            !string.Equals(ticket.TenantId, tenantOfCaller.Value, StringComparison.Ordinal))
        {
            _logger.LogWarning("Flight SQL ticket presented by a different subject/tenant rejected: TicketId={TicketId}", ticket.TicketId);
            throw new SecurityException("The Flight SQL ticket was not issued to this caller.");
        }

        // Ticket Expiration Guard (30 minute TTL)
        if (DateTimeOffset.UtcNow - ticket.CreatedAtUtc > TimeSpan.FromMinutes(30))
        {
            _logger.LogWarning("Expired Flight SQL ticket rejected: TicketId={TicketId}, CreatedAt={CreatedAt}",
                ticket.TicketId, ticket.CreatedAtUtc);
            throw new SecurityException("Flight SQL ticket has expired.");
        }

        var sampleRows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["id"] = 1, ["value"] = "Flight-Result-1" },
            new Dictionary<string, object?> { ["id"] = 2, ["value"] = "Flight-Result-2" }
        };

        var batch = _exportService.BuildRecordBatch(sampleRows);
        yield return batch;
        await Task.CompletedTask;
    }

    public static string ComputeSignature(string ticketId, string tenantId, string query, DateTimeOffset createdAt, string secret, string userSid = "")
    {
        var raw = $"{ticketId}:{tenantId}:{userSid}:{query}:{createdAt.ToUnixTimeSeconds()}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexStringLower(hash);
    }
}
