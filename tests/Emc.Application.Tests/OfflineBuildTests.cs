using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Emc.Application.Tests;

/// <summary>
/// Checks, from source, the parts of the air-gap constraint that can be checked from source:
/// the SDK is pinned exactly; lock files exist for every project; the offline NuGet
/// configuration cannot reach the Internet; no package version floats; and the web project
/// references no remote asset. See docs/air-gapped-build-and-maintenance.md.
///
/// [CONTROL] - a program constraint, not an AR 195-5 requirement.
/// </summary>
public class OfflineBuildTests
{
    internal static readonly string Root = FindRepositoryRoot();

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Emc.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Emc.sln not found above the test directory.");
    }

    private static IEnumerable<string> ProjectFiles()
        => Directory.EnumerateFiles(Root, "*.csproj", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !p.Contains("dependency-bundle"));

    [Fact]
    public void TheSdkIsPinnedExactly_AndDoesNotRollForward()
    {
        var text = File.ReadAllText(Path.Combine(Root, "global.json"));

        Assert.Matches("\"version\"\\s*:\\s*\"\\d+\\.\\d+\\.\\d+\"", text);
        Assert.Contains("\"rollForward\": \"disable\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryProjectHasACommittedLockFile()
    {
        foreach (var project in ProjectFiles())
        {
            var lockFile = Path.Combine(Path.GetDirectoryName(project)!, "packages.lock.json");
            Assert.True(File.Exists(lockFile), $"Missing packages.lock.json beside {Path.GetFileName(project)}.");
        }
    }

    [Fact]
    public void LockFilesAreEnabledForTheWholeSolution()
    {
        var props = File.ReadAllText(Path.Combine(Root, "Directory.Build.props"));

        Assert.Contains("<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>", props, StringComparison.Ordinal);
        Assert.Contains("RestoreLockedMode", props, StringComparison.Ordinal);
    }

    [Fact]
    public void NoPackageVersionFloats()
    {
        foreach (var project in ProjectFiles())
        {
            var doc = XDocument.Load(project);
            foreach (var reference in doc.Descendants("PackageReference"))
            {
                var version = reference.Attribute("Version")?.Value ?? reference.Element("Version")?.Value;
                Assert.False(string.IsNullOrWhiteSpace(version), $"{Path.GetFileName(project)}: {reference.Attribute("Include")?.Value} has no version.");
                Assert.DoesNotContain("*", version, StringComparison.Ordinal);
                Assert.DoesNotContain("[", version, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void TheOfflineNuGetConfigurationCannotReachTheInternet()
    {
        var doc = XDocument.Load(Path.Combine(Root, "NuGet.Offline.Config"));

        var sources = doc.Root!.Element("packageSources")!;
        Assert.NotNull(sources.Element("clear"));

        foreach (var add in sources.Elements("add"))
        {
            var value = add.Attribute("value")!.Value;
            Assert.False(value.StartsWith("http", StringComparison.OrdinalIgnoreCase), $"Offline source is a URL: {value}");
            Assert.False(Path.IsPathRooted(value), $"Offline source is machine-specific: {value}");
        }

        Assert.Single(sources.Elements("add"));

        var audit = doc.Root.Element("auditSources");
        Assert.NotNull(audit);
        Assert.NotNull(audit!.Element("clear"));
        Assert.Empty(audit.Elements("add"));

        Assert.DoesNotContain("password", File.ReadAllText(Path.Combine(Root, "NuGet.Offline.Config")), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheWebProjectReferencesNoRemoteAsset()
    {
        // CDN scripts, web fonts, remote stylesheets, remote imports: none. Comments and the
        // launch-settings file are not assets and are excluded.
        var web = Path.Combine(Root, "src", "Emc.Web");
        var files = Directory.EnumerateFiles(web, "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".css", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));

        var remote = new Regex(
            "(src|href)\\s*=\\s*[\"'](https?:)?//|url\\(\\s*[\"']?(https?:)?//|@import\\s+[\"'(]*https?://|fonts\\.googleapis|cdn\\.|cdnjs|jsdelivr|unpkg",
            RegexOptions.IgnoreCase);

        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.TrimStart().StartsWith("/*", StringComparison.Ordinal) || line.TrimStart().StartsWith("*", StringComparison.Ordinal)
                    || line.TrimStart().StartsWith("@*", StringComparison.Ordinal) || line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                Assert.False(remote.IsMatch(line), $"{Path.GetRelativePath(Root, file)}:{i + 1} references a remote asset: {line.Trim()}");
            }
        }
    }

    [Fact]
    public void TheDependencyBundleIsNotCommitted()
    {
        var gitignore = File.ReadAllText(Path.Combine(Root, ".gitignore"));
        Assert.Contains("dependency-bundle/*", gitignore, StringComparison.Ordinal);
    }

    [Fact]
    public void TheArtifactManifestExampleCarriesEveryFieldTheExportRequires()
    {
        // The non-NuGet artifact path (OCR engine, models). The example is what a staging
        // reviewer copies; if it drifts from what Export-DependencyBundle.ps1 demands, the first
        // real export fails in staging instead of here.
        using var doc = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(Root, "scripts", "staging", "artifacts.manifest.example.json")));
        Assert.Equal("emc-artifact-manifest/1", doc.RootElement.GetProperty("schema").GetString());

        var script = File.ReadAllText(Path.Combine(Root, "scripts", "staging", "Export-DependencyBundle.ps1"));
        var required = new[] { "name", "kind", "version", "path", "origin", "sha256", "license", "classification", "retrievedUtc", "reviewStatus", "reviewedBy", "reviewedUtc" };
        foreach (var field in required)
        {
            Assert.Contains($"'{field}'", script, StringComparison.Ordinal);
        }

        var kinds = new[] { "ocr-engine", "ocr-model", "native-runtime", "pdf-rasterizer" };
        var artifacts = doc.RootElement.GetProperty("artifacts").EnumerateArray().ToList();
        Assert.NotEmpty(artifacts);
        foreach (var a in artifacts)
        {
            foreach (var field in required)
            {
                Assert.True(a.TryGetProperty(field, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String && v.GetString()!.Length > 0, $"{field} missing");
            }

            Assert.Contains(a.GetProperty("kind").GetString(), kinds);
            Assert.Equal("approved", a.GetProperty("reviewStatus").GetString());
            Assert.Matches("^[0-9a-f]{64}$", a.GetProperty("sha256").GetString()!);
            if (a.GetProperty("kind").GetString() == "ocr-model")
            {
                Assert.False(string.IsNullOrEmpty(a.GetProperty("modelId").GetString()), "ocr-model without modelId");
            }
        }

        // The example is an example: placeholder hashes only, so nobody mistakes it for a review.
        Assert.All(artifacts, a => Assert.Equal(new string('0', 64), a.GetProperty("sha256").GetString()));

        // Both verifiers know the same kinds.
        var ps = File.ReadAllText(Path.Combine(Root, "scripts", "airgap", "Verify-DependencyBundle.ps1"));
        var sh = File.ReadAllText(Path.Combine(Root, "scripts", "airgap", "verify-dependency-bundle.sh"));
        foreach (var kind in kinds)
        {
            Assert.Contains(kind, ps, StringComparison.Ordinal);
            Assert.Contains(kind, sh, StringComparison.Ordinal);
        }
    }

    // ----- Release bundle (docs/release-bundle.md, SEC-013) -----

    [Fact]
    public void TheReleaseBundleIsNotCommitted()
    {
        var gitignore = File.ReadAllText(Path.Combine(Root, ".gitignore"));
        Assert.Contains("release/", gitignore, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRuntimesArePinnedSolutionWide_NeverSelfContained_AndEveryLockFileCarriesThem()
    {
        // A publish for a runtime flows that runtime into every referenced project, so the pin
        // lives in Directory.Build.props and every lock file carries the runtime-specific graph;
        // otherwise an offline LOCKED restore for the release publish fails.
        var props = File.ReadAllText(Path.Combine(Root, "Directory.Build.props"));
        var rids = Regex.Match(props, "<RuntimeIdentifiers>([^<]+)</RuntimeIdentifiers>").Groups[1].Value.Split(';', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("win-x64", rids);
        Assert.Contains("<SelfContained>false</SelfContained>", props, StringComparison.Ordinal);
        Assert.Contains("<EnableRuntimePackDownload>false</EnableRuntimePackDownload>", props, StringComparison.Ordinal);
        Assert.DoesNotContain("PublishSingleFile", props, StringComparison.Ordinal);
        Assert.DoesNotContain("PublishTrimmed", props, StringComparison.Ordinal);
        Assert.Matches("<VersionPrefix>\\d+\\.\\d+\\.\\d+</VersionPrefix>", props);

        foreach (var project in ProjectFiles())
        {
            var lockFile = File.ReadAllText(Path.Combine(Path.GetDirectoryName(project)!, "packages.lock.json"));
            foreach (var rid in rids)
            {
                Assert.True(lockFile.Contains($"\"net10.0/{rid}\"", StringComparison.Ordinal), $"{Path.GetFileName(project)}: packages.lock.json has no net10.0/{rid} target; run a connected restore with --force-evaluate.");
            }
        }
    }

    [Fact]
    public void TheDependencyBundleExportCarriesTheApphostPacks()
    {
        // NuGet fetches Microsoft.NETCore.App.Host.<rid> as a package download the lock files do
        // not record; the export must add it or the offline publish cannot produce an executable.
        var export = File.ReadAllText(Path.Combine(Root, "scripts", "staging", "Export-DependencyBundle.ps1"));
        Assert.Contains("Microsoft.NETCore.App.Host.", export, StringComparison.Ordinal);
        Assert.Contains("BundledNETCoreAppPackageVersion", export, StringComparison.Ordinal);
        Assert.Contains("<RuntimeIdentifiers>", export, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReleaseScriptsReachNoNetwork_RestoreLocked_AndAgreeOnTheBundle()
    {
        var dir = Path.Combine(Root, "scripts", "release");
        var producers = new[] { "New-ReleaseBundle.ps1", "new-release-bundle.sh" }.Select(f => (Name: f, Text: File.ReadAllText(Path.Combine(dir, f)))).ToList();
        var verifiers = new[] { "Verify-ReleaseBundle.ps1", "verify-release-bundle.sh" }.Select(f => (Name: f, Text: File.ReadAllText(Path.Combine(dir, f)))).ToList();

        foreach (var (name, text) in producers.Concat(verifiers))
        {
            // The loopback address the smoke test binds the published web host to is not a network.
            var withoutLoopback = text.Replace("http://127.0.0.1", string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("http://", withoutLoopback, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("https://", withoutLoopback, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("emc-release-bundle/1", text, StringComparison.Ordinal);
        }

        foreach (var (name, text) in producers)
        {
            // Offline by default: the dependency bundle is verified and restore is locked and
            // reads NuGet.Offline.Config with EMC_OFFLINE=true; nothing is restored after that.
            Assert.Contains("NuGet.Offline.Config", text, StringComparison.Ordinal);
            Assert.Contains("--locked-mode", text, StringComparison.Ordinal);
            Assert.Contains("EMC_OFFLINE=true", text, StringComparison.Ordinal);
            Assert.Contains("--no-restore", text, StringComparison.Ordinal);
            Assert.Contains("--self-contained false", text, StringComparison.Ordinal);
            Assert.Contains("SourceRevisionId", text, StringComparison.Ordinal);
            Assert.Contains("appsettings.Development.json", text, StringComparison.Ordinal);
            Assert.Contains("db/schema-v1.sql", text.Replace('\\', '/'), StringComparison.Ordinal);
            Assert.Contains("docs/release-bundle.md", text.Replace('\\', '/'), StringComparison.Ordinal);
            Assert.Contains("MANIFEST.sha256", text, StringComparison.Ordinal);
            Assert.Contains("STAGING DRY RUN", text, StringComparison.Ordinal);
            Assert.Contains("dirty", text, StringComparison.OrdinalIgnoreCase);
            // A publish without a test run is marked, never silent.
            Assert.Contains("tests", text, StringComparison.Ordinal);
            Assert.Contains("verify-release-bundle.sh", text, StringComparison.Ordinal);
            Assert.Contains("Verify-ReleaseBundle.ps1", text, StringComparison.Ordinal);
        }

        foreach (var (name, text) in verifiers)
        {
            Assert.Contains("MANIFEST.sha256", text, StringComparison.Ordinal);
            Assert.Contains("release-manifest.json", text, StringComparison.Ordinal);
            Assert.Contains("aspNetCore processPath=", text, StringComparison.Ordinal);
            Assert.Contains("appsettings.Development.json", text, StringComparison.Ordinal);
            Assert.Contains("password=", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("entryPoint", text, StringComparison.Ordinal);
            Assert.Contains("STAGING DRY RUN", text, StringComparison.Ordinal);
        }
    }

    // ----- The SQL Server lane and the DBA schema script, checked without a SQL Server -----

    private static Dictionary<string, HashSet<string>> SchemaColumns()
    {
        var script = File.ReadAllText(Path.Combine(Root, "db", "schema-v1.sql"));
        var tables = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (Match table in Regex.Matches(script, @"CREATE TABLE \[(?<name>\w+)\] \((?<body>.*?)\n\s*\);", RegexOptions.Singleline))
        {
            tables[table.Groups["name"].Value] = Regex.Matches(table.Groups["body"].Value, @"\n\s+\[(?<col>\w+)\]")
                .Select(m => m.Groups["col"].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        Assert.NotEmpty(tables);
        return tables;
    }

    [Fact]
    public void TheSqlServerLaneNamesOnlyColumnsTheSchemaHas()
    {
        // The lane runs only against a real SQL Server, so a column it misnames surfaces there as
        // error 207 instead of the trigger or index error the test expects. Check its statements
        // against the committed schema script here, where every run sees them.
        var tables = SchemaColumns();
        var lane = File.ReadAllText(Path.Combine(Root, "tests", "Emc.Application.Tests", "SqlServer", "SqlServerReleaseValidationTests.cs"));

        foreach (Match insert in Regex.Matches(lane, @"INSERT INTO (?<table>\w+)\s*\((?<cols>[^)]*)\)", RegexOptions.Singleline))
        {
            var table = insert.Groups["table"].Value;
            Assert.True(tables.ContainsKey(table), $"lane inserts into unknown table {table}");
            foreach (var column in insert.Groups["cols"].Value.Split(',').Select(c => c.Trim()).Where(c => c.Length > 0))
            {
                Assert.True(tables[table].Contains(column), $"lane inserts into {table}.{column}, which the schema does not have");
            }

            // Every NOT NULL column without a default must be supplied, or the insert fails with 515.
            var body = Regex.Match(File.ReadAllText(Path.Combine(Root, "db", "schema-v1.sql")), @"CREATE TABLE \[" + table + @"\] \((?<body>.*?)\n\s*\);", RegexOptions.Singleline).Groups["body"].Value;
            foreach (Match col in Regex.Matches(body, @"\n\s+\[(?<col>\w+)\] [^,\n]*? NOT NULL(?<rest>[^,\n]*)"))
            {
                var name = col.Groups["col"].Value;
                if (col.Groups["rest"].Value.Contains("IDENTITY", StringComparison.Ordinal) || col.Groups["rest"].Value.Contains("DEFAULT", StringComparison.Ordinal)) continue;
                Assert.True(insert.Groups["cols"].Value.Split(',').Select(c => c.Trim()).Contains(name, StringComparer.OrdinalIgnoreCase),
                    $"lane insert into {table} omits NOT NULL column {name}");
            }
        }

        foreach (Match update in Regex.Matches(lane, @"UPDATE (?<table>\w+) SET (?<col>\w+) ="))
        {
            var table = update.Groups["table"].Value;
            Assert.True(tables.ContainsKey(table) && tables[table].Contains(update.Groups["col"].Value), $"lane updates {table}.{update.Groups["col"].Value}, which the schema does not have");
        }
    }

    [Fact]
    public void EveryTriggerInTheSchemaScriptIsItsOwnBatch()
    {
        // SQL Server error 111: CREATE TRIGGER must be the first statement in a batch. The
        // idempotent script wraps each migration statement in IF NOT EXISTS ... BEGIN ... END, so
        // a bare CREATE TRIGGER there fails for the DBA; each must run through EXEC(N'...').
        var script = File.ReadAllText(Path.Combine(Root, "db", "schema-v1.sql"));
        var wrapped = Regex.Matches(script, @"EXEC\(N'CREATE OR ALTER TRIGGER (?<name>TR_\w+)").Select(m => m.Groups["name"].Value).ToList();
        var bare = Regex.Matches(script, @"^\s*CREATE (OR ALTER )?TRIGGER", RegexOptions.Multiline).Count;

        Assert.Equal(0, bare);
        Assert.Equal(Emc.Infrastructure.Persistence.AppendOnlyTriggers.Definitions.Count, wrapped.Count);
        Assert.Equal(wrapped.Count, wrapped.Distinct(StringComparer.Ordinal).Count());
        Assert.All(Emc.Infrastructure.Persistence.AppendOnlyTriggers.All, sql => Assert.StartsWith("EXEC(N'CREATE OR ALTER TRIGGER ", sql, StringComparison.Ordinal));
        // Every THROW number is distinct, so a test can tell the triggers apart.
        var numbers = Regex.Matches(script, @"THROW (5\d{4})").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(numbers.Count, numbers.Distinct().Count());
        Assert.Equal(wrapped.Count, numbers.Count);
    }
}
