namespace Autheris.Api.Endpoints;

using System;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Extensions;
using Autheris.Api.Security;
using Autheris.Api.UI;
using Autheris.Application.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Security.Totp.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;

public static class DevPortalEndpoints
{
    public static IEndpointRouteBuilder MapDevPortalEndpoints(this IEndpointRouteBuilder app, GatewayOptions gatewayOptions)
    {
        IResult HandleDevPortal(HttpContext context, IWebHostEnvironment env)
        {
            // SEC-T2 Hardening: Strictly forbidden outside of Development environment (Fail-Closed)
            if (!env.IsDevelopment())
            {
                return Results.NotFound();
            }

            var nonce = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
            SetPortalSecurityHeaders(context, nonce, noStore: false);

            var html = GenerateDevPortalHtml(gatewayOptions, env.EnvironmentName, nonce);
            return Results.Content(html, "text/html;charset=utf-8");
        }

        app.MapGet("/", HandleDevPortal).AllowAnonymous().WithAuditExemption("Root gateway landing/discovery page");
        app.MapGet("/getting-started", HandleDevPortal).AllowAnonymous().WithAuditExemption("Developer getting started guide");

        // R-60 / R-64: DevPortal 2FA Enrollment Web Console
        app.MapGet("/portal/2fa/enroll", (
            HttpContext context,
            ITotpVerificationService totpService) =>
        {
            var nonce = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
            SetPortalSecurityHeaders(context, nonce, noStore: true);

            var userSid = context.User.GetUserSid()?.Value
                          ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                          ?? "developer";

            var enrollment = totpService.GenerateEnrollment(userSid, $"{userSid}@autheris.local", "Autheris Gateway");
            var svg = SvgQrCodeGenerator.GenerateSvg(enrollment.QrCodeUri);

            var formattedSecret = string.Join(" ", Enumerable.Range(0, Math.Max(1, enrollment.SecretBase32.Length / 4))
                .Select(i => enrollment.SecretBase32.Substring(i * 4, Math.Min(4, enrollment.SecretBase32.Length - i * 4))));

            var html = Generate2FaEnrollHtml(userSid, formattedSecret, enrollment.SecretBase32, svg, nonce);
            return Results.Content(html, "text/html;charset=utf-8");
        });

        app.MapPost("/portal/2fa/enroll/verify", async (
            HttpContext context,
            ITotpVerificationService totpService,
            ITotpSecretStore? secretStore,
            CancellationToken ct) =>
        {
            string code = string.Empty;
            string secret = string.Empty;

            if (context.Request.HasFormContentType)
            {
                var form = await context.Request.ReadFormAsync(ct);
                code = form["totpCode"].ToString();
                secret = form["secret"].ToString();
            }
            else
            {
                try
                {
                    var body = await context.Request.ReadFromJsonAsync<System.Text.Json.JsonElement>(ct);
                    code = body.TryGetProperty("totpCode", out var c) ? c.GetString() ?? "" : "";
                    secret = body.TryGetProperty("secret", out var s) ? s.GetString() ?? "" : "";
                }
                catch
                {
                    // Ignore JSON parse errors
                }
            }

            var userSid = context.User.GetUserSid()?.Value
                          ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                          ?? "developer";

            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(secret))
            {
                return Results.BadRequest(new { error = "Code and secret are required." });
            }

            bool valid = await totpService.VerifyAndConsumeTotpAsync(userSid, secret, code, ct);
            if (!valid)
            {
                return Results.BadRequest(new { error = "Invalid or expired TOTP code." });
            }

            if (secretStore != null)
            {
                await secretStore.SetSecretAsync(userSid, secret, ct);
            }

            return Results.Ok(new { success = true, message = "2FA Enrollment completed successfully." });
        });

        // R-64 UX: DevPortal HitL Step-Up Approvals Console
        app.MapGet("/portal/approvals", (
            HttpContext context,
            IHitLStepUpApprovalService hitlService) =>
        {
            var nonce = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
            SetPortalSecurityHeaders(context, nonce, noStore: true);

            var tenant = EndpointSecurity.GetRequestTenant(context);
            var tickets = hitlService.GetPendingTickets(tenant.Value);

            var html = GenerateApprovalsHtml(tenant.Value, tickets, nonce);
            return Results.Content(html, "text/html;charset=utf-8");
        });

