using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>WP-OI-CI-8 (#611): the TLS governance database of the sim-api-fuzz job (SEC-CI-8-1..8).</summary>
public sealed partial class SimulationIsolationArchitectureTests
{
    internal static string SimApiJob(string workflow) =>
        Jobs(workflow).TryGetValue("sim-api-fuzz", out var job) ? job : throw new InvalidOperationException("sim-api-fuzz job not found: vacuous guard.");

    /// <summary>SEC-CI-8-3/6: the governance DB connection string, certificate and environment rules of the sim-api-fuzz job.</summary>
    internal static IReadOnlyList<string> GovernanceDbViolations(string workflow)
    {
        var bad = new List<string>();
        var job = SimApiJob(workflow);
        foreach (var required in new[]
        {
            "AUTHERIS_SIM_GOVERNANCE_DB=", "SSL Mode=VerifyFull", "Root Certificate=", "-c ssl=on", "ssl_cert_file=", "ssl_key_file=",
            "subjectAltName=IP:127.0.0.1,DNS:localhost", "extendedKeyUsage=serverAuth", "-days 1 ", "openssl genrsa -out server.key 2048", "mktemp -d \"$RUNNER_TEMP/",
        })
        {
            if (!job.Contains(required, StringComparison.Ordinal))
            {
                bad.Add($"missing {required}");
            }
        }

        if (Regex.IsMatch(job, @"Trust Server Certificate|NoVerify|SSL Mode=(Require|Prefer|Disable|VerifyCA)|ssl=off", RegexOptions.IgnoreCase))
        {
            bad.Add("TLS relaxation present");
        }

        if (Regex.IsMatch(workflow, @"(ASPNETCORE|DOTNET)_ENVIRONMENT"))
        {
            bad.Add("environment name is set");
        }

        // The CA key is deleted right after signing and never mounted.
        if (!Regex.IsMatch(job, @"rm -f ca\.key") || Regex.IsMatch(job, @"-v\s+\S*ca\.key"))
        {
            bad.Add("CA key must be deleted and never mounted");
        }

        // Mode 0600 must be set while the runner still owns the key (an unprivileged chmod after the chown fails), or via sudo.
        var chown = job.IndexOf("sudo chown 70:70 server.key", StringComparison.Ordinal);
        var chmod = job.IndexOf("chmod 0600 server.key", StringComparison.Ordinal);
        var chmodOk = chmod >= 0 && (chmod < chown || job.Contains("sudo chmod 0600 server.key", StringComparison.Ordinal));
        if (Regex.IsMatch(job, @"chmod\s+(0?644|0?640|0?666|0?777)\s+server\.key") || chown < 0 || !chmodOk)
        {
            bad.Add("server.key must be uid 70 with mode 0600 (chmod before chown, or sudo chmod)");
        }

        return bad;
    }

    /// <summary>SEC-CI-8-4/5/7/8: password handling, loopback bind, image pin, teardown and log handling for the governance PostgreSQL container.</summary>
    internal static IReadOnlyList<string> PostgresContainerViolations(string workflow)
    {
        var bad = new List<string>();
        var job = SimApiJob(workflow);
        var joined = string.Join('\n', ParityIsolationArchitectureTests.JoinedLines(job).Where(l => !l.TrimStart().StartsWith('#')));
        var run = joined.Split('\n').FirstOrDefault(l => Regex.IsMatch(l, @"\bdocker\s+run\b") && l.Contains("$SIM_POSTGRES_IMAGE", StringComparison.Ordinal));
        if (run is null)
        {
            return ["no postgres docker run found (vacuous guard)"];
        }

        if (!Regex.IsMatch(job, @"SIM_POSTGRES_IMAGE: 'postgres:16-alpine@sha256:[0-9a-f]{64}'"))
        {
            bad.Add("postgres image must be pinned by digest");
        }

        if (!Regex.IsMatch(run, @"-p\s+""127\.0\.0\.1:\$port:5432""") || Regex.Count(run, @"\s-p\s") != 1)
        {
            bad.Add("postgres must publish exactly one port bound to 127.0.0.1");
        }

        foreach (var forbidden in new[] { "--network", "--privileged", "docker.sock", "--restart", "POSTGRES_PASSWORD=" })
        {
            if (run.Contains(forbidden, StringComparison.Ordinal))
            {
                bad.Add($"postgres docker run must not contain {forbidden}");
            }
        }

        if (!run.Contains("--rm", StringComparison.Ordinal) || !run.Contains("--env-file", StringComparison.Ordinal) || Regex.IsMatch(run, @"\s-e\s"))
        {
            bad.Add("postgres needs --rm and --env-file and no -e");
        }

        if (Regex.Count(run, @"-v\s+""\$tls/server\.(crt|key):/tls/server\.(crt|key):ro""") != 2 || Regex.Count(run, @"-v\s") != 2)
        {
            bad.Add("postgres may mount only the server certificate and key read-only");
        }

        // Masking happens before any other use of the password and of the connection string.
        var maskPw = job.IndexOf("echo \"::add-mask::$pw\"", StringComparison.Ordinal);
        var maskConn = job.IndexOf("echo \"::add-mask::$conn\"", StringComparison.Ordinal);
        var firstPwUse = job.IndexOf("pw=\"$(openssl rand -hex 24)\"", StringComparison.Ordinal);
        var otherUse = Regex.Matches(job, @"\$pw\b").Select(m => m.Index).Where(i => i != maskPw + 21).DefaultIfEmpty(int.MaxValue).Min();
        if (maskPw < 0 || firstPwUse < 0 || maskPw < firstPwUse || maskPw > otherUse)
        {
            bad.Add("password must come from openssl rand -hex 24 and be masked before any other use");
        }

        if (maskConn < 0 || maskConn > job.IndexOf("docker run", StringComparison.Ordinal) || maskConn > job.IndexOf("GITHUB_ENV", StringComparison.Ordinal))
        {
            bad.Add("connection string must be masked before docker run and GITHUB_ENV");
        }

        if (job.Split('\n').Any(l => Regex.IsMatch(l, @"\becho\b[^\n]*\$(pw|conn)\b") && !Regex.IsMatch(l, @"add-mask::|echo ""(AUTHERIS_SIM_GOVERNANCE_DB|SIM_GOV_PASSWORD)=")) || Regex.IsMatch(job, @"docker\s+logs") || Regex.IsMatch(job, @"secrets\."))
        {
            bad.Add("password echoed, container logs read or secrets used");
        }

        // Fail-closed readiness and an always() teardown that removes the container.
        if (!job.Contains("pg_isready", StringComparison.Ordinal) || !Regex.IsMatch(job, @"ready"" -ne 1 \]; then\s+echo ""::error::[^\n]*\s+exit 1")
            || !job.Contains("openssl s_client -starttls postgres", StringComparison.Ordinal) || !job.Contains("-verify_return_error", StringComparison.Ordinal))
        {
            bad.Add("readiness and TLS verification must fail closed");
        }

        var teardown = Steps(job).FirstOrDefault(st => st.Contains("docker rm -f sim-gov-pg", StringComparison.Ordinal));
        if (teardown is null || !teardown.Contains("if: always()", StringComparison.Ordinal))
        {
            bad.Add("container teardown step with if: always() missing");
        }

        if (Steps(job).Any(st => st.Contains("continue-on-error", StringComparison.Ordinal)))
        {
            bad.Add("continue-on-error is forbidden in the sim-api-fuzz job");
        }

        return bad;
    }

    /// <summary>SEC-CI-8-1/6/7: certificate material stays out of the workspace and of uploads; only the scrubbed report is uploaded.</summary>
    internal static IReadOnlyList<string> GovernanceUploadViolations(string workflow)
    {
        var bad = new List<string>();
        var job = SimApiJob(workflow);
        var uploads = Steps(job).Where(st => st.Contains("actions/upload-artifact@", StringComparison.Ordinal)).ToList();
        if (uploads.Count != 1 || !Regex.IsMatch(uploads[0], @"path: \$\{\{ github\.workspace \}\}/sim-report-api\r?\n") || Regex.Count(uploads[0], @"\n\s{10}path:") != 1)
        {
            bad.Add("exactly one upload of sim-report-api expected");
        }

        if (Regex.IsMatch(job, @"sim-pg-tls[^\n]*(GITHUB_WORKSPACE|github\.workspace)|(GITHUB_WORKSPACE|github\.workspace)[^\n]*sim-pg-tls") || job.Contains("postgres-log", StringComparison.Ordinal))
        {
            bad.Add("certificate material or postgres logs near the workspace or an upload");
        }

        if (!job.Contains("mktemp -d \"$RUNNER_TEMP/sim-pg-tls.XXXXXX\"", StringComparison.Ordinal) || !job.Contains("umask 077", StringComparison.Ordinal))
        {
            bad.Add("certificate directory must be a mktemp -d under RUNNER_TEMP with umask 077");
        }

        return bad;
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Sim_api_job_provides_governance_db_with_verify_full()
    {
        // SEC-CI-8-1, SEC-CI-8-2, SEC-CI-8-3, SEC-CI-8-6
        GovernanceDbViolations(SimWorkflowText()).ShouldBeEmpty();

        // negative self-check
        var good = SimWorkflowText();
        GovernanceDbViolations(good.Replace("SSL Mode=VerifyFull", "SSL Mode=Require")).ShouldNotBeEmpty();
        GovernanceDbViolations(good.Replace("Root Certificate=", "Trust Server Certificate=true;Root Certificate=")).ShouldNotBeEmpty();
        GovernanceDbViolations(good.Replace("    env:\n      AUTHERIS_SIM_RUN", "    env:\n      ASPNETCORE_ENVIRONMENT: Development\n      AUTHERIS_SIM_RUN")).ShouldNotBeEmpty();
        GovernanceDbViolations(good.Replace("-c ssl=on", "-c ssl=off")).ShouldNotBeEmpty();
        GovernanceDbViolations(good.Replace("chmod 0600 server.key", "chmod 644 server.key")).ShouldNotBeEmpty();
        GovernanceDbViolations(good.Replace("          chmod 0600 server.key\n          sudo chown 70:70 server.key\n", "          sudo chown 70:70 server.key\n          chmod 0600 server.key\n")).ShouldNotBeEmpty();
        GovernanceDbViolations(good.Replace("          chmod 0600 server.key\n          sudo chown 70:70 server.key\n", "          sudo chown 70:70 server.key\n          sudo chmod 0600 server.key\n")).ShouldBeEmpty();
        GovernanceDbViolations(good.Replace("          chmod 0600 server.key\n", "")).ShouldNotBeEmpty();
        GovernanceDbViolations(good.Replace("rm -f ca.key", "rm -f ca.srl")).ShouldNotBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Sim_api_job_masks_generated_password_and_binds_postgres_to_loopback()
    {
        // SEC-CI-8-4, SEC-CI-8-5, SEC-CI-8-7, SEC-CI-8-8
        PostgresContainerViolations(SimWorkflowText()).ShouldBeEmpty();

        // the helper script redacts the password and connection string and adds them to the canary list
        var script = File.ReadAllText(Path.Combine(Root, "scripts", "sim", "run-api-fuzz.sh"));
        script.ShouldContain("ENVIRON[\"SIM_GOV_PASSWORD\"]");
        script.ShouldContain("ENVIRON[\"AUTHERIS_SIM_GOVERNANCE_DB\"]");
        script.ShouldContain("\"${SIM_GOV_PASSWORD:-}\" \"${AUTHERIS_SIM_GOVERNANCE_DB:-}\"");

        // the DockerRunViolations exemption holds for exactly one postgres docker run, inside sim-api-fuzz
        var pgRun = "docker run -d --rm --env-file x -p \"127.0.0.1:$port:5432\" \"$SIM_POSTGRES_IMAGE\"";
        var wf = SimWorkflowText();
        DockerRunViolations([wf, "docker run --rm $SIM_POSTGRES_IMAGE"]).ShouldNotBeEmpty();
        DockerRunViolations([wf.Replace("jobs:\n", "jobs:\n  other:\n    steps:\n      - run: " + pgRun + "\n", StringComparison.Ordinal)]).ShouldNotBeEmpty();
        DockerRunViolations(WorkflowAndScriptTexts()).ShouldBeEmpty();

        // negative self-check
        var good = SimWorkflowText();
        PostgresContainerViolations(good.Replace("-p \"127.0.0.1:$port:5432\"", "-p 5432:5432")).ShouldNotBeEmpty();
        PostgresContainerViolations(good.Replace("--env-file \"$tls/pg.env\"", "-e POSTGRES_PASSWORD=$pw")).ShouldNotBeEmpty();
        PostgresContainerViolations(good.Replace("docker run -d --rm", "docker run -d --network host --rm")).ShouldNotBeEmpty();
        PostgresContainerViolations(good.Replace("docker run -d --rm", "docker run -d")).ShouldNotBeEmpty();
        PostgresContainerViolations(good.Replace("postgres:16-alpine@sha256:", "postgres:16-alpine@sha256x:")).ShouldNotBeEmpty();
        PostgresContainerViolations(good.Replace("echo \"::add-mask::$pw\"", "echo \"$pw\"")).ShouldNotBeEmpty();
        PostgresContainerViolations(good.Replace("        if: always()\n        run: |\n          docker rm -f sim-gov-pg", "        run: |\n          docker rm -f sim-gov-pg")).ShouldNotBeEmpty();
        PostgresContainerViolations(good.Replace("            exit 1\n          fi\n          # TLS check", "            true\n          fi\n          # TLS check")).ShouldNotBeEmpty();
        PostgresContainerViolations(good.Replace("sleep 1\n          done", "sleep 1\n          done\n          docker logs sim-gov-pg")).ShouldNotBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Sim_api_job_permissions_stay_contents_read_and_upload_only_scrubbed_report()
    {
        // SEC-CI-8-1, SEC-CI-8-6, SEC-CI-8-7
        PermissionViolations(SimWorkflowText()).ShouldBeEmpty();
        GovernanceUploadViolations(SimWorkflowText()).ShouldBeEmpty();
        SecretViolations(SimWorkflowText()).ShouldBeEmpty();

        // negative self-check
        var good = SimWorkflowText();
        GovernanceUploadViolations(good.Replace("path: ${{ github.workspace }}/sim-report-api", "path: ${{ github.workspace }}")).ShouldNotBeEmpty();
        GovernanceUploadViolations(good.Replace("mktemp -d \"$RUNNER_TEMP/sim-pg-tls.XXXXXX\"", "mktemp -d \"$GITHUB_WORKSPACE/sim-pg-tls.XXXXXX\"")).ShouldNotBeEmpty();
        GovernanceUploadViolations(good.Replace("          umask 077\n          tls=", "          tls=")).ShouldNotBeEmpty();
    }
}
