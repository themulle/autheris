using System.Diagnostics;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

public sealed class HelmValuesTests
{
    [Fact]
    [Trait("Category", "Security")]
    public void Image_tag_is_pinned_and_not_latest()
    {
        var valuesPath = Path.Combine(RepositoryScan.RepoRoot, "deploy", "helm", "autheris", "values.yaml");
        var helpersPath = Path.Combine(RepositoryScan.RepoRoot, "deploy", "helm", "autheris", "templates", "_helpers.tpl");

        File.Exists(valuesPath).ShouldBeTrue();
        File.Exists(helpersPath).ShouldBeTrue();

        var valuesContent = File.ReadAllText(valuesPath);
        var helpersContent = File.ReadAllText(helpersPath);

        // Extract image.tag from values.yaml
        var tagMatch = Regex.Match(valuesContent, @"tag:\s*""?([^""\r\n]*)""?");
        tagMatch.Success.ShouldBeTrue("image.tag must be present in values.yaml");

        var tagValue = tagMatch.Groups[1].Value.Trim();

        // Never allow "latest" as default image tag
        tagValue.ToLowerInvariant().ShouldNotBe("latest");

        // Must be empty (to fall back to AppVersion) or a sha256 digest
        var isTagValid = string.IsNullOrEmpty(tagValue) || tagValue.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase);
        isTagValid.ShouldBeTrue($"image.tag must be empty or a digest, but was '{tagValue}'");