        app.MapPost("/portal/approvals/{ticketId}/step-up", async (
            string ticketId,
            HttpContext context,
            IHitLStepUpApprovalService hitlService,
            CancellationToken ct) =>
        {
            string totpCode = string.Empty;
            if (context.Request.HasFormContentType)
            {
                var form = await context.Request.ReadFormAsync(ct);
                totpCode = form["totpCode"].ToString();
            }
            else
            {
                try
                {
                    var body = await context.Request.ReadFromJsonAsync<System.Text.Json.JsonElement>(ct);
                    totpCode = body.TryGetProperty("totpCode", out var c) ? c.GetString() ?? "" : "";
                }
                catch
                {
                    // Ignore
                }
            }

            var approver = HitLEndpoints.BuildApproverContext(context);
            if (approver == null)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var result = await hitlService.ApproveStepUpRequestAsync(ticketId, approver, totpCode, ct);
            if (!result.IsApproved)
            {
                return Results.BadRequest(new { error = result.Message ?? "Step-Up Approval failed." });
            }

            return Results.Ok(new { success = true, confirmationToken = result.Ticket?.Signature ?? result.Ticket?.ApprovalId ?? ticketId });
        });

        return app;
    }

    private static string GenerateDevPortalHtml(GatewayOptions options, string environment, string nonce)
    {
        var isQuickstart = options.IsQuickstartProfile;
        var isOpenSchema = options.IsOpenSchemaAllowed;
        var modeBadge = isQuickstart ? "🚀 Quickstart Dev Mode" : "🔒 Zero-Trust Strict";
        var badgeColor = isQuickstart ? "#10b981" : "#3b82f6";

        return $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1" />
          <title>Autheris - Developer Quickstart Hub</title>
          <style>
            :root {
              --bg: #0f172a;
              --card: #1e293b;
              --card-border: #334155;
              --text: #f8fafc;
              --text-muted: #94a3b8;
              --primary: #38bdf8;
              --primary-hover: #0284c7;
              --accent: #10b981;
              --code-bg: #0b1120;
            }
            * { box-sizing: border-box; margin: 0; padding: 0; }
            body {
              font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif;
              background-color: var(--bg);
              color: var(--text);
              padding: 2rem;
              line-height: 1.5;
            }
            .container { max-width: 1200px; margin: 0 auto; }
            header {
              display: flex;
              align-items: center;
              justify-content: space-between;
              padding-bottom: 2rem;
              border-bottom: 1px solid var(--card-border);
              margin-bottom: 2rem;
              flex-wrap: wrap;
              gap: 1rem;
            }
            .logo-group h1 { font-size: 2rem; font-weight: 700; color: var(--primary); }
            .logo-group p { color: var(--text-muted); font-size: 0.95rem; }
            .badge-bar { display: flex; gap: 0.5rem; align-items: center; flex-wrap: wrap; }
            .badge {
              padding: 0.35rem 0.75rem;
              border-radius: 9999px;
              font-size: 0.8rem;
              font-weight: 600;
              text-transform: uppercase;
              letter-spacing: 0.05em;
            }
            .grid {
              display: grid;
              grid-template-columns: repeat(auto-fit, minmax(320px, 1fr));
              gap: 1.5rem;
              margin-bottom: 2.5rem;
            }
            .card {
              background-color: var(--card);
              border: 1px solid var(--card-border);
              border-radius: 0.75rem;
              padding: 1.5rem;
              display: flex;
              flex-direction: column;
              justify-content: space-between;
            }
            .card h3 { font-size: 1.25rem; margin-bottom: 0.5rem; color: var(--text); display: flex; align-items: center; gap: 0.5rem; }
            .card p { color: var(--text-muted); font-size: 0.9rem; margin-bottom: 1.25rem; flex-grow: 1; }
            .btn {
              display: inline-block;
              background-color: var(--primary);
              color: #0f172a;
              font-weight: 600;
              padding: 0.5rem 1rem;
              border-radius: 0.5rem;
              text-decoration: none;
              text-align: center;
              transition: background-color 0.15s ease;
            }
            .btn:hover { background-color: var(--primary-hover); }
            .btn-secondary {
              background-color: transparent;
              color: var(--primary);
              border: 1px solid var(--primary);
              margin-left: 0.5rem;
            }
            .btn-secondary:hover { background-color: rgba(56, 189, 248, 0.1); }
            .section-title { font-size: 1.5rem; font-weight: 600; margin-bottom: 1rem; color: var(--primary); }
            .identity-panel {
              background-color: var(--card);
              border: 1px solid var(--card-border);
              border-radius: 0.75rem;
              padding: 1.5rem;
              margin-bottom: 2.5rem;
            }
            .persona-list {
              display: grid;
              grid-template-columns: repeat(auto-fit, minmax(260px, 1fr));
              gap: 1rem;
              margin-top: 1rem;
            }
            .persona-card {
              background-color: var(--code-bg);
              border: 1px solid var(--card-border);
              border-radius: 0.5rem;
              padding: 1rem;
            }
            .persona-card h4 { color: var(--text); margin-bottom: 0.25rem; }
            .persona-card small { color: var(--text-muted); display: block; margin-bottom: 0.75rem; }
            .code-box {
              background-color: var(--code-bg);
              border: 1px solid var(--card-border);
              border-radius: 0.5rem;
              padding: 1rem;
              font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
              font-size: 0.85rem;
              color: #e2e8f0;
              overflow-x: auto;
              position: relative;
              margin-top: 0.5rem;
            }
            .copy-btn {
              background-color: var(--card);
              color: var(--text);
              border: 1px solid var(--card-border);
              padding: 0.25rem 0.5rem;
              border-radius: 0.25rem;
              font-size: 0.75rem;
              cursor: pointer;
              margin-top: 0.5rem;
            }
            .copy-btn:hover { background-color: var(--primary); color: #0f172a; }
          </style>
        </head>
        <body>
          <div class="container">
            <header>
              <div class="logo-group">
                <h1>Autheris Developer Hub</h1>
                <p>High-Performance Zero-Trust GraphQL, OData & MCP Gateway (.NET 10)</p>
              </div>
              <div class="badge-bar">
                <span class="badge" style="background-color: {{badgeColor}}; color: #fff;">{{modeBadge}}</span>
                <span class="badge" style="background-color: #334155; color: #f8fafc;">Env: {{System.Net.WebUtility.HtmlEncode(environment)}}</span>
                {{(isOpenSchema ? "<span class=\"badge\" style=\"background-color: #059669; color: #fff;\">OpenSchema Active</span>" : "")}}
              </div>
            </header>

            <h2 class="section-title">🚀 Quick Launchpad</h2>
            <div class="grid">
              <div class="card">
                <div>
                  <h3>🍩 GraphQL Explorer</h3>
                  <p>Interactive Banana Cake Pop IDE for testing GraphQL queries, subscriptions and mutations.</p>
                </div>
                <div>
                  <a href="/graphql" target="_blank" class="btn">Open GraphQL IDE</a>
                  <a href="/graphql/finance" target="_blank" class="btn btn-secondary">Finance Scope</a>
                </div>
              </div>

              <div class="card">
                <div>
                  <h3>📄 OpenAPI 3.1 & Swagger</h3>
                  <p>Standards-compliant OpenAPI 3.1 documentation and interactive Swagger UI explorer.</p>
                </div>
                <div>
                  <a href="/docs" target="_blank" class="btn">Open Swagger UI</a>
                  <a href="/odata/v4/$openapi/index" target="_blank" class="btn btn-secondary">OpenAPI Index</a>
                </div>
              </div>

              <div class="card">
                <div>
                  <h3>🤖 AI Model Context Protocol (MCP)</h3>
                  <p>Streamable HTTP & SSE endpoint for autonomous AI agents (Cursor, Windsurf, Claude Desktop).</p>
                </div>
                <div>
                  <a href="/mcp" target="_blank" class="btn">MCP Endpoint</a>
                </div>
              </div>

              <div class="card">
                <div>
                  <h3>⚡ Governed WebSQL Engine</h3>
                  <p>HTTP-based SQL execution with ANTLR4 Trino AST linter, RLS injection and PII masking.</p>
                </div>
                <div>
                  <a href="/docs?domain=sql" class="btn">WebSQL Documentation</a>
                </div>
              </div>

              <div class="card">
                <div>
                  <h3>📊 Health & Telemetrie</h3>
                  <p>Standardized system metrics, resource group concurrency slots and cluster state.</p>
                </div>
                <div>
                  <a href="/health" target="_blank" class="btn">Health Check</a>
                  <a href="/api/governance/system/metrics" target="_blank" class="btn btn-secondary">Metrics</a>
                </div>
              </div>
            </div>

            <div class="identity-panel">
              <h2 class="section-title">👤 Interactive Identity & Test Personas</h2>
              <p style="color: var(--text-muted); font-size: 0.9rem;">
                In the Development environment, you can simulate requests with the following headers to test RLS and PII masking live:
              </p>

              <div class="persona-list">
                <div class="persona-card">
                  <h4>Alice (Finance Analyst)</h4>
                  <small>Role: FinanceManager | Sees invoices, PII masked</small>
                  <button class="copy-btn" data-copy='-H "X-Test-User-Sid: S-1-5-21-ALICE-FINANCE" -H "X-Test-Roles: FinanceManager" -H "X-Test-Tenant: tenant-default"'>Copy Header</button>
                </div>

                <div class="persona-card">
                  <h4>Bob (HR Manager)</h4>
                  <small>Role: HrManager | Sees employee salaries in plain text</small>
                  <button class="copy-btn" data-copy='-H "X-Test-User-Sid: S-1-5-21-BOB-HR" -H "X-Test-Roles: HrManager" -H "X-Test-Tenant: tenant-default"'>Copy Header</button>
                </div>

                <div class="persona-card">
                  <h4>Carol (Governance Admin)</h4>
                  <small>Role: GovernanceAdmin | Full access to mutations & policies</small>
                  <button class="copy-btn" data-copy='-H "X-Test-User-Sid: S-1-5-21-ADMIN-CAROL" -H "X-Test-Roles: GovernanceAdmin,ClusterAdmin" -H "X-Test-Tenant: tenant-default"'>Copy Header</button>
                </div>

                <div class="persona-card">
                  <h4>Autonomous AI Agent</h4>
                  <small>Role: AiAgent | MCP Tool Execution & Catalog Inspection</small>
                  <button class="copy-btn" data-copy='-H "X-Test-User-Sid: S-1-5-21-AI-AGENT" -H "X-Test-Roles: AiAgent" -H "X-Test-Tenant: tenant-default"'>Copy Header</button>
                </div>
              </div>
            </div>

            <div class="identity-panel">
              <h2 class="section-title">💡 Claude Desktop & Cursor MCP Config</h2>
              <p style="color: var(--text-muted); font-size: 0.9rem;">
                Add this snippet to your <code>claude_desktop_config.json</code> or <code>.cursor/mcp.json</code>:
              </p>
              <div class="code-box">
                {
                  "mcpServers": {
                    "gql-gateway": {
                      "url": "http://localhost:5000/mcp"
                    }
                  }
                }
              </div>
            </div>
          </div>

          <script nonce="{{nonce}}">
            document.querySelectorAll('.copy-btn').forEach(btn => {
              btn.addEventListener('click', () => {
                const text = btn.getAttribute('data-copy');
                if (text && navigator.clipboard) {
                  navigator.clipboard.writeText(text);
                  const oldText = btn.textContent;
                  btn.textContent = 'Copied!';
                  setTimeout(() => btn.textContent = oldText, 2000);
                }
              });
            });
          </script>
        </body>
        </html>
        """;
    }

    private static void SetPortalSecurityHeaders(HttpContext context, string nonce, bool noStore = false)
    {
        context.Response.Headers.ContentSecurityPolicy = $"default-src 'self'; script-src 'self' 'nonce-{nonce}'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; frame-ancestors 'none'; object-src 'none'; base-uri 'self';";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["X-Frame-Options"] = "DENY";
        if (noStore)
        {
            context.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, private";
            context.Response.Headers["Pragma"] = "no-cache";
        }
    }

    private static string Generate2FaEnrollHtml(string userSid, string formattedSecret, string rawSecret, string svgQrCode, string nonce)
    {
        return $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1" />
          <title>Autheris DevPortal - Two-Factor Authentication Setup</title>
          <style>
            :root {
              --bg: #0f172a; --card: #1e293b; --card-border: #334155;
              --text: #f8fafc; --text-muted: #94a3b8; --primary: #38bdf8;
              --primary-hover: #0284c7; --accent: #10b981; --code-bg: #0b1120;
            }
            * { box-sizing: border-box; margin: 0; padding: 0; }
            body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; background-color: var(--bg); color: var(--text); padding: 2rem; }
            .container { max-width: 600px; margin: 0 auto; background: var(--card); border: 1px solid var(--card-border); border-radius: 12px; padding: 2rem; box-shadow: 0 4px 20px rgba(0,0,0,0.4); }
            h1 { font-size: 1.5rem; color: var(--primary); margin-bottom: 0.5rem; }
            p { color: var(--text-muted); font-size: 0.95rem; margin-bottom: 1.5rem; }
            .qr-wrapper { max-width: 240px; margin: 0 auto 1.5rem auto; border-radius: 8px; overflow: hidden; padding: 10px; background: #0f172a; border: 1px solid var(--card-border); }
            .secret-box { background: var(--code-bg); border: 1px solid var(--card-border); border-radius: 6px; padding: 0.75rem 1rem; font-family: monospace; font-size: 1.1rem; text-align: center; letter-spacing: 0.1em; color: var(--accent); margin-bottom: 1.5rem; }
            .form-group { margin-bottom: 1.25rem; }
            label { display: block; font-size: 0.9rem; color: var(--text); margin-bottom: 0.5rem; }
            input[type="text"] { width: 100%; padding: 0.75rem; border-radius: 6px; background: var(--code-bg); border: 1px solid var(--card-border); color: var(--text); font-size: 1.2rem; text-align: center; letter-spacing: 0.2em; }
            button { width: 100%; background: var(--primary); color: #0f172a; font-weight: 600; padding: 0.75rem; border: none; border-radius: 6px; cursor: pointer; font-size: 1rem; }
            button:hover { background: var(--primary-hover); }
            .status { margin-top: 1rem; padding: 0.75rem; border-radius: 6px; display: none; text-align: center; }
          </style>
        </head>
        <body>
          <div class="container">
            <h1>🔐 Two-Factor Authentication Setup</h1>
            <p>Scan this QR code with Microsoft Authenticator, Google Authenticator, or 1Password.</p>
            <div class="qr-wrapper">
              {{svgQrCode}}
            </div>
            <p style="text-align: center; margin-bottom: 0.5rem; font-size: 0.85rem;">Or enter key manually:</p>
            <div class="secret-box">{{formattedSecret}}</div>
            <form id="verifyForm">
              <input type="hidden" id="secret" value="{{rawSecret}}" />
              <div class="form-group">
                <label for="totpCode">Enter 6-digit Code from Authenticator App:</label>
                <input type="text" id="totpCode" maxlength="6" pattern="[0-9]{6}" placeholder="123456" required autocomplete="off" />
              </div>
              <button type="submit">Verify & Activate 2FA</button>
            </form>
            <div id="status" class="status"></div>
          </div>
          <script nonce="{{nonce}}">
            document.getElementById('verifyForm').addEventListener('submit', async (e) => {
              e.preventDefault();
              const code = document.getElementById('totpCode').value.trim();
              const secret = document.getElementById('secret').value.trim();
              const statusDiv = document.getElementById('status');

              try {
                const res = await fetch('/portal/2fa/enroll/verify', {
                  method: 'POST',
                  headers: { 'Content-Type': 'application/json' },
                  body: JSON.stringify({ totpCode: code, secret: secret })
                });
                const data = await res.json();
                statusDiv.style.display = 'block';
                if (res.ok) {
                  statusDiv.style.background = '#064e3b';
                  statusDiv.style.color = '#34d399';
                  statusDiv.textContent = '✅ ' + data.message;
                } else {
                  statusDiv.style.background = '#7f1d1d';
                  statusDiv.style.color = '#f87171';
                  statusDiv.textContent = '❌ ' + (data.error || 'Verification failed');
                }
              } catch (err) {
                statusDiv.style.display = 'block';
                statusDiv.style.background = '#7f1d1d';
                statusDiv.style.color = '#f87171';
                statusDiv.textContent = '❌ Request error: ' + err.message;
              }
            });
          </script>
        </body>
        </html>
        """;
    }

    private static string GenerateApprovalsHtml(string tenantId, System.Collections.Generic.IReadOnlyList<HitLApprovalTicket> tickets, string nonce)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var t in tickets)
        {
            sb.Append($$"""
            <tr>
              <td><code>{{t.ApprovalId}}</code></td>
              <td>{{t.RequesterSid}}</td>
              <td><code>{{t.TargetTable}}</code></td>
              <td>{{t.ToolName}}</td>
              <td>
                <form class="step-up-form" data-ticket="{{t.ApprovalId}}">
                  <input type="text" class="totp-input" maxlength="6" placeholder="TOTP" required style="width: 80px; padding: 4px; text-align: center; border-radius: 4px; background: #0b1120; border: 1px solid #334155; color: #fff;" />
                  <button type="submit" style="padding: 4px 10px; background: #38bdf8; border: none; border-radius: 4px; color: #0f172a; font-weight: bold; cursor: pointer;">Approve</button>
                </form>
              </td>
            </tr>
            """);
        }

        var tableBody = sb.Length > 0 ? sb.ToString() : "<tr><td colspan=\"5\" style=\"text-align: center; padding: 2rem; color: #94a3b8;\">No pending approval tickets for tenant.</td></tr>";

        return $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1" />
          <title>Autheris DevPortal - Pending Approvals</title>
          <style>
            :root { --bg: #0f172a; --card: #1e293b; --card-border: #334155; --text: #f8fafc; --text-muted: #94a3b8; --primary: #38bdf8; --accent: #10b981; }
            * { box-sizing: border-box; margin: 0; padding: 0; }
            body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; background-color: var(--bg); color: var(--text); padding: 2rem; }
            .container { max-width: 900px; margin: 0 auto; }
            h1 { font-size: 1.8rem; color: var(--primary); margin-bottom: 0.5rem; }
            p { color: var(--text-muted); margin-bottom: 2rem; }
            table { width: 100%; border-collapse: collapse; background: var(--card); border: 1px solid var(--card-border); border-radius: 8px; overflow: hidden; }
            th, td { padding: 1rem; text-align: left; border-bottom: 1px solid var(--card-border); font-size: 0.9rem; }
            th { background: #0b1120; color: var(--text-muted); font-weight: 600; text-transform: uppercase; letter-spacing: 0.05em; }
            tr:hover { background: rgba(56, 189, 248, 0.05); }
            code { background: #0b1120; padding: 2px 6px; border-radius: 4px; color: var(--accent); }
            .status-banner { margin-bottom: 1.5rem; padding: 1rem; border-radius: 6px; display: none; }
          </style>
        </head>
        <body>
          <div class="container">
            <h1>🛡️ Human-in-the-Loop Step-Up Approvals</h1>
            <p>Pending authorization tickets for tenant <code>{{tenantId}}</code>. Enter your 6-digit TOTP code to confirm step-up approvals.</p>
            <div id="statusBanner" class="status-banner"></div>
            <table>
              <thead>
                <tr>
                  <th>Ticket ID</th>
                  <th>Requester</th>
                  <th>Resource</th>
                  <th>Required Level</th>
                  <th>Action</th>
                </tr>
              </thead>
              <tbody>
                {{tableBody}}
              </tbody>
            </table>
          </div>
          <script nonce="{{nonce}}">
            document.querySelectorAll('.step-up-form').forEach(form => {
              form.addEventListener('submit', async (e) => {
                e.preventDefault();
                const ticketId = form.getAttribute('data-ticket');
                const totpInput = form.querySelector('.totp-input');
                const code = totpInput.value.trim();
                const banner = document.getElementById('statusBanner');

                try {
                  const res = await fetch(`/portal/approvals/${ticketId}/step-up`, {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ totpCode: code })
                  });
                  const data = await res.json();
                  banner.style.display = 'block';
                  if (res.ok) {
                    banner.style.background = '#064e3b';
                    banner.style.color = '#34d399';
                    banner.textContent = `✅ Ticket ${ticketId} approved. Token: ${data.confirmationToken}`;
                    form.closest('tr').style.opacity = '0.4';
                  } else {
                    banner.style.background = '#7f1d1d';
                    banner.style.color = '#f87171';
                    banner.textContent = `❌ Approval failed: ${data.error || 'Invalid code'}`;
                  }
                } catch (err) {
                  banner.style.display = 'block';
                  banner.style.background = '#7f1d1d';
                  banner.style.color = '#f87171';
                  banner.textContent = `❌ Request error: ${err.message}`;
                }
              });
            });
          </script>
        </body>
        </html>
        """;
    }
}
