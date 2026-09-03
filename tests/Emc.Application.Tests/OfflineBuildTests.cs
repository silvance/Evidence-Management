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
}