        // Helper autheris.image must fall back to .Chart.AppVersion
        helpersContent.ShouldContain("default .Chart.AppVersion .Values.image.tag");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void App_credentials_separate_from_migration()
    {
        var chartRoot = Path.Combine(RepositoryScan.RepoRoot, "deploy", "helm", "autheris");
        var valuesPath = Path.Combine(chartRoot, "values.yaml");
        var migrationJobPath = Path.Combine(chartRoot, "templates", "governance-migration-job.yaml");
        var deploymentPath = Path.Combine(chartRoot, "templates", "deployment.yaml");
        var helpersPath = Path.Combine(chartRoot, "templates", "_helpers.tpl");
        var pgRoleSqlPath = Path.Combine(RepositoryScan.RepoRoot, "deploy", "sql", "governance-app-role.postgresql.sql");
        var ssRoleSqlPath = Path.Combine(RepositoryScan.RepoRoot, "deploy", "sql", "governance-app-role.sqlserver.sql");

        var values = File.ReadAllText(valuesPath);
        var migrationJob = File.ReadAllText(migrationJobPath);
        var deployment = File.ReadAllText(deploymentPath);
        var helpers = File.ReadAllText(helpersPath);

        // 1. values.yaml defines distinct runtime and migration secret keys.
        var runtimeSecretKey = ExtractYamlScalar(values, "governanceDb", "connectionStringSecretKey");
        var migrationSecretKey = ExtractYamlScalar(values, "governanceMigrationJob", "migrationConnectionStringSecretKey");
        var migrationExistingSecretMatch = Regex.Match(
            ExtractSection(values, "governanceMigrationJob"),
            @"^\s*existingSecret\s*:",
            RegexOptions.Multiline);

        runtimeSecretKey.ShouldNotBeNullOrWhiteSpace();
        migrationSecretKey.ShouldNotBeNullOrWhiteSpace();
        migrationSecretKey.ShouldNotBe(runtimeSecretKey);
        migrationExistingSecretMatch.Success.ShouldBeTrue("governanceMigrationJob.existingSecret must be declared in values.yaml");

        // 2. Migration job requires separate credentials (trimmed fail guard) and injects MigrationConnectionString.
        migrationJob.ShouldContain("toString | trim");
        migrationJob.ShouldContain("fail \"governanceMigrationJob.migrationConnectionStringSecretKey must reference DDL migration credentials separate from gateway.governanceDb.connectionStringSecretKey\"");
        migrationJob.ShouldContain("Gateway__GovernanceDb__MigrationConnectionString");
        migrationJob.ShouldContain("name: {{ $migSecret }}");
        migrationJob.ShouldContain("key: {{ $migKey }}");

        // 3. Runtime app pod never receives MigrationConnectionString and runs in Migrations__Mode=Verify when migration job is enabled.
        deployment.ShouldNotContain("Gateway__GovernanceDb__MigrationConnectionString");
        helpers.ShouldNotContain("Gateway__GovernanceDb__MigrationConnectionString");
        deployment.ShouldNotContain("migrationConnectionStringSecretKey");
        helpers.ShouldNotContain("migrationConnectionStringSecretKey");
        deployment.ShouldContain("Gateway__GovernanceDb__Migrations__Mode");
        deployment.ShouldContain("value: \"Verify\"");

        // 4. Least-privilege SQL provisioning scripts exist and enforce INSERT+SELECT only on audit log tables.
        File.Exists(pgRoleSqlPath).ShouldBeTrue($"Missing PostgreSQL app-role SQL script: {pgRoleSqlPath}");
        File.Exists(ssRoleSqlPath).ShouldBeTrue($"Missing SQL Server app-role SQL script: {ssRoleSqlPath}");

        var pgSql = File.ReadAllText(pgRoleSqlPath);
        pgSql.ShouldContain("audit_log_entries");
        pgSql.ShouldContain("autheris_schema_migrations");
        pgSql.ShouldContain("REVOKE CREATE ON SCHEMA public FROM PUBLIC");
        pgSql.ShouldContain("GRANT SELECT, INSERT");
        pgSql.ShouldContain("pg_get_serial_sequence");
        pgSql.ShouldContain("rolbypassrls");
        pgSql.ShouldContain("pg_has_role(app_role, c.relowner, 'USAGE')");
        Regex.IsMatch(pgSql, @"GRANT[^;]*\b(UPDATE|DELETE|TRUNCATE|ALL)\b[^;]*audit_log_entries", RegexOptions.IgnoreCase)
            .ShouldBeFalse("PostgreSQL script must never grant UPDATE/DELETE/TRUNCATE/ALL on audit_log_entries");

        var ssSql = File.ReadAllText(ssRoleSqlPath);
        ssSql.ShouldContain("AUDIT_LOG_ENTRIES");
        ssSql.ShouldContain("AUDIT_LOG_TRUNCATE_GUARD");
        ssSql.ShouldContain("autheris_schema_migrations");
        ssSql.ShouldContain("GRANT SELECT, INSERT");
        ssSql.ShouldContain("GRANT VIEW DEFINITION");
        ssSql.ShouldContain("DENY UPDATE, DELETE, ALTER, TAKE OWNERSHIP, REFERENCES");
        ssSql.ShouldNotContain("DENY SELECT, INSERT, UPDATE, DELETE, ALTER, CONTROL");
        ssSql.ShouldNotContain("DENY UPDATE, DELETE, ALTER, CONTROL");
        ssSql.ShouldNotContain("DENY INSERT, UPDATE, DELETE, ALTER, CONTROL");
        ssSql.ShouldContain("N'$' + N'(AppUser)'");
        Regex.IsMatch(ssSql, @"GRANT[^;]*\b(UPDATE|DELETE|ALTER|CONTROL)\b[^;]*AUDIT_LOG_ENTRIES", RegexOptions.IgnoreCase)
            .ShouldBeFalse("SQL Server script must never grant UPDATE/DELETE/ALTER/CONTROL on AUDIT_LOG_ENTRIES");

        // 5. When helm CLI is installed, verify live template rendering & rejection of shared/whitespace credentials.
        var helm = TryFindHelmBinary();
        if (helm is not null)
        {
            var ok = RunHelm(helm, ["template", "autheris", chartRoot, "--set", "governanceMigrationJob.enabled=true"]);
            ok.ExitCode.ShouldBe(0, ok.Output);
            ok.Output.ShouldContain("Gateway__GovernanceDb__MigrationConnectionString");
            var deploymentManifest = ExtractManifestKind(ok.Output, "Deployment");
            deploymentManifest.ShouldNotBeNullOrWhiteSpace();
            deploymentManifest.ShouldContain("value: \"Verify\"");
            deploymentManifest.ShouldNotContain("Gateway__GovernanceDb__MigrationConnectionString");

            var sameKey = RunHelm(helm, ["template", "autheris", chartRoot, "--set", "governanceMigrationJob.enabled=true", "--set", "governanceMigrationJob.migrationConnectionStringSecretKey=connectionString"]);
            sameKey.ExitCode.ShouldNotBe(0);
            sameKey.Output.ShouldContain("must reference DDL migration credentials separate");

            var paddedSameKey = RunHelm(helm, ["template", "autheris", chartRoot, "--set", "governanceMigrationJob.enabled=true", "--set", "governanceMigrationJob.existingSecret= autheris-db-secrets ", "--set", "governanceMigrationJob.migrationConnectionStringSecretKey= connectionString "]);
            paddedSameKey.ExitCode.ShouldNotBe(0);
            paddedSameKey.Output.ShouldContain("must reference DDL migration credentials separate");

            var whitespaceKey = RunHelm(helm, ["template", "autheris", chartRoot, "--set", "governanceMigrationJob.enabled=true", "--set", "governanceMigrationJob.migrationConnectionStringSecretKey=   "]);
            whitespaceKey.ExitCode.ShouldNotBe(0);
            whitespaceKey.Output.ShouldContain("must reference DDL migration credentials separate");

            var emptyKey = RunHelm(helm, ["template", "autheris", chartRoot, "--set", "governanceMigrationJob.enabled=true", "--set", "governanceMigrationJob.migrationConnectionStringSecretKey="]);
            emptyKey.ExitCode.ShouldNotBe(0);
            emptyKey.Output.ShouldContain("must reference DDL migration credentials separate");
        }
    }

