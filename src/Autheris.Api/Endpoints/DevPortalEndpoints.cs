namespace Autheris.Api.Endpoints;

using System;
using Autheris.Api.Extensions;
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
            context.Response.Headers.ContentSecurityPolicy = $"default-src 'self'; script-src 'self' 'nonce-{nonce}'; style-src 'self' 'unsafe-inline'; frame-ancestors 'none'; object-src 'none'; base-uri 'self';";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["X-Frame-Options"] = "DENY";

            var html = GenerateDevPortalHtml(gatewayOptions, env.EnvironmentName, nonce);
            return Results.Content(html, "text/html;charset=utf-8");
        }

        app.MapGet("/", HandleDevPortal).AllowAnonymous().WithAuditExemption("Root gateway landing/discovery page");
        app.MapGet("/getting-started", HandleDevPortal).AllowAnonymous().WithAuditExemption("Developer getting started guide");

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
}