    private static string ExtractSection(string yaml, string sectionKey)
    {
        var match = Regex.Match(
            yaml,
            $@"^(?<indent>[ \t]*){Regex.Escape(sectionKey)}:\s*\r?\n(?<body>(?:\k<indent>[ \t]+[^\r\n]*\r?\n|\s*\r?\n)*)",
            RegexOptions.Multiline);
        match.Success.ShouldBeTrue($"YAML section '{sectionKey}' not found in values.yaml");
        return match.Groups["body"].Value;
    }

    private static string ExtractYamlScalar(string yaml, string sectionKey, string propertyKey)
    {
        var section = ExtractSection(yaml, sectionKey);
        var match = Regex.Match(
            section,
            $@"^[ \t]*{Regex.Escape(propertyKey)}:\s*""?(?<val>[^""#\r\n]*)""?",
            RegexOptions.Multiline);
        match.Success.ShouldBeTrue($"YAML property '{sectionKey}.{propertyKey}' not found in values.yaml");
        return match.Groups["val"].Value.Trim();
    }

    private static string? TryFindHelmBinary()
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv))
        {
            return null;
        }

        var binaryName = OperatingSystem.IsWindows() ? "helm.exe" : "helm";
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, binaryName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static (int ExitCode, string Output) RunHelm(string helmPath, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(helmPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"helm {string.Join(' ', args)} timed out after 10s");
        }

        Task.WaitAll(stdoutTask, stderrTask);
        return (process.ExitCode, stdoutTask.Result + "\n" + stderrTask.Result);
    }

    private static string ExtractManifestKind(string renderedYaml, string kind)
    {
        foreach (var doc in Regex.Split(renderedYaml, @"(?m)^---\s*$"))
        {
            if (Regex.IsMatch(doc, $@"(?m)^kind:\s*{Regex.Escape(kind)}\s*$"))
            {
                return doc;
            }
        }

        return string.Empty;
    }
}
